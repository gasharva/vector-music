namespace SvgMusic.Scene;

public sealed record ArcDiagnostic(string ShapeId, string Result, string Reason, string Metrics);

public interface IArcExtractor
{
    bool TryCreateArc(GeometricShape shape, out CurvedStroke curvedStroke);
    IReadOnlyList<ArcDiagnostic> Diagnostics { get; }
    void ClearDiagnostics();
}

public sealed class ArcExtractor : IArcExtractor
{
    private readonly double _minBend;
    private readonly double _maxBend;
    private readonly double _minSameSideRatio;
    private readonly double _maxRelativeThickness;
    private readonly List<ArcDiagnostic> _diagnostics = [];
    public IReadOnlyList<ArcDiagnostic> Diagnostics => _diagnostics;

    public ArcExtractor(double minBend = 0.025, double maxBend = 1.25,
        double minSameSideRatio = 0.82, double maxRelativeThickness = 0.35)
    {
        _minBend = minBend; _maxBend = maxBend; _minSameSideRatio = minSameSideRatio;
        _maxRelativeThickness = maxRelativeThickness;
    }

    public void ClearDiagnostics() => _diagnostics.Clear();

    public bool TryCreateArc(GeometricShape shape, out CurvedStroke curvedStroke)
    {
        curvedStroke = default!;
        if (!shape.IsClosed || shape.Points.Count < 8) return Reject(shape, "not a sufficiently sampled closed contour", "");
        var contour = shape.Points.ToList();
        if (Distance(contour[0], contour[^1]) < 1e-6) contour.RemoveAt(contour.Count - 1);
        if (contour.Count < 7) return Reject(shape, "too few contour points", $"points={contour.Count}");

        var (ai, bi) = FarthestPair(contour);
        var sideA = SliceCircular(contour, ai, bi);
        var sideB = SliceCircular(contour, bi, ai); sideB.Reverse();
        const int n = 33;
        var a = ResampleByArcLength(sideA, n); var b = ResampleByArcLength(sideB, n);
        if (a.Count != n || b.Count != n) return Reject(shape, "could not resample both sides", "");

        var center = new List<PointD>(n); var widths = new double[n];
        for (var i = 0; i < n; i++) { center.Add(Midpoint(a[i], b[i])); widths[i] = Distance(a[i], b[i]); }
        var start = center[0]; var end = center[^1]; var chord = Distance(start, end);
        if (chord <= 1e-6) return Reject(shape, "degenerate chord", $"chord={chord:F4}");

        var body = widths.Skip(3).Take(widths.Length - 6).OrderBy(x => x).ToArray();
        var width = body.Length == 0 ? widths.Average() : body[body.Length / 2];
        var thickness = width / chord;
        if (thickness > _maxRelativeThickness) return Reject(shape, "too thick", Metrics(chord, thickness, null, null, null));

        var signed = center.Select(p => SignedDistanceToLine(p, start, end)).ToArray();
        var peak = signed.Skip(1).Take(signed.Length - 2).Max(x => Math.Abs(x));
        var bend = peak / chord;
        if (bend < _minBend) return Reject(shape, "bend below minimum", Metrics(chord, thickness, bend, null, null));
        if (bend > _maxBend) return Reject(shape, "bend above maximum", Metrics(chord, thickness, bend, null, null));

        var dominantSign = signed.OrderByDescending(x => Math.Abs(x)).First() >= 0 ? 1.0 : -1.0;
        var meaningful = signed.Skip(2).Take(signed.Length - 4).Where(x => Math.Abs(x) > chord * 0.005).ToArray();
        if (meaningful.Length == 0) return Reject(shape, "no meaningful bend samples", Metrics(chord, thickness, bend, null, null));
        var sideRatio = meaningful.Count(x => x * dominantSign > 0) / (double)meaningful.Length;
        if (sideRatio < _minSameSideRatio) return Reject(shape, "centreline changes side", Metrics(chord, thickness, bend, sideRatio, null));

        var profile = signed.Select(Math.Abs).ToArray();
        var peakIndex = Array.IndexOf(profile, profile.Max());
        if (peakIndex < 3 || peakIndex > profile.Length - 4)
            return Reject(shape, "bend peak too close to endpoint", Metrics(chord, thickness, bend, sideRatio, null) + $" peak={peakIndex}/{n}");
        var rise = MonotonicAgreement(profile, 0, peakIndex, true);
        var fall = MonotonicAgreement(profile, peakIndex, profile.Length - 1, false);
        if (rise < 0.70 || fall < 0.70)
            return Reject(shape, "bend profile is not a single arch", Metrics(chord, thickness, bend, sideRatio, null) + $" rise={rise:F3} fall={fall:F3}");

        // Bezier fit is descriptive only. A real curved stroke is allowed to be
        // more complex than one quadratic curve.
        var control = FitQuadratic(center, start, end);
        var fit = QuadraticFitError(center, start, control, end) / chord;
        var approximation = new QuadraticApproximation(start, control, end, fit);

        curvedStroke = new CurvedStroke(shape.Id, center, widths, bend, sideRatio, approximation,
            shape.SourceKind, shape.SourceIndex);
        _diagnostics.Add(new ArcDiagnostic(shape.Id, "ACCEPT", "curved stroke", Metrics(chord, thickness, bend, sideRatio, fit)));
        return true;
    }

