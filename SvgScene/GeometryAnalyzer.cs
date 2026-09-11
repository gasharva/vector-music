namespace SvgMusic.Scene;

public sealed record StrokeDiagnostic(
    string ShapeId,
    string Result,
    string Reason,
    string Metrics);

public interface IGeometryAnalyzer
{
    bool TryCreateStroke(GeometricShape shape, out Stroke stroke);
    IReadOnlyList<StrokeDiagnostic> Diagnostics { get; }
    void ClearDiagnostics();
}

/// <summary>
/// Detects straight line-like geometry independently of SVG representation.
/// Analysis is contour-aware: only a single geometric contour can currently
/// collapse into one Stroke. Multi-contour shapes are left intact for later stages.
/// </summary>
public sealed class GeometryAnalyzer : IGeometryAnalyzer
{
    private readonly double _minElongation;
    private readonly double _minClosedFillRatio;
    private readonly List<StrokeDiagnostic> _diagnostics = [];

    public IReadOnlyList<StrokeDiagnostic> Diagnostics => _diagnostics;

    public GeometryAnalyzer(
        double minElongation = 5.0,
        double minClosedFillRatio = 0.55)
    {
        _minElongation = minElongation;
        _minClosedFillRatio = minClosedFillRatio;
    }

    public void ClearDiagnostics()
    {
        _diagnostics.Clear();
    }

    public bool TryCreateStroke(GeometricShape shape, out Stroke stroke)
    {
        stroke = default!;

        var contours = shape.EffectiveContours
            .Where(c => c.Points.Count > 0)
            .ToList();

        if (contours.Count != 1)
        {
            return Reject(
                shape,
                "multiple contours",
                $"kind={shape.SourceKind} contours={contours.Count} points={shape.Points.Count}");
        }

        var contour = contours[0];
        var points = contour.Points;

        if (points.Count < 2)
        {
            return Reject(
                shape,
                "too few points",
                $"kind={shape.SourceKind} closed={contour.IsClosed} points={points.Count}");
        }

        // A genuine two-point open contour is already an exact centreline.
        if (!contour.IsClosed && points.Count == 2)
        {
            var start = points[0];
            var end = points[1];
            var _length = Distance(start, end);

            if (_length <= 1e-9)
            {
                return Reject(
                    shape,
                    "zero length",
                    Metrics(shape, contour, _length, 0, 0, null));
            }

            OrderEndpoints(ref start, ref end);

            stroke = new Stroke(
                shape.Id,
                start,
                end,
                Math.Max(shape.StrokeWidth, 1.0),
                shape.SourceKind,
                shape.SourceIndex);

            Accept(
                shape,
                "two-point open contour",
                Metrics(
                    shape,
                    contour,
                    _length,
                    shape.StrokeWidth,
                    _length / Math.Max(shape.StrokeWidth, 1e-9),
                    null));

            return true;
        }

        var centroid = new PointD(
            points.Average(p => p.X),
            points.Average(p => p.Y));

        double xx = 0;
        double xy = 0;
        double yy = 0;

        foreach (var point in points)
        {
            var dx = point.X - centroid.X;
            var dy = point.Y - centroid.Y;

            xx += dx * dx;
            xy += dx * dy;
            yy += dy * dy;
        }

        xx /= points.Count;
        xy /= points.Count;
        yy /= points.Count;

        var (axisX, axisY) = PrincipalAxis(xx, xy, yy);
        var normalX = -axisY;
        var normalY = axisX;

        var minAlong = double.PositiveInfinity;
        var maxAlong = double.NegativeInfinity;
        var minAcross = double.PositiveInfinity;
        var maxAcross = double.NegativeInfinity;

        foreach (var point in points)
        {
            var dx = point.X - centroid.X;
            var dy = point.Y - centroid.Y;

            var along = dx * axisX + dy * axisY;
            var across = dx * normalX + dy * normalY;

            minAlong = Math.Min(minAlong, along);
            maxAlong = Math.Max(maxAlong, along);
            minAcross = Math.Min(minAcross, across);
            maxAcross = Math.Max(maxAcross, across);
        }

        var length = maxAlong - minAlong;
        var contourThickness = maxAcross - minAcross;
        var effectiveThickness = Math.Max(contourThickness, shape.StrokeWidth);

        if (length <= 1e-9 || effectiveThickness <= 1e-9)
        {
            return Reject(
                shape,
                "degenerate geometry",
                Metrics(shape, contour, length, effectiveThickness, 0, null));
        }

        var elongation = length / effectiveThickness;

        if (elongation < _minElongation)
        {
            return Reject(
                shape,
                "elongation too low",
                Metrics(shape, contour, length, effectiveThickness, elongation, null));
        }

        double? fillRatio = null;

        if (contour.IsClosed)
        {
            var area = Math.Abs(SignedArea(points));
            var orientedBoxArea = length * Math.Max(contourThickness, 1e-9);
            fillRatio = area / orientedBoxArea;

            if (fillRatio < _minClosedFillRatio)
            {
                return Reject(
                    shape,
                    "closed contour fill too low",
                    Metrics(shape, contour, length, effectiveThickness, elongation, fillRatio));
            }
        }
        else if (points.Count > 2)
        {
            var straightnessTolerance = Math.Max(
                shape.StrokeWidth * 2.0,
                length * 0.04);

            if (contourThickness > straightnessTolerance)
            {
                var metrics = Metrics(
                    shape,
                    contour,
                    length,
                    effectiveThickness,
                    elongation,
                    null);

                metrics += $" across={contourThickness:0.###}";
                metrics += $" tolerance={straightnessTolerance:0.###}";

                return Reject(
                    shape,
                    "open contour not straight",
                    metrics);
            }
        }

        var acrossCenter = (minAcross + maxAcross) / 2.0;

        var startPoint = new PointD(
            centroid.X + axisX * minAlong + normalX * acrossCenter,
            centroid.Y + axisY * minAlong + normalY * acrossCenter);

        var endPoint = new PointD(
            centroid.X + axisX * maxAlong + normalX * acrossCenter,
            centroid.Y + axisY * maxAlong + normalY * acrossCenter);

        OrderEndpoints(ref startPoint, ref endPoint);

        stroke = new Stroke(
            shape.Id,
            startPoint,
            endPoint,
            effectiveThickness,
            shape.SourceKind,
            shape.SourceIndex);

        Accept(
            shape,
            contour.IsClosed
                ? "elongated filled contour"
                : "straight open contour",
            Metrics(
                shape,
                contour,
                length,
                effectiveThickness,
                elongation,
                fillRatio));

        return true;
    }

