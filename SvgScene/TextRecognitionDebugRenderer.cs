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

        var recognized = analysis.Observations
            .Where(item => !string.IsNullOrWhiteSpace(item.Recognition?.Text))
            .OrderBy(item => item.Kind)
            .ThenBy(item => item.Bounds.MinY)
            .ThenBy(item => item.Bounds.MinX)
            .ToArray();

        var recognizedRuns = recognized
            .Where(item => item.Kind == TextCandidateKind.HorizontalRun)
            .ToArray();

        var primaryLabels = recognized
            .Where(item =>
                item.Kind == TextCandidateKind.HorizontalRun
                || IsCompoundPrototype(item))
            .Where(item =>
                item.Kind == TextCandidateKind.HorizontalRun
                || !recognizedRuns.Any(run => SharesSourceShape(run, item)))
            .OrderBy(item => item.Bounds.MinY)
            .ThenBy(item => item.Bounds.MinX)
            .ToArray();

        var group = new XElement(
            ns + "g",
            new XAttribute("id", "debug-ocr-text"));

        foreach (var observation in recognized)
        {
            AddObservationBox(group, ns, observation, unit);
        }

        AddPrimaryLabels(group, ns, primaryLabels, unit);

        root.Add(group);
        document.Save(output, SaveOptions.DisableFormatting);
    }

    private static void AddObservationBox(
        XElement group,
        XNamespace ns,
        TextRecognitionObservation observation,
        double unit)
    {
        var recognition = observation.Recognition!;
        var bounds = observation.Bounds;
        var isRun = observation.Kind == TextCandidateKind.HorizontalRun;
        var isCompound = IsCompoundPrototype(observation);
        var boxColor = isRun ? RunColor : PrototypeColor;
        var strokeWidth = isRun
            ? 1.7 * unit
            : isCompound
                ? 1.1 * unit
                : 0.55 * unit;
        var dash = isRun ? null : isCompound ? "5 2" : "2 2";
        var opacity = isRun ? 0.90 : isCompound ? 0.72 : 0.38;
        var fillOpacity = isRun ? 0.08 : isCompound ? 0.035 : 0.0;

        var tooltip = $"{KindLabel(observation)}: {recognition.Text} "
            + $"({recognition.Confidence * 100.0:0.0}%)"
            + $" | {string.Join(", ", observation.SourceShapeIds)}";

        var rect = new XElement(
            ns + "rect",
            new XAttribute("x", F(bounds.MinX)),
            new XAttribute("y", F(bounds.MinY)),
            new XAttribute("width", F(Math.Max(bounds.Width, unit))),
            new XAttribute("height", F(Math.Max(bounds.Height, unit))),
            new XAttribute("fill", boxColor),
            new XAttribute("fill-opacity", F(fillOpacity)),
            new XAttribute("stroke", boxColor),
            new XAttribute("stroke-width", F(strokeWidth)),
            new XAttribute("opacity", F(opacity)),
            new XAttribute("pointer-events", "all"),
            new XElement(ns + "title", tooltip));

        if (dash is not null)
        {
            rect.Add(new XAttribute("stroke-dasharray", dash));
        }

        group.Add(rect);
    }

    private static void AddPrimaryLabels(
        XElement group,
        XNamespace ns,
        IReadOnlyList<TextRecognitionObservation> observations,
        double unit)
    {
        var placed = new List<LabelBox>();

        foreach (var observation in observations)
        {
            var recognition = observation.Recognition!;
            var bounds = observation.Bounds;
            var isRun = observation.Kind == TextCandidateKind.HorizontalRun;
            var borderColor = isRun ? RunColor : PrototypeColor;
            var confidenceText = (recognition.Confidence * 100.0).ToString(
                "0",
                CultureInfo.InvariantCulture) + "%";
            var label = $"{recognition.Text} · {confidenceText}";
            var fontSize = isRun ? 4.8 * unit : 4.2 * unit;
            var paddingX = 1.8 * unit;
            var paddingY = 1.1 * unit;
            var labelWidth = Math.Max(
                18.0 * unit,
                label.Length * fontSize * 0.61 + 2 * paddingX);
            var labelHeight = fontSize + 2 * paddingY;
            var position = FindLabelPosition(
                bounds,
                labelWidth,
                labelHeight,
                unit,
                placed);

            placed.Add(position);

            var anchorX = Math.Clamp(
                bounds.CenterX,
                position.X,
                position.X + position.Width);
            var anchorY = position.Y > bounds.CenterY
                ? position.Y
                : position.Y + position.Height;
            var sourceY = position.Y > bounds.CenterY
                ? bounds.MaxY
                : bounds.MinY;

            group.Add(new XElement(
                ns + "line",
                new XAttribute("x1", F(bounds.CenterX)),
                new XAttribute("y1", F(sourceY)),
                new XAttribute("x2", F(anchorX)),
                new XAttribute("y2", F(anchorY)),
                new XAttribute("stroke", borderColor),
                new XAttribute("stroke-width", F(0.55 * unit)),
                new XAttribute("opacity", "0.72"),
                new XAttribute("pointer-events", "none")));

            group.Add(new XElement(
                ns + "rect",
                new XAttribute("x", F(position.X)),
                new XAttribute("y", F(position.Y)),
                new XAttribute("width", F(position.Width)),
                new XAttribute("height", F(position.Height)),
                new XAttribute("rx", F(1.2 * unit)),
                new XAttribute("fill", "white"),
                new XAttribute("fill-opacity", "0.94"),
                new XAttribute("stroke", borderColor),
                new XAttribute("stroke-width", F(0.7 * unit)),
                new XAttribute("pointer-events", "none")));

            group.Add(new XElement(
                ns + "text",
                new XAttribute("x", F(position.X + paddingX)),
                new XAttribute("y", F(position.Y + paddingY + fontSize * 0.82)),
                new XAttribute("font-family", "Arial, sans-serif"),
                new XAttribute("font-size", F(fontSize)),
                new XAttribute("font-weight", isRun ? "700" : "600"),
                new XAttribute("fill", ConfidenceColor(recognition.Confidence)),
                new XAttribute("pointer-events", "none"),
                label));
        }
    }

    private static LabelBox FindLabelPosition(
        BoundsD bounds,
        double width,
        double height,
        double unit,
        IReadOnlyList<LabelBox> placed)
    {
        var gap = 1.8 * unit;
        var laneGap = 0.9 * unit;

        for (var lane = 0; lane < 5; lane++)
        {
            var candidate = new LabelBox(
                bounds.MinX,
                bounds.MinY - gap - height - lane * (height + laneGap),
                width,
                height);

            if (candidate.Y >= 0 && !placed.Any(item => Intersects(candidate, item, unit)))
            {
                return candidate;
            }
        }

        for (var lane = 0; lane < 5; lane++)
        {
            var candidate = new LabelBox(
                bounds.MinX,
                bounds.MaxY + gap + lane * (height + laneGap),
                width,
                height);

            if (!placed.Any(item => Intersects(candidate, item, unit)))
            {
                return candidate;
            }
        }

        return new LabelBox(
            bounds.MinX,
            Math.Max(0, bounds.MinY - gap - height),
            width,
            height);
    }

    private static bool Intersects(LabelBox first, LabelBox second, double unit)
    {
        var margin = 0.8 * unit;
        return first.X < second.X + second.Width + margin
            && first.X + first.Width + margin > second.X
            && first.Y < second.Y + second.Height + margin
            && first.Y + first.Height + margin > second.Y;
    }

    private static bool IsCompoundPrototype(TextRecognitionObservation observation) =>
        observation.Kind == TextCandidateKind.Prototype
        && observation.SourceShapeIds.Count > 1;

    private static bool SharesSourceShape(
        TextRecognitionObservation first,
        TextRecognitionObservation second)
    {
        var firstIds = first.SourceShapeIds.ToHashSet(StringComparer.Ordinal);
        return second.SourceShapeIds.Any(firstIds.Contains);
    }

    private static string KindLabel(TextRecognitionObservation observation) =>
        observation.Kind == TextCandidateKind.HorizontalRun
            ? "OCR RUN"
            : IsCompoundPrototype(observation)
                ? "OCR COMPOUND"
                : "OCR GLYPH";

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

    private sealed record LabelBox(
        double X,
        double Y,
        double Width,
        double Height);
}
