using System.Globalization;
using System.Xml.Linq;

namespace SvgMusic.Scene;

public sealed class LogicalOwnershipDebugRenderer
{
    private static readonly string[] Palette =
    [
        "#d32f2f",
        "#1976d2",
        "#388e3c",
        "#f57c00",
        "#7b1fa2",
        "#00838f",
        "#5d4037",
        "#455a64",
        "#c2185b",
        "#00796b",
        "#512da8",
        "#689f38"
    ];

    public void Render(
        string input,
        GeometricScene geometry,
        NotationScene notation,
        ScoreLayout layout,
        LogicalOwnershipScene ownership,
        string output)
    {
        new ScoreLayoutDebugRenderer().Render(input, layout, output);

        var document = XDocument.Load(output, LoadOptions.PreserveWhitespace);
        var root = document.Root
            ?? throw new InvalidOperationException("SVG has no root element.");

        var ns = root.Name.Namespace;
        var unit = DebugUnit(root);
        var colors = BuildColorMap(layout);
        var coordinateCenters = BuildCoordinateCenters(layout);
        var shapesById = geometry.Shapes.ToDictionary(
            shape => shape.Id,
            StringComparer.Ordinal);
        var assignments = ownership.Assignments.ToDictionary(
            assignment => assignment.ShapeId,
            StringComparer.Ordinal);

        var defs = new XElement(ns + "defs");
        var group = new XElement(
            ns + "g",
            new XAttribute("id", "debug-logical-ownership"),
            new XAttribute("pointer-events", "none"));

        foreach (var stroke in notation.Strokes)
        {
            if (!assignments.TryGetValue(stroke.ShapeId, out var assignment))
            {
                continue;
            }

            var paint = ResolvePaint(
                assignment.Ownership,
                colors,
                coordinateCenters,
                defs,
                ns,
                stroke.ShapeId);

            group.Add(new XElement(
                ns + "line",
                new XAttribute("x1", F(stroke.Start.X)),
                new XAttribute("y1", F(stroke.Start.Y)),
                new XAttribute("x2", F(stroke.End.X)),
                new XAttribute("y2", F(stroke.End.Y)),
                new XAttribute("stroke", paint),
                new XAttribute("stroke-width", F(Math.Max(stroke.Width + unit, 1.7 * unit))),
                new XAttribute("stroke-linecap", "round"),
                new XAttribute("opacity", F(Opacity(assignment.Ownership)))));
        }

        foreach (var curve in notation.CurvedStrokes)
        {
            if (curve.Centerline.Count < 2
                || !assignments.TryGetValue(curve.ShapeId, out var assignment))
            {
                continue;
            }

            var paint = ResolvePaint(
                assignment.Ownership,
                colors,
                coordinateCenters,
                defs,
                ns,
                curve.ShapeId);
            var pathData = "M "
                + F(curve.Centerline[0].X)
                + " "
                + F(curve.Centerline[0].Y)
                + string.Concat(curve.Centerline
                    .Skip(1)
                    .Select(point => $" L {F(point.X)} {F(point.Y)}"));

            group.Add(new XElement(
                ns + "path",
                new XAttribute("d", pathData),
                new XAttribute("fill", "none"),
                new XAttribute("stroke", paint),
                new XAttribute("stroke-width", F(2.1 * unit)),
                new XAttribute("stroke-linecap", "round"),
                new XAttribute("stroke-linejoin", "round"),
                new XAttribute("opacity", F(Opacity(assignment.Ownership)))));
        }

        foreach (var ellipse in notation.Ellipses)
        {
            if (!assignments.TryGetValue(ellipse.ShapeId, out var assignment))
            {
                continue;
            }

            var paint = ResolvePaint(
                assignment.Ownership,
                colors,
                coordinateCenters,
                defs,
                ns,
                ellipse.ShapeId);
            var degrees = ellipse.Rotation * 180.0 / Math.PI;

            group.Add(new XElement(
                ns + "ellipse",
                new XAttribute("cx", F(ellipse.Center.X)),
                new XAttribute("cy", F(ellipse.Center.Y)),
                new XAttribute("rx", F(ellipse.MajorRadius)),
                new XAttribute("ry", F(ellipse.MinorRadius)),
                new XAttribute(
                    "transform",
                    $"rotate({F(degrees)} {F(ellipse.Center.X)} {F(ellipse.Center.Y)})"),
                new XAttribute("fill", ellipse.IsHollow ? "none" : paint),
                new XAttribute("fill-opacity", ellipse.IsHollow ? "0" : F(Opacity(assignment.Ownership))),
                new XAttribute("stroke", paint),
                new XAttribute("stroke-width", F(1.4 * unit)),
                new XAttribute("opacity", F(Opacity(assignment.Ownership)))));
        }

        foreach (var instance in notation.Instances)
        {
            if (!assignments.TryGetValue(instance.ShapeId, out var assignment)
                || !shapesById.TryGetValue(instance.ShapeId, out var shape))
            {
                continue;
            }

            var paint = ResolvePaint(
                assignment.Ownership,
                colors,
                coordinateCenters,
                defs,
                ns,
                instance.ShapeId);

            foreach (var contour in shape.EffectiveContours)
            {
                if (contour.Points.Count < 2)
                {
                    continue;
                }

                group.Add(new XElement(
                    ns + "path",
                    new XAttribute("d", BuildPathData(contour)),
                    new XAttribute("fill", contour.IsClosed ? paint : "none"),
                    new XAttribute("fill-opacity", contour.IsClosed ? F(Opacity(assignment.Ownership) * 0.72) : "0"),
                    new XAttribute("stroke", paint),
                    new XAttribute("stroke-width", F(Math.Max(unit, shape.StrokeWidth))),
                    new XAttribute("stroke-linecap", "round"),
                    new XAttribute("stroke-linejoin", "round"),
                    new XAttribute("opacity", F(Opacity(assignment.Ownership)))));
            }
        }

        if (defs.HasElements)
        {
            root.AddFirst(defs);
        }

        root.Add(group);
        document.Save(output, SaveOptions.DisableFormatting);
    }

