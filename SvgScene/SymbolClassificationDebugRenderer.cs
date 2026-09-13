using System.Globalization;
using System.Xml.Linq;

namespace SvgMusic.Scene;

public sealed class SymbolClassificationDebugRenderer
{
    private const double MinimumConfidence = 0.75;

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

            if (classification is null
                || classification.Confidence < MinimumConfidence
                || !TryStyle(classification.Label, out var style)
                || !shapesById.TryGetValue(instance.ShapeId, out var shape))
            {
                continue;
            }

            AddShapeOverlay(
                group,
                ns,
                shape,
                style.Color,
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
                    style.Color,
                    unit);
            }

            if (style.DigitText is not null)
            {
                AddDigitLabel(
                    group,
                    ns,
                    instance,
                    style.DigitText,
                    classification.Confidence,
                    style.Color,
                    unit);
            }
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
                new XAttribute("fill-opacity", contour.IsClosed ? "0.72" : "0"),
                new XAttribute("stroke", color),
                new XAttribute(
                    "stroke-width",
                    F(Math.Max(unit, shape.StrokeWidth))),
                new XAttribute("stroke-linecap", "round"),
                new XAttribute("stroke-linejoin", "round"),
                new XAttribute("opacity", "0.9")));
        }
    }

    private static void AddDigitLabel(
        XElement group,
        XNamespace ns,
        ShapeInstance instance,
        string digit,
        double confidence,
        string color,
        double unit)
    {
        var fontSize = Math.Clamp(
            Math.Max(instance.Width, instance.Height) * 0.24,
            3.8 * unit,
            7.0 * unit);

        var label = $"{digit} {confidence:P0}";

        group.Add(new XElement(
            ns + "text",
            new XAttribute("x", F(instance.X + instance.Width + 1.2 * unit)),
            new XAttribute("y", F(instance.Y + fontSize)),
            new XAttribute("font-family", "Arial, sans-serif"),
            new XAttribute("font-size", F(fontSize)),
            new XAttribute("font-weight", "700"),
            new XAttribute("fill", color),
            new XAttribute("stroke", "white"),
            new XAttribute("stroke-width", F(0.7 * unit)),
            new XAttribute("paint-order", "stroke fill"),
            label));
    }

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

    private static bool TryStyle(
        string label,
        out SymbolStyle style)
    {
        if (label is "G_CLEF" or "F_CLEF")
        {
            style = new SymbolStyle("#7b1fa2", null);
            return true;
        }

        if (TryDigit(label, out var digit))
        {
            style = new SymbolStyle("#1565c0", digit);
            return true;
        }

        if (label.Contains("REST", StringComparison.Ordinal))
        {
            style = new SymbolStyle("#2e7d32", null);
            return true;
        }

        if (label.StartsWith("FLAG_1", StringComparison.Ordinal))
        {
            style = new SymbolStyle("#f9a825", null);
            return true;
        }

        if (label.StartsWith("FLAG_2", StringComparison.Ordinal))
        {
            style = new SymbolStyle("#ef6c00", null);
            return true;
        }

        if (label.Contains("FLAT", StringComparison.Ordinal)
            || label.Contains("SHARP", StringComparison.Ordinal)
            || label.Contains("NATURAL", StringComparison.Ordinal))
        {
            style = new SymbolStyle("#c62828", null);
            return true;
        }

        style = default!;
        return false;
    }

    private static bool TryDigit(
        string label,
        out string? digit)
    {
        const string timePrefix = "TIME_";
        const string tupletPrefix = "TUPLET_";
        const string digitPrefix = "DIGIT_";

        string? suffix = null;

        if (label.StartsWith(timePrefix, StringComparison.Ordinal))
        {
            suffix = label[timePrefix.Length..];
        }
        else if (label.StartsWith(tupletPrefix, StringComparison.Ordinal))
        {
            suffix = label[tupletPrefix.Length..];
        }
        else if (label.StartsWith(digitPrefix, StringComparison.Ordinal))
        {
            suffix = label[digitPrefix.Length..];
        }

        digit = suffix switch
        {
            "0" or "ZERO" => "0",
            "1" or "ONE" => "1",
            "2" or "TWO" => "2",
            "3" or "THREE" => "3",
            "4" or "FOUR" => "4",
            "5" or "FIVE" => "5",
            "6" or "SIX" => "6",
            "7" or "SEVEN" => "7",
            "8" or "EIGHT" => "8",
            "9" or "NINE" => "9",
            "10" or "TEN" => "10",
            "11" or "ELEVEN" => "11",
            "12" or "TWELVE" => "12",
            _ => null
        };

        return digit is not null;
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

    private sealed record SymbolStyle(
        string Color,
        string? DigitText);
}
