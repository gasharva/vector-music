namespace SvgMusic.Scene;

internal static class GeometryAlgorithms
{
    public static double Distance(PointD a, PointD b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;

        return Math.Sqrt(dx * dx + dy * dy);
    }

    public static PointD Midpoint(PointD a, PointD b)
    {
        return new PointD(
            (a.X + b.X) / 2.0,
            (a.Y + b.Y) / 2.0);
    }

    public static double SignedArea(IReadOnlyList<PointD> points)
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

        return area / 2.0;
    }

    public static (double X, double Y) PrincipalAxis(
        double a,
        double b,
        double d)
    {
        var trace = a + d;
        var delta = Math.Sqrt(
            (a - d) * (a - d) +
            4.0 * b * b);

        var lambda = (trace + delta) / 2.0;

        var x = b;
        var y = lambda - a;
        var norm = Math.Sqrt(x * x + y * y);

        if (norm <= 1e-12)
        {
            return a >= d
                ? (1.0, 0.0)
                : (0.0, 1.0);
        }

        return (
            x / norm,
            y / norm);
    }

    public static void OrderEndpoints(ref PointD a, ref PointD b)
    {
        var xIsAfter = a.X > b.X;
        var sameX = Math.Abs(a.X - b.X) < 1e-9;
        var yIsAfter = a.Y > b.Y;

        if (xIsAfter || sameX && yIsAfter)
        {
            (a, b) = (b, a);
        }
    }

    public static double PolylineLength(IReadOnlyList<PointD> points)
    {
        var length = 0.0;

        for (var i = 1; i < points.Count; i++)
        {
            length += Distance(points[i - 1], points[i]);
        }

        return length;
    }

    public static double SignedDistanceToLine(
        PointD point,
        PointD start,
        PointD end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);

        if (length <= 1e-12)
        {
            return 0;
        }

        return (
            dx * (point.Y - start.Y) -
            dy * (point.X - start.X)) / length;
    }

    public static List<PointD> ResampleByArcLength(
        IReadOnlyList<PointD> input,
        int count)
    {
        if (input.Count < 2)
        {
            return [];
        }

        var cumulative = new double[input.Count];

        for (var i = 1; i < input.Count; i++)
        {
            cumulative[i] = cumulative[i - 1] +
                            Distance(input[i - 1], input[i]);
        }

        var total = cumulative[^1];

        if (total <= 1e-9)
        {
            return [];
        }

        var result = new List<PointD>(count);
        var segment = 1;

        for (var sample = 0; sample < count; sample++)
        {
            var target = total * sample / (count - 1.0);

            while (segment < cumulative.Length - 1 &&
                   cumulative[segment] < target)
            {
                segment++;
            }

            var from = segment - 1;
            var span = cumulative[segment] - cumulative[from];
            var t = span <= 1e-12
                ? 0
                : (target - cumulative[from]) / span;

            result.Add(new PointD(
                input[from].X + (input[segment].X - input[from].X) * t,
                input[from].Y + (input[segment].Y - input[from].Y) * t));
        }

        return result;
    }

    public static PointD FitQuadratic(
        IReadOnlyList<PointD> points,
        PointD start,
        PointD end)
    {
        double denominator = 0;
        double controlX = 0;
        double controlY = 0;

        for (var i = 1; i < points.Count - 1; i++)
        {
            var t = i / (double)(points.Count - 1);
            var oneMinusT = 1.0 - t;
            var k = 2.0 * oneMinusT * t;

            var baseX = oneMinusT * oneMinusT * start.X +
                        t * t * end.X;

            var baseY = oneMinusT * oneMinusT * start.Y +
                        t * t * end.Y;

            denominator += k * k;
            controlX += k * (points[i].X - baseX);
            controlY += k * (points[i].Y - baseY);
        }

        if (denominator <= 1e-12)
        {
            return Midpoint(start, end);
        }

        return new PointD(
            controlX / denominator,
            controlY / denominator);
    }

    public static double QuadraticFitError(
        IReadOnlyList<PointD> points,
        PointD start,
        PointD control,
        PointD end)
    {
        var squaredError = 0.0;

        for (var i = 0; i < points.Count; i++)
        {
            var t = i / (double)(points.Count - 1);
            var oneMinusT = 1.0 - t;

            var fitted = new PointD(
                oneMinusT * oneMinusT * start.X +
                2.0 * oneMinusT * t * control.X +
                t * t * end.X,
                oneMinusT * oneMinusT * start.Y +
                2.0 * oneMinusT * t * control.Y +
                t * t * end.Y);

            var distance = Distance(points[i], fitted);
            squaredError += distance * distance;
        }

        return Math.Sqrt(squaredError / points.Count);
    }

    public static double MonotonicAgreement(
        double[] values,
        int from,
        int to,
        bool increasing)
    {
        var good = 0;
        var total = 0;
        var tolerance = values.Max() * 0.04;

        for (var i = from + 1; i <= to; i++)
        {
            var delta = values[i] - values[i - 1];

            var matchesDirection = increasing
                ? delta > 0
                : delta < 0;

            if (Math.Abs(delta) <= tolerance || matchesDirection)
            {
                good++;
            }

            total++;
        }

        return total == 0
            ? 1.0
            : good / (double)total;
    }
}
