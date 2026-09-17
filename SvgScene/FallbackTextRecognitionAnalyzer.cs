namespace SvgMusic.Scene;

/// <summary>
/// OCR fallback that runs only after the existing music-symbol classifier has had
/// its chance. Only residual glyphs that the normal classifier did not recognize
/// confidently are considered. They are partitioned into disjoint greedy maximal
/// horizontal trains and only those trains (or remaining singletons) are sent to OCR.
/// </summary>
public sealed class FallbackTextRecognitionAnalyzer
{
    public const double ClearMusicClassificationConfidence = 0.75;

    private readonly ITextRecognizer _recognizer;
    private readonly FallbackHorizontalTextTrainBuilder _trainBuilder;

    public FallbackTextRecognitionAnalyzer(
        ITextRecognizer recognizer,
        FallbackHorizontalTextTrainBuilder? trainBuilder = null)
    {
        _recognizer = recognizer;
        _trainBuilder = trainBuilder ?? new FallbackHorizontalTextTrainBuilder();
    }

    public TextRecognitionAnalysisResult Analyze(
        GeometricScene geometry,
        NotationScene notation,
        ScoreLayout layout)
    {
        var poorGlyphs = SelectPoorlyRecognizedGlyphShapes(
            geometry,
            notation,
            layout);

        var observations = _trainBuilder
            .Build(poorGlyphs, layout)
            .Select(candidate => new TextRecognitionObservation(
                candidate.Id,
                candidate.Kind,
                candidate.Bounds,
                candidate.SourceShapeIds,
                null,
                _recognizer.Recognize(candidate)))
            .ToArray();

        return new TextRecognitionAnalysisResult(observations);
    }

