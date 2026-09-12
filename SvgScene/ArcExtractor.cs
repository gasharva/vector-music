namespace SvgMusic.Scene;

public sealed record ArcDiagnostic(
    string ShapeId,
    string Result,
    string Reason,
    string Metrics);

public interface IArcExtractor
{
    bool TryCreateArc(
        GeometricShape shape,
        out CurvedStroke curvedStroke);

    IReadOnlyList<ArcDiagnostic> Diagnostics { get; }

    void ClearDiagnostics();
}

/// <summary>
/// Recognizes curved strokes in two source representations:
///
/// 1. a filled closed ribbon around a slur/tie;
/// 2. a stroked open curved path whose geometry is already the centreline.
///
/// The closed-ribbon algorithm remains the primary path. Open curves are handled
/// by a separate fallback extractor so the two representations do not blur into
/// one large method.
/// </summary>
public sealed class ArcExtractor : IArcExtractor
{
    private const int SampleCount = 33;

    private readonly double _minBend;
    private readonly double _maxBend;
    private readonly double _minSameSideRatio;
    private readonly double _maxRelativeThickness;
    private readonly double _maxRelativeFitError;
    private readonly OpenCurveArcExtractor _openCurveExtractor;
    private readonly List<ArcDiagnostic> _diagnostics = [];

    public IReadOnlyList<ArcDiagnostic> Diagnostics => _diagnostics;

    public ArcExtractor(
        double minBend = 0.025,
        double maxBend = 1.25,
        double minSameSideRatio = 0.82,
        double maxRelativeThickness = 0.35,
        double maxRelativeFitError = 0.075)
    {
        _minBend = minBend;
        _maxBend = maxBend;
        _minSameSideRatio = minSameSideRatio;
        _maxRelativeThickness = maxRelativeThickness;
        _maxRelativeFitError = maxRelativeFitError;

        _openCurveExtractor = new OpenCurveArcExtractor(
            minBend,
            maxBend,
            minSameSideRatio,
            maxRelativeFitError);
    }

    public void ClearDiagnostics()
    {
        _diagnostics.Clear();
    }

    public bool TryCreateArc(
        GeometricShape shape,
        out CurvedStroke curvedStroke)
    {
        curvedStroke = default!;

        var contours = shape.EffectiveContours
            .Where(contour => contour.Points.Count > 0)
            .ToList();

        if (contours.Count != 1)
        {
            return Reject(
                shape,
                "not a single contour",
                $"contours={contours.Count}");
        }

        var contour = contours[0];

        if (!contour.IsClosed)
        {
            return TryCreateOpenCurve(
                shape,
                out curvedStroke);
        }

        return TryCreateClosedRibbon(
            shape,
            contour,
            out curvedStroke);
    }

    private bool TryCreateOpenCurve(
        GeometricShape shape,
        out CurvedStroke curvedStroke)
    {
        curvedStroke = default!;

        var result = _openCurveExtractor.TryExtract(shape);

        if (!result.Accepted || result.CurvedStroke is null)
        {
            return Reject(
                shape,
                result.Reason,
                result.Metrics);
        }

        curvedStroke = result.CurvedStroke;

        Accept(
            shape,
            result.Reason,
            result.Metrics);

        return true;
    }

    private bool TryCreateClosedRibbon(
        GeometricShape shape,
        GeometricContour geometricContour,
        out CurvedStroke curvedStroke)
    {
        curvedStroke = default!;

        if (geometricContour.Points.Count < 8)
        {
            return Reject(
                shape,
                "not a sufficiently sampled closed contour",
                $"points={geometricContour.Points.Count}");
        }

        var contour = geometricContour.Points.ToList();

        if (GeometryAlgorithms.Distance(
            contour[0],
            contour[^1]) < 1e-6)
        {
            contour.RemoveAt(contour.Count - 1);
        }

        if (contour.Count < 7)
        {
            return Reject(
                shape,
                "too few contour points",
                $"points={contour.Count}");
        }

        var (firstEndpointIndex, secondEndpointIndex) =
            FarthestPair(contour);

        var sideA = SliceCircular(
            contour,
            firstEndpointIndex,
            secondEndpointIndex);

        var sideB = SliceCircular(
            contour,
            secondEndpointIndex,
            firstEndpointIndex);

        sideB.Reverse();

        var resampledA = GeometryAlgorithms.ResampleByArcLength(
            sideA,
            SampleCount);

        var resampledB = GeometryAlgorithms.ResampleByArcLength(
            sideB,
            SampleCount);

        if (resampledA.Count != SampleCount ||
            resampledB.Count != SampleCount)
        {
            return Reject(
                shape,
                "could not resample both sides",
                string.Empty);
        }

        var centerline = new List<PointD>(SampleCount);
        var widths = new double[SampleCount];

        for (var i = 0; i < SampleCount; i++)
        {
            centerline.Add(
                GeometryAlgorithms.Midpoint(
                    resampledA[i],
                    resampledB[i]));

            widths[i] = GeometryAlgorithms.Distance(
                resampledA[i],
                resampledB[i]);
        }

        var start = centerline[0];
        var end = centerline[^1];
        var chord = GeometryAlgorithms.Distance(start, end);

        if (chord <= 1e-6)
        {
            return Reject(
                shape,
                "degenerate chord",
                $"chord={chord:0.####}");
        }

        var bodyWidths = widths
            .Skip(3)
            .Take(widths.Length - 6)
            .OrderBy(width => width)
            .ToArray();

        var representativeWidth = bodyWidths.Length == 0
            ? widths.Average()
            : bodyWidths[bodyWidths.Length / 2];

        var relativeThickness =
            representativeWidth / chord;

        if (relativeThickness > _maxRelativeThickness)
        {
            return Reject(
                shape,
                "too thick",
                Metrics(
                    chord,
                    relativeThickness,
                    null,
                    null,
                    null));
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
                shape,
                "bend below minimum",
                Metrics(
                    chord,
                    relativeThickness,
                    bend,
                    null,
                    null));
        }

