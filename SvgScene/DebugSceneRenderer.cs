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

    public void RenderAll(string inputSvgPath, NotationScene scene, string outputDirectory, string baseName)
    {
        Render(inputSvgPath, Path.Combine(outputDirectory, baseName + ".strokes.svg"),
            (root, ns) => AddStrokes(root, ns, scene));
        Render(inputSvgPath, Path.Combine(outputDirectory, baseName + ".arcs.svg"),
            (root, ns) => AddArcs(root, ns, scene));
        Render(inputSvgPath, Path.Combine(outputDirectory, baseName + ".contours.svg"),
            (root, ns) => AddContours(root, ns, scene));
    }

    private static void Render(string input, string output, Action<XElement, XNamespace> addOverlay)
    {
        var document = XDocument.Load(input, LoadOptions.PreserveWhitespace);
        var root = document.Root ?? throw new InvalidOperationException("SVG has no root element.");
        addOverlay(root, root.Name.Namespace);
        document.Save(output, SaveOptions.DisableFormatting);
    }

    private static void AddStrokes(XElement root, XNamespace ns, NotationScene scene)
    {
        var overlay = Group(ns, "debug-strokes");
        foreach (var stroke in scene.Strokes)
        {
            overlay.Add(new XElement(ns + "line",
                new XAttribute("x1", F(stroke.Start.X)), new XAttribute("y1", F(stroke.Start.Y)),
                new XAttribute("x2", F(stroke.End.X)), new XAttribute("y2", F(stroke.End.Y)),
                new XAttribute("stroke", "#00a7d6"),
                new XAttribute("stroke-width", F(Math.Clamp(stroke.Width + 3, 4, 12))),
                new XAttribute("stroke-linecap", "round"), new XAttribute("opacity", "0.75")));
        }
        root.Add(overlay);
    }

    private static void AddArcs(XElement root, XNamespace ns, NotationScene scene)
    {
        var overlay = Group(ns, "debug-arcs");
        foreach (var arc in scene.Arcs)
        {
            var d = $"M {F(arc.Start.X)} {F(arc.Start.Y)} Q {F(arc.Control.X)} {F(arc.Control.Y)} {F(arc.End.X)} {F(arc.End.Y)}";
            overlay.Add(new XElement(ns + "path",
                new XAttribute("d", d), new XAttribute("fill", "none"),
                new XAttribute("stroke", "#d81b60"), new XAttribute("stroke-width", "5"),
                new XAttribute("stroke-linecap", "round"), new XAttribute("opacity", "0.82")));
        }
        root.Add(overlay);
    }

    private static void AddContours(XElement root, XNamespace ns, NotationScene scene)
    {
        var overlay = Group(ns, "debug-contours");
        foreach (var instance in scene.Instances)
        {
            var index = ParsePrototypeIndex(instance.PrototypeId);
            var color = Palette[(index - 1) % Palette.Length];
            var fontSize = Math.Clamp(Math.Max(instance.Width, instance.Height) * 0.28, 14, 28);
            overlay.Add(
                new XElement(ns + "rect",
                    new XAttribute("x", F(instance.X)), new XAttribute("y", F(instance.Y)),
                    new XAttribute("width", F(instance.Width)), new XAttribute("height", F(instance.Height)),
                    new XAttribute("fill", "none"), new XAttribute("stroke", color), new XAttribute("stroke-width", "3")),
                new XElement(ns + "text",
                    new XAttribute("x", F(instance.X)), new XAttribute("y", F(Math.Max(fontSize, instance.Y - 4))),
                    new XAttribute("font-family", "Arial, sans-serif"), new XAttribute("font-size", F(fontSize)),
                    new XAttribute("font-weight", "700"), new XAttribute("fill", color),
                    new XAttribute("stroke", "white"), new XAttribute("stroke-width", "2"),
                    new XAttribute("paint-order", "stroke fill"), $"p{index}"));
        }
        root.Add(overlay);
    }

    private static XElement Group(XNamespace ns, string id) =>
        new(ns + "g", new XAttribute("id", id), new XAttribute("pointer-events", "none"));

    private static int ParsePrototypeIndex(string id)
    {
        var dash = id.LastIndexOf('-');
        return dash >= 0 && int.TryParse(id[(dash + 1)..], out var value) ? Math.Max(1, value) : 1;
    }

    private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
