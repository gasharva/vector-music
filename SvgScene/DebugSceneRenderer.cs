using System.Globalization;
using System.Xml.Linq;

namespace SvgMusic.Scene;

public sealed class DebugSceneRenderer
{
    private static readonly string[] Palette =
    [
        "#e6194b", "#3cb44b", "#4363d8", "#f58231", "#911eb4", "#008b8b",
        "#f032e6", "#6b8e23", "#c71585", "#008080", "#7b68ee", "#9a6324",
        "#b8860b", "#800000", "#2e8b57", "#808000", "#d2691e", "#000075"
    ];

    public void RenderAll(
        string input,
        NotationScene scene,
        string dir,
        string name)
    {
        Render(
            input,
            Path.Combine(dir, name + ".strokes.svg"),
            (root, ns, unit) => AddStrokes(root, ns, scene, unit));

        Render(
            input,
            Path.Combine(dir, name + ".arcs.svg"),
            (root, ns, unit) => AddCurves(root, ns, scene, unit));

        Render(
            input,
            Path.Combine(dir, name + ".ellipses.svg"),
            (root, ns, unit) => AddEllipses(root, ns, scene, unit));

        Render(
            input,
            Path.Combine(dir, name + ".hairpins.svg"),
            (root, ns, unit) => AddHairpins(root, ns, scene, unit));

        Render(
            input,
            Path.Combine(dir, name + ".brackets.svg"),
            (root, ns, unit) => AddBracketSpanners(root, ns, scene, unit));

        Render(
            input,
            Path.Combine(dir, name + ".contours.svg"),
            (root, ns, unit) => AddContours(root, ns, scene, unit));
    }

    private static void Render(
        string input,
        string output,
        Action<XElement, XNamespace, double> add)
    {
        var document = XDocument.Load(input, LoadOptions.PreserveWhitespace);
        var root = document.Root
            ?? throw new InvalidOperationException("SVG has no root element.");

        add(root, root.Name.Namespace, DebugUnit(root));
        document.Save(output, SaveOptions.DisableFormatting);
    }

