using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace SvgMusic.Scene;

public sealed class SymbolClassificationDebugRenderer
{
    private const string ShapeOverlayColor = "#1976d2";
    private const double GreenConfidence = 0.80;
    private const double AmberConfidence = 0.60;

    public void Render(
        string input,
        GeometricScene geometry,
        NotationScene scene,
        string output)
    {
        var document = XDocument.Load(input, LoadOptions.PreserveWhitespace);
        var root = document.Root
            ?? throw new InvalidOperationException("SVG has no root element.");

        var ns = root.Name.Namespace;
        var unit = DebugUnit(root);
        var shapesById = geometry.Shapes.ToDictionary(
            shape => shape.Id,
            StringComparer.Ordinal);

        var group = new XElement(
            ns + "g",
            new XAttribute("id", "debug-classified-symbols"),
            new XAttribute("pointer-events", "none"));

        foreach (var instance in scene.Instances)
        {
            var classification = instance.Classification;

            // This diagnostic intentionally shows every winner emitted by the
            // classifier. Semantic passes may later reject or reinterpret it, but
            // hiding low-confidence or unfamiliar labels here makes classifier
            // behaviour much harder to inspect visually.
            if (classification is null
                || !shapesById.TryGetValue(instance.ShapeId, out var shape))
            {
                continue;
            }

            AddShapeOverlay(
                group,
                ns,
                shape,
                ShapeOverlayColor,
                unit);

            foreach (var absorbedShapeId in instance.AbsorbedPrimitiveShapeIds
                         ?? Array.Empty<string>())
            {
                if (!shapesById.TryGetValue(absorbedShapeId, out var absorbedShape))
                {
                    continue;
                }

                AddShapeOverlay(
                    group,
                    ns,
                    absorbedShape,
                    ShapeOverlayColor,
                    unit);
            }

            AddClassificationLabel(
                group,
                ns,
                instance,
                HumanReadableLabel(classification.Label),
                classification.Confidence,
                ConfidenceColor(classification.Confidence),
                unit);
        }

        root.Add(group);
        document.Save(output, SaveOptions.DisableFormatting);
    }

    private static void AddShapeOverlay(
        XElement group,
        XNamespace ns,
        GeometricShape shape,
        string color,
        double unit)
    {
        foreach (var contour in shape.EffectiveContours)
        {
            if (contour.Points.Count < 2)
            {
                continue;
            }

            var data = BuildPathData(contour);

            group.Add(new XElement(
                ns + "path",
                new XAttribute("d", data),
                new XAttribute("fill", contour.IsClosed ? color : "none"),
                new XAttribute("fill-opacity", contour.IsClosed ? "0.18" : "0"),
                new XAttribute("stroke", color),
                new XAttribute(
                    "stroke-width",
                    F(Math.Max(0.75 * unit, Math.Min(shape.StrokeWidth, 1.5 * unit)))),
                new XAttribute("stroke-linecap", "round"),
                new XAttribute("stroke-linejoin", "round"),
                new XAttribute("opacity", "0.55")));
        }
    }

    private static void AddClassificationLabel(
        XElement group,
        XNamespace ns,
        ShapeInstance instance,
        string classLabel,
        double confidence,
        string confidenceColor,
        double unit)
    {
        var fontSize = Math.Clamp(
            Math.Max(instance.Width, instance.Height) * 0.20,
            4.0 * unit,
            8.0 * unit);

        // Avoid culture-specific percent spacing (for example, InvariantCulture
        // renders P0 as "92 %"). The diagnostic label should stay compact and
        // predictable: TUPLET_5 92%.
        var confidenceText = (confidence * 100.0).ToString(
            "0",
            CultureInfo.InvariantCulture) + "%";
        var label = $"{classLabel} {confidenceText}";

        group.Add(new XElement(
            ns + "text",
            new XAttribute("x", F(instance.X + instance.Width + 1.2 * unit)),
            new XAttribute("y", F(instance.Y + fontSize)),
            new XAttribute("font-family", "Arial, sans-serif"),
            new XAttribute("font-size", F(fontSize)),
            new XAttribute("font-weight", "700"),
            new XAttribute("fill", confidenceColor),
            new XAttribute("stroke", "white"),
            new XAttribute("stroke-width", F(0.9 * unit)),
            new XAttribute("stroke-linejoin", "round"),
            new XAttribute("paint-order", "stroke fill"),
            label));
    }

    private static string HumanReadableLabel(string label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return "UNKNOWN";
        }

        var result = new StringBuilder(label.Length);
        var previousWasSeparator = false;

        foreach (var character in label.Trim())
        {
            if (char.IsLetterOrDigit(character))
            {
                result.Append(char.ToUpperInvariant(character));
                previousWasSeparator = false;
            }
            else if (!previousWasSeparator && result.Length > 0)
            {
                result.Append('_');
                previousWasSeparator = true;
            }
        }

        return result.ToString().TrimEnd('_');
    }

    private static string ConfidenceColor(double confidence) =>
        confidence >= GreenConfidence
            ? "#1a7f37"
            : confidence >= AmberConfidence
                ? "#bf8700"
                : "#cf222e";

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
