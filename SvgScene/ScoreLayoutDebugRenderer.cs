using System.Globalization;
using System.Xml.Linq;

namespace SvgMusic.Scene;

public sealed class ScoreLayoutDebugRenderer
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
        "#455a64"
    ];

    public void Render(
        string input,
        ScoreLayout layout,
        string output)
    {
        var document = XDocument.Load(input, LoadOptions.PreserveWhitespace);
        var root = document.Root
            ?? throw new InvalidOperationException("SVG has no root element.");

        var ns = root.Name.Namespace;
        var unit = DebugUnit(root);
        var group = new XElement(
            ns + "g",
            new XAttribute("id", "debug-score-layout"),
            new XAttribute("pointer-events", "none"));

        for (var systemIndex = 0; systemIndex < layout.Systems.Count; systemIndex++)
        {
            var system = layout.Systems[systemIndex];
            var color = Palette[systemIndex % Palette.Length];

            foreach (var pair in system.StaffPairs)
            {
                AddPairBounds(group, ns, pair, color, unit);

                var upper = layout.Staffs.First(staff => staff.Id == pair.UpperStaffId);
                var lower = layout.Staffs.First(staff => staff.Id == pair.LowerStaffId);

                AddStaff(group, ns, upper, color, unit);
                AddStaff(group, ns, lower, color, unit);
                AddMeasureBoundaries(group, ns, pair, color, unit);

                group.Add(new XElement(
                    ns + "text",
                    new XAttribute("x", F(pair.Bounds.MinX)),
                    new XAttribute("y", F(pair.Bounds.MinY - 3.0 * unit)),
                    new XAttribute("font-family", "Arial, sans-serif"),
                    new XAttribute("font-size", F(7.5 * unit)),
                    new XAttribute("font-weight", "700"),
                    new XAttribute("fill", color),
                    new XAttribute("stroke", "white"),
                    new XAttribute("stroke-width", F(0.8 * unit)),
                    new XAttribute("paint-order", "stroke fill"),
                    $"{system.Id} / {pair.Id}"));
            }
        }

        root.Add(group);
        document.Save(output, SaveOptions.DisableFormatting);
    }

    private static void AddPairBounds(
        XElement group,
        XNamespace ns,
        StaffPairLayout pair,
        string color,
        double unit)
    {
        group.Add(new XElement(
            ns + "rect",
            new XAttribute("x", F(pair.Bounds.MinX)),
            new XAttribute("y", F(pair.Bounds.MinY)),
            new XAttribute("width", F(pair.Bounds.Width)),
            new XAttribute("height", F(pair.Bounds.Height)),
            new XAttribute("fill", color),
            new XAttribute("fill-opacity", "0.04"),
            new XAttribute("stroke", color),
            new XAttribute("stroke-width", F(0.8 * unit)),
            new XAttribute("stroke-dasharray", $"{F(4 * unit)} {F(3 * unit)}"),
            new XAttribute("opacity", "0.75")));
    }

    private static void AddStaff(
        XElement group,
        XNamespace ns,
        StaffLayout staff,
        string color,
        double unit)
    {
        foreach (var line in staff.Lines)
        {
            group.Add(new XElement(
                ns + "line",
                new XAttribute("x1", F(line.XStart)),
                new XAttribute("y1", F(line.Y)),
                new XAttribute("x2", F(line.XEnd)),
                new XAttribute("y2", F(line.Y)),
                new XAttribute("stroke", color),
                new XAttribute("stroke-width", F(1.8 * unit)),
                new XAttribute("opacity", "0.72")));
        }

        foreach (var level in staff.LedgerLevels)
        {
            foreach (var segment in level.Segments)
            {
                group.Add(new XElement(
                    ns + "line",
                    new XAttribute("x1", F(segment.XStart)),
                    new XAttribute("y1", F(level.Y)),
                    new XAttribute("x2", F(segment.XEnd)),
                    new XAttribute("y2", F(level.Y)),
                    new XAttribute("stroke", color),
                    new XAttribute("stroke-width", F(2.6 * unit)),
                    new XAttribute("stroke-linecap", "round"),
                    new XAttribute("opacity", "0.88")));
            }
        }
    }

    private static void AddMeasureBoundaries(
        XElement group,
        XNamespace ns,
        StaffPairLayout pair,
        string color,
        double unit)
    {
        foreach (var boundary in pair.Boundaries)
        {
            group.Add(new XElement(
                ns + "line",
                new XAttribute("x1", F(boundary.X)),
                new XAttribute("y1", F(boundary.UpperY)),
                new XAttribute("x2", F(boundary.X)),
                new XAttribute("y2", F(boundary.LowerY)),
                new XAttribute("stroke", color),
                new XAttribute("stroke-width", F(2.2 * unit)),
                new XAttribute("opacity", "0.82")));
        }
    }

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
