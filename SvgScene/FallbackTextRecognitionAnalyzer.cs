namespace SvgMusic.Scene;

/// <summary>
/// OCR fallback that runs only after the existing music-symbol classifier has had
/// its chance. Poorly classified residual glyphs are used only as seeds that activate
/// a horizontal row. OCR then sees complete geometric glyph segments from that row,
/// regardless of how the normal parser interpreted the neighbouring glyphs.
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
            .Build(geometry, poorGlyphs, layout)
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
            // ScenePipeline and duplicate ink already present in split geometry.
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
/// A poorly recognized glyph activates its horizontal row, but is not itself the
/// boundary of the OCR candidate. The row is populated from all small geometric
/// glyphs at a compatible Y position, without looking at symbol classification,
/// primitive interpretation or SVG provenance. After sorting by X, the row is cut
/// only where a horizontal gap exceeds two average glyph widths. Every resulting
/// piece is sent to OCR; a one-glyph piece naturally becomes a singleton fallback.
/// </summary>
public sealed class FallbackHorizontalTextTrainBuilder
{
    public const double MaxGapAverageGlyphWidths = 2.0;

    private const double MaxAtomWidthInSpacings = 8.0;
    private const double MaxAtomHeightInSpacings = 5.0;
    private const double MinimumVerticalOverlap = 0.20;
    private const double VerticalJitterInSpacings = 0.75;

    public IReadOnlyList<TextRecognitionCandidate> Build(
        GeometricScene geometry,
        IReadOnlyList<GeometricShape> poorGlyphs,
        ScoreLayout layout)
    {
        var spacing = Math.Max(
            0.001,
            GlyphRasterizer.ResolveSourceInterline(layout));

        var seeds = poorGlyphs
            .Where(shape => IsSmallGlyph(shape, spacing))
            .GroupBy(shape => shape.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(shape => shape.Bounds.MinY)
            .ThenBy(shape => shape.Bounds.MinX)
            .ThenBy(shape => shape.Id, StringComparer.Ordinal)
            .ToArray();

        if (seeds.Length == 0)
        {
            return Array.Empty<TextRecognitionCandidate>();
        }

        var atoms = geometry.Shapes
            // Synthetic whole-path classifier alternatives duplicate raw ink and
            // therefore must not become extra OCR wagons.
            .Where(shape => !shape.Id.EndsWith(".whole", StringComparison.Ordinal))
            .Where(shape => IsSmallGlyph(shape, spacing))
            .GroupBy(shape => shape.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();

        var seedRows = BuildSeedRows(seeds, spacing);
        var uniquePieces = new Dictionary<string, GeometricShape[]>(
            StringComparer.Ordinal);

        foreach (var seedRow in seedRows)
        {
            // X distance deliberately does not participate here. Once a bad glyph
            // activates a row, collect every small glyph that lies on the same
            // horizontal band. Classification and primitive type are ignored.
            var rowGlyphs = atoms
                .Where(atom => seedRow.Any(seed =>
                    IsVerticallyCompatible(seed.Bounds, atom.Bounds, spacing)))
                .OrderBy(shape => shape.Bounds.MinX)
                .ThenBy(shape => shape.Bounds.MinY)
                .ThenBy(shape => shape.Id, StringComparer.Ordinal)
                .ToArray();

            if (rowGlyphs.Length == 0)
            {
                continue;
            }

            var averageWidth = Math.Max(
                0.001,
                rowGlyphs.Average(shape => shape.Bounds.Width));
            var maxGap = averageWidth * MaxGapAverageGlyphWidths;

            foreach (var piece in SplitAtLargeHorizontalGaps(rowGlyphs, maxGap))
            {
                var ordered = piece
                    .OrderBy(shape => shape.Bounds.MinX)
                    .ThenBy(shape => shape.Bounds.MinY)
                    .ThenBy(shape => shape.Id, StringComparer.Ordinal)
                    .ToArray();
                var key = string.Join(
                    "\u001f",
                    ordered.Select(shape => shape.Id));
                uniquePieces.TryAdd(key, ordered);
            }
        }

        var runIndex = 0;
        return uniquePieces.Values
            .OrderBy(piece => piece.Min(shape => shape.Bounds.MinY))
            .ThenBy(piece => piece.Min(shape => shape.Bounds.MinX))
            .ThenBy(piece => piece[0].Id, StringComparer.Ordinal)
            .Select(piece =>
            {
                if (piece.Length == 1)
                {
                    return TextRecognitionCandidateSvg.Create(
                        $"fallback-glyph:{piece[0].Id}",
                        TextCandidateKind.Prototype,
                        piece);
                }

                runIndex++;
                return TextRecognitionCandidateSvg.Create(
                    $"fallback-train:{runIndex}",
                    TextCandidateKind.HorizontalRun,
                    piece);
            })
            .ToArray();
    }

    private static IReadOnlyList<IReadOnlyList<GeometricShape>> BuildSeedRows(
        IReadOnlyList<GeometricShape> seeds,
        double spacing)
    {
        var rows = new List<List<GeometricShape>>();

        foreach (var seed in seeds)
        {
            var row = rows.FirstOrDefault(existing =>
                existing.Any(member =>
                    IsVerticallyCompatible(
                        member.Bounds,
                        seed.Bounds,
                        spacing)));

            if (row is null)
            {
                rows.Add([seed]);
            }
            else
            {
                row.Add(seed);
            }
        }

        return rows;
    }

    private static IEnumerable<IReadOnlyList<GeometricShape>> SplitAtLargeHorizontalGaps(
        IReadOnlyList<GeometricShape> ordered,
        double maxGap)
    {
        if (ordered.Count == 0)
        {
            yield break;
        }

        var current = new List<GeometricShape> { ordered[0] };

        for (var index = 1; index < ordered.Count; index++)
        {
            var previous = ordered[index - 1];
            var next = ordered[index];
            var gap = next.Bounds.MinX - previous.Bounds.MaxX;

            if (gap > maxGap)
            {
                yield return current.ToArray();
                current = [next];
            }
            else
            {
                current.Add(next);
            }
        }

        yield return current.ToArray();
    }

    private static bool IsVerticallyCompatible(
        BoundsD first,
        BoundsD second,
        double spacing)
    {
        var overlap = Math.Max(
            0,
            Math.Min(first.MaxY, second.MaxY)
            - Math.Max(first.MinY, second.MinY));
        var minimumHeight = Math.Max(
            0.001,
            Math.Min(first.Height, second.Height));
        var overlapRatio = overlap / minimumHeight;
        var centerDelta = Math.Abs(first.CenterY - second.CenterY);

        return overlapRatio >= MinimumVerticalOverlap
            || centerDelta <= spacing * VerticalJitterInSpacings;
    }

    public static bool IsSmallGlyph(
        GeometricShape shape,
        double spacing) =>
        shape.Bounds.Width > 0
        && shape.Bounds.Height > 0
        && shape.Bounds.Width <= spacing * MaxAtomWidthInSpacings
        && shape.Bounds.Height <= spacing * MaxAtomHeightInSpacings;
}
