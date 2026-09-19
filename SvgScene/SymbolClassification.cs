using AudiverisGlyphPoc;

namespace SvgMusic.Scene;

public interface ISymbolClassifier
{
    IReadOnlyList<SymbolPrediction> Classify(
        RasterGlyphData glyph,
        int top = 8);
}

public sealed record SymbolPrediction(
    string Label,
    double Confidence,
    int Interline);

public sealed record SymbolScaleResult(
    int Interline,
    string Label,
    double Confidence,
    IReadOnlyList<SymbolPrediction> Predictions);

public sealed record SymbolClassification(
    string Label,
    double Confidence,
    int Interline,
    IReadOnlyList<SymbolScaleResult> Scales);

public sealed class AudiverisSymbolClassifier : ISymbolClassifier
{
    private readonly AudiverisModel _model;

    private AudiverisSymbolClassifier(AudiverisModel model)
    {
        _model = model;
        MixGlyphDescriptor.ValidateAgainst(_model);
    }

    public static async Task<AudiverisSymbolClassifier> CreateAsync(
        string? modelPath = null,
        CancellationToken cancellationToken = default)
    {
        var archivePath = await ModelCache.EnsureAsync(
            modelPath,
            cancellationToken);

        return new AudiverisSymbolClassifier(
            AudiverisModel.Load(archivePath));
    }

    public IReadOnlyList<SymbolPrediction> Classify(
        RasterGlyphData glyph,
        int top = 8)
    {
        var x = new List<int>();
        var y = new List<int>();

        for (var row = 0; row < glyph.Height; row++)
        {
            for (var column = 0; column < glyph.Width; column++)
            {
                if (glyph.Pixels[row * glyph.Width + column] >= 128)
                {
                    continue;
                }

                x.Add(column);
                y.Add(row);
            }
        }

        // ART moments need a non-zero radius around the glyph centroid.
        // Rasterization of very small/degenerate SVG geometry (common after
        // PDF -> SVG conversion) can legitimately collapse to a single foreground
        // pixel. Such a point is not a classifiable music symbol and must not take
        // down classification of the whole page.
        if (x.Count < 2)
        {
            return Array.Empty<SymbolPrediction>();
        }

        var binaryGlyph = new BinaryGlyph(x, y);
        var features = MixGlyphDescriptor.Extract(
            binaryGlyph,
            glyph.Interline);

        return _model
            .Evaluate(features, top)
            .Select(prediction => new SymbolPrediction(
                prediction.Label,
                prediction.Score,
                glyph.Interline))
            .ToArray();
    }
}

public sealed record PrototypeScaleClassificationDiagnostic(
    int Interline,
    int RasterWidth,
    int RasterHeight,
    int ForegroundPixels,
    double RasterizeMilliseconds,
    double ClassifyMilliseconds);

public sealed record PrototypeClassificationDiagnostic(
    string PrototypeId,
    string ShapeId,
    double ShapeWidth,
    double ShapeHeight,
    int PointCount,
    double TotalMilliseconds,
    bool Skipped,
    string Reason,
    IReadOnlyList<PrototypeScaleClassificationDiagnostic> Scales);

public sealed class PrototypeSymbolClassifier
{
    public IReadOnlyList<PrototypeClassificationDiagnostic> LastDiagnostics { get; private set; }
        = Array.Empty<PrototypeClassificationDiagnostic>();
    private static readonly int[] Interlines = [20, 30, 40];

    private readonly ISymbolClassifier _classifier;
    private readonly GlyphRasterizer _rasterizer;
    private readonly PrototypeClassifierSettings _settings;

    public PrototypeSymbolClassifier(
        ISymbolClassifier classifier,
        GlyphRasterizer? rasterizer = null,
        SvgMusicSettings? settings = null)
    {
        _classifier = classifier;
        _rasterizer = rasterizer ?? new GlyphRasterizer();
        _settings = (settings ?? SvgMusicSettings.Default).PrototypeClassifier;
    }

