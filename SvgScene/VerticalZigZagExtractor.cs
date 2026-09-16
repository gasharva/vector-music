namespace SvgMusic.Scene;

public sealed record VerticalZigZagProfile(
    int TurnCount,
    double Width,
    double Height,
    double AspectRatio,
    double VerticalMonotonicity,
    double MeanAmplitude,
    double AmplitudeVariation,
    double MeanHalfWaveHeight,
    double HalfWaveHeightVariation);

/// <summary>
/// Purely geometric detector for a narrow, mostly vertical, regularly oscillating
/// polyline/contour. The detector deliberately does not assign musical meaning;
/// a vertical zigzag may later be interpreted as an arpeggio by a semantic pass.
/// </summary>
public sealed class VerticalZigZagExtractor
{
    private readonly double _minHeight;
    private readonly double _minAspectRatio;
    private readonly int _minTurnCount;
    private readonly double _minVerticalMonotonicity;
    private readonly double _minAmplitude;
    private readonly double _maxAmplitudeVariation;
    private readonly double _maxHalfWaveHeightVariation;
    private readonly double _epsilon;

    public VerticalZigZagExtractor(
        double minHeight = 10.0,
        double minAspectRatio = 2.5,
        int minTurnCount = 4,
        double minVerticalMonotonicity = 0.85,
        double minAmplitude = 0.35,
        double maxAmplitudeVariation = 0.85,
        double maxHalfWaveHeightVariation = 0.90,
        double epsilon = 0.01)
    {
        _minHeight = minHeight;
        _minAspectRatio = minAspectRatio;
        _minTurnCount = minTurnCount;
        _minVerticalMonotonicity = minVerticalMonotonicity;
        _minAmplitude = minAmplitude;
        _maxAmplitudeVariation = maxAmplitudeVariation;
        _maxHalfWaveHeightVariation = maxHalfWaveHeightVariation;
        _epsilon = epsilon;
    }

    public bool TryCreateVerticalZigZag(
        GeometricShape shape,
        out VerticalZigZagPrimitive zigZag)
    {
        zigZag = default!;

        if (shape.Points.Count < 6)
            return false;

        var profile = Analyze(shape.Points);

        if (profile.Height < _minHeight
            || profile.AspectRatio < _minAspectRatio
            || profile.TurnCount < _minTurnCount
            || profile.VerticalMonotonicity < _minVerticalMonotonicity
            || profile.MeanAmplitude < _minAmplitude
            || profile.AmplitudeVariation > _maxAmplitudeVariation
            || profile.HalfWaveHeightVariation > _maxHalfWaveHeightVariation)
        {
            return false;
        }

        zigZag = new VerticalZigZagPrimitive(
            shape.Id,
            shape.Bounds,
            profile.TurnCount,
            profile.MeanAmplitude,
            profile.MeanHalfWaveHeight,
            Confidence(profile),
            shape.SourceKind,
            shape.SourceIndex);

        return true;
    }

    public VerticalZigZagProfile Analyze(IReadOnlyList<PointD> points)
    {
        if (points.Count < 3)
            return EmptyProfile();

        var bounds = BoundsD.FromPoints(points);
        var width = bounds.Width;
        var height = bounds.Height;
        var aspectRatio = width <= _epsilon
            ? double.PositiveInfinity
            : height / width;

        var verticalMonotonicity = CalculateVerticalMonotonicity(points);
        var extrema = FindHorizontalExtrema(points);
        var centerX = bounds.CenterX;

        var amplitudes = extrema
            .Select(index => Math.Abs(points[index].X - centerX))
            .Where(value => value > _epsilon)
            .ToArray();

        var halfWaveHeights = new List<double>();
        for (var i = 1; i < extrema.Count; i++)
        {
            var dy = Math.Abs(points[extrema[i]].Y - points[extrema[i - 1]].Y);
            if (dy > _epsilon)
                halfWaveHeights.Add(dy);
        }

        return new VerticalZigZagProfile(
            extrema.Count,
            width,
            height,
            aspectRatio,
            verticalMonotonicity,
            Mean(amplitudes),
            CoefficientOfVariation(amplitudes),
            Mean(halfWaveHeights),
            CoefficientOfVariation(halfWaveHeights));
    }

    private double CalculateVerticalMonotonicity(IReadOnlyList<PointD> points)
    {
        var positive = 0.0;
        var negative = 0.0;

        for (var i = 1; i < points.Count; i++)
        {
            var dy = points[i].Y - points[i - 1].Y;
            if (dy > 0)
                positive += dy;
            else
                negative -= dy;
        }

        var total = positive + negative;
        return total <= _epsilon
            ? 0
            : Math.Max(positive, negative) / total;
    }

    private List<int> FindHorizontalExtrema(IReadOnlyList<PointD> points)
    {
        var extrema = new List<int>();
        var previousSign = 0;

        for (var i = 1; i < points.Count; i++)
        {
            var dx = points[i].X - points[i - 1].X;
            var sign = Math.Abs(dx) <= _epsilon ? 0 : Math.Sign(dx);
            if (sign == 0)
                continue;

            if (previousSign != 0 && sign != previousSign)
                extrema.Add(i - 1);

            previousSign = sign;
        }

        return extrema;
    }

    private double Confidence(VerticalZigZagProfile profile)
    {
        var turns = Math.Clamp((profile.TurnCount - _minTurnCount + 1) / 5.0, 0, 1);
        var monotonicity = Math.Clamp(
            (profile.VerticalMonotonicity - _minVerticalMonotonicity)
            / Math.Max(1e-9, 1 - _minVerticalMonotonicity),
            0,
            1);
        var regularity = 1 - Math.Clamp(
            (profile.AmplitudeVariation + profile.HalfWaveHeightVariation) / 2.0,
            0,
            1);

        return Math.Clamp(0.60 + 0.15 * turns + 0.15 * monotonicity + 0.10 * regularity, 0, 0.99);
    }

    private VerticalZigZagProfile EmptyProfile() =>
        new(0, 0, 0, 0, 0, 0, double.PositiveInfinity, 0, double.PositiveInfinity);

    private static double Mean(IEnumerable<double> values)
    {
        var array = values as double[] ?? values.ToArray();
        return array.Length == 0 ? 0 : array.Average();
    }

    private double CoefficientOfVariation(IEnumerable<double> values)
    {
        var array = values as double[] ?? values.ToArray();
        if (array.Length == 0)
            return double.PositiveInfinity;

        var mean = array.Average();
        if (mean <= _epsilon)
            return double.PositiveInfinity;

        var variance = array.Average(value =>
        {
            var delta = value - mean;
            return delta * delta;
        });

        return Math.Sqrt(variance) / mean;
    }
}
