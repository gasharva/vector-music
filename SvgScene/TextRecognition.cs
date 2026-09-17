using System.Globalization;
using System.Text;

namespace SvgMusic.Scene;

public enum TextCandidateKind
{
    Prototype,
    HorizontalRun
}

public sealed record TextRecognition(
    string Text,
    double Confidence,
    string Engine);

public sealed record TextRecognitionCandidate(
    string Id,
    TextCandidateKind Kind,
    BoundsD Bounds,
    IReadOnlyList<string> SourceShapeIds,
    string Svg);

public sealed record TextRecognitionObservation(
    string Id,
    TextCandidateKind Kind,
    BoundsD Bounds,
    IReadOnlyList<string> SourceShapeIds,
    string? PrototypeId,
    TextRecognition? Recognition);

public sealed record TextRecognitionAnalysisResult(
    IReadOnlyList<TextRecognitionObservation> Observations)
{
    public IReadOnlyList<TextRecognitionObservation> Recognized =>
        Observations
            .Where(item => !string.IsNullOrWhiteSpace(item.Recognition?.Text))
            .ToArray();
}

public interface ITextRecognizer
{
    TextRecognition? Recognize(TextRecognitionCandidate candidate);
}

public sealed class NullTextRecognizer : ITextRecognizer
{
    public static NullTextRecognizer Instance { get; } = new();

    private NullTextRecognizer()
    {
    }

    public TextRecognition? Recognize(TextRecognitionCandidate candidate) => null;
}

public sealed class TextRecognitionAnalyzer
{
    private readonly ITextRecognizer _recognizer;
    private readonly RawHorizontalTextRunBuilder _runBuilder;

    public TextRecognitionAnalyzer(
        ITextRecognizer recognizer,
        RawHorizontalTextRunBuilder? runBuilder = null)
    {
        _recognizer = recognizer;
        _runBuilder = runBuilder ?? new RawHorizontalTextRunBuilder();
    }

    public TextRecognitionAnalysisResult Analyze(
        GeometricScene geometry,
        NotationScene notation,
        ScoreLayout layout)
    {
        var shapesById = geometry.Shapes.ToDictionary(
            shape => shape.Id,
            StringComparer.Ordinal);
        var instancesByPrototype = notation.Instances
            .GroupBy(instance => instance.PrototypeId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray(),
                StringComparer.Ordinal);
        var singletonRecognitions = new Dictionary<string, TextRecognition?>(
            StringComparer.Ordinal);
        var observations = new List<TextRecognitionObservation>();

        // Recognize each reusable residual/compound prototype once, then project the
        // result to its page instances. Primitive extraction may have removed other
        // shapes from NotationScene; that is deliberate. Raw run grouping below works
        // from GeometricScene and treats such shapes as uncertain singleton glyphs.
        foreach (var prototype in notation.Prototypes)
        {
            if (!shapesById.TryGetValue(prototype.RepresentativeShapeId, out var shape))
            {
                continue;
            }

            var candidate = TextRecognitionCandidateSvg.Create(
                $"prototype:{prototype.Id}",
                TextCandidateKind.Prototype,
                [shape]);
            var recognition = _recognizer.Recognize(candidate);

            if (!instancesByPrototype.TryGetValue(prototype.Id, out var instances))
            {
                continue;
            }

            foreach (var instance in instances)
            {
                if (!shapesById.TryGetValue(instance.ShapeId, out var instanceShape))
                {
                    continue;
                }

                singletonRecognitions[instance.ShapeId] = recognition;

                observations.Add(new TextRecognitionObservation(
                    $"prototype:{prototype.Id}@{instance.ShapeId}",
                    TextCandidateKind.Prototype,
                    instanceShape.Bounds,
                    instance.AbsorbedPrimitiveShapeIds is { Count: > 0 }
                        ? [instance.ShapeId, .. instance.AbsorbedPrimitiveShapeIds]
                        : [instance.ShapeId],
                    prototype.Id,
                    recognition));
            }
        }

        // Horizontal text runs deliberately ignore primitive/music-symbol decisions.
        // They are built from raw geometric glyphs, subject only to geometric size,
        // neighbourhood and the requirement that at least one member was not clearly
        // recognized as an isolated OCR glyph.
        foreach (var candidate in _runBuilder.Build(
                     geometry,
                     layout,
                     singletonRecognitions))
        {
            observations.Add(new TextRecognitionObservation(
                candidate.Id,
                candidate.Kind,
                candidate.Bounds,
                candidate.SourceShapeIds,
                null,
                _recognizer.Recognize(candidate)));
        }

        return new TextRecognitionAnalysisResult(observations);
    }

