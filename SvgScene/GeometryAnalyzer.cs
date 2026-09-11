namespace SvgMusic.Scene;

public interface IGeometryAnalyzer
{
    bool TryCreateStroke(GeometricShape shape, out Stroke stroke);
}

/// <summary>
/// Detects line-like geometry independently of how it was represented in SVG.
/// Direct two-point line/polyline geometry is accepted immediately; closed
/// contours are reduced with a tiny 2D PCA and accepted when they are strongly
/// elongated and fill most of their oriented bounding box.
/// </summary>
public sealed class GeometryAnalyzer : IGeometryAnalyzer
{
    private readonly double _minElongation;
    private readonly double _minClosedFillRatio;

    public GeometryAnalyzer(
        double minElongation = 8.0,
        double minClosedFillRatio = 0.55)
    {
        _minElongation = minElongation;
        _minClosedFillRatio = minClosedFillRatio;
    }

    public bool TryCreateStroke(GeometricShape shape, out Stroke stroke)
    {
        stroke = default!;

        if (shape.Points.Count < 2)
            return false;

        // SVG <line> and the common two-point <polyline> case already tell us
        // exactly what the centre line is.  Keep the declared stroke width.
        if (!shape.IsClosed && shape.Points.Count == 2)
        {
            var start = shape.Points[0];
            var end = shape.Points[1];
            var length = Distance(start, end);
            if (length <= 1e-9)
                return false;

            OrderEndpoints(ref start, ref end);
            stroke = new Stroke(
                shape.Id,
                start,
                end,
                Math.Max(shape.StrokeWidth, 1.0),
                shape.SourceKind,
                shape.SourceIndex);
            return true;
        }

        var centroid = new PointD(
            shape.Points.Average(p => p.X),
            shape.Points.Average(p => p.Y));

        double xx = 0, xy = 0, yy = 0;
        foreach (var p in shape.Points)
        {
            var dx = p.X - centroid.X;
            var dy = p.Y - centroid.Y;
            xx += dx * dx;
            xy += dx * dy;
            yy += dy * dy;
        }

        xx /= shape.Points.Count;
        xy /= shape.Points.Count;
        yy /= shape.Points.Count;

        var (axisX, axisY) = PrincipalAxis(xx, xy, yy);
        var normalX = -axisY;
        var normalY = axisX;

        var minAlong = double.PositiveInfinity;
        var maxAlong = double.NegativeInfinity;
        var minAcross = double.PositiveInfinity;
        var maxAcross = double.NegativeInfinity;

        foreach (var p in shape.Points)
        {
            var dx = p.X - centroid.X;
            var dy = p.Y - centroid.Y;
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
            return false;

        var elongation = length / effectiveThickness;
        if (elongation < _minElongation)
            return false;

        if (shape.IsClosed)
        {
            var area = Math.Abs(SignedArea(shape.Points));
            var orientedBoxArea = length * Math.Max(contourThickness, 1e-9);
            var fillRatio = area / orientedBoxArea;

            // This rejects long but curved/thin symbols such as slurs while
            // accepting beams, stems or barlines encoded as filled polygons.
            if (fillRatio < _minClosedFillRatio)
                return false;
        }
        else if (shape.Points.Count > 2)
        {
            // Open paths/polylines must remain close to one straight axis.
            // A declared SVG stroke width provides a natural tolerance.
            var straightnessTolerance = Math.Max(shape.StrokeWidth * 2.0, length * 0.04);
            if (contourThickness > straightnessTolerance)
                return false;
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
        return true;
    }

    private static (double X, double Y) PrincipalAxis(double a, double b, double d)
    {
        var trace = a + d;
        var delta = Math.Sqrt((a - d) * (a - d) + 4.0 * b * b);
        var lambda = (trace + delta) / 2.0;

        var x = b;
        var y = lambda - a;
        var norm = Math.Sqrt(x * x + y * y);

        if (norm <= 1e-12)
            return a >= d ? (1.0, 0.0) : (0.0, 1.0);

        return (x / norm, y / norm);
    }

    private static double SignedArea(IReadOnlyList<PointD> points)
    {
        if (points.Count < 3) return 0;

        var area = 0.0;
        for (var i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            area += a.X * b.Y - b.X * a.Y;
        }

        return area / 2.0;
    }

    private static double Distance(PointD a, PointD b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static void OrderEndpoints(ref PointD a, ref PointD b)
    {
        if (a.X > b.X || (Math.Abs(a.X - b.X) < 1e-9 && a.Y > b.Y))
            (a, b) = (b, a);
    }
}