    public static IReadOnlyList<GeometricShape> SelectPoorlyRecognizedGlyphShapes(
        GeometricScene geometry,
        NotationScene notation,
        ScoreLayout layout)
    {
        var spacing = Math.Max(
            0.001,
            GlyphRasterizer.ResolveSourceInterline(layout));
        var shapesById = geometry.Shapes.ToDictionary(
            shape => shape.Id,
            StringComparer.Ordinal);

        return notation.Instances
            .Where(instance => IsPoorlyRecognized(instance.Classification))
            .Select(instance => shapesById.GetValueOrDefault(instance.ShapeId))
            .Where(shape => shape is not null)
            .Select(shape => shape!)
            // `.whole` entries are classification-only alternatives added by
            // ScenePipeline and duplicate the same ink already present in their
            // split glyphs. They are not independent OCR wagons.
            .Where(shape => !shape.Id.EndsWith(".whole", StringComparison.Ordinal))
            .Where(shape => FallbackHorizontalTextTrainBuilder.IsSmallGlyph(
                shape,
                spacing))
            .GroupBy(shape => shape.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(shape => shape.Bounds.MinY)
            .ThenBy(shape => shape.Bounds.MinX)
            .ThenBy(shape => shape.Id, StringComparer.Ordinal)
            .ToArray();
    }

    public static bool IsPoorlyRecognized(SymbolClassification? classification) =>
        classification is null
        || classification.Confidence < ClearMusicClassificationConfidence
        || classification.Label.Equals("CLUTTER", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Greedily partitions poorly recognized residual glyphs into disjoint horizontal
/// trains. No confidently classified glyph, primitive interpretation or SVG source
/// provenance participates in grouping. A gap may be as large as two average glyph
/// widths of the train assembled so far. A singleton is emitted only when that glyph
/// could not be consumed by any larger train.
/// </summary>
public sealed class FallbackHorizontalTextTrainBuilder
{
    public const double MaxGapAverageGlyphWidths = 2.0;

    private const double MaxAtomWidthInSpacings = 8.0;
    private const double MaxAtomHeightInSpacings = 5.0;
    private const double MaxNegativeGapAverageGlyphWidths = 0.35;
    private const double MinimumVerticalOverlap = 0.20;
    private const double CenterToleranceAverageHeight = 0.75;

    public IReadOnlyList<TextRecognitionCandidate> Build(
        IReadOnlyList<GeometricShape> poorGlyphs,
        ScoreLayout layout)
    {
        var spacing = Math.Max(
            0.001,
            GlyphRasterizer.ResolveSourceInterline(layout));

        var glyphs = poorGlyphs
            .Where(shape => IsSmallGlyph(shape, spacing))
            .GroupBy(shape => shape.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(shape => shape.Bounds.MinX)
            .ThenBy(shape => shape.Bounds.MinY)
            .ThenBy(shape => shape.Id, StringComparer.Ordinal)
            .ToArray();

        if (glyphs.Length == 0)
        {
            return Array.Empty<TextRecognitionCandidate>();
        }

        var used = new HashSet<string>(StringComparer.Ordinal);
        var trains = new List<IReadOnlyList<GeometricShape>>();

        foreach (var seed in glyphs)
        {
            if (used.Contains(seed.Id))
            {
                continue;
            }

            var train = new List<GeometricShape> { seed };
            used.Add(seed.Id);

            while (true)
            {
                var next = FindBestRightNeighbour(train, glyphs, used);
                if (next is null)
                {
                    break;
                }

                train.Add(next);
                used.Add(next.Id);
            }

            trains.Add(train);
        }

        var runIndex = 0;
        return trains
            .Select(train =>
            {
                var ordered = train
                    .OrderBy(shape => shape.Bounds.MinX)
                    .ThenBy(shape => shape.Bounds.MinY)
                    .ThenBy(shape => shape.Id, StringComparer.Ordinal)
                    .ToArray();

                if (ordered.Length == 1)
                {
                    return TextRecognitionCandidateSvg.Create(
                        $"fallback-glyph:{ordered[0].Id}",
                        TextCandidateKind.Prototype,
                        ordered);
                }

                runIndex++;
                return TextRecognitionCandidateSvg.Create(
                    $"fallback-train:{runIndex}",
                    TextCandidateKind.HorizontalRun,
                    ordered);
            })
            .OrderBy(candidate => candidate.Bounds.MinY)
            .ThenBy(candidate => candidate.Bounds.MinX)
            .ThenBy(candidate => candidate.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static GeometricShape? FindBestRightNeighbour(
        IReadOnlyList<GeometricShape> train,
        IReadOnlyList<GeometricShape> glyphs,
        IReadOnlySet<string> used)
    {
        var rightEdge = train
            .OrderByDescending(shape => shape.Bounds.MaxX)
            .ThenBy(shape => shape.Bounds.MinY)
            .First();
        var averageWidth = Math.Max(
            0.001,
            train.Average(shape => shape.Bounds.Width));
        var averageHeight = Math.Max(
            0.001,
            train.Average(shape => shape.Bounds.Height));
        var trainMinY = train.Min(shape => shape.Bounds.MinY);
        var trainMaxY = train.Max(shape => shape.Bounds.MaxY);
        var trainCenterY = (trainMinY + trainMaxY) / 2.0;
        var maxGap = averageWidth * MaxGapAverageGlyphWidths;
        var maxNegativeGap = averageWidth * MaxNegativeGapAverageGlyphWidths;

        return glyphs
            .Where(candidate => !used.Contains(candidate.Id))
            .Where(candidate => candidate.Bounds.CenterX > rightEdge.Bounds.CenterX)
            .Select(candidate => new
            {
                Shape = candidate,
                Gap = candidate.Bounds.MinX - rightEdge.Bounds.MaxX,
                CenterDelta = Math.Abs(candidate.Bounds.CenterY - trainCenterY)
            })
            .Where(item => item.Gap >= -maxNegativeGap && item.Gap <= maxGap)
            .Where(item => IsVerticallyCompatible(
                item.Shape.Bounds,
                trainMinY,
                trainMaxY,
                trainCenterY,
                averageHeight))
            .OrderBy(item => Math.Max(0, item.Gap))
            .ThenBy(item => item.CenterDelta)
            .ThenBy(item => item.Shape.Bounds.MinX)
            .ThenBy(item => item.Shape.Id, StringComparer.Ordinal)
            .Select(item => item.Shape)
            .FirstOrDefault();
    }

    private static bool IsVerticallyCompatible(
        BoundsD candidate,
        double trainMinY,
        double trainMaxY,
        double trainCenterY,
        double averageHeight)
    {
        var overlap = Math.Max(
            0,
            Math.Min(candidate.MaxY, trainMaxY)
            - Math.Max(candidate.MinY, trainMinY));
        var minimumHeight = Math.Max(
            0.001,
            Math.Min(candidate.Height, trainMaxY - trainMinY));
        var overlapRatio = overlap / minimumHeight;
        var centerDelta = Math.Abs(candidate.CenterY - trainCenterY);

        return overlapRatio >= MinimumVerticalOverlap
            || centerDelta <= averageHeight * CenterToleranceAverageHeight;
    }

    public static bool IsSmallGlyph(
        GeometricShape shape,
        double spacing) =>
        shape.Bounds.Width > 0
        && shape.Bounds.Height > 0
        && shape.Bounds.Width <= spacing * MaxAtomWidthInSpacings
        && shape.Bounds.Height <= spacing * MaxAtomHeightInSpacings;
}
