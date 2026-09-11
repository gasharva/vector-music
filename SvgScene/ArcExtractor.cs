namespace SvgMusic.Scene;

public interface IArcExtractor
{
    bool TryCreateArc(GeometricShape shape, out Arc arc);
}

/// <summary>
/// Extracts a simple curved stroke from a thin closed contour.  Slurs/ties in
/// exported SVG are commonly filled closed shapes: two nearby curved sides with
/// pointed ends.  We split the contour at its two most distant points, resample
/// both sides, average them into a centreline, then test that centreline for a
/// smooth one-sided arch and fit a quadratic Bezier only to that centreline.
/// </summary>
public sealed class ArcExtractor : IArcExtractor
{
    private readonly double _minBend;
    private readonly double _maxBend;
    private readonly double _minSameSideRatio;
    private readonly double _maxRelativeFitError;
    private readonly double _maxRelativeThickness;

    public ArcExtractor(
        double minBend = 0.025,
        double maxBend = 1.25,
        double minSameSideRatio = 0.82,
        double maxRelativeFitError = 0.06,
        double maxRelativeThickness = 0.35)
    {
        _minBend = minBend;
        _maxBend = maxBend;
        _minSameSideRatio = minSameSideRatio;
        _maxRelativeFitError = maxRelativeFitError;
        _maxRelativeThickness = maxRelativeThickness;
    }

    public bool TryCreateArc(GeometricShape shape, out Arc arc)
    {
        arc = default!;
        if (!shape.IsClosed || shape.Points.Count < 8)
            return false;

        var contour = shape.Points.ToList();
        if (Distance(contour[0], contour[^1]) < 1e-6)
            contour.RemoveAt(contour.Count - 1);
        if (contour.Count < 7)
            return false;

        var (aIndex, bIndex) = FarthestPair(contour);
        var sideA = SliceCircular(contour, aIndex, bIndex);
        var sideB = SliceCircular(contour, bIndex, aIndex);
        sideB.Reverse(); // both sides now run A -> B

        const int sampleCount = 33;
        var a = ResampleByArcLength(sideA, sampleCount);
        var b = ResampleByArcLength(sideB, sampleCount);
        if (a.Count != sampleCount || b.Count != sampleCount)
            return false;

        var center = new List<PointD>(sampleCount);
        var widths = new double[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            center.Add(Midpoint(a[i], b[i]));
            widths[i] = Distance(a[i], b[i]);
        }

        var start = center[0];
        var end = center[^1];
        var chord = Distance(start, end);
        if (chord <= 1e-6)
            return false;

        // A curved stroke must be thin compared with its length.  Ignore the
        // pointed tips when estimating its useful thickness.
        var bodyWidths = widths.Skip(3).Take(widths.Length - 6).OrderBy(x => x).ToArray();
        var width = bodyWidths.Length == 0 ? widths.Average() : bodyWidths[bodyWidths.Length / 2];
        if (width / chord > _maxRelativeThickness)
            return false;

        var signed = center.Select(p => SignedDistanceToLine(p, start, end)).ToArray();
        var peak = signed.Skip(1).Take(signed.Length - 2).Max(x => Math.Abs(x));
        var bend = peak / chord;
        if (bend < _minBend || bend > _maxBend)
            return false;

        var dominantSign = signed.OrderByDescending(x => Math.Abs(x)).First() >= 0 ? 1.0 : -1.0;
        var meaningful = signed.Skip(2).Take(signed.Length - 4)
            .Where(x => Math.Abs(x) > chord * 0.005).ToArray();
        if (meaningful.Length == 0)
            return false;

        var sameSideRatio = meaningful.Count(x => x * dominantSign > 0) / (double)meaningful.Length;
        if (sameSideRatio < _minSameSideRatio)
            return false;

        // Reject wavy/S-shaped centre lines.  A musical arch should mostly grow
        // to one broad peak and then decay.  Small sampling noise is tolerated.
        var profile = signed.Select(x => Math.Abs(x)).ToArray();
        var peakIndex = Array.IndexOf(profile, profile.Max());
        if (peakIndex < 3 || peakIndex > profile.Length - 4)
            return false;
        if (MonotonicAgreement(profile, 0, peakIndex, increasing: true) < 0.70
            || MonotonicAgreement(profile, peakIndex, profile.Length - 1, increasing: false) < 0.70)
            return false;

        var control = FitQuadratic(center, start, end);
        var relativeError = QuadraticFitError(center, start, control, end) / chord;
        if (relativeError > _maxRelativeFitError)
            return false;

        arc = new Arc(
            shape.Id,
            start,
            control,
            end,
            Math.Max(width, 1.0),
            relativeError,
            shape.SourceKind,
            shape.SourceIndex);
        return true;
    }