    // Compatibility entry point for diagnostics/tests that used the previous helper.
    // Notation is intentionally ignored: raw geometry is the source of text atoms.
    public static IReadOnlyList<TextRecognitionCandidate> BuildHorizontalRuns(
        GeometricScene geometry,
        NotationScene notation,
        ScoreLayout layout)
    {
        _ = notation;
        return new RawHorizontalTextRunBuilder().Build(
            geometry,
            layout,
            new Dictionary<string, TextRecognition?>(StringComparer.Ordinal));
    }
}

/// <summary>
/// Builds horizontal OCR candidates from raw geometry rather than residual notation.
/// Primitive extraction, semantic ownership and music-symbol labels are intentionally
/// ignored. The only semantic gate is singleton OCR uncertainty.
/// </summary>
public sealed class RawHorizontalTextRunBuilder
{
    private const double MaxAtomWidthInSpacings = 8.0;
    private const double MaxAtomHeightInSpacings = 5.0;
    private const double MaxHorizontalGapInSpacings = 0.90;
    private const double MaxNegativeGapInSpacings = 0.15;
    private const double MinimumVerticalOverlap = 0.35;
    private const double MaxRunWidthInSpacings = 18.0;
    private const double ClearSingletonConfidence = 0.90;

    public IReadOnlyList<TextRecognitionCandidate> Build(
        GeometricScene geometry,
        ScoreLayout layout,
        IReadOnlyDictionary<string, TextRecognition?> singletonRecognitions)
    {
        var spacing = Math.Max(
            0.001,
            GlyphRasterizer.ResolveSourceInterline(layout));

        var atoms = SelectPreferredGlyphShapes(geometry.Shapes)
            .Where(shape =>
                shape.Bounds.Width > 0
                && shape.Bounds.Height > 0
                && shape.Bounds.Width <= spacing * MaxAtomWidthInSpacings
                && shape.Bounds.Height <= spacing * MaxAtomHeightInSpacings)
            .Select(shape => new TextAtom(
                shape,
                TryGetRecognition(singletonRecognitions, shape.Id)))
            .OrderBy(atom => atom.Shape.Bounds.MinX)
            .ThenBy(atom => atom.Shape.Bounds.MinY)
            .ToArray();

        if (atoms.Length < 2)
        {
            return Array.Empty<TextRecognitionCandidate>();
        }

        var parent = Enumerable.Range(0, atoms.Length).ToArray();

        int Find(int value)
        {
            while (parent[value] != value)
            {
                parent[value] = parent[parent[value]];
                value = parent[value];
            }

            return value;
        }

        void Union(int left, int right)
        {
            var leftRoot = Find(left);
            var rightRoot = Find(right);

            if (leftRoot != rightRoot)
            {
                parent[rightRoot] = leftRoot;
            }
        }

        for (var left = 0; left < atoms.Length; left++)
        {
            for (var right = left + 1; right < atoms.Length; right++)
            {
                var leftBounds = atoms[left].Shape.Bounds;
                var rightBounds = atoms[right].Shape.Bounds;

                if (rightBounds.MinX - leftBounds.MaxX
                    > spacing * MaxHorizontalGapInSpacings)
                {
                    break;
                }

                if (AreTextNeighbours(leftBounds, rightBounds, spacing))
                {
                    Union(left, right);
                }
            }
        }

        var result = new List<TextRecognitionCandidate>();
        var runIndex = 0;

        foreach (var component in Enumerable.Range(0, atoms.Length)
                     .GroupBy(Find)
                     .Select(group => group.Select(index => atoms[index])
                         .OrderBy(atom => atom.Shape.Bounds.MinX)
                         .ThenBy(atom => atom.Shape.Bounds.MinY)
                         .ToArray())
                     .Where(group => group.Length >= 2)
                     .Where(group => group.Any(atom => IsUncertain(atom.Recognition)))
                     .OrderBy(group => group[0].Shape.Bounds.MinY)
                     .ThenBy(group => group[0].Shape.Bounds.MinX))
        {
            var shapes = component.Select(atom => atom.Shape).ToArray();
            var bounds = UnionBounds(shapes.Select(shape => shape.Bounds));

            if (bounds.Width > spacing * MaxRunWidthInSpacings)
            {
                continue;
            }

            runIndex++;
            result.Add(TextRecognitionCandidateSvg.Create(
                $"raw-run:{runIndex}",
                TextCandidateKind.HorizontalRun,
                shapes));
        }

        return result;
    }

