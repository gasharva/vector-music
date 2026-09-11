namespace SvgMusic.Scene;

public interface IEllipseLikeExtractor
{
    bool TryCreateEllipse(GeometricShape shape, out EllipseLike ellipse);
}

/// <summary>
/// Recognises closed, approximately elliptical contours independently of axis
/// alignment. PCA supplies the orientation; points are then tested against the
/// fitted ellipse in local coordinates. Multiple nested ellipse-like subpaths
/// are represented as one hollow primitive.
/// </summary>
public sealed class EllipseLikeExtractor : IEllipseLikeExtractor
{
    private readonly double _maxFitError;
    private readonly double _maxAxisRatio;

    public EllipseLikeExtractor(double maxFitError = 0.18, double maxAxisRatio = 4.0)
    {
        _maxFitError = maxFitError;
        _maxAxisRatio = maxAxisRatio;
    }

    public bool TryCreateEllipse(GeometricShape shape, out EllipseLike ellipse)
    {
        ellipse = default!;
        var closed = shape.EffectiveContours.Where(c => c.IsClosed && c.Points.Count >= 8).ToList();
        if (closed.Count == 0) return false;

        var fits = closed.Select(Fit).Where(x => x is not null).Cast<EllipseFit>().OrderByDescending(x => x.Area).ToList();
        if (fits.Count == 0) return false;

        var outer = fits[0];
        if (outer.Error > _maxFitError || outer.MajorRadius / Math.Max(outer.MinorRadius, 1e-9) > _maxAxisRatio)
            return false;

        EllipseFit? inner = null;
        foreach (var candidate in fits.Skip(1))
        {
            if (candidate.Error > _maxFitError) continue;
            if (!Contains(outer, candidate.Center)) continue;
            var centerDistance = Distance(outer.Center, candidate.Center) / Math.Max(outer.MajorRadius, 1e-9);
            if (centerDistance > 0.30) continue;
            if (candidate.MajorRadius >= outer.MajorRadius * 0.92 || candidate.MinorRadius >= outer.MinorRadius * 0.92) continue;
            inner = candidate; break;
        }

        ellipse = new EllipseLike(shape.Id, outer.Center, outer.MajorRadius, outer.MinorRadius, outer.Rotation,
            inner is not null, inner is null ? null : inner.Area / outer.Area, outer.Error,
            shape.SourceKind, shape.SourceIndex);
        return true;
    }

    private static EllipseFit? Fit(GeometricContour contour)
    {
        var points = contour.Points;
        if (points.Count > 1 && Distance(points[0], points[^1]) < 1e-7) points = points.Take(points.Count - 1).ToArray();
        if (points.Count < 6) return null;

        var center = new PointD(points.Average(p => p.X), points.Average(p => p.Y));
        double xx=0,xy=0,yy=0;
        foreach(var p in points){var dx=p.X-center.X;var dy=p.Y-center.Y;xx+=dx*dx;xy+=dx*dy;yy+=dy*dy;}
        xx/=points.Count;xy/=points.Count;yy/=points.Count;
        var angle=0.5*Math.Atan2(2*xy,xx-yy);var ca=Math.Cos(angle);var sa=Math.Sin(angle);
        var local=points.Select(p=>{var dx=p.X-center.X;var dy=p.Y-center.Y;return new PointD(dx*ca+dy*sa,-dx*sa+dy*ca);}).ToArray();
        var rx=(local.Max(p=>p.X)-local.Min(p=>p.X))/2.0;var ry=(local.Max(p=>p.Y)-local.Min(p=>p.Y))/2.0;
        if(rx<=1e-6||ry<=1e-6)return null;
        // Keep MajorRadius associated with Rotation.
        if(ry>rx){(rx,ry)=(ry,rx);angle+=Math.PI/2;ca=Math.Cos(angle);sa=Math.Sin(angle);local=points.Select(p=>{var dx=p.X-center.X;var dy=p.Y-center.Y;return new PointD(dx*ca+dy*sa,-dx*sa+dy*ca);}).ToArray();}
        var radial=local.Select(p=>Math.Sqrt((p.X*p.X)/(rx*rx)+(p.Y*p.Y)/(ry*ry))).ToArray();
        var error=Math.Sqrt(radial.Select(r=>(r-1)*(r-1)).Average());
        return new EllipseFit(center,rx,ry,angle,error,Math.PI*rx*ry);
    }

    private static bool Contains(EllipseFit e, PointD p){var dx=p.X-e.Center.X;var dy=p.Y-e.Center.Y;var c=Math.Cos(e.Rotation);var s=Math.Sin(e.Rotation);var x=dx*c+dy*s;var y=-dx*s+dy*c;return x*x/(e.MajorRadius*e.MajorRadius)+y*y/(e.MinorRadius*e.MinorRadius)<1.0;}
    private static double Distance(PointD a,PointD b){var dx=a.X-b.X;var dy=a.Y-b.Y;return Math.Sqrt(dx*dx+dy*dy);}
    private sealed record EllipseFit(PointD Center,double MajorRadius,double MinorRadius,double Rotation,double Error,double Area);
}
