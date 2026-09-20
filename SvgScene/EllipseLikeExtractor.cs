namespace SvgMusic.Scene;

public interface IEllipseLikeExtractor
{
    bool TryCreateEllipse(
        GeometricShape shape,
        out EllipseLike ellipse);
}

/// <summary>
/// Recognizes closed, approximately elliptical contours independently of axis
/// alignment. PCA-like covariance supplies the orientation; points are then
/// tested against the fitted ellipse in local coordinates.
///
/// Multiple nested ellipse-like subpaths are represented as one hollow
/// primitive rather than as unrelated contours.
/// </summary>
public sealed class EllipseLikeExtractor : IEllipseLikeExtractor
{
    private readonly double _maxFitError;
    private readonly double _maxHollowOuterFitError;
    private readonly double _maxAxisRatio;

    public EllipseLikeExtractor(
        double maxFitError = 0.18,
        double maxHollowOuterFitError = 0.26,
        double maxAxisRatio = 4.0)
    {
        _maxFitError = maxFitError;
        _maxHollowOuterFitError = Math.Max(
            maxFitError,
            maxHollowOuterFitError);
        _maxAxisRatio = maxAxisRatio;
    }

    public bool TryCreateEllipse(
        GeometricShape shape,
        out EllipseLike ellipse)
    {
        ellipse = default!;

        var closedContours = shape.EffectiveContours
            .Where(contour =>
                contour.IsClosed &&
                contour.Points.Count >= 8)
            .ToList();

        if (closedContours.Count == 0)
        {
            return false;
        }

        var fits = closedContours
            .Select(Fit)
            .Where(fit => fit is not null)
            .Cast<EllipseFit>()
            .OrderByDescending(fit => fit.Area)
            .ToList();

        if (fits.Count == 0)
        {
            return false;
        }

        var outer = fits[0];
        var inner = FindInnerEllipse(
            outer,
            fits.Skip(1));

        // Filled noteheads and dots should remain strict: a single distorted
        // closed contour is far too easy to confuse with arbitrary notation.
        //
        // Hollow noteheads give us stronger purely geometric evidence: a second,
        // well-fitted, concentric ellipse-like contour inside the outer contour.
        // Engraving fonts can deliberately stylize the outer bowl enough to exceed
        // the ordinary ellipse fit threshold (Finale Maestro is one example), while
        // the inner hole remains clean. Allow that outer contour a modestly wider
        // fit band only when the inner geometric witness is present.
        if (!IsAcceptableEllipse(outer)
            && !(inner is not null
                && IsAcceptableHollowOuterEllipse(outer)))
        {
            return false;
        }

        ellipse = new EllipseLike(
            shape.Id,
            outer.Center,
            outer.MajorRadius,
            outer.MinorRadius,
            outer.Rotation,
            inner is not null,
            inner is null
                ? null
                : inner.Area / outer.Area,
            outer.Error,
            shape.SourceKind,
            shape.SourceIndex);

        return true;
    }

    private bool IsAcceptableEllipse(EllipseFit fit)
    {
        if (fit.Error > _maxFitError)
        {
            return false;
        }

        var axisRatio =
            fit.MajorRadius /
            Math.Max(fit.MinorRadius, 1e-9);

        return axisRatio <= _maxAxisRatio;
    }

    private bool IsAcceptableHollowOuterEllipse(
        EllipseFit fit)
    {
        if (fit.Error > _maxHollowOuterFitError)
        {
            return false;
        }

        var axisRatio =
            fit.MajorRadius /
            Math.Max(fit.MinorRadius, 1e-9);

        return axisRatio <= _maxAxisRatio;
    }

    private EllipseFit? FindInnerEllipse(
        EllipseFit outer,
        IEnumerable<EllipseFit> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!IsAcceptableEllipse(candidate))
            {
                continue;
            }

            if (!Contains(
                outer,
                candidate.Center))
            {
                continue;
            }