    private static string ResolvePaint(
        LogicalOwnership ownership,
        IReadOnlyDictionary<LogicalCoordinate, string> colors,
        IReadOnlyDictionary<LogicalCoordinate, PointD> centers,
        XElement defs,
        XNamespace ns,
        string shapeId)
    {
        var startColor = colors.GetValueOrDefault(ownership.Start, "#616161");

        if (!ownership.IsSpan)
        {
            return startColor;
        }

        var endColor = colors.GetValueOrDefault(ownership.End, startColor);
        var start = centers.GetValueOrDefault(ownership.Start, new PointD(0, 0));
        var end = centers.GetValueOrDefault(ownership.End, start);
        var gradientId = "ownership-gradient-" + MakeSafeId(shapeId);

        defs.Add(new XElement(
            ns + "linearGradient",
            new XAttribute("id", gradientId),
            new XAttribute("gradientUnits", "userSpaceOnUse"),
            new XAttribute("x1", F(start.X)),
            new XAttribute("y1", F(start.Y)),
            new XAttribute("x2", F(end.X)),
            new XAttribute("y2", F(end.Y)),
            new XElement(
                ns + "stop",
                new XAttribute("offset", "0%"),
                new XAttribute("stop-color", startColor)),
            new XElement(
                ns + "stop",
                new XAttribute("offset", "100%"),
                new XAttribute("stop-color", endColor))));

        return $"url(#{gradientId})";
    }

    private static IReadOnlyDictionary<LogicalCoordinate, string> BuildColorMap(
        ScoreLayout layout)
    {
        var result = new Dictionary<LogicalCoordinate, string>();
        var regionIndex = 0;

        foreach (var system in layout.Systems)
        {
            foreach (var pair in system.StaffPairs)
            {
                foreach (var measure in pair.Measures)
                {
                    result[new LogicalCoordinate(pair.UpperStaffId, measure.Id)] =
                        Palette[regionIndex++ % Palette.Length];
                    result[new LogicalCoordinate(pair.LowerStaffId, measure.Id)] =
                        Palette[regionIndex++ % Palette.Length];
                }
            }
        }

        return result;
    }

    private static IReadOnlyDictionary<LogicalCoordinate, PointD> BuildCoordinateCenters(
        ScoreLayout layout)
    {
        var result = new Dictionary<LogicalCoordinate, PointD>();
        var staffs = layout.Staffs.ToDictionary(staff => staff.Id, StringComparer.Ordinal);

        foreach (var system in layout.Systems)
        {
            foreach (var pair in system.StaffPairs)
            {
                foreach (var measure in pair.Measures)
                {
                    foreach (var staffId in new[] { pair.UpperStaffId, pair.LowerStaffId })
                    {
                        var staff = staffs[staffId];
                        result[new LogicalCoordinate(staffId, measure.Id)] = new PointD(
                            (measure.XStart + measure.XEnd) / 2.0,
                            staff.Bounds.CenterY);
                    }
                }
            }
        }

        return result;
    }

    private static double Opacity(LogicalOwnership ownership)
    {
        if (ownership.Reason == "BetweenStaffBridge")
        {
            return 0.78;
        }

        return ownership.Generation switch
        {
            1 => 0.90,
            2 => 0.62,
            3 => 0.38,
            _ => 0.30
        };
    }

    private static string BuildPathData(GeometricContour contour)
    {
        var first = contour.Points[0];
        var data = "M " + F(first.X) + " " + F(first.Y);

        foreach (var point in contour.Points.Skip(1))
        {
            data += " L " + F(point.X) + " " + F(point.Y);
        }

        if (contour.IsClosed)
        {
            data += " Z";
        }

        return data;
    }

    private static string MakeSafeId(string value) =>
        new(value
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray());

    private static double DebugUnit(XElement root)
    {
        var viewBox = ((string?)root.Attribute("viewBox"))?.Split(
            [' ', ',', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries);

        if (viewBox is { Length: 4 }
            && double.TryParse(
                viewBox[2],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var width)
            && double.TryParse(
                viewBox[3],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var height)
            && width > 0
            && height > 0)
        {
            return Math.Max(Math.Min(width, height) / 1000.0, 0.05);
        }

        return 1.0;
    }

    private static string F(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);
}
