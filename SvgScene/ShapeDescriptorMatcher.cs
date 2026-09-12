namespace SvgMusic.Scene;

/// <summary>
/// Builds normalized descriptors for residual contours and compares them.
/// Primitive extraction happens before this stage, so this matcher only sees
/// shapes that were not recognized as strokes, curved strokes, or ellipses.
/// </summary>
internal sealed class ShapeDescriptorMatcher
{
    public ShapeDescriptor Describe(GeometricShape shape)
    {
        var bounds = shape.Bounds;
        var width = Math.Max(bounds.Width, 1e-9);
        var height = Math.Max(bounds.Height, 1e-9);

        var normalizedPoints = shape.Points
            .Select(point => new PointD(
                (point.X - bounds.MinX) / width,
                (point.Y - bounds.MinY) / height))
            .ToList();

        var relativeArea = Math.Abs(
            GeometryAlgorithms.SignedArea(normalizedPoints));

        return new ShapeDescriptor(
            width / height,
            relativeArea,
            normalizedPoints);
    }

    public double Distance(
        ShapeDescriptor left,
        ShapeDescriptor right)
    {
        var aspectPenalty = Math.Abs(
            Math.Log(
                Math.Max(left.AspectRatio, 1e-9) /
                Math.Max(right.AspectRatio, 1e-9)));

        var areaPenalty = Math.Abs(
            left.RelativeArea - right.RelativeArea);

        var sampleCount = Math.Min(
            left.NormalizedPoints.Count,
            right.NormalizedPoints.Count);

        if (sampleCount == 0)
        {
            return double.PositiveInfinity;
        }

        var squaredDistance = 0.0;

        for (var index = 0; index < sampleCount; index++)
        {
            var leftPoint = SampleAt(
                left.NormalizedPoints,
                index,
                sampleCount);

            var rightPoint = SampleAt(
                right.NormalizedPoints,
                index,
                sampleCount);

            var dx = leftPoint.X - rightPoint.X;
            var dy = leftPoint.Y - rightPoint.Y;

            squaredDistance += dx * dx + dy * dy;
        }

        var pointDistance = Math.Sqrt(
            squaredDistance / sampleCount);

        return pointDistance +
               0.25 * aspectPenalty +
               0.15 * areaPenalty;
    }

    private static PointD SampleAt(
        IReadOnlyList<PointD> points,
        int index,
        int targetCount)
    {
        if (targetCount <= 1 || points.Count == 1)
        {
            return points[0];
        }

        var position =
            index * (points.Count - 1.0) /
            (targetCount - 1.0);

        var leftIndex = (int)Math.Floor(position);
        var rightIndex = Math.Min(
            leftIndex + 1,
            points.Count - 1);

        var t = position - leftIndex;

        return new PointD(
            points[leftIndex].X +
            (points[rightIndex].X - points[leftIndex].X) * t,
            points[leftIndex].Y +
            (points[rightIndex].Y - points[leftIndex].Y) * t);
    }
}