    private static IEnumerable<GeometricShape> SelectPreferredGlyphShapes(
        IReadOnlyList<GeometricShape> shapes)
    {
        // ScenePipeline may append a reconstructed `<base>.whole` candidate for an
        // original SVG path that CompoundShapeSplitter had split into disconnected
        // contours. For text this is exactly the glyph-level representation we want,
        // so prefer the whole and suppress its numbered parts to avoid duplicates.
        var wholeBases = shapes
            .Select(shape => TryGetWholeBaseId(shape.Id))
            .Where(baseId => baseId is not null)
            .Select(baseId => baseId!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var shape in shapes)
        {
            var splitBaseId = TryGetSplitBaseId(shape.Id);
            if (splitBaseId is not null && wholeBases.Contains(splitBaseId))
            {
                continue;
            }

            yield return shape;
        }
    }

    private static TextRecognition? TryGetRecognition(
        IReadOnlyDictionary<string, TextRecognition?> recognitions,
        string shapeId)
    {
        return recognitions.TryGetValue(shapeId, out var recognition)
            ? recognition
            : null;
    }

    private static bool IsUncertain(TextRecognition? recognition) =>
        recognition is null
        || string.IsNullOrWhiteSpace(recognition.Text)
        || recognition.Confidence < ClearSingletonConfidence;

    private static bool AreTextNeighbours(
        BoundsD first,
        BoundsD second,
        double spacing)
    {
        var left = first.CenterX <= second.CenterX ? first : second;
        var right = first.CenterX <= second.CenterX ? second : first;
        var gap = right.MinX - left.MaxX;

        if (gap < -spacing * MaxNegativeGapInSpacings
            || gap > spacing * MaxHorizontalGapInSpacings)
        {
            return false;
        }

        var overlap = Math.Max(
            0,
            Math.Min(left.MaxY, right.MaxY) - Math.Max(left.MinY, right.MinY));
        var minimumHeight = Math.Max(
            0.001,
            Math.Min(left.Height, right.Height));
        var overlapRatio = overlap / minimumHeight;
        var centerDelta = Math.Abs(left.CenterY - right.CenterY);
        var centerTolerance = Math.Max(
            spacing * 0.55,
            minimumHeight * 0.60);

        return overlapRatio >= MinimumVerticalOverlap
            && centerDelta <= centerTolerance;
    }

    private static BoundsD UnionBounds(IEnumerable<BoundsD> bounds)
    {
        var materialized = bounds.ToArray();
        return new BoundsD(
            materialized.Min(item => item.MinX),
            materialized.Min(item => item.MinY),
            materialized.Max(item => item.MaxX),
            materialized.Max(item => item.MaxY));
    }

