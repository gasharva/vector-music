namespace SvgMusic.Scene;

public interface IArcExtractor
{
    bool TryCreateArc(GeometricShape shape, out Arc arc);
}

/// <summary>
/// Finds simple open curved primitives without knowing any musical semantics.
/// The sampled contour is reduced to a quadratic Bezier centreline.  Straight
/// geometry is deliberately rejected: Stroke extraction gets first refusal.
/// </summary>
public sealed class ArcExtractor : IArcExtractor
{
    private readonly double _maxRelativeFitError;
    private readonly double _minRelativeBend;

    public ArcExtractor(
        double maxRelativeFitError = 0.045,
        double minRelativeBend = 0.025)
    {
        _maxRelativeFitError = maxRelativeFitError;
        _minRelativeBend = minRelativeBend;
    }

    public bool TryCreateArc(GeometricShape shape, out Arc arc)
    {
        arc = default!;
        if (shape.IsClosed || shape.Points.Count < 4)
            return false;

        var points = ResampleByArcLength(shape.Points, 25);
        if (points.Count < 4)
            return false;

        var start = points[0];
        var end = points[^1];
        var chord = Distance(start, end);
        if (chord <= 1e-6)
            return false;

        // Fit Q(t)=(1-t)^2 P0 + 2(1-t)t C + t^2 P2 in least squares.
        double denominator = 0, controlX = 0, controlY = 0;
        for (var i = 1; i < points.Count - 1; i++)
        {
            var t = i / (double)(points.Count - 1);
            var a = 2.0 * (1.0 - t) * t;
            var baseX = (1.0 - t) * (1.0 - t) * start.X + t * t * end.X;
            var baseY = (1.0 - t) * (1.0 - t) * start.Y + t * t * end.Y;
            denominator += a * a;
            controlX += a * (points[i].X - baseX);
            controlY += a * (points[i].Y - baseY);
        }

        if (denominator <= 1e-12)
            return false;

        var control = new PointD(controlX / denominator, controlY / denominator);
        var bend = DistanceToLine(control, start, end);
        if (bend / chord < _minRelativeBend)
            return false;

        double squaredError = 0;
        for (var i = 0; i < points.Count; i++)
        {
            var t = i / (double)(points.Count - 1);
            var q = Quadratic(start, control, end, t);
            var d = Distance(points[i], q);
            squaredError += d * d;
        }

        var rms = Math.Sqrt(squaredError / points.Count);
        var scale = Math.Max(chord, 1e-6);
        var relativeError = rms / scale;
        if (relativeError > _maxRelativeFitError)
            return false;

        arc = new Arc(
            shape.Id,
            start,
            control,
            end,
            Math.Max(shape.StrokeWidth, 1.0),
            relativeError,
            shape.SourceKind,
            shape.SourceIndex);
        return true;
    }

    private static List<PointD> ResampleByArcLength(IReadOnlyList<PointD> input, int count)
    {
        if (input.Count < 2) return input.ToList();

        var cumulative = new double[input.Count];
        for (var i = 1; i < input.Count; i++)
            cumulative[i] = cumulative[i - 1] + Distance(input[i - 1], input[i]);

        var total = cumulative[^1];
        if (total <= 1e-9) return [];

        var result = new List<PointD>(count);
        var segment = 1;
        for (var sample = 0; sample < count; sample++)
        {
            var target = total * sample / (count - 1.0);
            while (segment < cumulative.Length - 1 && cumulative[segment] < target)
                segment++;

            var from = segment - 1;
            var span = cumulative[segment] - cumulative[from];
            var t = span <= 1e-12 ? 0 : (target - cumulative[from]) / span;
            result.Add(new PointD(
                input[from].X + (input[segment].X - input[from].X) * t,
                input[from].Y + (input[segment].Y - input[from].Y) * t));
        }
        return result;
    }

    private static PointD Quadratic(PointD p0, PointD c, PointD p2, double t)
    {
        var mt = 1.0 - t;
        return new PointD(
            mt * mt * p0.X + 2 * mt * t * c.X + t * t * p2.X,
            mt * mt * p0.Y + 2 * mt * t * c.Y + t * t * p2.Y);
    }

    private static double DistanceToLine(PointD p, PointD a, PointD b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        return length <= 1e-12
            ? Distance(p, a)
            : Math.Abs(dy * p.X - dx * p.Y + b.X * a.Y - b.Y * a.X) / length;
    }

    private static double Distance(PointD a, PointD b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
