using System.Globalization;
using System.Xml.Linq;

namespace SvgMusic.Scene;

public sealed class DebugSceneRenderer
{
    private static readonly string[] Palette =
    [
        "#e6194b", "#3cb44b", "#4363d8", "#f58231", "#911eb4", "#46f0f0",
        "#f032e6", "#bcf60c", "#fabebe", "#008080", "#e6beff", "#9a6324",
        "#fffac8", "#800000", "#aaffc3", "#808000", "#ffd8b1", "#000075",
        "#808080", "#ffe119", "#42d4f4", "#f58231", "#469990", "#dcbeff"
    ];

    public void Render(string inputSvgPath, NotationScene scene, string outputSvgPath)
    {
        var document = XDocument.Load(inputSvgPath, LoadOptions.PreserveWhitespace);
        var root = document.Root ?? throw new InvalidOperationException("SVG has no root element.");
        var ns = root.Name.Namespace;

        var overlay = new XElement(ns + "g",
            new XAttribute("id", "shape-cluster-debug-overlay"),
            new XAttribute("pointer-events", "none"));

        foreach (var instance in scene.Instances)
        {
            var prototypeIndex = ParsePrototypeIndex(instance.PrototypeId);
            var color = Palette[(prototypeIndex - 1) % Palette.Length];
            var label = $"p{prototypeIndex}";
            var maxDimension = Math.Max(instance.Width, instance.Height);
            var fontSize = Math.Clamp(maxDimension * 0.28, 8.0, 28.0);

            overlay.Add(
                new XElement(ns + "rect",
                    new XAttribute("x", F(instance.X)),
                    new XAttribute("y", F(instance.Y)),
                    new XAttribute("width", F(instance.Width)),
                    new XAttribute("height", F(instance.Height)),
                    new XAttribute("fill", "none"),
                    new XAttribute("stroke", color),
                    new XAttribute("stroke-width", F(Math.Max(0.8, maxDimension * 0.025))),
                    new XAttribute("vector-effect", "non-scaling-stroke"),
                    new XAttribute("opacity", "0.9")),
                new XElement(ns + "text",
                    new XAttribute("x", F(instance.X)),
                    new XAttribute("y", F(Math.Max(0, instance.Y - 2))),
                    new XAttribute("font-family", "Arial, sans-serif"),
                    new XAttribute("font-size", F(fontSize)),
                    new XAttribute("font-weight", "bold"),
                    new XAttribute("fill", color),
                    new XAttribute("stroke", "white"),
                    new XAttribute("stroke-width", F(Math.Max(0.8, fontSize * 0.08))),
                    new XAttribute("paint-order", "stroke"),
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
