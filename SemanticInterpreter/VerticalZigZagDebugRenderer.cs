using System.Globalization;
using System.Xml.Linq;
using SvgMusic.Scene;

namespace SvgMusic.Semantics;

public sealed class VerticalZigZagDebugRenderer
{
    public void Render(
        string sourceSvgPath,
        IReadOnlyList<VerticalZigZagPrimitive> zigZags,
        string outputPath)
    {
        var document = XDocument.Load(sourceSvgPath, LoadOptions.PreserveWhitespace);
        var root = document.Root
            ?? throw new InvalidOperationException("SVG document has no root element.");
        var ns = root.Name.Namespace;

        var overlay = new XElement(ns + "g",
            new XAttribute("id", "vertical-zigzag-debug"),
            new XAttribute("fill", "none"),
            new XAttribute("stroke", "#ff00aa"),
            new XAttribute("stroke-width", "2"),
            new XAttribute("vector-effect", "non-scaling-stroke"));

        foreach (var zigZag in zigZags)
        {
            var bounds = zigZag.Bounds;
            var pad = Math.Max(1.5, Math.Min(bounds.Width, bounds.Height) * 0.15);

            overlay.Add(new XElement(ns + "rect",
                new XAttribute("x", F(bounds.MinX - pad)),
                new XAttribute("y", F(bounds.MinY - pad)),
                new XAttribute("width", F(bounds.Width + pad * 2)),
                new XAttribute("height", F(bounds.Height + pad * 2)),
                new XAttribute("rx", "2"),
                new XAttribute("stroke-dasharray", "6,3")));

            overlay.Add(new XElement(ns + "circle",
                new XAttribute("cx", F(bounds.CenterX)),
                new XAttribute("cy", F(bounds.CenterY)),
                new XAttribute("r", "2.5"),
                new XAttribute("fill", "#ff00aa"),
                new XAttribute("stroke", "none")));

            overlay.Add(new XElement(ns + "text",
                new XAttribute("x", F(bounds.MaxX + pad + 2)),
                new XAttribute("y", F(bounds.MinY)),
                new XAttribute("fill", "#ff00aa"),
                new XAttribute("stroke", "none"),
                new XAttribute("font-size", "12"),
                new XAttribute("font-family", "monospace"),
                $"zigzag {zigZag.ShapeId} turns={zigZag.TurnCount} conf={zigZag.Confidence:P0}"));
        }

        root.Add(overlay);
        document.Save(outputPath);
    }

    private static string F(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);
}
