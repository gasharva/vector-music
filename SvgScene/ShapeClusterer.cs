namespace SvgMusic.Scene;

public interface IShapeClusterer { NotationScene Cluster(GeometricScene scene); IReadOnlyList<ArcDiagnostic> ArcDiagnostics { get; } }
public sealed class ShapeClusterer:IShapeClusterer
{
    private readonly double _distanceThreshold;private readonly IGeometryAnalyzer _geometryAnalyzer;private readonly IArcExtractor _arcExtractor;private readonly IEllipseLikeExtractor _ellipseExtractor;
    public IReadOnlyList<ArcDiagnostic> ArcDiagnostics=>_arcExtractor.Diagnostics;
    public ShapeClusterer(IGeometryAnalyzer? geometryAnalyzer=null,IArcExtractor? arcExtractor=null,IEllipseLikeExtractor? ellipseExtractor=null,double distanceThreshold=0.035){_geometryAnalyzer=geometryAnalyzer??new GeometryAnalyzer();_arcExtractor=arcExtractor??new ArcExtractor();_ellipseExtractor=ellipseExtractor??new EllipseLikeExtractor();_distanceThreshold=distanceThreshold;}
    public NotationScene Cluster(GeometricScene scene)
    {
        _arcExtractor.ClearDiagnostics();var prototypes=new List<ShapePrototype>();var instances=new List<ShapeInstance>();var strokes=new List<Stroke>();var curved=new List<CurvedStroke>();var ellipses=new List<EllipseLike>();
        foreach(var shape in scene.Shapes)
        {
            if(_geometryAnalyzer.TryCreateStroke(shape,out var stroke)){strokes.Add(stroke);continue;}
            if(_arcExtractor.TryCreateArc(shape,out var curve)){curved.Add(curve);continue;}
            if(_ellipseExtractor.TryCreateEllipse(shape,out var ellipse)){ellipses.Add(ellipse);continue;}
            var descriptor=Describe(shape);ShapePrototype? best=null;var bestDistance=double.PositiveInfinity;foreach(var p in prototypes){var d=Distance(descriptor,p.Descriptor);if(d<bestDistance){bestDistance=d;best=p;}}
            if(best is null||bestDistance>_distanceThreshold){best=new ShapePrototype($"prototype-{prototypes.Count+1}",shape.Id,descriptor);prototypes.Add(best);}instances.Add(new ShapeInstance(shape.Id,best.Id,shape.Bounds.MinX,shape.Bounds.MinY,shape.Bounds.Width,shape.Bounds.Height,shape.SourceKind,shape.SourceIndex));
        }
        return new NotationScene(prototypes,instances,strokes,curved,ellipses);
    }
    private static ShapeDescriptor Describe(GeometricShape s){var b=s.Bounds;var w=Math.Max(b.Width,1e-9);var h=Math.Max(b.Height,1e-9);var n=s.Points.Select(p=>new PointD((p.X-b.MinX)/w,(p.Y-b.MinY)/h)).ToList();return new(w/h,Math.Abs(SignedArea(n)),n);}
    private static double Distance(ShapeDescriptor a,ShapeDescriptor b){var ap=Math.Abs(Math.Log(Math.Max(a.AspectRatio,1e-9)/Math.Max(b.AspectRatio,1e-9)));var area=Math.Abs(a.RelativeArea-b.RelativeArea);var count=Math.Min(a.NormalizedPoints.Count,b.NormalizedPoints.Count);if(count==0)return double.PositiveInfinity;var sq=0.0;for(var i=0;i<count;i++){var x=SampleAt(a.NormalizedPoints,i,count);var y=SampleAt(b.NormalizedPoints,i,count);var dx=x.X-y.X;var dy=x.Y-y.Y;sq+=dx*dx+dy*dy;}return Math.Sqrt(sq/count)+.25*ap+.15*area;}
    private static PointD SampleAt(IReadOnlyList<PointD> p,int i,int target){if(target<=1||p.Count==1)return p[0];var pos=i*(p.Count-1.0)/(target-1.0);var l=(int)Math.Floor(pos);var r=Math.Min(l+1,p.Count-1);var t=pos-l;return new(p[l].X+(p[r].X-p[l].X)*t,p[l].Y+(p[r].Y-p[l].Y)*t);}
    private static double SignedArea(IReadOnlyList<PointD> p){if(p.Count<3)return 0;var a=0.0;for(var i=0;i<p.Count;i++){var x=p[i];var y=p[(i+1)%p.Count];a+=x.X*y.Y-y.X*x.Y;}return a/2;}
}
