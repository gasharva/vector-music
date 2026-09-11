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
        var doc = XDocument.Load(fileName);
        var root = doc.Root ?? throw new InvalidDataException("SVG root element is missing.");

        // <use> may reference a path directly, but it may equally well reference a
        // <g>, <symbol>, etc. Keep an element map rather than making assumptions
        // about how a particular SVG producer organises its <defs> section.
        var definitions = root.Descendants()
            .Where(x => x.Attribute("id") is not null)
            .GroupBy(x => (string)x.Attribute("id")!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var shapes = new List<GeometricShape>();
        var no = 0;

        foreach (var element in root.Descendants())
        {
            if (IsInsideDefs(element)) continue;

            if (element.Name.LocalName == "use")
            {
                var href = (string?)element.Attribute("href") ?? element.Attributes().FirstOrDefault(a => a.Name.LocalName == "href")?.Value;
                if (string.IsNullOrWhiteSpace(href) || !href.StartsWith('#') || !definitions.TryGetValue(href[1..], out var target))
                    continue;

                var placement = UsePlacementTransform(element);
                foreach (var source in GeometryElements(target))
                {
                    var relative = TransformFromElementToAncestor(source, target);
                    AddGeometry(source, "use", href[1..], relative.Then(placement), element, shapes, ref no);
                }
                continue;
            }

            if (IsGeometryElement(element))
                AddGeometry(element, element.Name.LocalName, null, FullTransform(element), element, shapes, ref no);
        }

        return new GeometricScene(shapes);
    }

    private void AddGeometry(
        XElement source,
        string sourceKind,
        string? sourceId,
        AffineTransform transform,
        XElement instanceElement,
        List<GeometricShape> shapes,
        ref int no)
    {
        var kind = source.Name.LocalName;

        if (kind == "path")
        {
            var d = (string?)source.Attribute("d");
            if (string.IsNullOrWhiteSpace(d)) return;
            var contours = TransformContours(ParseAndSamplePath(d), transform);
            if (contours.Count == 0) return;
            var points = contours.SelectMany(c => c.Points).ToList();
            shapes.Add(BuildShape(++no, sourceKind, instanceElement, points, sourceId,
                contours.Count == 1 && contours[0].IsClosed, StrokeWidth(source), contours));
            return;
        }

        if (kind == "line")
        {
            var p = new List<PointD>
            {
                new(DoubleAttr(source, "x1"), DoubleAttr(source, "y1")),
                new(DoubleAttr(source, "x2"), DoubleAttr(source, "y2"))
            }.Select(transform.Apply).ToList();
            shapes.Add(BuildShape(++no, sourceKind, instanceElement, p, sourceId, false, StrokeWidth(source), [new GeometricContour(p, false)]));
            return;
        }

        if (kind is "polyline" or "polygon")
        {
            var p = ParsePoints((string?)source.Attribute("points"));
            if (p.Count < 2) return;
            var closed = kind == "polygon";
            if (closed && p[^1] != p[0]) p.Add(p[0]);
            p = p.Select(transform.Apply).ToList();
            shapes.Add(BuildShape(++no, sourceKind, instanceElement, p, sourceId, closed, StrokeWidth(source), [new GeometricContour(p, closed)]));
            return;
        }

        if (kind == "rect")
        {
            var x = DoubleAttr(source, "x");
            var y = DoubleAttr(source, "y");
            var w = DoubleAttr(source, "width");
            var h = DoubleAttr(source, "height");
            if (w <= 0 || h <= 0) return;
            var p = new List<PointD> { new(x,y), new(x+w,y), new(x+w,y+h), new(x,y+h), new(x,y) }
                .Select(transform.Apply).ToList();
            shapes.Add(BuildShape(++no, sourceKind, instanceElement, p, sourceId, true, StrokeWidth(source), [new GeometricContour(p, true)]));
        }
    }

    private static IEnumerable<XElement> GeometryElements(XElement target)
    {
        if (IsGeometryElement(target)) yield return target;
        foreach (var e in target.Descendants())
            if (IsGeometryElement(e) && !e.Ancestors().TakeWhile(a => a != target).Any(a => a.Name.LocalName == "defs"))
                yield return e;
    }

    private static bool IsGeometryElement(XElement e) => e.Name.LocalName is "path" or "line" or "polyline" or "polygon" or "rect";

    private static AffineTransform FullTransform(XElement element)
    {
        var result = AffineTransform.Identity;
        for (XElement? e = element; e is not null; e = e.Parent)
            result = result.Then(ParseTransform((string?)e.Attribute("transform")));
        return result;
    }

    private static AffineTransform UsePlacementTransform(XElement use)
    {
        var result = AffineTransform.Translation(DoubleAttr(use, "x"), DoubleAttr(use, "y"));
        result = result.Then(ParseTransform((string?)use.Attribute("transform")));
        for (var e = use.Parent; e is not null; e = e.Parent)
            result = result.Then(ParseTransform((string?)e.Attribute("transform")));
        return result;
    }

    private static AffineTransform TransformFromElementToAncestor(XElement element, XElement ancestorInclusive)
    {
        var result = AffineTransform.Identity;
        for (XElement? e = element; e is not null; e = e.Parent)
        {
            result = result.Then(ParseTransform((string?)e.Attribute("transform")));
            if (e == ancestorInclusive) break;
        }
        return result;
    }

    private static List<GeometricContour> TransformContours(IReadOnlyList<GeometricContour> contours, AffineTransform t) =>
        contours.Select(c => new GeometricContour(c.Points.Select(t.Apply).ToList(), c.IsClosed)).ToList();

    private static GeometricShape BuildShape(int number, string kind, XElement element, IReadOnlyList<PointD> points,
        string? sourceId = null, bool isClosed = false, double strokeWidth = 0, IReadOnlyList<GeometricContour>? contours = null) =>
        new($"shape-{number}", kind, points, BoundsD.FromPoints(points), sourceId, (string?)element.Attribute("data-index"), isClosed, strokeWidth, contours);

    private IReadOnlyList<GeometricContour> ParseAndSamplePath(string d)
    {
        var tokens = PathTokenRegex.Matches(d).Select(m => m.Value).ToList();
        var result = new List<GeometricContour>();
        List<PointD>? points = null;
        var i = 0;
        var current = new PointD(0, 0);
        var start = current;
        char command = '\0';

        void Finish(bool closed = false)
        {
            // A trailing "M x y" after Z is common in generated SVGs. It moves
            // the pen but draws nothing, so it is not a geometric contour.
            if (points is null || points.Count < 2) { points = null; return; }
            if (closed && points[^1] != start) points.Add(start);
            result.Add(new GeometricContour(points, closed || PointsAreClosed(points)));
            points = null;
        }

        while (i < tokens.Count)
        {
            if (IsCommand(tokens[i])) { command = tokens[i][0]; i++; }
            switch (command)
            {
                case 'M':
                    Finish(); current = new PointD(Number(tokens[i++]), Number(tokens[i++])); start = current; points = [current]; command = 'L'; break;
                case 'L':
                    points ??= []; current = new PointD(Number(tokens[i++]), Number(tokens[i++])); points.Add(current); break;
                case 'C':
                    points ??= []; var p0 = current; var p1 = new PointD(Number(tokens[i++]), Number(tokens[i++])); var p2 = new PointD(Number(tokens[i++]), Number(tokens[i++])); var p3 = new PointD(Number(tokens[i++]), Number(tokens[i++]));
                    for (var s = 1; s <= _samplesPerCurve; s++) { var t = s / (double)_samplesPerCurve; points.Add(Cubic(p0, p1, p2, p3, t)); }
                    current = p3; break;
                case 'Z': case 'z':
                    Finish(true); current = start; command = '\0'; break;
                case '\0': throw new InvalidDataException($"Path data contains numbers without a command: {d}");
                default: throw new NotSupportedException($"SVG path command '{command}' is not supported by this PoC yet.");
            }
        }
        Finish();
        return result;
    }

    private static List<PointD> ParsePoints(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var n = Regex.Matches(text, @"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?", RegexOptions.CultureInvariant).Select(m => Number(m.Value)).ToArray();
        var p = new List<PointD>(n.Length / 2);
        for (var i = 0; i + 1 < n.Length; i += 2) p.Add(new(n[i], n[i + 1]));
        return p;
    }

    private static bool PointsAreClosed(IReadOnlyList<PointD> p)
    {
        if (p.Count < 3) return false;
        var dx = p[0].X - p[^1].X; var dy = p[0].Y - p[^1].Y;
        return dx * dx + dy * dy <= 1e-12;
    }

    private static double StrokeWidth(XElement e)
    {
        var direct = (string?)e.Attribute("stroke-width");
        if (double.TryParse(direct, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return Math.Max(0, v);
        var style = (string?)e.Attribute("style");
        if (!string.IsNullOrWhiteSpace(style))
            foreach (var declaration in style.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = declaration.Split(':', 2, StringSplitOptions.TrimEntries);
                if (pair.Length == 2 && pair[0].Equals("stroke-width", StringComparison.OrdinalIgnoreCase) && double.TryParse(pair[1], NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return Math.Max(0, v);
            }
        return 0;
    }

    private static PointD Cubic(PointD p0, PointD p1, PointD p2, PointD p3, double t)
    {
        var mt = 1 - t;
        return new(mt*mt*mt*p0.X + 3*mt*mt*t*p1.X + 3*mt*t*t*p2.X + t*t*t*p3.X,
                   mt*mt*mt*p0.Y + 3*mt*mt*t*p1.Y + 3*mt*t*t*p2.Y + t*t*t*p3.Y);
    }

    private static AffineTransform ParseTransform(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return AffineTransform.Identity;
        var r = AffineTransform.Identity;
        foreach (Match m in TransformRegex.Matches(text))
        {
            var a = m.Groups["args"].Value.Split(new[]{',',' ','\t','\r','\n'}, StringSplitOptions.RemoveEmptyEntries).Select(Number).ToArray();
            var next = m.Groups["name"].Value.ToLowerInvariant() switch
            {
                "translate" when a.Length >= 1 => AffineTransform.Translation(a[0], a.Length >= 2 ? a[1] : 0),
                "matrix" when a.Length == 6 => new AffineTransform(a[0], a[1], a[2], a[3], a[4], a[5]),
                _ => throw new NotSupportedException($"Unsupported SVG transform: {m.Value}")
            };
            r = r.Then(next);
        }
        return r;
    }

    private static bool IsInsideDefs(XElement e) => e.Ancestors().Any(x => x.Name.LocalName == "defs");
    private static bool IsCommand(string t) => t.Length == 1 && char.IsLetter(t[0]);
    private static double DoubleAttr(XElement e, string name) => double.TryParse((string?)e.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    private static double Number(string t) => double.Parse(t, NumberStyles.Float, CultureInfo.InvariantCulture);

    private readonly record struct AffineTransform(double A, double B, double C, double D, double E, double F)
    {
        public static AffineTransform Identity => new(1,0,0,1,0,0);
        public static AffineTransform Translation(double x,double y) => new(1,0,0,1,x,y);
        public PointD Apply(PointD p) => new(A*p.X + C*p.Y + E, B*p.X + D*p.Y + F);
        public AffineTransform Then(AffineTransform n) => new(n.A*A+n.C*B, n.B*A+n.D*B, n.A*C+n.C*D, n.B*C+n.D*D, n.A*E+n.C*F+n.E, n.B*E+n.D*F+n.F);
    }
}
