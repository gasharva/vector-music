namespace SvgMusic.Scene;

/// <summary>
/// Repairs the one deliberate mismatch between primitive extraction and symbol
/// recognition that matters for an F clef: the two dots are useful ellipse
/// primitives on their own, but the classifier expects them together with the
/// main clef contour.
/// </summary>
public sealed record BassClefCompositeRepairStatistics(
    int Prototypes,
    int DotPairCandidates,
    int CompositeClassifications,
    int ReusedConfidentFClefs,
    int AbsorbedEllipses);

public sealed class BassClefCompositeRepair
{
    private const double MinimumConfidence = 0.75;
    private static readonly int[] Interlines = [20, 30, 40];

    private readonly ISymbolClassifier _classifier;
    private readonly GlyphRasterizer _rasterizer;
    private readonly List<string> _diagnostics = [];

    public BassClefCompositeRepair(
        ISymbolClassifier classifier,
        GlyphRasterizer? rasterizer = null)
    {
        _classifier = classifier;
        _rasterizer = rasterizer ?? new GlyphRasterizer();
    }

    public IReadOnlyList<string> Diagnostics => _diagnostics;

    public BassClefCompositeRepairStatistics LastStatistics { get; private set; } =
        new(0, 0, 0, 0, 0);

    public NotationScene Repair(
        GeometricScene geometry,
        NotationScene notation,
        ScoreLayout layout)
    {
        _diagnostics.Clear();

        var shapesById = geometry.Shapes.ToDictionary(
            shape => shape.Id,
            StringComparer.Ordinal);
        var sourceInterline = GlyphRasterizer.ResolveSourceInterline(layout);
        var classifications = notation.Prototypes.ToDictionary(
            prototype => prototype.Id,
            prototype => prototype.Classification,
            StringComparer.Ordinal);
        var absorbedByInstance = new Dictionary<string, IReadOnlyList<string>>(
            StringComparer.Ordinal);
        var consumedEllipseShapeIds = new HashSet<string>(StringComparer.Ordinal);
        var filledEllipses = notation.Ellipses
            .Where(ellipse => !ellipse.IsHollow)
            .ToArray();
        var dotPairCandidates = 0;
        var compositeClassifications = 0;
        var reusedConfidentFClefs = 0;

        foreach (var prototype in notation.Prototypes)
        {
            if (!shapesById.TryGetValue(
                    prototype.RepresentativeShapeId,
                    out var mainShape))
            {
                continue;
            }

            var representativePair = FindDotPair(
                mainShape,
                filledEllipses,
                consumedEllipseShapeIds);

            if (representativePair is null)
            {
                continue;
            }

            dotPairCandidates++;

            var dotShapes = representativePair
                .Select(ellipse => shapesById.GetValueOrDefault(ellipse.ShapeId))
                .Where(shape => shape is not null)
                .Cast<GeometricShape>()
                .ToArray();

            if (dotShapes.Length != 2)
            {
                continue;
            }

            _diagnostics.Add(
                $"CANDIDATE {prototype.Id} main={mainShape.Id} "
                + $"dots={string.Join(',', representativePair.Select(item => item.ShapeId))}");

            var existing = classifications.GetValueOrDefault(
                prototype.Id);
            SymbolClassification? classification;

            if (existing is not null
                && existing.Label.Equals(
                    "F_CLEF",
                    StringComparison.Ordinal)
                && existing.Confidence >= MinimumConfidence)
            {
                // The prototype classifier already proved this is an F clef.
                // The repair step only needs the dots to absorb them; running the
                // expensive three-scale composite classifier again adds no evidence.
                classification = existing;
                reusedConfidentFClefs++;
            }
            else
            {
                compositeClassifications++;
                classification = ClassifyComposite(
                    [mainShape, .. dotShapes],
                    sourceInterline);
            }

            if (classification is null)
            {
                _diagnostics.Add(
                    $"REJECT    {prototype.Id} no classifier result");
                continue;
            }

            _diagnostics.Add(
                $"RESULT    {prototype.Id} label={classification.Label} "
                + $"confidence={classification.Confidence:P1} "
                + $"interline={classification.Interline}");

            if (!classification.Label.Equals(
                    "F_CLEF",
                    StringComparison.Ordinal)
                || classification.Confidence < MinimumConfidence)
            {
                _diagnostics.Add(
                    $"REJECT    {prototype.Id} composite is not a confident F_CLEF");
                continue;
            }

            classifications[prototype.Id] = classification;

            foreach (var instance in notation.Instances.Where(instance =>
                         instance.PrototypeId.Equals(
                             prototype.Id,
                             StringComparison.Ordinal)))
            {
                if (!shapesById.TryGetValue(instance.ShapeId, out var instanceShape))
                {
                    continue;
                }

                var pair = FindDotPair(
                    instanceShape,
                    filledEllipses,
                    consumedEllipseShapeIds);

                if (pair is null)
                {
                    _diagnostics.Add(
                        $"WARN      {prototype.Id}/{instance.ShapeId} "
                        + "classified as F_CLEF but no matching dot pair was found");
                    continue;
                }

                var absorbed = pair
                    .Select(ellipse => ellipse.ShapeId)
                    .ToArray();

                absorbedByInstance[instance.ShapeId] = absorbed;

                foreach (var shapeId in absorbed)
                {
                    consumedEllipseShapeIds.Add(shapeId);
                }

                _diagnostics.Add(
                    $"ABSORB    {prototype.Id}/{instance.ShapeId} "
                    + $"ellipses={string.Join(',', absorbed)}");
            }
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
                Classification = classifications.GetValueOrDefault(instance.PrototypeId),
                AbsorbedPrimitiveShapeIds = absorbedByInstance.GetValueOrDefault(instance.ShapeId)
            })
            .ToArray();

