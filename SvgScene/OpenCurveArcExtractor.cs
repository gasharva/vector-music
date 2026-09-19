namespace SvgMusic.Scene;

internal sealed record OpenCurveArcResult(
    bool Accepted,
    CurvedStroke? CurvedStroke,
    string Reason,
    string Metrics);

/// <summary>
/// Recognizes an open curved path directly as a CurvedStroke centreline.
///
/// Closed ribbon-like slurs are handled by ArcExtractor's primary algorithm.
/// This fallback exists for SVGs that encode the visible curve as a stroked
/// open path instead of a filled closed contour.
/// </summary>
internal sealed class OpenCurveArcExtractor
{
    private const int SampleCount = 33;

    private readonly double _minBend;
    private readonly double _maxBend;
    private readonly double _minSameSideRatio;
    private readonly double _maxRelativeFitError;

    public OpenCurveArcExtractor(
        double minBend,
        double maxBend,
        double minSameSideRatio,
        double maxRelativeFitError)
    {
        _minBend = minBend;
        _maxBend = maxBend;
        _minSameSideRatio = minSameSideRatio;
        _maxRelativeFitError = maxRelativeFitError;
    }

    public OpenCurveArcResult TryExtract(GeometricShape shape)
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

        if (contour.IsClosed)
        {
            return Reject(
                "contour is closed",
                $"points={contour.Points.Count}");
        }

        if (contour.Points.Count < 4)
        {
            return Reject(
                "too few points",
                $"points={contour.Points.Count}");
        }

        var centerline = GeometryAlgorithms.ResampleByArcLength(
            contour.Points,
            SampleCount);

        if (centerline.Count != SampleCount)
        {
            return Reject(
                "could not resample centreline",
                $"points={contour.Points.Count}");
        }

        var start = centerline[0];
        var end = centerline[^1];
        var chord = GeometryAlgorithms.Distance(start, end);

        if (chord <= 1e-6)
        {
            return Reject(
                "degenerate chord",
                $"chord={chord:0.###}");
        }

        var signedDistances = centerline
            .Select(point => GeometryAlgorithms.SignedDistanceToLine(
                point,
                start,
                end))
            .ToArray();

        var peak = signedDistances
            .Skip(1)
            .Take(signedDistances.Length - 2)
            .Max(value => Math.Abs(value));

        var bend = peak / chord;

        if (bend < _minBend)
        {
            return Reject(
                "bend below minimum",
                Metrics(chord, bend, null, null));
        }

        if (bend > _maxBend)
        {
            return Reject(
                "bend above maximum",
                Metrics(chord, bend, null, null));
        }

        var dominantSign = signedDistances
            .OrderByDescending(value => Math.Abs(value))
            .First() >= 0
                ? 1.0
                : -1.0;

        var meaningfulDistances = signedDistances
            .Skip(2)
            .Take(signedDistances.Length - 4)
            .Where(value => Math.Abs(value) > chord * 0.005)
            .ToArray();

        if (meaningfulDistances.Length == 0)
        {
            return Reject(
                "no meaningful bend samples",
                Metrics(chord, bend, null, null));
        }

        var sameSideRatio = meaningfulDistances.Count(
            value => value * dominantSign > 0) /
            (double)meaningfulDistances.Length;

        if (sameSideRatio < _minSameSideRatio)
        {
            return Reject(
                "centreline changes side",
                Metrics(chord, bend, sameSideRatio, null));
        }

        var profile = signedDistances
            .Select(Math.Abs)
            .ToArray();

        var peakIndex = Array.IndexOf(
            profile,
            profile.Max());

        if (peakIndex < 3 || peakIndex > profile.Length - 4)
        {
            return Reject(
                "bend peak too close to endpoint",
                Metrics(chord, bend, sameSideRatio, null) +
                $" peak={peakIndex}/{SampleCount}");
        }

        var riseAgreement = GeometryAlgorithms.MonotonicAgreement(
            profile,
            0,
            peakIndex,
            true);

        var fallAgreement = GeometryAlgorithms.MonotonicAgreement(
            profile,
            peakIndex,
            profile.Length - 1,
            false);

        if (riseAgreement < 0.70 || fallAgreement < 0.70)
        {
            return Reject(
                "bend profile is not a single arch",
                Metrics(chord, bend, sameSideRatio, null) +
                $" rise={riseAgreement:0.###}" +
                $" fall={fallAgreement:0.###}");
        }

        var control = GeometryAlgorithms.FitQuadratic(
            centerline,
            start,
            end);

        var fitError = GeometryAlgorithms.QuadraticFitError(
            centerline,
            start,
            control,
            end) / chord;

        if (fitError > _maxRelativeFitError)
        {
            return Reject(
                "centreline not regular enough",
                Metrics(
                    chord,
                    bend,
                    sameSideRatio,
                    fitError));
        }

        var width = Math.Max(shape.StrokeWidth, 1.0);
        var widthProfile = Enumerable
            .Repeat(width, centerline.Count)
            .ToArray();

        var approximation = new QuadraticApproximation(
            start,
            control,
            end,
            fitError);

        var curvedStroke = new CurvedStroke(
            shape.Id,
            centerline,
            widthProfile,
            bend,
            sameSideRatio,
            approximation,
            shape.SourceKind,
            shape.SourceIndex,
            SourceClass: shape.SourceClass);

        return new OpenCurveArcResult(
            true,
            curvedStroke,
            "open curved centreline",
            Metrics(
                chord,
                bend,
                sameSideRatio,
                fitError));
    }

    private static string Metrics(
        double chord,
        double bend,
        double? sameSideRatio,
        double? fitError)
    {
        var result =
            $"chord={chord:0.###} " +
            $"bend={bend:0.###}";

        if (sameSideRatio is not null)
        {
            result += $" side={sameSideRatio:0.###}";
        }

        if (fitError is not null)
        {
            result += $" fit={fitError:0.###}";
        }

        return result;
    }

    private static OpenCurveArcResult Reject(
        string reason,
        string metrics)
    {
        return new OpenCurveArcResult(
            false,
            null,
            reason,
            metrics);
    }
}
