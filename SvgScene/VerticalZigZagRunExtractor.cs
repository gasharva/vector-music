namespace SvgMusic.Scene;

public sealed record VerticalZigZagRunExtraction(
    IReadOnlyList<VerticalZigZagPrimitive> ZigZags,
    IReadOnlySet<string> ConsumedShapeIds);

/// <summary>
/// Recognizes a vertical zigzag assembled from a regular column of repeated,
/// geometrically equivalent glyph tiles. MuseScore commonly engraves an
/// arpeggio this way instead of emitting one continuous wavy path.
///
/// The extractor intentionally ignores SourceClass and other musical metadata;
/// it uses only normalized geometry, alignment and repetition spacing.
/// </summary>
public sealed class VerticalZigZagRunExtractor
{
    private readonly ShapeDescriptorMatcher _matcher = new();
    private readonly int _minTileCount;
    private readonly double _maxDescriptorDistance;
    private readonly double _maxCenterXFraction;
    private readonly double _minStepHeightFraction;
    private readonly double _maxStepHeightFraction;
    private readonly double _maxStepVariation;
    private readonly double _minOverallAspect;

    public VerticalZigZagRunExtractor(
        int minTileCount = 5,
        double maxDescriptorDistance = 0.025,
        double maxCenterXFraction = 0.18,
        double minStepHeightFraction = 0.45,
        double maxStepHeightFraction = 1.25,
        double maxStepVariation = 0.12,
        double minOverallAspect = 3.0)
    {
        _minTileCount = minTileCount;
        _maxDescriptorDistance = maxDescriptorDistance;
        _maxCenterXFraction = maxCenterXFraction;
        _minStepHeightFraction = minStepHeightFraction;
        _maxStepHeightFraction = maxStepHeightFraction;
        _maxStepVariation = maxStepVariation;
        _minOverallAspect = minOverallAspect;
    }

    public VerticalZigZagRunExtraction Extract(GeometricScene scene)
    {
        var candidates = scene.Shapes
            .Where(IsTileCandidate)
            .Select(shape => new Candidate(
                shape,
                _matcher.Describe(shape)))
            .ToArray();

        var consumed = new HashSet<string>();
        var zigZags = new List<VerticalZigZagPrimitive>();

        foreach (var seed in candidates.OrderBy(item => item.Shape.Bounds.CenterX))
        {
            if (consumed.Contains(seed.Shape.Id))
                continue;

            var nearColumn = candidates
                .Where(candidate =>
                    !consumed.Contains(candidate.Shape.Id)
                    && SimilarSize(seed.Shape.Bounds, candidate.Shape.Bounds)
                    && Math.Abs(candidate.Shape.Bounds.CenterX - seed.Shape.Bounds.CenterX)
                        <= Math.Max(1.0, seed.Shape.Bounds.Width * _maxCenterXFraction)
                    && _matcher.Distance(seed.Descriptor, candidate.Descriptor)
                        <= _maxDescriptorDistance)
                .OrderBy(candidate => candidate.Shape.Bounds.CenterY)
                .ToArray();

            if (nearColumn.Length < _minTileCount)
                continue;

            foreach (var run in SplitIntoRegularRuns(nearColumn))
            {
                if (run.Count < _minTileCount)
                    continue;

                var bounds = UnionBounds(run.Select(item => item.Shape.Bounds));
                if (bounds.Width <= 1e-9 || bounds.Height / bounds.Width < _minOverallAspect)
                    continue;

                var steps = Enumerable.Range(1, run.Count - 1)
                    .Select(index => run[index].Shape.Bounds.CenterY - run[index - 1].Shape.Bounds.CenterY)
                    .ToArray();
                var meanStep = steps.Average();
                var stepVariation = CoefficientOfVariation(steps);
                if (stepVariation > _maxStepVariation)
                    continue;

                var shapeIds = run.Select(item => item.Shape.Id).ToArray();
                foreach (var shapeId in shapeIds)
                    consumed.Add(shapeId);

                var meanTileWidth = run.Average(item => item.Shape.Bounds.Width);
                var confidence = Math.Clamp(
                    0.78
                    + 0.015 * Math.Min(run.Count - _minTileCount, 8)
                    + 0.08 * (1.0 - Math.Min(stepVariation / _maxStepVariation, 1.0)),
                    0.0,
                    0.99);

                zigZags.Add(new VerticalZigZagPrimitive(
                    shapeIds[0],
                    bounds,
                    run.Count - 1,
                    meanTileWidth / 2.0,
                    meanStep,
                    confidence,
                    run[0].Shape.SourceKind,
                    run[0].Shape.SourceIndex)
                {
                    SourceShapeIds = shapeIds
                });
            }
        }

        return new VerticalZigZagRunExtraction(zigZags, consumed);
    }

    private bool IsTileCandidate(GeometricShape shape)
    {
        var width = shape.Bounds.Width;
        var height = shape.Bounds.Height;
        if (width <= 1e-6 || height <= 1e-6 || shape.Points.Count < 8)
            return false;

        var aspect = height / width;
        return aspect >= 0.75 && aspect <= 2.5;
    }

    private static bool SimilarSize(BoundsD left, BoundsD right)
    {
        var widthRatio = Math.Max(left.Width, right.Width) / Math.Max(1e-9, Math.Min(left.Width, right.Width));
        var heightRatio = Math.Max(left.Height, right.Height) / Math.Max(1e-9, Math.Min(left.Height, right.Height));
        return widthRatio <= 1.12 && heightRatio <= 1.12;
    }

    private IEnumerable<List<Candidate>> SplitIntoRegularRuns(IReadOnlyList<Candidate> column)
    {
        if (column.Count == 0)
            yield break;

        var current = new List<Candidate> { column[0] };

        for (var index = 1; index < column.Count; index++)
        {
            var previous = column[index - 1];
            var next = column[index];
            var step = next.Shape.Bounds.CenterY - previous.Shape.Bounds.CenterY;
            var tileHeight = (previous.Shape.Bounds.Height + next.Shape.Bounds.Height) / 2.0;
            var minStep = tileHeight * _minStepHeightFraction;
            var maxStep = tileHeight * _maxStepHeightFraction;

            if (step >= minStep && step <= maxStep)
            {
                current.Add(next);
                continue;
            }

            yield return current;
            current = new List<Candidate> { next };
        }

        yield return current;
    }

    private static BoundsD UnionBounds(IEnumerable<BoundsD> bounds)
    {
        var items = bounds.ToArray();
        return new BoundsD(
            items.Min(item => item.MinX),
            items.Min(item => item.MinY),
            items.Max(item => item.MaxX),
            items.Max(item => item.MaxY));
    }

    private static double CoefficientOfVariation(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
            return double.PositiveInfinity;

        var mean = values.Average();
        if (Math.Abs(mean) <= 1e-9)
            return double.PositiveInfinity;

        var variance = values.Average(value =>
        {
            var delta = value - mean;
            return delta * delta;
        });

        return Math.Sqrt(variance) / mean;
    }

    private sealed record Candidate(
        GeometricShape Shape,
        ShapeDescriptor Descriptor);
}
