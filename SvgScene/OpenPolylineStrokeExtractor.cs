namespace SvgMusic.Scene;

internal sealed record OpenPolylineStrokeResult(
    bool Accepted,
    Stroke? Stroke,
    string Reason,
    string Metrics);

/// <summary>
/// Recognizes an open sampled contour as a straight stroke even when the source
/// path contains a few redundant or slightly offset points.
///
/// This is intentionally more tolerant than the primary PCA check, but it still
/// requires the polyline to stay close to the direct chord and to avoid a large
/// detour. The result is one stroke spanning the original endpoints.
/// </summary>
internal sealed class OpenPolylineStrokeExtractor
{
    private readonly double _maxRelativeDeviation;
    private readonly double _maxPathToChordRatio;

    public OpenPolylineStrokeExtractor(
        double maxRelativeDeviation = 0.15,
        double maxPathToChordRatio = 1.08)
    {
        _maxRelativeDeviation = maxRelativeDeviation;
        _maxPathToChordRatio = maxPathToChordRatio;
    }

    public OpenPolylineStrokeResult TryExtract(GeometricShape shape)
    {
        var contours = shape.EffectiveContours
            .Where(contour => contour.Points.Count > 0)
            .ToList();

        if (contours.Count != 1)
        {
            return Reject(
                "not a single contour",
                $"contours={contours.Count}");
        }

        var contour = contours[0];
        var points = contour.Points;

        if (contour.IsClosed)
        {
            return Reject(
                "contour is closed",
                $"points={points.Count}");
        }

        if (points.Count < 3)
        {
            return Reject(
                "not a sampled polyline",
                $"points={points.Count}");
        }

        var start = points[0];
        var end = points[^1];
        var chord = GeometryAlgorithms.Distance(start, end);

        if (chord <= 1e-9)
        {
            return Reject(
                "degenerate chord",
                $"points={points.Count} chord={chord:0.###}");
        }

        var pathLength = GeometryAlgorithms.PolylineLength(points);
        var pathToChordRatio = pathLength / chord;

        var maxDeviation = 0.0;

        foreach (var point in points)
        {
            var deviation = Math.Abs(
                GeometryAlgorithms.SignedDistanceToLine(
                    point,
                    start,
                    end));

            maxDeviation = Math.Max(maxDeviation, deviation);
        }

        var relativeDeviation = maxDeviation / chord;

        var metrics =
            $"points={points.Count} " +
            $"chord={chord:0.###} " +
            $"path={pathLength:0.###} " +
            $"path/chord={pathToChordRatio:0.###} " +
            $"deviation={maxDeviation:0.###} " +
            $"relativeDeviation={relativeDeviation:0.###}";

        if (pathToChordRatio > _maxPathToChordRatio)
        {
            return Reject(
                "polyline detour too large",
                metrics);
        }

        if (relativeDeviation > _maxRelativeDeviation)
        {
            return Reject(
                "polyline bends too far from chord",
                metrics);
        }

        GeometryAlgorithms.OrderEndpoints(
            ref start,
            ref end);

        var stroke = new Stroke(
            shape.Id,
            start,
            end,
            Math.Max(shape.StrokeWidth, 1.0),
            shape.SourceKind,
            shape.SourceIndex);

        return new OpenPolylineStrokeResult(
            true,
            stroke,
            "approximately straight open polyline",
            metrics);
    }

    private static OpenPolylineStrokeResult Reject(
        string reason,
        string metrics)
    {
        return new OpenPolylineStrokeResult(
            false,
            null,
            reason,
            metrics);
    }
}
