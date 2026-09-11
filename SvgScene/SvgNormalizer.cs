using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SvgMusic.Scene;

public interface ISvgNormalizer { GeometricScene Normalize(string fileName); }

public sealed class SvgNormalizer : ISvgNormalizer
{
    private static readonly Regex PathTokenRegex = new(@"[A-Za-z]|[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TransformRegex = new(@"(?<name>matrix|translate)\s*\((?<args>[^)]*)\)", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private readonly int _samplesPerCurve;
    public SvgNormalizer(int samplesPerCurve = 12) { _samplesPerCurve = samplesPerCurve; }

    public GeometricScene Normalize(string fileName)
    {
        var doc=XDocument.Load(fileName);var root=doc.Root??throw new InvalidDataException("SVG root element is missing.");
        var definitions=root.Descendants().Where(x=>x.Name.LocalName=="path"&&x.Attribute("id") is not null).ToDictionary(x=>(string)x.Attribute("id")!,x=>(string?)x.Attribute("d")??string.Empty,StringComparer.Ordinal);
        var shapes=new List<GeometricShape>();var no=0;
        foreach(var element in root.Descendants())
        {
            if(IsInsideDefs(element))continue;var kind=element.Name.LocalName;var transform=ParseTransform((string?)element.Attribute("transform"));
            if(kind=="path")
            {
                var d=(string?)element.Attribute("d");if(string.IsNullOrWhiteSpace(d))continue;
                var local=ParseAndSamplePath(d);var contours=TransformContours(local,transform);var points=contours.SelectMany(c=>c.Points).ToList();
                shapes.Add(BuildShape(++no,"path",element,points,isClosed:contours.Count==1&&contours[0].IsClosed,strokeWidth:StrokeWidth(element),contours:contours));
            }
            else if(kind=="use")
            {
                var href=(string?)element.Attribute("href")??element.Attributes().FirstOrDefault(a=>a.Name.LocalName=="href")?.Value;
                if(string.IsNullOrWhiteSpace(href)||!href.StartsWith('#')||!definitions.TryGetValue(href[1..],out var d)||string.IsNullOrWhiteSpace(d))continue;
                var t=AffineTransform.Translation(DoubleAttr(element,"x"),DoubleAttr(element,"y")).Then(transform);
                var contours=TransformContours(ParseAndSamplePath(d),t);var points=contours.SelectMany(c=>c.Points).ToList();
                shapes.Add(BuildShape(++no,"use",element,points,href[1..],contours.Count==1&&contours[0].IsClosed,StrokeWidth(element),contours));
            }
            else if(kind=="line")
            {
                var p=new List<PointD>{new(DoubleAttr(element,"x1"),DoubleAttr(element,"y1")),new(DoubleAttr(element,"x2"),DoubleAttr(element,"y2"))}.Select(transform.Apply).ToList();
                shapes.Add(BuildShape(++no,"line",element,p,isClosed:false,strokeWidth:StrokeWidth(element),contours:[new GeometricContour(p,false)]));
            }
            else if(kind is "polyline" or "polygon")
            {
                var p=ParsePoints((string?)element.Attribute("points"));if(p.Count<2)continue;var closed=kind=="polygon";if(closed&&p[^1]!=p[0])p.Add(p[0]);p=p.Select(transform.Apply).ToList();
                shapes.Add(BuildShape(++no,kind,element,p,isClosed:closed,strokeWidth:StrokeWidth(element),contours:[new GeometricContour(p,closed)]));
            }
            else if(kind=="rect")
            {
                var x=DoubleAttr(element,"x");var y=DoubleAttr(element,"y");var w=DoubleAttr(element,"width");var h=DoubleAttr(element,"height");if(w<=0||h<=0)continue;
                var p=new List<PointD>{new(x,y),new(x+w,y),new(x+w,y+h),new(x,y+h),new(x,y)}.Select(transform.Apply).ToList();
                shapes.Add(BuildShape(++no,"rect",element,p,isClosed:true,strokeWidth:StrokeWidth(element),contours:[new GeometricContour(p,true)]));
            }
        }
        return new GeometricScene(shapes);
    }

    private static List<GeometricContour> TransformContours(IReadOnlyList<GeometricContour> contours,AffineTransform t)=>contours.Select(c=>new GeometricContour(c.Points.Select(t.Apply).ToList(),c.IsClosed)).ToList();
    private static GeometricShape BuildShape(int number,string kind,XElement element,IReadOnlyList<PointD> points,string? sourceId=null,bool isClosed=false,double strokeWidth=0,IReadOnlyList<GeometricContour>? contours=null)=>new($"shape-{number}",kind,points,BoundsD.FromPoints(points),sourceId,(string?)element.Attribute("data-index"),isClosed,strokeWidth,contours);