        var ellipses = notation.Ellipses
            .Where(ellipse => !consumedEllipseShapeIds.Contains(ellipse.ShapeId))
            .ToArray();

        LastStatistics = new BassClefCompositeRepairStatistics(
            notation.Prototypes.Count,
            dotPairCandidates,
            compositeClassifications,
            reusedConfidentFClefs,
            consumedEllipseShapeIds.Count);

        _diagnostics.Add(
            $"SUMMARY   prototypes={LastStatistics.Prototypes}; "
            + $"dotPairCandidates={LastStatistics.DotPairCandidates}; "
            + $"compositeClassifications={LastStatistics.CompositeClassifications}; "
            + $"reusedConfidentFClefs={LastStatistics.ReusedConfidentFClefs}; "
            + $"absorbedEllipses={consumedEllipseShapeIds.Count}; "
            + $"remainingEllipses={ellipses.Length}");

        return notation with
        {
            Prototypes = prototypes,
            Instances = instances,
            Ellipses = ellipses
        };
    }

    private SymbolClassification? ClassifyComposite(
        IReadOnlyList<GeometricShape> shapes,
        double sourceInterline)
    {
        var scales = new List<SymbolScaleResult>();

        foreach (var interline in Interlines)
        {
            var glyph = _rasterizer.Rasterize(
                shapes,
                sourceInterline,
                interline);
            var predictions = _classifier.Classify(glyph);
            var winner = predictions.FirstOrDefault();

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

        if (scales.Count == 0)
        {
            return null;
        }

        var best = scales
            .OrderByDescending(scale => scale.Confidence)
            .First();

        return new SymbolClassification(
            best.Label,
            best.Confidence,
            best.Interline,
            scales);
    }

    private static IReadOnlyList<EllipseLike>? FindDotPair(
        GeometricShape mainShape,
        IReadOnlyList<EllipseLike> ellipses,
        IReadOnlySet<string> excludedShapeIds)
    {
        if (mainShape.Bounds.Width <= 0)
        {
            return null;
        }

        var maxDistance = mainShape.Bounds.Width * 0.5;

        // Before this filter every prototype scanned all filled ellipses to its
        // right and then tried every pair, including noteheads on unrelated
        // staves. A real F-clef dot pair must live vertically next to the main
        // contour, so this cheap bounding window collapses the quadratic pair
        // search to a handful of local ellipses without changing viable pairs.
        var minY = mainShape.Bounds.MinY - maxDistance;
        var maxY = mainShape.Bounds.MaxY + maxDistance;

        var candidates = ellipses
            .Where(ellipse => !excludedShapeIds.Contains(ellipse.ShapeId))
            .Where(ellipse => ellipse.Center.X > mainShape.Bounds.MaxX)
            .Where(ellipse =>
                ellipse.Center.X - mainShape.Bounds.MaxX <= maxDistance)
            .Where(ellipse =>
                ellipse.Center.Y >= minY
                && ellipse.Center.Y <= maxY)
            .OrderBy(ellipse => ellipse.Center.Y)
            .ToArray();

        var pairs = new List<(EllipseLike Upper, EllipseLike Lower, double Score)>();

        for (var first = 0; first < candidates.Length; first++)
        {
            for (var second = first + 1; second < candidates.Length; second++)
            {
                var upper = candidates[first];
                var lower = candidates[second];
                var verticalDistance = lower.Center.Y - upper.Center.Y;
                var horizontalDistance = Math.Abs(lower.Center.X - upper.Center.X);

                var pairCenterY = (upper.Center.Y + lower.Center.Y) / 2.0;

                if (verticalDistance <= 0
                    || verticalDistance > maxDistance
                    || horizontalDistance > maxDistance
                    || pairCenterY < mainShape.Bounds.MinY
                    || pairCenterY > mainShape.Bounds.MaxY)
                {
                    continue;
                }

                var score = verticalDistance
                    + horizontalDistance
                    + (upper.Center.X - mainShape.Bounds.MaxX)
                    + (lower.Center.X - mainShape.Bounds.MaxX);

                pairs.Add((upper, lower, score));
            }
        }

        var best = pairs
            .OrderBy(pair => pair.Score)
            .FirstOrDefault();

        return best.Upper is null
            ? null
            : [best.Upper, best.Lower];
    }
}
