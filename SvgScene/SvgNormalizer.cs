using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SvgMusic.Scene;

public interface ISvgNormalizer
{
    GeometricScene Normalize(string fileName);
}

public sealed class SvgNormalizer : ISvgNormalizer
{
    private static readonly Regex PathTokenRegex = new(
        @"[A-Za-z]|[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TransformRegex = new(
        @"(?<name>matrix|translate)\s*\((?<args>[^)]*)\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly int _samplesPerCurve;

    public SvgNormalizer(int samplesPerCurve = 12)
    {
        _samplesPerCurve = samplesPerCurve;
    }

    public GeometricScene Normalize(string fileName)
    {
        var doc = XDocument.Load(fileName);
        var root = doc.Root ?? throw new InvalidDataException("SVG root element is missing.");

        var definitions = root
            .Descendants()
            .Where(x => x.Name.LocalName == "path" && x.Attribute("id") is not null)
            .ToDictionary(
                x => (string)x.Attribute("id")!,
                x => (string?)x.Attribute("d") ?? string.Empty,
                StringComparer.Ordinal);

        var shapes = new List<GeometricShape>();
        var shapeNo = 0;

        foreach (var element in root.Descendants())
        {
            if (IsInsideDefs(element)) continue;

            var localName = element.Name.LocalName;
            if (localName == "path")
            {
                var d = (string?)element.Attribute("d");
                if (string.IsNullOrWhiteSpace(d)) continue;

                var points = ParseAndSamplePath(d);
                var transform = ParseTransform((string?)element.Attribute("transform"));
                points = points.Select(transform.Apply).ToList();

                shapes.Add(BuildShape(++shapeNo, "path", element, points));
            }
            else if (localName == "use")
            {
                var href = (string?)element.Attribute("href")
                    ?? element.Attributes().FirstOrDefault(a => a.Name.LocalName == "href")?.Value;

                if (string.IsNullOrWhiteSpace(href) || !href.StartsWith('#')) continue;
                if (!definitions.TryGetValue(href[1..], out var d) || string.IsNullOrWhiteSpace(d)) continue;

                var points = ParseAndSamplePath(d);

                var x = DoubleAttr(element, "x");
                var y = DoubleAttr(element, "y");
                var transform = AffineTransform.Translation(x, y)
                    .Then(ParseTransform((string?)element.Attribute("transform")));

                points = points.Select(transform.Apply).ToList();

                shapes.Add(BuildShape(++shapeNo, "use", element, points, href[1..]));
            }
        }

        return new GeometricScene(shapes);
    }

    private static GeometricShape BuildShape(
        int number,
        string sourceKind,
        XElement element,
        IReadOnlyList<PointD> points,
        string? sourceId = null)
    {
        return new GeometricShape(
            $"shape-{number}",
            sourceKind,
            points,
            BoundsD.FromPoints(points),
            sourceId,
            (string?)element.Attribute("data-index"));
    }

    private IReadOnlyList<PointD> ParseAndSamplePath(string d)
    {
        var tokens = PathTokenRegex.Matches(d).Select(m => m.Value).ToList();
        var points = new List<PointD>();
        var i = 0;
        var current = new PointD(0, 0);
        var start = current;
        char command = '\0';

        while (i < tokens.Count)
        {
            if (IsCommand(tokens[i]))
            {
                command = tokens[i][0];
                i++;
            }

            switch (command)
            {
                case 'M':
                    current = new PointD(Number(tokens[i++]), Number(tokens[i++]));
                    start = current;
                    points.Add(current);
                    command = 'L';
                    break;

                case 'L':
                    current = new PointD(Number(tokens[i++]), Number(tokens[i++]));
                    points.Add(current);
                    break;

                case 'C':
                {
                    var p0 = current;
                    var p1 = new PointD(Number(tokens[i++]), Number(tokens[i++]));
                    var p2 = new PointD(Number(tokens[i++]), Number(tokens[i++]));
                    var p3 = new PointD(Number(tokens[i++]), Number(tokens[i++]));

                    for (var sample = 1; sample <= _samplesPerCurve; sample++)
                    {
                        var t = sample / (double)_samplesPerCurve;
                        points.Add(Cubic(p0, p1, p2, p3, t));
                    }

                    current = p3;
                    break;
                }

                case 'Z':
                case 'z':
                    if (points.Count > 0 && points[^1] != start)
                        points.Add(start);
                    command = '\0';
                    break;

                case '\0':
                    throw new InvalidDataException($"Path data contains numbers without a command: {d}");

                default:
                    throw new NotSupportedException(
                        $"SVG path command '{command}' is not supported by this PoC yet. " +
                        "The scene model is independent of the parser, so additional commands can be added incrementally.");
            }
        }

        return points;
    }

    private static PointD Cubic(PointD p0, PointD p1, PointD p2, PointD p3, double t)
    {
        var mt = 1.0 - t;
        var a = mt * mt * mt;
        var b = 3.0 * mt * mt * t;
        var c = 3.0 * mt * t * t;
        var d = t * t * t;

        return new PointD(
            a * p0.X + b * p1.X + c * p2.X + d * p3.X,
            a * p0.Y + b * p1.Y + c * p2.Y + d * p3.Y);
    }

    private static AffineTransform ParseTransform(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return AffineTransform.Identity;

        var result = AffineTransform.Identity;
        foreach (Match match in TransformRegex.Matches(text))
        {
            var args = match.Groups["args"].Value
                .Split(new[] { ',', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(Number)
                .ToArray();

            var next = match.Groups["name"].Value.ToLowerInvariant() switch
            {
                "translate" when args.Length >= 1 =>
                    AffineTransform.Translation(args[0], args.Length >= 2 ? args[1] : 0),

                "matrix" when args.Length == 6 =>
                    new AffineTransform(args[0], args[1], args[2], args[3], args[4], args[5]),

                _ => throw new NotSupportedException($"Unsupported SVG transform: {match.Value}")
            };

            result = result.Then(next);
        }

        return result;
    }

    private static bool IsInsideDefs(XElement element) =>
        element.Ancestors().Any(x => x.Name.LocalName == "defs");

    private static bool IsCommand(string token) =>
        token.Length == 1 && char.IsLetter(token[0]);

    private static double DoubleAttr(XElement element, string name) =>
        double.TryParse((string?)element.Attribute(name),
            NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    private static double Number(string text) =>
        double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);

    private readonly record struct AffineTransform(
        double A, double B, double C, double D, double E, double F)
    {
        public static AffineTransform Identity => new(1, 0, 0, 1, 0, 0);

        public static AffineTransform Translation(double x, double y) =>
            new(1, 0, 0, 1, x, y);

        public PointD Apply(PointD point) =>
            new(
                A * point.X + C * point.Y + E,
                B * point.X + D * point.Y + F);

        public AffineTransform Then(AffineTransform next) =>
            new(
                next.A * A + next.C * B,
                next.B * A + next.D * B,
                next.A * C + next.C * D,
                next.B * C + next.D * D,
                next.A * E + next.C * F + next.E,
                next.B * E + next.D * F + next.F);
    }
}
