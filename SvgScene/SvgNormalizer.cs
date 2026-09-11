using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SvgMusic.Scene;

public interface ISvgNormalizer { GeometricScene Normalize(string fileName); }

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
            if (IsInsideDefs(element))
                continue;

            if (element.Name.LocalName == "use")
            {
                var href = (string?)element.Attribute("href")
                    ?? element.Attributes().FirstOrDefault(a => a.Name.LocalName == "href")?.Value;

                if (string.IsNullOrWhiteSpace(href)
                    || !href.StartsWith('#')
                    || !definitions.TryGetValue(href[1..], out var target))
                {
                    continue;
                }

                var placement = UsePlacementTransform(element);

                foreach (var source in GeometryElements(target))
                {
                    var relative = TransformFromElementToAncestor(source, target);
                    AddGeometry(
                        source,
                        "use",
                        href[1..],
                        relative.Then(placement),
                        element,
                        shapes,
                        ref no);
                }

                continue;
            }

            if (IsGeometryElement(element))
            {
                AddGeometry(
                    element,
                    element.Name.LocalName,
                    null,
                    FullTransform(element),
                    element,
                    shapes,
                    ref no);
            }
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
            if (string.IsNullOrWhiteSpace(d))
                return;

            var contours = TransformContours(ParseAndSamplePath(d), transform);
            if (contours.Count == 0)
                return;

            var points = contours.SelectMany(c => c.Points).ToList();

            shapes.Add(BuildShape(
                ++no,
                sourceKind,
                instanceElement,
                points,
                sourceId,
                contours.Count == 1 && contours[0].IsClosed,
                StrokeWidth(source),
                contours));

            return;
        }

        if (kind == "line")
        {
            var points = new List<PointD>
            {
                new(DoubleAttr(source, "x1"), DoubleAttr(source, "y1")),
                new(DoubleAttr(source, "x2"), DoubleAttr(source, "y2"))
            }.Select(transform.Apply).ToList();

            shapes.Add(BuildShape(
                ++no,
                sourceKind,
                instanceElement,
                points,
                sourceId,
                false,
                StrokeWidth(source),
                [new GeometricContour(points, false)]));

            return;
        }

        if (kind is "polyline" or "polygon")
        {
            var points = ParsePoints((string?)source.Attribute("points"));
            if (points.Count < 2)
                return;

            var closed = kind == "polygon";
            if (closed && points[^1] != points[0])
                points.Add(points[0]);

            points = points.Select(transform.Apply).ToList();

            shapes.Add(BuildShape(
                ++no,
                sourceKind,
                instanceElement,
                points,
                sourceId,
                closed,
                StrokeWidth(source),
                [new GeometricContour(points, closed)]));

            return;
        }

        if (kind == "rect")
        {
            var x = DoubleAttr(source, "x");
            var y = DoubleAttr(source, "y");
            var width = DoubleAttr(source, "width");
            var height = DoubleAttr(source, "height");

            if (width <= 0 || height <= 0)
                return;

            var points = new List<PointD>
            {
                new(x, y),
                new(x + width, y),
                new(x + width, y + height),
                new(x, y + height),
                new(x, y)
            }.Select(transform.Apply).ToList();

            shapes.Add(BuildShape(
                ++no,
                sourceKind,
                instanceElement,
                points,
                sourceId,
                true,
                StrokeWidth(source),
                [new GeometricContour(points, true)]));
        }
    }

    private static IEnumerable<XElement> GeometryElements(XElement target)
    {
        if (IsGeometryElement(target))
            yield return target;

        foreach (var element in target.Descendants())
        {
            if (IsGeometryElement(element)
                && !element.Ancestors()
                    .TakeWhile(a => a != target)
                    .Any(a => a.Name.LocalName == "defs"))
            {
                yield return element;
            }
        }
    }

    private static bool IsGeometryElement(XElement element) =>
        element.Name.LocalName is "path" or "line" or "polyline" or "polygon" or "rect";

    private static AffineTransform FullTransform(XElement element)
    {
        var result = AffineTransform.Identity;

        for (XElement? current = element; current is not null; current = current.Parent)
            result = result.Then(ParseTransform((string?)current.Attribute("transform")));

        return result;
    }

    private static AffineTransform UsePlacementTransform(XElement use)
    {
        var result = AffineTransform.Translation(
            DoubleAttr(use, "x"),
            DoubleAttr(use, "y"));

        result = result.Then(ParseTransform((string?)use.Attribute("transform")));

        for (var current = use.Parent; current is not null; current = current.Parent)
            result = result.Then(ParseTransform((string?)current.Attribute("transform")));

        return result;
    }

    private static AffineTransform TransformFromElementToAncestor(
        XElement element,
        XElement ancestorInclusive)
    {
        var result = AffineTransform.Identity;

        for (XElement? current = element; current is not null; current = current.Parent)
        {
            result = result.Then(ParseTransform((string?)current.Attribute("transform")));
            if (current == ancestorInclusive)
                break;
        }

        return result;
    }

    private static List<GeometricContour> TransformContours(
        IReadOnlyList<GeometricContour> contours,
        AffineTransform transform) =>
        contours
            .Select(c => new GeometricContour(
                c.Points.Select(transform.Apply).ToList(),
                c.IsClosed))
            .ToList();

    private static GeometricShape BuildShape(
        int number,
        string kind,
        XElement element,
        IReadOnlyList<PointD> points,
        string? sourceId = null,
        bool isClosed = false,
        double strokeWidth = 0,
        IReadOnlyList<GeometricContour>? contours = null) =>
        new(
            $"shape-{number}",
            kind,
            points,
            BoundsD.FromPoints(points),
            sourceId,
            (string?)element.Attribute("data-index"),
            isClosed,
            strokeWidth,
            contours);

    private IReadOnlyList<GeometricContour> ParseAndSamplePath(string d)
    {
        var tokens = PathTokenRegex.Matches(d)
            .Select(match => match.Value)
            .ToList();

        var result = new List<GeometricContour>();
        List<PointD>? points = null;

        var i = 0;
        var current = new PointD(0, 0);
        var start = current;
        var command = '\0';
        var previousCommand = '\0';
        PointD? lastCubicControl = null;
        PointD? lastQuadraticControl = null;

        void Finish(bool closed = false)
        {
            // A trailing "M x y" after Z is common in generated SVGs. It moves
            // the pen but draws nothing, so it is not a geometric contour.
            if (points is null || points.Count < 2)
            {
                points = null;
                return;
            }

            if (closed && points[^1] != start)
                points.Add(start);

            result.Add(new GeometricContour(
                points,
                closed || PointsAreClosed(points)));

            points = null;
        }

        PointD ReadPoint(bool relative)
        {
            var point = new PointD(
                Number(tokens[i++]),
                Number(tokens[i++]));

            return relative
                ? new PointD(current.X + point.X, current.Y + point.Y)
                : point;
        }

        void AddLine(PointD point)
        {
            points ??= [];
            current = point;
            points.Add(current);
            lastCubicControl = null;
            lastQuadraticControl = null;
        }

        void AddCubic(PointD control1, PointD control2, PointD end)
        {
            points ??= [];
            var begin = current;

            for (var sample = 1; sample <= _samplesPerCurve; sample++)
            {
                var t = sample / (double)_samplesPerCurve;
                points.Add(Cubic(begin, control1, control2, end, t));
            }

            current = end;
            lastCubicControl = control2;
            lastQuadraticControl = null;
        }

        void AddQuadratic(PointD control, PointD end)
        {
            points ??= [];
            var begin = current;

            for (var sample = 1; sample <= _samplesPerCurve; sample++)
            {
                var t = sample / (double)_samplesPerCurve;
                points.Add(Quadratic(begin, control, end, t));
            }

            current = end;
            lastQuadraticControl = control;
            lastCubicControl = null;
        }

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
                case 'm':
                {
                    var relative = command == 'm';
                    Finish();

                    current = ReadPoint(relative);
                    start = current;
                    points = [current];
                    lastCubicControl = null;
                    lastQuadraticControl = null;
                    previousCommand = command;

                    // Additional coordinate pairs after moveto are implicit lineto.
                    command = relative ? 'l' : 'L';
                    break;
                }

                case 'L':
                case 'l':
                {
                    AddLine(ReadPoint(command == 'l'));
                    previousCommand = command;
                    break;
                }

                case 'H':
                case 'h':
                {
                    var x = Number(tokens[i++]);
                    if (command == 'h')
                        x += current.X;

                    AddLine(new PointD(x, current.Y));
                    previousCommand = command;
                    break;
                }

                case 'V':
                case 'v':
                {
                    var y = Number(tokens[i++]);
                    if (command == 'v')
                        y += current.Y;

                    AddLine(new PointD(current.X, y));
                    previousCommand = command;
                    break;
                }

                case 'C':
                case 'c':
                {
                    var relative = command == 'c';
                    var control1 = ReadPoint(relative);
                    var control2 = ReadPoint(relative);
                    var end = ReadPoint(relative);

                    AddCubic(control1, control2, end);
                    previousCommand = command;
                    break;
                }

                case 'S':
                case 's':
                {
                    var relative = command == 's';
                    var control1 = previousCommand is 'C' or 'c' or 'S' or 's'
                        ? Reflect(lastCubicControl ?? current, current)
                        : current;

                    var control2 = ReadPoint(relative);
                    var end = ReadPoint(relative);

                    AddCubic(control1, control2, end);
                    previousCommand = command;
                    break;
                }

                case 'Q':
                case 'q':
                {
                    var relative = command == 'q';
                    var control = ReadPoint(relative);
                    var end = ReadPoint(relative);

                    AddQuadratic(control, end);
                    previousCommand = command;
                    break;
                }

                case 'T':
                case 't':
                {
                    var control = previousCommand is 'Q' or 'q' or 'T' or 't'
                        ? Reflect(lastQuadraticControl ?? current, current)
                        : current;

                    var end = ReadPoint(command == 't');

                    AddQuadratic(control, end);
                    previousCommand = command;
                    break;
                }

                case 'Z':
                case 'z':
                {
                    Finish(true);
                    current = start;
                    lastCubicControl = null;
                    lastQuadraticControl = null;
                    previousCommand = command;
                    command = '\0';
                    break;
                }

                case '\0':
                    throw new InvalidDataException(
                        $"Path data contains numbers without a command: {d}");

                default:
                    throw new NotSupportedException(
                        $"SVG path command '{command}' is not supported by this PoC yet.");
            }
        }

        Finish();
        return result;
    }

    private static List<PointD> ParsePoints(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var numbers = Regex.Matches(
                text,
                @"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?",
                RegexOptions.CultureInvariant)
            .Select(match => Number(match.Value))
            .ToArray();

        var points = new List<PointD>(numbers.Length / 2);

        for (var i = 0; i + 1 < numbers.Length; i += 2)
            points.Add(new PointD(numbers[i], numbers[i + 1]));

        return points;
    }

    private static bool PointsAreClosed(IReadOnlyList<PointD> points)
    {
        if (points.Count < 3)
            return false;

        var dx = points[0].X - points[^1].X;
        var dy = points[0].Y - points[^1].Y;

        return dx * dx + dy * dy <= 1e-12;
    }

    private static double StrokeWidth(XElement element)
    {
        var direct = (string?)element.Attribute("stroke-width");

        if (double.TryParse(
            direct,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var value))
        {
            return Math.Max(0, value);
        }

        var style = (string?)element.Attribute("style");

        if (!string.IsNullOrWhiteSpace(style))
        {
            foreach (var declaration in style.Split(
                ';',
                StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = declaration.Split(
                    ':',
                    2,
                    StringSplitOptions.TrimEntries);

                if (pair.Length == 2
                    && pair[0].Equals(
                        "stroke-width",
                        StringComparison.OrdinalIgnoreCase)
                    && double.TryParse(
                        pair[1],
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out value))
                {
                    return Math.Max(0, value);
                }
            }
        }

        return 0;
    }

    private static PointD Cubic(
        PointD p0,
        PointD p1,
        PointD p2,
        PointD p3,
        double t)
    {
        var mt = 1 - t;

        return new PointD(
            mt * mt * mt * p0.X
            + 3 * mt * mt * t * p1.X
            + 3 * mt * t * t * p2.X
            + t * t * t * p3.X,
            mt * mt * mt * p0.Y
            + 3 * mt * mt * t * p1.Y
            + 3 * mt * t * t * p2.Y
            + t * t * t * p3.Y);
    }

    private static PointD Quadratic(
        PointD p0,
        PointD p1,
        PointD p2,
        double t)
    {
        var mt = 1 - t;

        return new PointD(
            mt * mt * p0.X + 2 * mt * t * p1.X + t * t * p2.X,
            mt * mt * p0.Y + 2 * mt * t * p1.Y + t * t * p2.Y);
    }

    private static PointD Reflect(PointD point, PointD around) =>
        new(
            2 * around.X - point.X,
            2 * around.Y - point.Y);

    private static AffineTransform ParseTransform(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return AffineTransform.Identity;

        var result = AffineTransform.Identity;

        foreach (Match match in TransformRegex.Matches(text))
        {
            var args = match.Groups["args"].Value
                .Split(
                    new[] { ',', ' ', '\t', '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries)
                .Select(Number)
                .ToArray();

            var next = match.Groups["name"].Value.ToLowerInvariant() switch
            {
                "translate" when args.Length >= 1 =>
                    AffineTransform.Translation(
                        args[0],
                        args.Length >= 2 ? args[1] : 0),

                "matrix" when args.Length == 6 =>
                    new AffineTransform(
                        args[0],
                        args[1],
                        args[2],
                        args[3],
                        args[4],
                        args[5]),

                _ => throw new NotSupportedException(
                    $"Unsupported SVG transform: {match.Value}")
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
        double.TryParse(
            (string?)element.Attribute(name),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : 0;

    private static double Number(string token) =>
        double.Parse(
            token,
            NumberStyles.Float,
            CultureInfo.InvariantCulture);

    private readonly record struct AffineTransform(
        double A,
        double B,
        double C,
        double D,
        double E,
        double F)
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
