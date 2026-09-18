using System.Globalization;
using System.Xml.Linq;
using SvgMusic.Scene;

namespace SvgMusic.Semantics;

public sealed class TextDebugRenderer
{
    public void Render(
        string inputSvg,
        IEnumerable<TextFact> facts,
        string outputSvg)
    {
        var document = XDocument.Load(inputSvg);
        var root = document.Root
            ?? throw new InvalidDataException("SVG document has no root element.");
        var ns = root.Name.Namespace;
        var overlay = new XElement(
            ns + "g",
            new XAttribute("id", "semantic-text-pass"),
            new XAttribute("font-family", "monospace"),
            new XAttribute("font-size", "18"));

        foreach (var fact in facts
                     .OrderBy(item => item.Bounds.MinY)
                     .ThenBy(item => item.Bounds.MinX))
        {
            var bounds = fact.Bounds;
            var stroke = fact.Role == SemanticTextRole.Unknown
                ? "#777"
                : "#7b2cbf";

            overlay.Add(new XElement(
                ns + "rect",
                new XAttribute("x", F(bounds.MinX)),
                new XAttribute("y", F(bounds.MinY)),
                new XAttribute("width", F(Math.Max(0.5, bounds.Width))),
                new XAttribute("height", F(Math.Max(0.5, bounds.Height))),
                new XAttribute("fill", "none"),
                new XAttribute("stroke", stroke),
                new XAttribute("stroke-width", "2"),
                new XAttribute("vector-effect", "non-scaling-stroke")));

            var label = $"{fact.Role}: {fact.Text}";
            var text = new XElement(
                ns + "text",
                new XAttribute("x", F(bounds.MinX)),
                new XAttribute("y", F(Math.Max(12, bounds.MinY - 5))),
                new XAttribute("fill", stroke),
                label);
            text.Add(new XElement(
                ns + "title",
                $"{fact.Reason}; OCR={fact.OcrConfidence:P1}; semantic={fact.Confidence:P1}; avgGlyphHeight={fact.AverageGlyphHeight:F2}"));
            overlay.Add(text);
        }

        root.Add(overlay);
        document.Save(outputSvg);
    }

    private static string F(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);
}
