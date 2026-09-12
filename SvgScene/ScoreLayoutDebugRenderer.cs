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
        "#455a64",
        "#c2185b",
        "#00796b",
        "#512da8",
        "#689f38"
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

        var regionIndex = 0;

        for (var systemIndex = 0; systemIndex < layout.Systems.Count; systemIndex++)
        {
            var system = layout.Systems[systemIndex];

            foreach (var pair in system.StaffPairs)
            {
                var upper = layout.Staffs.First(staff => staff.Id == pair.UpperStaffId);
                var lower = layout.Staffs.First(staff => staff.Id == pair.LowerStaffId);

                for (var measureIndex = 0; measureIndex < pair.Measures.Count; measureIndex++)
                {
                    var measure = pair.Measures[measureIndex];
                    var color = Palette[regionIndex % Palette.Length];
                    regionIndex++;

                    AddMeasureRegion(
                        group,
                        ns,
                        systemIndex + 1,
                        measureIndex + 1,
                        measure.XStart,
                        measure.XEnd,
                        pair.Bounds.MinY,
                        pair.Bounds.MaxY,
                        color,
                        unit);

                    AddStaffSlice(group, ns, upper, measure.XStart, measure.XEnd, color, unit);
                    AddStaffSlice(group, ns, lower, measure.XStart, measure.XEnd, color, unit);
                    AddLedgerSlice(group, ns, upper, measure.XStart, measure.XEnd, color, unit);
                    AddLedgerSlice(group, ns, lower, measure.XStart, measure.XEnd, color, unit);
                }

                AddMeasureBoundaries(group, ns, pair, unit);
            }
        }

        root.Add(group);
        document.Save(output, SaveOptions.DisableFormatting);
    }

    private static void AddMeasureRegion(
        XElement group,
        XNamespace ns,
        int systemNumber,
        int measureNumber,
        double xStart,
        double xEnd,
        double yStart,
        double yEnd,
        string color,
        double unit)
    {
        group.Add(new XElement(
            ns + "rect",
            new XAttribute("x", F(xStart)),
            new XAttribute("y", F(yStart)),
            new XAttribute("width", F(Math.Max(0, xEnd - xStart))),
            new XAttribute("height", F(Math.Max(0, yEnd - yStart))),
            new XAttribute("fill", color),
            new XAttribute("fill-opacity", "0.055"),
            new XAttribute("stroke", color),
            new XAttribute("stroke-width", F(0.9 * unit)),
            new XAttribute("stroke-dasharray", $"{F(4 * unit)} {F(3 * unit)}"),
            new XAttribute("opacity", "0.9")));

        var label = $"s{systemNumber}+m{measureNumber}";
        var labelX = xStart + 2.5 * unit;
        var labelY = yStart + 8.5 * unit;

        group.Add(new XElement(
            ns + "text",
            new XAttribute("x", F(labelX)),
            new XAttribute("y", F(labelY)),
            new XAttribute("font-family", "Arial, sans-serif"),
            new XAttribute("font-size", F(7.5 * unit)),
            new XAttribute("font-weight", "700"),
            new XAttribute("fill", color),
            new XAttribute("stroke", "white"),
            new XAttribute("stroke-width", F(1.1 * unit)),
            new XAttribute("paint-order", "stroke fill"),
            label));
    }

    private static void AddStaffSlice(
        XElement group,
        XNamespace ns,
        StaffLayout staff,
        double xStart,
        double xEnd,
        string color,
        double unit)
    {
        foreach (var line in staff.Lines)
        {
            var start = Math.Max(line.XStart, xStart);
            var end = Math.Min(line.XEnd, xEnd);

            if (end <= start)
                continue;

            group.Add(new XElement(
                ns + "line",
                new XAttribute("x1", F(start)),
                new XAttribute("y1", F(line.Y)),
                new XAttribute("x2", F(end)),
                new XAttribute("y2", F(line.Y)),
                new XAttribute("stroke", color),
                new XAttribute("stroke-width", F(1.8 * unit)),
                new XAttribute("opacity", "0.78")));
        }
    }

    private static void AddLedgerSlice(
        XElement group,
        XNamespace ns,
        StaffLayout staff,
        double xStart,
        double xEnd,
        string color,
        double unit)
    {
        foreach (var level in staff.LedgerLevels)
        {
            foreach (var segment in level.Segments)
            {
                var start = Math.Max(segment.XStart, xStart);
                var end = Math.Min(segment.XEnd, xEnd);

                if (end <= start)
                    continue;

                group.Add(new XElement(
                    ns + "line",
                    new XAttribute("x1", F(start)),
                    new XAttribute("y1", F(level.Y)),
                    new XAttribute("x2", F(end)),
                    new XAttribute("y2", F(level.Y)),
                    new XAttribute("stroke", color),
                    new XAttribute("stroke-width", F(2.6 * unit)),
                    new XAttribute("stroke-linecap", "round"),
                    new XAttribute("opacity", "0.9")));
            }
        }
    }

    private static void AddMeasureBoundaries(
        XElement group,
        XNamespace ns,
        StaffPairLayout pair,
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
                new XAttribute("stroke", "#202020"),
                new XAttribute("stroke-width", F(1.25 * unit)),
                new XAttribute("opacity", "0.72")));
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