    private bool Reject(
        GeometricShape shape,
        string reason,
        string metrics)
    {
        _diagnostics.Add(new StrokeDiagnostic(
            shape.Id,
            "REJECT",
            reason,
            metrics));

        return false;
    }

    private void Accept(
        GeometricShape shape,
        string reason,
        string metrics)
    {
        _diagnostics.Add(new StrokeDiagnostic(
            shape.Id,
            "ACCEPT",
            reason,
            metrics));
    }

    private static string Metrics(
        GeometricShape shape,
        GeometricContour contour,
        double length,
        double thickness,
        double elongation,
        double? fill)
    {
        var result =
            $"kind={shape.SourceKind} " +
            $"closed={contour.IsClosed} " +
            $"points={contour.Points.Count} " +
            $"length={length:0.###} " +
            $"thickness={thickness:0.###} " +
            $"elong={elongation:0.###}";

        if (fill is not null)
        {
            result += $" fill={fill:0.###}";
        }

        return result;
    }

    private static (double X, double Y) PrincipalAxis(
        double a,
        double b,
        double d)
    {
        var trace = a + d;
        var delta = Math.Sqrt((a - d) * (a - d) + 4 * b * b);
        var lambda = (trace + delta) / 2;

        var x = b;
        var y = lambda - a;
        var norm = Math.Sqrt(x * x + y * y);

        if (norm <= 1e-12)
        {
            return a >= d
                ? (1, 0)
                : (0, 1);
        }

        return (x / norm, y / norm);
    }

    private static double SignedArea(IReadOnlyList<PointD> points)
    {
        if (points.Count < 3)
        {
            return 0;
        }

        var area = 0.0;

        for (var i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];

            area += a.X * b.Y - b.X * a.Y;
        }

        return area / 2;
    }

    private static double Distance(PointD a, PointD b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;

        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static void OrderEndpoints(ref PointD a, ref PointD b)
    {
        if (a.X > b.X ||
            (Math.Abs(a.X - b.X) < 1e-9 && a.Y > b.Y))
        {
            (a, b) = (b, a);
        }
    }
}
