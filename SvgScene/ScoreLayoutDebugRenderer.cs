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

        var staffNumbers = layout.Staffs
            .Select((staff, index) => new { staff.Id, Number = index + 1 })
            .ToDictionary(x => x.Id, x => x.Number, StringComparer.Ordinal);

        var regionIndex = 0;

        foreach (var system in layout.Systems)
        {
            foreach (var pair in system.StaffPairs)
            {
                var upper = layout.Staffs.First(staff => staff.Id == pair.UpperStaffId);
                var lower = layout.Staffs.First(staff => staff.Id == pair.LowerStaffId);

                for (var measureIndex = 0; measureIndex < pair.Measures.Count; measureIndex++)
                {
                    var measure = pair.Measures[measureIndex];

                    AddStaffMeasureRegion(
                        group,
                        ns,
                        upper,
                        staffNumbers[upper.Id],
                        measureIndex + 1,
                        measure.XStart,
                        measure.XEnd,
                        Palette[regionIndex++ % Palette.Length],
                        unit);

                    AddStaffMeasureRegion(
                        group,
                        ns,
                        lower,
                        staffNumbers[lower.Id],
                        measureIndex + 1,
                        measure.XStart,
                        measure.XEnd,
                        Palette[regionIndex++ % Palette.Length],
                        unit);
                }

                AddMeasureBoundaries(group, ns, pair, unit);
            }
        }

        root.Add(group);
        document.Save(output, SaveOptions.DisableFormatting);
    }

    private static void AddStaffMeasureRegion(
        XElement group,
        XNamespace ns,
        StaffLayout staff,
        int staffNumber,
        int measureNumber,
        double xStart,
        double xEnd,
        string color,
        double unit)
    {
        AddStaffRegionBounds(
            group,
            ns,
            staff,
            xStart,
            xEnd,
            color,
            unit);

        AddStaffSlice(
            group,
            ns,
            staff,
            xStart,
            xEnd,
            color,
            unit);

        AddLedgerSlice(
            group,
            ns,
            staff,
            xStart,
            xEnd,
            color,
            unit);

        AddLabel(
            group,
            ns,
            staffNumber,
            measureNumber,
            xStart,
            staff.Bounds.MinY,
            color,
            unit);
    }

    private static void AddStaffRegionBounds(
        XElement group,
        XNamespace ns,
        StaffLayout staff,
        double xStart,
        double xEnd,
        string color,
        double unit)
    {
        group.Add(new XElement(
            ns + "rect",
            new XAttribute("x", F(xStart)),
            new XAttribute("y", F(staff.Bounds.MinY)),
            new XAttribute("width", F(Math.Max(0, xEnd - xStart))),
            new XAttribute("height", F(staff.Bounds.Height)),
            new XAttribute("fill", color),
            new XAttribute("fill-opacity", "0.045"),
            new XAttribute("stroke", color),
            new XAttribute("stroke-width", F(0.8 * unit)),
            new XAttribute("stroke-dasharray", $"{F(4 * unit)} {F(3 * unit)}"),
            new XAttribute("opacity", "0.88")));
    }

    private static void AddLabel(
        XElement group,
        XNamespace ns,
        int staffNumber,
        int measureNumber,
        double xStart,
        double staffTopY,
        string color,
        double unit)
    {
        var label = $"s{staffNumber}+m{measureNumber}";
        var labelX = xStart + 2.5 * unit;
        var labelY = staffTopY - 3.0 * unit;

        group.Add(new XElement(
            ns + "text",
            new XAttribute("x", F(labelX)),
            new XAttribute("y", F(labelY)),
            new XAttribute("font-family", "Arial, sans-serif"),
            new XAttribute("font-size", F(7.5 * unit)),
            new XAttribute("font-weight", "700"),
            new XAttribute("fill", color),
            new XAttribute("stroke", "white"),
            new XAttribute("stroke-width", F(1.2 * unit)),
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
                new XAttribute("opacity", "0.82")));
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
                    new XAttribute("opacity", "0.92")));
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