    private static string? TryGetWholeBaseId(string shapeId)
    {
        const string suffix = ".whole";
        return shapeId.EndsWith(suffix, StringComparison.Ordinal)
            ? shapeId[..^suffix.Length]
            : null;
    }

    private static string? TryGetSplitBaseId(string shapeId)
    {
        var separator = shapeId.LastIndexOf('.');
        if (separator <= 0
            || separator == shapeId.Length - 1
            || !int.TryParse(shapeId[(separator + 1)..], out _))
        {
            return null;
        }

        return shapeId[..separator];
    }

    private sealed record TextAtom(
        GeometricShape Shape,
        TextRecognition? Recognition);
}

public static class TextRecognitionCandidateSvg
{
    private const double MinimumPadding = 1.0;

    public static TextRecognitionCandidate Create(
        string id,
        TextCandidateKind kind,
        IReadOnlyList<GeometricShape> shapes)
    {
        if (shapes.Count == 0)
        {
            throw new ArgumentException("At least one shape is required.", nameof(shapes));
        }

        var bounds = new BoundsD(
            shapes.Min(shape => shape.Bounds.MinX),
            shapes.Min(shape => shape.Bounds.MinY),
            shapes.Max(shape => shape.Bounds.MaxX),
            shapes.Max(shape => shape.Bounds.MaxY));
        var svg = BuildSvg(shapes, bounds);

        return new TextRecognitionCandidate(
            id,
            kind,
            bounds,
            shapes.Select(shape => shape.Id).ToArray(),
            svg);
    }

    private static string BuildSvg(
        IReadOnlyList<GeometricShape> shapes,
        BoundsD bounds)
    {
        var padding = Math.Max(
            MinimumPadding,
            Math.Max(bounds.Width, bounds.Height) * 0.04);
        var width = Math.Max(1.0, bounds.Width + 2 * padding);
        var height = Math.Max(1.0, bounds.Height + 2 * padding);
        var sb = new StringBuilder();

        sb.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(width)}\" height=\"{F(height)}\" viewBox=\"0 0 {F(width)} {F(height)}\">");
        sb.AppendLine($"  <rect width=\"{F(width)}\" height=\"{F(height)}\" fill=\"white\"/>");

        foreach (var shape in shapes)
        {
            var closed = shape.EffectiveContours
                .Where(contour => contour.IsClosed && contour.Points.Count > 0)
                .ToArray();

            if (closed.Length > 0)
            {
                var data = string.Join(
                    " ",
                    closed.Select(contour => PathData(
                        contour,
                        bounds.MinX - padding,
                        bounds.MinY - padding)));
                sb.AppendLine($"  <path d=\"{data}\" fill=\"black\" fill-rule=\"evenodd\"/>");
            }

            foreach (var contour in shape.EffectiveContours
                         .Where(contour => !contour.IsClosed && contour.Points.Count > 0))
            {
                var data = PathData(
                    contour,
                    bounds.MinX - padding,
                    bounds.MinY - padding);
                var strokeWidth = shape.StrokeWidth > 0
                    ? shape.StrokeWidth
                    : Math.Max(height / 96.0, 0.5);
                sb.AppendLine($"  <path d=\"{data}\" fill=\"none\" stroke=\"black\" stroke-width=\"{F(strokeWidth)}\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/>");
            }
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    private static string PathData(
        GeometricContour contour,
        double originX,
        double originY)
    {
        var first = contour.Points[0];
        var sb = new StringBuilder();
        sb.Append("M ")
            .Append(F(first.X - originX))
            .Append(' ')
            .Append(F(first.Y - originY));

        foreach (var point in contour.Points.Skip(1))
        {
            sb.Append(" L ")
                .Append(F(point.X - originX))
                .Append(' ')
                .Append(F(point.Y - originY));
        }

        if (contour.IsClosed)
        {
            sb.Append(" Z");
        }

        return sb.ToString();
    }

    private static string F(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);
}