        if (bend > _maxBend)
        {
            return Reject(
                shape,
                "bend above maximum",
                Metrics(
                    chord,
                    relativeThickness,
                    bend,
                    null,
                    null));
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
                shape,
                "no meaningful bend samples",
                Metrics(
                    chord,
                    relativeThickness,
                    bend,
                    null,
                    null));
        }

        var sameSideRatio = meaningfulDistances.Count(
            value => value * dominantSign > 0) /
            (double)meaningfulDistances.Length;

        if (sameSideRatio < _minSameSideRatio)
        {
            return Reject(
                shape,
                "centreline changes side",
                Metrics(
                    chord,
                    relativeThickness,
                    bend,
                    sameSideRatio,
                    null));
        }

        var profile = signedDistances
            .Select(Math.Abs)
            .ToArray();

        var peakIndex = Array.IndexOf(
            profile,
            profile.Max());

        if (peakIndex < 3 ||
            peakIndex > profile.Length - 4)
        {
            return Reject(
                shape,
                "bend peak too close to endpoint",
                Metrics(
                    chord,
                    relativeThickness,
                    bend,
                    sameSideRatio,
                    null) +
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

        if (riseAgreement < 0.70 ||
            fallAgreement < 0.70)
        {
            return Reject(
                shape,
                "bend profile is not a single arch",
                Metrics(
                    chord,
                    relativeThickness,
                    bend,
                    sameSideRatio,
                    null) +
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
                shape,
                "centreline not regular enough",
                Metrics(
                    chord,
                    relativeThickness,
                    bend,
                    sameSideRatio,
                    fitError));
        }

        var approximation = new QuadraticApproximation(
            start,
            control,
            end,
            fitError);

        curvedStroke = new CurvedStroke(
            shape.Id,
            centerline,
            widths,
            bend,
            sameSideRatio,
            approximation,
            shape.SourceKind,
            shape.SourceIndex);

        Accept(
            shape,
            "curved stroke",
            Metrics(
                chord,
                relativeThickness,
                bend,
                sameSideRatio,
                fitError));

        return true;
    }

    private bool Reject(
        GeometricShape shape,
        string reason,
        string metrics)
    {
        _diagnostics.Add(new ArcDiagnostic(
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
        _diagnostics.Add(new ArcDiagnostic(
            shape.Id,
            "ACCEPT",
            reason,
            metrics));
    }

    private static string Metrics(
        double chord,
        double thickness,
        double? bend,
        double? sideRatio,
        double? fitError)
    {
        var result =
            $"chord={chord:0.##} " +
            $"thickness={thickness:0.###}";

        if (bend is not null)
        {
            result += $" bend={bend:0.###}";
        }

        if (sideRatio is not null)
        {
            result += $" side={sideRatio:0.###}";
        }

        if (fitError is not null)
        {
            result += $" fit={fitError:0.###}";
        }

        return result;
    }

    private static (int A, int B) FarthestPair(
        IReadOnlyList<PointD> points)
    {
        var bestDistanceSquared = -1.0;
        var firstIndex = 0;
        var secondIndex = 1;

        for (var i = 0; i < points.Count - 1; i++)
        {
            for (var j = i + 1; j < points.Count; j++)
            {
                var dx = points[i].X - points[j].X;
                var dy = points[i].Y - points[j].Y;
                var distanceSquared = dx * dx + dy * dy;

                if (distanceSquared <= bestDistanceSquared)
                {
                    continue;
                }

                bestDistanceSquared = distanceSquared;
                firstIndex = i;
                secondIndex = j;
            }
        }

        return (
            firstIndex,
            secondIndex);
    }

    private static List<PointD> SliceCircular(
        IReadOnlyList<PointD> points,
        int start,
        int end)
    {
        var result = new List<PointD>();
        var index = start;

        while (true)
        {
            result.Add(points[index]);

            if (index == end)
            {
                break;
            }

            index = (index + 1) % points.Count;
        }

        return result;
    }
}