    private IReadOnlyList<GeometricContour> ParseAndSamplePath(string d)
    {
        var tokens=PathTokenRegex.Matches(d).Select(m=>m.Value).ToList();var result=new List<GeometricContour>();List<PointD>? points=null;var i=0;var current=new PointD(0,0);var start=current;char command='\0';
        void Finish(bool closed=false){if(points is null||points.Count==0)return;if(closed&&points[^1]!=start)points.Add(start);result.Add(new GeometricContour(points,closed||PointsAreClosed(points)));points=null;}
        while(i<tokens.Count)
        {
            if(IsCommand(tokens[i])){command=tokens[i][0];i++;}
            switch(command)
            {
                case 'M':
                    Finish();current=new PointD(Number(tokens[i++]),Number(tokens[i++]));start=current;points=[current];command='L';break;
                case 'L':
                    points??=[];current=new PointD(Number(tokens[i++]),Number(tokens[i++]));points.Add(current);break;
                case 'C':
                    points??=[];var p0=current;var p1=new PointD(Number(tokens[i++]),Number(tokens[i++]));var p2=new PointD(Number(tokens[i++]),Number(tokens[i++]));var p3=new PointD(Number(tokens[i++]),Number(tokens[i++]));
                    for(var s=1;s<=_samplesPerCurve;s++){var t=s/(double)_samplesPerCurve;points.Add(Cubic(p0,p1,p2,p3,t));}current=p3;break;
                case 'Z': case 'z':
                    Finish(true);current=start;command='\0';break;
                case '\0': throw new InvalidDataException($"Path data contains numbers without a command: {d}");
                default: throw new NotSupportedException($"SVG path command '{command}' is not supported by this PoC yet. The scene model is independent of the parser, so additional commands can be added incrementally.");
            }
        }
        Finish();return result;
    }

    private static List<PointD> ParsePoints(string? text){if(string.IsNullOrWhiteSpace(text))return[];var n=Regex.Matches(text,@"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?",RegexOptions.CultureInvariant).Select(m=>Number(m.Value)).ToArray();var p=new List<PointD>(n.Length/2);for(var i=0;i+1<n.Length;i+=2)p.Add(new(n[i],n[i+1]));return p;}
    private static bool PointsAreClosed(IReadOnlyList<PointD> p){if(p.Count<3)return false;var dx=p[0].X-p[^1].X;var dy=p[0].Y-p[^1].Y;return dx*dx+dy*dy<=1e-12;}
    private static double StrokeWidth(XElement e){var direct=(string?)e.Attribute("stroke-width");if(double.TryParse(direct,NumberStyles.Float,CultureInfo.InvariantCulture,out var v))return Math.Max(0,v);var style=(string?)e.Attribute("style");if(!string.IsNullOrWhiteSpace(style))foreach(var declaration in style.Split(';',StringSplitOptions.RemoveEmptyEntries)){var pair=declaration.Split(':',2,StringSplitOptions.TrimEntries);if(pair.Length==2&&pair[0].Equals("stroke-width",StringComparison.OrdinalIgnoreCase)&&double.TryParse(pair[1],NumberStyles.Float,CultureInfo.InvariantCulture,out v))return Math.Max(0,v);}return 0;}
    private static PointD Cubic(PointD p0,PointD p1,PointD p2,PointD p3,double t){var mt=1-t;return new(mt*mt*mt*p0.X+3*mt*mt*t*p1.X+3*mt*t*t*p2.X+t*t*t*p3.X,mt*mt*mt*p0.Y+3*mt*mt*t*p1.Y+3*mt*t*t*p2.Y+t*t*t*p3.Y);}
    private static AffineTransform ParseTransform(string? text){if(string.IsNullOrWhiteSpace(text))return AffineTransform.Identity;var r=AffineTransform.Identity;foreach(Match m in TransformRegex.Matches(text)){var a=m.Groups["args"].Value.Split(new[]{',',' ','\t','\r','\n'},StringSplitOptions.RemoveEmptyEntries).Select(Number).ToArray();var next=m.Groups["name"].Value.ToLowerInvariant() switch{"translate" when a.Length>=1=>AffineTransform.Translation(a[0],a.Length>=2?a[1]:0),"matrix" when a.Length==6=>new AffineTransform(a[0],a[1],a[2],a[3],a[4],a[5]),_=>throw new NotSupportedException($"Unsupported SVG transform: {m.Value}")};r=r.Then(next);}return r;}
    private static bool IsInsideDefs(XElement e)=>e.Ancestors().Any(x=>x.Name.LocalName=="defs");private static bool IsCommand(string t)=>t.Length==1&&char.IsLetter(t[0]);
    private static double DoubleAttr(XElement e,string name)=>double.TryParse((string?)e.Attribute(name),NumberStyles.Float,CultureInfo.InvariantCulture,out var v)?v:0;private static double Number(string t)=>double.Parse(t,NumberStyles.Float,CultureInfo.InvariantCulture);
    private readonly record struct AffineTransform(double A,double B,double C,double D,double E,double F){public static AffineTransform Identity=>new(1,0,0,1,0,0);public static AffineTransform Translation(double x,double y)=>new(1,0,0,1,x,y);public PointD Apply(PointD p)=>new(A*p.X+C*p.Y+E,B*p.X+D*p.Y+F);public AffineTransform Then(AffineTransform n)=>new(n.A*A+n.C*B,n.B*A+n.D*B,n.A*C+n.C*D,n.B*C+n.D*D,n.A*E+n.C*F+n.E,n.B*E+n.D*F+n.F);}
}
