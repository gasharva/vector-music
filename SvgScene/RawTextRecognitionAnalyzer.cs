namespace SvgMusic.Scene;

/// <summary>
/// OCR pass that deliberately starts from raw geometric glyphs rather than from
/// notation survivors. Every eligible raw glyph is OCR-probed first. Horizontal
/// runs are accepted only when at least one member was actually probed and the
/// singleton result was empty or below the confidence threshold.
/// </summary>
public sealed class RawTextRecognitionAnalyzer
{
    public const double ClearSingletonConfidence = 0.90;

    private const double MaxAtomWidthInSpacings = 8.0;
    private const double MaxAtomHeightInSpacings = 5.0;

    private readonly ITextRecognizer _recognizer;
    private readonly RawHorizontalTextRunBuilder _runBuilder;

    public RawTextRecognitionAnalyzer(
        ITextRecognizer recognizer,
        RawHorizontalTextRunBuilder? runBuilder = null)
    {
        _recognizer = recognizer;
        _runBuilder = runBuilder ?? new RawHorizontalTextRunBuilder();
    }

    public TextRecognitionAnalysisResult Analyze(
        GeometricScene geometry,
        ScoreLayout layout)
    {
        var singletonRecognitions = new Dictionary<string, TextRecognition?>(
            StringComparer.Ordinal);
        var observations = new List<TextRecognitionObservation>();

        foreach (var shape in SelectEligibleGlyphShapes(geometry, layout))
        {
            var candidate = TextRecognitionCandidateSvg.Create(
                $"raw-glyph:{shape.Id}",
                TextCandidateKind.Prototype,
                [shape]);
            var recognition = _recognizer.Recognize(candidate);

            // The presence of the key is significant: null means "OCR was attempted
            // and returned no text", while an absent key means "never attempted".
            singletonRecognitions[shape.Id] = recognition;

            observations.Add(new TextRecognitionObservation(
                candidate.Id,
                candidate.Kind,
                candidate.Bounds,
                candidate.SourceShapeIds,
                null,
                recognition));
        }

        foreach (var candidate in _runBuilder.Build(
                     geometry,
                     layout,
                     singletonRecognitions))
        {
            if (!HasActuallyProbedUncertainMember(
                    candidate,
                    singletonRecognitions))
            {
                continue;
            }

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

    public static IReadOnlyList<GeometricShape> SelectEligibleGlyphShapes(
        GeometricScene geometry,
        ScoreLayout layout)
    {
        var spacing = Math.Max(
            0.001,
            GlyphRasterizer.ResolveSourceInterline(layout));

        return SelectPreferredGlyphShapes(geometry.Shapes)
            .Where(shape =>
                shape.Bounds.Width > 0
                && shape.Bounds.Height > 0
                && shape.Bounds.Width <= spacing * MaxAtomWidthInSpacings
                && shape.Bounds.Height <= spacing * MaxAtomHeightInSpacings)
            .OrderBy(shape => shape.Bounds.MinY)
            .ThenBy(shape => shape.Bounds.MinX)
            .ThenBy(shape => shape.Id, StringComparer.Ordinal)
            .ToArray();
    }

    public static bool IsUncertainSingleton(TextRecognition? recognition) =>
        recognition is null
        || string.IsNullOrWhiteSpace(recognition.Text)
        || recognition.Confidence < ClearSingletonConfidence;

    private static bool HasActuallyProbedUncertainMember(
        TextRecognitionCandidate candidate,
        IReadOnlyDictionary<string, TextRecognition?> singletonRecognitions)
    {
        return candidate.SourceShapeIds.Any(shapeId =>
            singletonRecognitions.TryGetValue(shapeId, out var recognition)
            && IsUncertainSingleton(recognition));
    }

    private static IEnumerable<GeometricShape> SelectPreferredGlyphShapes(
        IReadOnlyList<GeometricShape> shapes)
    {
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
}
