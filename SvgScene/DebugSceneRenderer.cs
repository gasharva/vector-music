using System.Globalization;
using System.Xml.Linq;

namespace SvgMusic.Scene;

public sealed class DebugSceneRenderer
{
    private static readonly string[] Palette =
    [
        "#e6194b", "#3cb44b", "#4363d8", "#f58231", "#911eb4", "#008b8b",
        "#f032e6", "#6b8e23", "#c71585", "#008080", "#7b68ee", "#9a6324",
        "#b8860b", "#800000", "#2e8b57", "#808000", "#d2691e", "#000075",
        "#696969", "#b59b00", "#008fb3", "#cc5500", "#267f73", "#7651a6"
    ];

    public void Render(string inputSvgPath, NotationScene scene, string outputSvgPath)
    {
        var document = XDocument.Load(inputSvgPath, LoadOptions.PreserveWhitespace);
        var root = document.Root ?? throw new InvalidOperationException("SVG has no root element.");
        var ns = root.Name.Namespace;

        var overlay = new XElement(ns + "g",
            new XAttribute("id", "shape-cluster-debug-overlay"),
            new XAttribute("pointer-events", "none"));

        // Strokes are deliberately not clustered.  Draw their normalized centre
        // lines in one diagnostic color so it is immediately obvious which
        // geometry was removed from contour clustering.
        var strokeOverlay = new XElement(ns + "g",
            new XAttribute("id", "normalized-strokes"));

        foreach (var stroke in scene.Strokes)
        {
            var debugWidth = Math.Clamp(stroke.Width + 3.0, 4.0, 12.0);
            strokeOverlay.Add(
                new XElement(ns + "line",
                    new XAttribute("x1", F(stroke.Start.X)),
                    new XAttribute("y1", F(stroke.Start.Y)),
                    new XAttribute("x2", F(stroke.End.X)),
                    new XAttribute("y2", F(stroke.End.Y)),
                    new XAttribute("stroke", "#00b7ff"),
                    new XAttribute("stroke-width", F(debugWidth)),
                    new XAttribute("stroke-linecap", "round"),
                    new XAttribute("opacity", "0.62")));
        }

        overlay.Add(strokeOverlay);

        foreach (var instance in scene.Instances)
        {
            var prototypeIndex = ParsePrototypeIndex(instance.PrototypeId);
            var color = Palette[(prototypeIndex - 1) % Palette.Length];
            var label = $"p{prototypeIndex}";
            var maxDimension = Math.Max(instance.Width, instance.Height);

            var fontSize = Math.Clamp(maxDimension * 0.28, 14.0, 28.0);

            overlay.Add(
                new XElement(ns + "rect",
                    new XAttribute("x", F(instance.X)),
                    new XAttribute("y", F(instance.Y)),
                    new XAttribute("width", F(instance.Width)),
                    new XAttribute("height", F(instance.Height)),
                    new XAttribute("fill", "none"),
                    new XAttribute("stroke", color),
                    new XAttribute("stroke-width", "3"),
                    new XAttribute("opacity", "1")),
                new XElement(ns + "text",
                    new XAttribute("x", F(instance.X)),
                    new XAttribute("y", F(Math.Max(fontSize, instance.Y - 4))),
                    new XAttribute("font-family", "Arial, sans-serif"),
                    new XAttribute("font-size", F(fontSize)),
                    new XAttribute("font-weight", "700"),
                    new XAttribute("fill", color),
                    new XAttribute("stroke", "white"),
                    new XAttribute("stroke-width", "2"),
                    new XAttribute("paint-order", "stroke fill"),
                    new XAttribute("stroke-linejoin", "round"),
                    label));
        }

        root.Add(overlay);
        document.Save(outputSvgPath, SaveOptions.DisableFormatting);
    }

    private static int ParsePrototypeIndex(string prototypeId)
    {
        var dash = prototypeId.LastIndexOf('-');
        return dash >= 0 && int.TryParse(prototypeId[(dash + 1)..], out var value)
            ? Math.Max(1, value)
            : 1;
    }

    private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
