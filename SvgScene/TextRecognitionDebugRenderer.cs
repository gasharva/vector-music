using System.Globalization;
using System.Xml.Linq;

namespace SvgMusic.Scene;

public sealed class TextRecognitionDebugRenderer
{
    private const string PrototypeColor = "#1565c0";
    private const string RunColor = "#8e24aa";
    private const double GreenConfidence = 0.90;
    private const double AmberConfidence = 0.70;

    public void Render(
        string input,
        TextRecognitionAnalysisResult analysis,
        string output)
    {
        var document = XDocument.Load(input, LoadOptions.PreserveWhitespace);
        var root = document.Root
            ?? throw new InvalidOperationException("SVG has no root element.");
        var ns = root.Name.Namespace;
        var unit = DebugUnit(root);

        var group = new XElement(
            ns + "g",
            new XAttribute("id", "debug-ocr-text"),
            new XAttribute("pointer-events", "none"));

        foreach (var observation in analysis.Observations
                     .Where(item => !string.IsNullOrWhiteSpace(item.Recognition?.Text))
                     .OrderBy(item => item.Kind)
                     .ThenBy(item => item.Bounds.MinY)
                     .ThenBy(item => item.Bounds.MinX))
        {
            AddObservation(group, ns, observation, unit);
        }

        root.Add(group);
        document.Save(output, SaveOptions.DisableFormatting);
    }

    private static void AddObservation(
        XElement group,
        XNamespace ns,
        TextRecognitionObservation observation,
        double unit)
    {
        var recognition = observation.Recognition!;
        var bounds = observation.Bounds;
        var boxColor = observation.Kind == TextCandidateKind.HorizontalRun
            ? RunColor
            : PrototypeColor;
        var strokeWidth = observation.Kind == TextCandidateKind.HorizontalRun
            ? 1.8 * unit
            : 1.0 * unit;
        var dash = observation.Kind == TextCandidateKind.HorizontalRun
            ? null
            : "3 2";

        var rect = new XElement(
            ns + "rect",
            new XAttribute("x", F(bounds.MinX)),
            new XAttribute("y", F(bounds.MinY)),
            new XAttribute("width", F(Math.Max(bounds.Width, unit))),
            new XAttribute("height", F(Math.Max(bounds.Height, unit))),
            new XAttribute("fill", boxColor),
            new XAttribute("fill-opacity", observation.Kind == TextCandidateKind.HorizontalRun ? "0.10" : "0.04"),
            new XAttribute("stroke", boxColor),
            new XAttribute("stroke-width", F(strokeWidth)),
            new XAttribute("opacity", "0.9"));

        if (dash is not null)
        {
            rect.Add(new XAttribute("stroke-dasharray", dash));
        }

        group.Add(rect);

        var confidenceText = (recognition.Confidence * 100.0).ToString(
            "0",
            CultureInfo.InvariantCulture) + "%";
        var prefix = observation.Kind == TextCandidateKind.HorizontalRun
            ? "OCR RUN"
            : "OCR";
        var label = $"{prefix}: {recognition.Text} {confidenceText}";
        var fontSize = Math.Clamp(
            Math.Max(bounds.Height * 0.30, 4.5 * unit),
            4.5 * unit,
            9.0 * unit);
        var x = bounds.MinX;
        var y = Math.Max(fontSize, bounds.MinY - 1.2 * unit);

        group.Add(new XElement(
            ns + "text",
            new XAttribute("x", F(x)),
            new XAttribute("y", F(y)),
            new XAttribute("font-family", "Arial, sans-serif"),
            new XAttribute("font-size", F(fontSize)),
            new XAttribute("font-weight", observation.Kind == TextCandidateKind.HorizontalRun ? "700" : "600"),
            new XAttribute("fill", ConfidenceColor(recognition.Confidence)),
            new XAttribute("stroke", "white"),
            new XAttribute("stroke-width", F(0.9 * unit)),
            new XAttribute("stroke-linejoin", "round"),
            new XAttribute("paint-order", "stroke fill"),
            label));
    }

    private static string ConfidenceColor(double confidence) =>
        confidence >= GreenConfidence
            ? "#1a7f37"
            : confidence >= AmberConfidence
                ? "#bf8700"
                : "#cf222e";

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