    public NotationScene Classify(
        GeometricScene geometry,
        NotationScene notation,
        ScoreLayout layout)
    {
        var shapesById = geometry.Shapes.ToDictionary(
            shape => shape.Id,
            StringComparer.Ordinal);

        var sourceInterline = GlyphRasterizer.ResolveSourceInterline(layout);
        var classifications = new Dictionary<string, SymbolClassification>(
            StringComparer.Ordinal);
        var diagnostics = new List<PrototypeClassificationDiagnostic>();

        foreach (var prototype in notation.Prototypes)
        {
            if (!shapesById.TryGetValue(
                    prototype.RepresentativeShapeId,
                    out var shape))
            {
                continue;
            }

            if (!PassesLocalMeasureSizeGate(
                    prototype,
                    notation,
                    layout,
                    out var gateReason))
            {
                diagnostics.Add(new PrototypeClassificationDiagnostic(
                    prototype.Id,
                    shape.Id,
                    shape.Bounds.Width,
                    shape.Bounds.Height,
                    shape.EffectiveContours.Sum(contour => contour.Points.Count),
                    0,
                    true,
                    gateReason,
                    Array.Empty<PrototypeScaleClassificationDiagnostic>()));
                continue;
            }

            var scales = new List<SymbolScaleResult>();
            var scaleDiagnostics = new List<PrototypeScaleClassificationDiagnostic>();
            var prototypeStopwatch = System.Diagnostics.Stopwatch.StartNew();

            foreach (var interline in Interlines)
            {
                var scaleStopwatch = System.Diagnostics.Stopwatch.StartNew();
                var glyph = _rasterizer.Rasterize(
                    shape,
                    sourceInterline,
                    interline);
                var rasterizeMilliseconds = scaleStopwatch.Elapsed.TotalMilliseconds;

                scaleStopwatch.Restart();
                var predictions = _classifier.Classify(glyph);
                var classifyMilliseconds = scaleStopwatch.Elapsed.TotalMilliseconds;
                var winner = predictions.FirstOrDefault();

                scaleDiagnostics.Add(new PrototypeScaleClassificationDiagnostic(
                    interline,
                    glyph.Width,
                    glyph.Height,
                    glyph.ForegroundPixels,
                    rasterizeMilliseconds,
                    classifyMilliseconds));

                if (winner is null)
                {
                    continue;
                }

                scales.Add(new SymbolScaleResult(
                    interline,
                    winner.Label,
                    winner.Confidence,
                    predictions));
            }

            diagnostics.Add(new PrototypeClassificationDiagnostic(
                prototype.Id,
                shape.Id,
                shape.Bounds.Width,
                shape.Bounds.Height,
                shape.EffectiveContours.Sum(contour => contour.Points.Count),
                prototypeStopwatch.Elapsed.TotalMilliseconds,
                false,
                "within local measure size gate",
                scaleDiagnostics));

            if (scales.Count == 0)
            {
                continue;
            }

            var best = scales
                .OrderByDescending(scale => scale.Confidence)
                .First();

            classifications[prototype.Id] = new SymbolClassification(
                best.Label,
                best.Confidence,
                best.Interline,
                scales);
        }

        var prototypes = notation.Prototypes
            .Select(prototype => prototype with
            {
                Classification = classifications.GetValueOrDefault(prototype.Id)
            })
            .ToArray();

        var instances = notation.Instances
            .Select(instance => instance with
            {
                Classification = classifications.GetValueOrDefault(instance.PrototypeId)
            })
            .ToArray();

        LastDiagnostics = diagnostics
            .OrderByDescending(item => item.TotalMilliseconds)
            .ToArray();

        return notation with
        {
            Prototypes = prototypes,
            Instances = instances
        };
    }

    private bool PassesLocalMeasureSizeGate(
        ShapePrototype prototype,
        NotationScene notation,
        ScoreLayout layout,
        out string reason)
    {
        var instance = notation.Instances
            .FirstOrDefault(item =>
                item.PrototypeId == prototype.Id
                && item.ShapeId == prototype.RepresentativeShapeId)
            ?? notation.Instances.FirstOrDefault(item =>
                item.PrototypeId == prototype.Id);

        if (instance is null)
        {
            reason = "no prototype instance";
            return false;
        }

        var centerX = instance.X + instance.Width / 2.0;
        var centerY = instance.Y + instance.Height / 2.0;

        foreach (var pair in layout.Systems.SelectMany(system => system.StaffPairs))
        {
            if (centerY < pair.Bounds.MinY
                || centerY > pair.Bounds.MaxY)
            {
                continue;
            }

            var measure = pair.Measures.FirstOrDefault(item =>
                centerX >= item.XStart
                && centerX <= item.XEnd);

            if (measure is null)
            {
                continue;
            }

            var measureWidth = measure.XEnd - measure.XStart;
            var measureHeight = pair.Bounds.Height;
            var maxWidth = measureWidth * _settings.MaxMeasureWidthFraction;
            var maxHeight = measureHeight * _settings.MaxMeasureHeightFraction;

            if (instance.Width <= maxWidth
                && instance.Height <= maxHeight)
            {
                reason =
                    $"local measure={measure.Id}; "
                    + $"shape={instance.Width:F2}x{instance.Height:F2}; "
                    + $"limit={maxWidth:F2}x{maxHeight:F2}";
                return true;
            }

            reason =
                $"outside local measure size gate; measure={measure.Id}; "
                + $"shape={instance.Width:F2}x{instance.Height:F2}; "
                + $"limit={maxWidth:F2}x{maxHeight:F2}";
            return false;
        }

        reason =
            $"no containing local measure for center=({centerX:F2},{centerY:F2})";
        return false;
    }
}