    private static double DebugUnit(XElement root)
    {
        var viewBox = ((string?)root.Attribute("viewBox"))?.Split(
            [' ', ',', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries);

        if (viewBox is { Length: 4 }
            && double.TryParse(viewBox[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var width)
            && double.TryParse(viewBox[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var height)
            && width > 0
            && height > 0)
        {
            return Math.Max(Math.Min(width, height) / 1000.0, 0.05);
        }

        return 1.0;
    }

    private static void AddStrokes(
        XElement root,
        XNamespace ns,
        NotationScene scene,
        double unit)
    {
        var group = Group(ns, "debug-strokes");

        foreach (var stroke in scene.Strokes)
        {
            group.Add(new XElement(
                ns + "line",
                new XAttribute("x1", F(stroke.Start.X)),
                new XAttribute("y1", F(stroke.Start.Y)),
                new XAttribute("x2", F(stroke.End.X)),
                new XAttribute("y2", F(stroke.End.Y)),
                new XAttribute("stroke", "#00a7d6"),
                new XAttribute(
                    "stroke-width",
                    F(Math.Clamp(stroke.Width + unit, 1.35 * unit, 4 * unit))),
                new XAttribute("stroke-linecap", "round"),
                new XAttribute("opacity", "0.68")));
        }

        root.Add(group);
    }

    private static void AddCurves(
        XElement root,
        XNamespace ns,
        NotationScene scene,
        double unit)
    {
        var group = Group(ns, "debug-curved-strokes");

        foreach (var curve in scene.CurvedStrokes)
        {
            if (curve.Centerline.Count < 2)
            {
                continue;
            }

            var pathData = "M "
                + F(curve.Centerline[0].X)
                + " "
                + F(curve.Centerline[0].Y)
                + string.Concat(
                    curve.Centerline
                        .Skip(1)
                        .Select(point => $" L {F(point.X)} {F(point.Y)}"));

            group.Add(new XElement(
                ns + "path",
                new XAttribute("d", pathData),
                new XAttribute("fill", "none"),
                new XAttribute("stroke", "#d81b60"),
                new XAttribute("stroke-width", F(1.7 * unit)),
                new XAttribute("stroke-linecap", "round"),
                new XAttribute("stroke-linejoin", "round"),
                new XAttribute("opacity", "0.78")));
        }

        root.Add(group);
    }

    private static void AddEllipses(
        XElement root,
        XNamespace ns,
        NotationScene scene,
        double unit)
    {
        var group = Group(ns, "debug-ellipses");

        foreach (var ellipse in scene.Ellipses)
        {
            var degrees = ellipse.Rotation * 180 / Math.PI;

            group.Add(new XElement(
                ns + "ellipse",
                new XAttribute("cx", F(ellipse.Center.X)),
                new XAttribute("cy", F(ellipse.Center.Y)),
                new XAttribute("rx", F(ellipse.MajorRadius)),
                new XAttribute("ry", F(ellipse.MinorRadius)),
                new XAttribute(
                    "transform",
                    $"rotate({F(degrees)} {F(ellipse.Center.X)} {F(ellipse.Center.Y)})"),
                new XAttribute("fill", ellipse.IsHollow ? "none" : "#20a050"),
                new XAttribute("fill-opacity", ellipse.IsHollow ? "0" : "0.20"),
                new XAttribute("stroke", ellipse.IsHollow ? "#7b1fa2" : "#20a050"),
                new XAttribute("stroke-width", F(1.35 * unit)),
                new XAttribute("opacity", "0.86")));
        }

        root.Add(group);
    }

    private static void AddHairpins(
        XElement root,
        XNamespace ns,
        NotationScene scene,
        double unit)
    {
        const string wedgeColor = "#ff00ff";
        const string apexColor = "#00e5ff";
        var group = Group(ns, "debug-hairpins");

        foreach (var hairpin in scene.Hairpins)
        {
            var pathData = $"M {F(hairpin.OpenUpper.X)} {F(hairpin.OpenUpper.Y)} "
                + $"L {F(hairpin.Apex.X)} {F(hairpin.Apex.Y)} "
                + $"L {F(hairpin.OpenLower.X)} {F(hairpin.OpenLower.Y)}";
            var labelX = Math.Min(hairpin.Apex.X, hairpin.OpenUpper.X)
                + Math.Abs(hairpin.OpenUpper.X - hairpin.Apex.X) * 0.50;
            var labelY = Math.Min(
                    hairpin.OpenUpper.Y,
                    Math.Min(hairpin.OpenLower.Y, hairpin.Apex.Y))
                - 3.5 * unit;
            var label = hairpin.Kind == HairpinKind.Crescendo
                ? "HP<"
                : "HP>";

            group.Add(
                new XElement(
                    ns + "path",
                    new XAttribute("d", pathData),
                    new XAttribute("fill", "none"),
                    new XAttribute("stroke", wedgeColor),
                    new XAttribute("stroke-width", F(3.2 * unit)),
                    new XAttribute("stroke-linecap", "round"),
                    new XAttribute("stroke-linejoin", "round"),
                    new XAttribute("opacity", "0.92")),
                new XElement(
                    ns + "circle",
                    new XAttribute("cx", F(hairpin.Apex.X)),
                    new XAttribute("cy", F(hairpin.Apex.Y)),
                    new XAttribute("r", F(3.0 * unit)),
                    new XAttribute("fill", apexColor),
                    new XAttribute("stroke", "#111111"),
                    new XAttribute("stroke-width", F(0.8 * unit))),
                new XElement(
                    ns + "text",
                    new XAttribute("x", F(labelX)),
                    new XAttribute("y", F(labelY)),
                    new XAttribute("font-family", "Arial, sans-serif"),
                    new XAttribute("font-size", F(7.5 * unit)),
                    new XAttribute("font-weight", "700"),
                    new XAttribute("fill", wedgeColor),
                    new XAttribute("stroke", "white"),
                    new XAttribute("stroke-width", F(1.1 * unit)),
                    new XAttribute("paint-order", "stroke fill"),
                    $"{label} {hairpin.ShapeId}"));
        }

        root.Add(group);
    }

    private static void AddBracketSpanners(
        XElement root,
        XNamespace ns,
        NotationScene scene,
        double unit)
    {
        const string solidColor = "#ff6d00";
        const string dashedColor = "#00c853";
        const string hookMarkerColor = "#00b8d4";
        var group = Group(ns, "debug-bracket-spanners");

        foreach (var bracket in scene.BracketSpanners)
        {
            var color = bracket.IsDashed ? dashedColor : solidColor;
            var pathData = $"M {F(bracket.Start.X)} {F(bracket.Start.Y)} "
                + $"L {F(bracket.End.X)} {F(bracket.End.Y)}";

            if (bracket.LeftHookEnd is PointD leftHook)
            {
                pathData += $" M {F(bracket.Start.X)} {F(bracket.Start.Y)} "
                    + $"L {F(leftHook.X)} {F(leftHook.Y)}";
            }

            if (bracket.RightHookEnd is PointD rightHook)
            {
                pathData += $" M {F(bracket.End.X)} {F(bracket.End.Y)} "
                    + $"L {F(rightHook.X)} {F(rightHook.Y)}";
            }

            var path = new XElement(
                ns + "path",
                new XAttribute("d", pathData),
                new XAttribute("fill", "none"),
                new XAttribute("stroke", color),
                new XAttribute("stroke-width", F(3.3 * unit)),
                new XAttribute("stroke-linecap", "round"),
                new XAttribute("stroke-linejoin", "round"),
                new XAttribute("opacity", "0.94"));

            if (bracket.IsDashed)
            {
                path.Add(new XAttribute(
                    "stroke-dasharray",
                    $"{F(6.0 * unit)} {F(4.0 * unit)}"));
            }

            group.Add(path);

            foreach (var hookEnd in new[]
                     {
                         bracket.LeftHookEnd,
                         bracket.RightHookEnd
                     }
                     .Where(point => point is not null)
                     .Select(point => point!.Value))
            {
                group.Add(new XElement(
                    ns + "circle",
                    new XAttribute("cx", F(hookEnd.X)),
                    new XAttribute("cy", F(hookEnd.Y)),
                    new XAttribute("r", F(2.7 * unit)),
                    new XAttribute("fill", hookMarkerColor),
                    new XAttribute("stroke", "#111111"),
                    new XAttribute("stroke-width", F(0.75 * unit))));
            }

            var labelX = (bracket.Start.X + bracket.End.X) / 2.0;
            var hookYs = new[]
            {
                bracket.Start.Y,
                bracket.End.Y,
                bracket.LeftHookEnd?.Y ?? bracket.Start.Y,
                bracket.RightHookEnd?.Y ?? bracket.End.Y
            };
            var labelY = hookYs.Min() - 3.5 * unit;
            var left = ShortHook(bracket.LeftHookDirection);
            var right = ShortHook(bracket.RightHookDirection);
            var pattern = bracket.IsDashed ? "BK~" : "BK";

            group.Add(new XElement(
                ns + "text",
                new XAttribute("x", F(labelX)),
                new XAttribute("y", F(labelY)),
                new XAttribute("font-family", "Arial, sans-serif"),
                new XAttribute("font-size", F(7.5 * unit)),
                new XAttribute("font-weight", "700"),
                new XAttribute("text-anchor", "middle"),
                new XAttribute("fill", color),
                new XAttribute("stroke", "white"),
                new XAttribute("stroke-width", F(1.1 * unit)),
                new XAttribute("paint-order", "stroke fill"),
                $"{pattern} {left}-{right} {bracket.Id}"));
        }

        root.Add(group);
    }

    private static string ShortHook(BracketHookDirection direction) =>
        direction switch
        {
            BracketHookDirection.Up => "U",
            BracketHookDirection.Down => "D",
            _ => "-"
        };

    private static void AddContours(
        XElement root,
        XNamespace ns,
        NotationScene scene,
        double unit)
    {
        var group = Group(ns, "debug-contours");

        foreach (var instance in scene.Instances)
        {
            var prototypeIndex = ParsePrototypeIndex(instance.PrototypeId);
            var color = Palette[(prototypeIndex - 1) % Palette.Length];
            var fontSize = Math.Clamp(
                Math.Max(instance.Width, instance.Height) * 0.28,
                4.7 * unit,
                9.4 * unit);

            var label = $"{instance.ShapeId} / p{prototypeIndex}";

            group.Add(
                new XElement(
                    ns + "rect",
                    new XAttribute("x", F(instance.X)),
                    new XAttribute("y", F(instance.Y)),
                    new XAttribute("width", F(instance.Width)),
                    new XAttribute("height", F(instance.Height)),
                    new XAttribute("fill", "none"),
                    new XAttribute("stroke", color),
                    new XAttribute("stroke-width", F(unit))),
                new XElement(
                    ns + "text",
                    new XAttribute("x", F(instance.X)),
                    new XAttribute(
                        "y",
                        F(Math.Max(fontSize, instance.Y - 1.35 * unit))),
                    new XAttribute("font-family", "Arial, sans-serif"),
                    new XAttribute("font-size", F(fontSize)),
                    new XAttribute("font-weight", "700"),
                    new XAttribute("fill", color),
                    new XAttribute("stroke", "white"),
                    new XAttribute("stroke-width", F(0.67 * unit)),
                    new XAttribute("paint-order", "stroke fill"),
                    label));
        }

        root.Add(group);
    }

    private static XElement Group(XNamespace ns, string id) =>
        new(
            ns + "g",
            new XAttribute("id", id),
            new XAttribute("pointer-events", "none"));

    private static int ParsePrototypeIndex(string id)
    {
        var dash = id.LastIndexOf('-');

        return dash >= 0
            && int.TryParse(id[(dash + 1)..], out var value)
                ? Math.Max(1, value)
                : 1;
    }

    private static string F(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);
}
