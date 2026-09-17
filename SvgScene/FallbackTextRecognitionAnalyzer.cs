namespace SvgMusic.Scene;

/// <summary>
/// OCR fallback that runs only after the existing music-symbol classifier has had
/// its chance. Poorly classified residual glyphs seed a purely geometric horizontal
/// search. Neighbours are accepted regardless of whether the normal parser already
/// interpreted them as another symbol/primitive; source provenance is never used.
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
        var shapesById = geometry.Shapes.ToDictionary(
            shape => shape.Id,
            StringComparer.Ordinal);

        var seeds = notation.Instances
            .Where(instance => IsPoorlyRecognized(instance.Classification))
            .Select(instance => shapesById.GetValueOrDefault(instance.ShapeId))
            .Where(shape => shape is not null)
            .Select(shape => shape!)
            .GroupBy(shape => shape.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(shape => shape.Bounds.MinY)
            .ThenBy(shape => shape.Bounds.MinX)
            .ThenBy(shape => shape.Id, StringComparer.Ordinal)
            .ToArray();

        var observations = _trainBuilder
            .Build(geometry, seeds, layout)
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

    public static bool IsPoorlyRecognized(SymbolClassification? classification) =>
        classification is null
        || classification.Confidence < ClearMusicClassificationConfidence
        || classification.Label.Equals("CLUTTER", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Builds one OCR candidate around each poorly classified residual glyph. The seed
/// can grow left/right through any nearby raw geometric shape, irrespective of the
/// normal parser's interpretation of that neighbour. No SVG provenance is consulted.
/// </summary>
public sealed class FallbackHorizontalTextTrainBuilder
{
    private const double MaxAtomWidthInSpacings = 8.0;
    private const double MaxAtomHeightInSpacings = 5.0;
    private const double MaxHorizontalGapInSpacings = 0.90;
    private const double MaxNegativeGapInSpacings = 0.15;
    private const double MinimumVerticalOverlap = 0.35;
    private const double MaxRunWidthInSpacings = 18.0;
    private const int MaxAtomsPerTrain = 20;

    public IReadOnlyList<TextRecognitionCandidate> Build(
        GeometricScene geometry,
        IReadOnlyList<GeometricShape> seeds,
        ScoreLayout layout)
    {
        var spacing = Math.Max(
            0.001,
            GlyphRasterizer.ResolveSourceInterline(layout));

        var atoms = geometry.Shapes
            // `.whole` shapes are synthetic classification alternatives appended by
            // ScenePipeline. Keeping them in the neighbour pool would duplicate the
            // same ink; this is not provenance-based grouping.
            .Where(shape => !shape.Id.EndsWith(".whole", StringComparison.Ordinal))
            .Where(shape => IsSmallGlyph(shape, spacing))
            .OrderBy(shape => shape.Bounds.MinX)
            .ThenBy(shape => shape.Bounds.MinY)
            .ThenBy(shape => shape.Id, StringComparer.Ordinal)
            .ToArray();

        var unique = new Dictionary<string, TextRecognitionCandidate>(
            StringComparer.Ordinal);
        var trainIndex = 0;

        foreach (var seed in seeds.Where(shape => IsSmallGlyph(shape, spacing)))
        {
            var chain = GrowTrain(seed, atoms, spacing);
            var ordered = chain
                .OrderBy(shape => shape.Bounds.MinX)
                .ThenBy(shape => shape.Bounds.MinY)
                .ThenBy(shape => shape.Id, StringComparer.Ordinal)
                .ToArray();

            var key = string.Join("\u001f", ordered.Select(shape => shape.Id));
            if (unique.ContainsKey(key))
            {
                continue;
            }

            TextRecognitionCandidate candidate;
            if (ordered.Length == 1)
            {
                candidate = TextRecognitionCandidateSvg.Create(
                    $"fallback-glyph:{seed.Id}",
                    TextCandidateKind.Prototype,
                    ordered);
            }
            else
            {
                trainIndex++;
                candidate = TextRecognitionCandidateSvg.Create(
                    $"fallback-train:{trainIndex}",
                    TextCandidateKind.HorizontalRun,
                    ordered);
            }

            unique[key] = candidate;
        }

        return unique.Values
            .OrderBy(candidate => candidate.Bounds.MinY)
            .ThenBy(candidate => candidate.Bounds.MinX)
            .ThenBy(candidate => candidate.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<GeometricShape> GrowTrain(
        GeometricShape seed,
        IReadOnlyList<GeometricShape> atoms,
        double spacing)
    {
        var result = new List<GeometricShape> { seed };
        var used = new HashSet<string>(StringComparer.Ordinal) { seed.Id };
        var leftEdge = seed;
        var rightEdge = seed;

        while (result.Count < MaxAtomsPerTrain)
        {
            var added = false;

            var left = FindNeighbour(
                atoms,
                leftEdge,
                used,
                spacing,
                searchLeft: true);
            if (left is not null && FitsWidth(result, left, spacing))
            {
                result.Add(left);
                used.Add(left.Id);
                leftEdge = left;
                added = true;
            }

            if (result.Count >= MaxAtomsPerTrain)
            {
                break;
            }

            var right = FindNeighbour(
                atoms,
                rightEdge,
                used,
                spacing,
                searchLeft: false);
            if (right is not null && FitsWidth(result, right, spacing))
            {
                result.Add(right);
                used.Add(right.Id);
                rightEdge = right;
                added = true;
            }

            if (!added)
            {
                break;
            }
        }

        return result;
    }

    private static GeometricShape? FindNeighbour(
        IReadOnlyList<GeometricShape> atoms,
        GeometricShape edge,
        IReadOnlySet<string> used,
        double spacing,
        bool searchLeft)
    {
        return atoms
            .Where(candidate => !used.Contains(candidate.Id))
            .Where(candidate => searchLeft
                ? candidate.Bounds.CenterX < edge.Bounds.CenterX
                : candidate.Bounds.CenterX > edge.Bounds.CenterX)
            .Where(candidate => AreTrainNeighbours(
                candidate.Bounds,
                edge.Bounds,
                spacing))
            .Select(candidate => new
            {
                Shape = candidate,
                Gap = searchLeft
                    ? edge.Bounds.MinX - candidate.Bounds.MaxX
                    : candidate.Bounds.MinX - edge.Bounds.MaxX,
                CenterDelta = Math.Abs(
                    candidate.Bounds.CenterY - edge.Bounds.CenterY)
            })
            .OrderBy(item => Math.Abs(item.Gap))
            .ThenBy(item => item.CenterDelta)
            .ThenBy(item => item.Shape.Bounds.MinX)
            .ThenBy(item => item.Shape.Id, StringComparer.Ordinal)
            .Select(item => item.Shape)
            .FirstOrDefault();
    }

    private static bool FitsWidth(
        IReadOnlyList<GeometricShape> current,
        GeometricShape candidate,
        double spacing)
    {
        var minX = Math.Min(
            candidate.Bounds.MinX,
            current.Min(shape => shape.Bounds.MinX));
        var maxX = Math.Max(
            candidate.Bounds.MaxX,
            current.Max(shape => shape.Bounds.MaxX));
        return maxX - minX <= spacing * MaxRunWidthInSpacings;
    }

    private static bool IsSmallGlyph(
        GeometricShape shape,
        double spacing) =>
        shape.Bounds.Width > 0
        && shape.Bounds.Height > 0
        && shape.Bounds.Width <= spacing * MaxAtomWidthInSpacings
        && shape.Bounds.Height <= spacing * MaxAtomHeightInSpacings;

    private static bool AreTrainNeighbours(
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
}