    private static (int A, int B) FarthestPair(IReadOnlyList<PointD> points)
    {
        var best = -1.0;
        var ai = 0;
        var bi = 1;
        for (var i = 0; i < points.Count - 1; i++)
        for (var j = i + 1; j < points.Count; j++)
        {
            var dx = points[i].X - points[j].X;
            var dy = points[i].Y - points[j].Y;
            var d2 = dx * dx + dy * dy;
            if (d2 > best) { best = d2; ai = i; bi = j; }
        }
        return (ai, bi);
    }

    private static List<PointD> SliceCircular(IReadOnlyList<PointD> points, int start, int end)
    {
        var result = new List<PointD>();
        var i = start;
        while (true)
        {
            result.Add(points[i]);
            if (i == end) break;
            i = (i + 1) % points.Count;
        }
        return result;
    }

    private static List<PointD> ResampleByArcLength(IReadOnlyList<PointD> input, int count)
    {
        if (input.Count < 2) return [];
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
            while (segment < cumulative.Length - 1 && cumulative[segment] < target) segment++;
            var from = segment - 1;
            var span = cumulative[segment] - cumulative[from];
            var t = span <= 1e-12 ? 0 : (target - cumulative[from]) / span;
            result.Add(new PointD(
                input[from].X + (input[segment].X - input[from].X) * t,
                input[from].Y + (input[segment].Y - input[from].Y) * t));
        }
        return result;
    }

    private static PointD FitQuadratic(IReadOnlyList<PointD> points, PointD start, PointD end)
    {
        double denominator = 0, cx = 0, cy = 0;
        for (var i = 1; i < points.Count - 1; i++)
        {
            var t = i / (double)(points.Count - 1);
            var k = 2 * (1 - t) * t;
            var bx = (1 - t) * (1 - t) * start.X + t * t * end.X;
            var by = (1 - t) * (1 - t) * start.Y + t * t * end.Y;
            denominator += k * k;
            cx += k * (points[i].X - bx);
            cy += k * (points[i].Y - by);
        }
        return denominator <= 1e-12 ? Midpoint(start, end) : new PointD(cx / denominator, cy / denominator);
    }

    private static double QuadraticFitError(IReadOnlyList<PointD> points, PointD p0, PointD c, PointD p2)
    {
        double sum = 0;
        for (var i = 0; i < points.Count; i++)
        {
            var t = i / (double)(points.Count - 1);
            var mt = 1 - t;
            var q = new PointD(
                mt * mt * p0.X + 2 * mt * t * c.X + t * t * p2.X,
                mt * mt * p0.Y + 2 * mt * t * c.Y + t * t * p2.Y);
            var d = Distance(points[i], q);
            sum += d * d;
        }
        return Math.Sqrt(sum / points.Count);
    }

    private static double MonotonicAgreement(double[] values, int from, int to, bool increasing)
    {
        var good = 0;
        var total = 0;
        var tolerance = values.Max() * 0.04;
        for (var i = from + 1; i <= to; i++)
        {
            var delta = values[i] - values[i - 1];
            if (Math.Abs(delta) <= tolerance || (increasing ? delta > 0 : delta < 0)) good++;
            total++;
        }
        return total == 0 ? 1 : good / (double)total;
    }

    private static double SignedDistanceToLine(PointD p, PointD a, PointD b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        return length <= 1e-12 ? 0 : (dx * (p.Y - a.Y) - dy * (p.X - a.X)) / length;
    }

    private static PointD Midpoint(PointD a, PointD b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);
    private static double Distance(PointD a, PointD b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