            var centerDistance =
                GeometryAlgorithms.Distance(
                    outer.Center,
                    candidate.Center) /
                Math.Max(outer.MajorRadius, 1e-9);

            if (centerDistance > 0.30)
            {
                continue;
            }

            var majorTooLarge =
                candidate.MajorRadius >=
                outer.MajorRadius * 0.92;

            var minorTooLarge =
                candidate.MinorRadius >=
                outer.MinorRadius * 0.92;

            if (majorTooLarge || minorTooLarge)
            {
                continue;
            }

            return candidate;
        }

        return null;
    }

    private static EllipseFit? Fit(
        GeometricContour contour)
    {
        IReadOnlyList<PointD> points = contour.Points;

        if (points.Count > 1 &&
            GeometryAlgorithms.Distance(
                points[0],
                points[^1]) < 1e-7)
        {
            points = points
                .Take(points.Count - 1)
                .ToArray();
        }

        if (points.Count < 6)
        {
            return null;
        }

        var center = new PointD(
            points.Average(point => point.X),
            points.Average(point => point.Y));

        double xx = 0;
        double xy = 0;
        double yy = 0;

        foreach (var point in points)
        {
            var dx = point.X - center.X;
            var dy = point.Y - center.Y;

            xx += dx * dx;
            xy += dx * dy;
            yy += dy * dy;
        }

        xx /= points.Count;
        xy /= points.Count;
        yy /= points.Count;

        var angle = 0.5 * Math.Atan2(
            2.0 * xy,
            xx - yy);

        var localPoints = RotateIntoLocalCoordinates(
            points,
            center,
            angle);

        var radiusX =
            (localPoints.Max(point => point.X) -
             localPoints.Min(point => point.X)) / 2.0;

        var radiusY =
            (localPoints.Max(point => point.Y) -
             localPoints.Min(point => point.Y)) / 2.0;

        if (radiusX <= 1e-6 ||
            radiusY <= 1e-6)
        {
            return null;
        }

        if (radiusY > radiusX)
        {
            (radiusX, radiusY) =
                (radiusY, radiusX);

            angle += Math.PI / 2.0;

            localPoints = RotateIntoLocalCoordinates(
                points,
                center,
                angle);
        }

        var normalizedRadii = localPoints
            .Select(point => Math.Sqrt(
                point.X * point.X /
                (radiusX * radiusX) +
                point.Y * point.Y /
                (radiusY * radiusY)))
            .ToArray();

        var error = Math.Sqrt(
            normalizedRadii
                .Select(radius =>
                    (radius - 1.0) *
                    (radius - 1.0))
                .Average());

        var area =
            Math.PI * radiusX * radiusY;

        return new EllipseFit(
            center,
            radiusX,
            radiusY,
            angle,
            error,
            area);
    }

    private static PointD[] RotateIntoLocalCoordinates(
        IReadOnlyList<PointD> points,
        PointD center,
        double angle)
    {
        var cosine = Math.Cos(angle);
        var sine = Math.Sin(angle);

        return points
            .Select(point =>
            {
                var dx = point.X - center.X;
                var dy = point.Y - center.Y;

                return new PointD(
                    dx * cosine + dy * sine,
                    -dx * sine + dy * cosine);
            })
            .ToArray();
    }

    private static bool Contains(
        EllipseFit ellipse,
        PointD point)
    {
        var dx = point.X - ellipse.Center.X;
        var dy = point.Y - ellipse.Center.Y;

        var cosine = Math.Cos(ellipse.Rotation);
        var sine = Math.Sin(ellipse.Rotation);

        var localX =
            dx * cosine + dy * sine;

        var localY =
            -dx * sine + dy * cosine;

        var normalized =
            localX * localX /
            (ellipse.MajorRadius * ellipse.MajorRadius) +
            localY * localY /
            (ellipse.MinorRadius * ellipse.MinorRadius);

        return normalized < 1.0;
    }

    private sealed record EllipseFit(
        PointD Center,
        double MajorRadius,
        double MinorRadius,
        double Rotation,
        double Error,
        double Area);
}