    private bool Reject(GeometricShape s,string reason,string metrics){_diagnostics.Add(new(s.Id,"REJECT",reason,metrics));return false;}
    private static string Metrics(double chord,double thickness,double? bend,double? side,double? fit)=>$"chord={chord:F2} thickness={thickness:F3}"+(bend is null?"":$" bend={bend:F3}")+(side is null?"":$" side={side:F3}")+(fit is null?"":$" fit={fit:F3}");
    private static (int A,int B) FarthestPair(IReadOnlyList<PointD> p){var best=-1.0;var ai=0;var bi=1;for(var i=0;i<p.Count-1;i++)for(var j=i+1;j<p.Count;j++){var dx=p[i].X-p[j].X;var dy=p[i].Y-p[j].Y;var d=dx*dx+dy*dy;if(d>best){best=d;ai=i;bi=j;}}return(ai,bi);}
    private static List<PointD> SliceCircular(IReadOnlyList<PointD> p,int start,int end){var r=new List<PointD>();var i=start;while(true){r.Add(p[i]);if(i==end)break;i=(i+1)%p.Count;}return r;}
    private static List<PointD> ResampleByArcLength(IReadOnlyList<PointD> input,int count){if(input.Count<2)return[];var c=new double[input.Count];for(var i=1;i<input.Count;i++)c[i]=c[i-1]+Distance(input[i-1],input[i]);var total=c[^1];if(total<=1e-9)return[];var r=new List<PointD>(count);var seg=1;for(var s=0;s<count;s++){var target=total*s/(count-1.0);while(seg<c.Length-1&&c[seg]<target)seg++;var from=seg-1;var span=c[seg]-c[from];var t=span<=1e-12?0:(target-c[from])/span;r.Add(new(input[from].X+(input[seg].X-input[from].X)*t,input[from].Y+(input[seg].Y-input[from].Y)*t));}return r;}
    private static PointD FitQuadratic(IReadOnlyList<PointD> p,PointD start,PointD end){double den=0,cx=0,cy=0;for(var i=1;i<p.Count-1;i++){var t=i/(double)(p.Count-1);var k=2*(1-t)*t;var bx=(1-t)*(1-t)*start.X+t*t*end.X;var by=(1-t)*(1-t)*start.Y+t*t*end.Y;den+=k*k;cx+=k*(p[i].X-bx);cy+=k*(p[i].Y-by);}return den<=1e-12?Midpoint(start,end):new(cx/den,cy/den);}
    private static double QuadraticFitError(IReadOnlyList<PointD> p,PointD p0,PointD c,PointD p2){double sum=0;for(var i=0;i<p.Count;i++){var t=i/(double)(p.Count-1);var mt=1-t;var q=new PointD(mt*mt*p0.X+2*mt*t*c.X+t*t*p2.X,mt*mt*p0.Y+2*mt*t*c.Y+t*t*p2.Y);var d=Distance(p[i],q);sum+=d*d;}return Math.Sqrt(sum/p.Count);}
    private static double MonotonicAgreement(double[] v,int from,int to,bool increasing){var good=0;var total=0;var tolerance=v.Max()*0.04;for(var i=from+1;i<=to;i++){var d=v[i]-v[i-1];if(Math.Abs(d)<=tolerance||(increasing?d>0:d<0))good++;total++;}return total==0?1:good/(double)total;}
    private static double SignedDistanceToLine(PointD p,PointD a,PointD b){var dx=b.X-a.X;var dy=b.Y-a.Y;var len=Math.Sqrt(dx*dx+dy*dy);return len<=1e-12?0:(dx*(p.Y-a.Y)-dy*(p.X-a.X))/len;}
    private static PointD Midpoint(PointD a,PointD b)=>new((a.X+b.X)/2,(a.Y+b.Y)/2);
    private static double Distance(PointD a,PointD b){var dx=a.X-b.X;var dy=a.Y-b.Y;return Math.Sqrt(dx*dx+dy*dy);}
}
