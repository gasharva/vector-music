using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace SvgMusic.Semantics;

public sealed class FlagDebugRenderer
{
    public void Render(
        string inputSvgPath,
        FlagAnalysisResult analysis,
        string outputSvgPath)
    {
        var document = XDocument.Load(
            inputSvgPath,
            LoadOptions.PreserveWhitespace);
        var root = document.Root
            ?? throw new InvalidOperationException("SVG has no root element.");
        var ns = root.Name.Namespace;
        var unit = DebugUnit(root);

        var group = new XElement(
            ns + "g",
            new XAttribute("id", "semantic-flags"),
            new XAttribute("pointer-events", "all"));

        foreach (var decision in analysis.Decisions)
        {
            if (decision.Accepted)
            {
                AddAcceptedFlag(
                    group,
                    ns,
                    unit,
                    decision);
            }
            else
            {
                AddRejectedFlag(
                    group,
                    ns,
                    unit,
                    decision);
            }
        }

        AddLegend(
            group,
            ns,
            root,
            unit,
            analysis);

        root.Add(group);
        document.Save(
            outputSvgPath,
            SaveOptions.DisableFormatting);
    }

    public void WriteReport(
        FlagAnalysisResult analysis,
        string outputTextPath)
    {
        var lines = new List<string>
        {
            "FLAG ATTACHMENT ANALYSIS",
            string.Empty,
            $"candidates={analysis.Decisions.Count}",
            $"accepted={analysis.Accepted.Count}",
            $"unmatched={analysis.Decisions.Count(decision => decision.Decision == "no-compatible-stem-tip")}",
            $"ambiguous={analysis.Decisions.Count(decision => decision.Decision == "ambiguous-stem-tip")}",
            $"stem-conflicts={analysis.Decisions.Count(decision => decision.Decision == "stem-already-has-better-flag")}",
            string.Empty,
            "ACCEPTED FLAGS"
        };

        foreach (var decision in analysis.Accepted)
        {
            lines.Add(Describe(decision));
        }

        lines.Add(string.Empty);
        lines.Add("REJECTED CLASSIFIED FLAGS");

        foreach (var decision in analysis.Decisions.Where(decision => !decision.Accepted))
        {
            lines.Add(Describe(decision));
        }

        File.WriteAllLines(
            outputTextPath,
            lines,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void AddAcceptedFlag(
        XElement group,
        XNamespace ns,
        double unit,
        FlagDecision decision)
    {
        var match = decision.Match
            ?? throw new InvalidOperationException(
                $"Accepted flag {decision.Candidate.Shape.ShapeId} has no stem match.");
        var bounds = decision.Candidate.Shape.Bounds;
        var padding = Math.Max(1.8 * unit, 0.6);
        const string color = "#1565c0";

        var rectangle = new XElement(
            ns + "rect",
            new XAttribute("x", F(bounds.MinX - padding)),
            new XAttribute("y", F(bounds.MinY - padding)),
            new XAttribute("width", F(bounds.Width + padding * 2)),
            new XAttribute("height", F(bounds.Height + padding * 2)),
            new XAttribute("fill", color),
            new XAttribute("fill-opacity", "0.10"),
            new XAttribute("stroke", color),
            new XAttribute("stroke-width", F(2.0 * unit)),
            new XAttribute("rx", F(1.5 * unit)),
            new XAttribute("data-flag-shape-id", decision.Candidate.Shape.ShapeId),
            new XAttribute("data-flag-level", decision.Candidate.Level),
            new XAttribute("data-stem-shape-id", match.Stem.StemShapeId));

        rectangle.Add(new XElement(
            ns + "title",
            Describe(decision)));
        group.Add(rectangle);

        var connectorEndX = bounds.MinX;
        var connectorEndY = Math.Clamp(
            match.TipY,
            bounds.MinY,
            bounds.MaxY);

        group.Add(new XElement(
            ns + "line",
            new XAttribute("x1", F(match.TipX)),
            new XAttribute("y1", F(match.TipY)),
            new XAttribute("x2", F(connectorEndX)),
            new XAttribute("y2", F(connectorEndY)),
            new XAttribute("stroke", color),
            new XAttribute("stroke-width", F(1.6 * unit)),
            new XAttribute("stroke-dasharray", $"{F(2.5 * unit)} {F(1.8 * unit)}")));

        group.Add(new XElement(
            ns + "circle",
            new XAttribute("cx", F(match.TipX)),
            new XAttribute("cy", F(match.TipY)),
            new XAttribute("r", F(2.3 * unit)),
            new XAttribute("fill", color),
            new XAttribute("stroke", "white"),
            new XAttribute("stroke-width", F(0.8 * unit))));

        group.Add(new XElement(
            ns + "text",
            new XAttribute("x", F(bounds.MaxX + 2.0 * unit)),
            new XAttribute("y", F(bounds.MinY - 1.5 * unit)),
            new XAttribute("font-family", "Arial, sans-serif"),
            new XAttribute("font-size", F(Math.Max(6.5 * unit, 3.4))),
            new XAttribute("font-weight", "700"),
            new XAttribute("fill", color),
            new XAttribute("stroke", "white"),
            new XAttribute("stroke-width", F(0.8 * unit)),
            new XAttribute("paint-order", "stroke fill"),
            $"F{decision.Candidate.Level}"));
    }

    private static void AddRejectedFlag(
        XElement group,
        XNamespace ns,
        double unit,
        FlagDecision decision)
    {
        var bounds = decision.Candidate.Shape.Bounds;
        var padding = Math.Max(1.4 * unit, 0.5);
        var color = decision.Decision == "ambiguous-stem-tip"
            ? "#c62828"
            : "#757575";

        var rectangle = new XElement(
            ns + "rect",
            new XAttribute("x", F(bounds.MinX - padding)),
            new XAttribute("y", F(bounds.MinY - padding)),
            new XAttribute("width", F(bounds.Width + padding * 2)),
            new XAttribute("height", F(bounds.Height + padding * 2)),
            new XAttribute("fill", "none"),
            new XAttribute("stroke", color),
            new XAttribute("stroke-width", F(1.2 * unit)),
            new XAttribute("stroke-dasharray", $"{F(3.0 * unit)} {F(2.5 * unit)}"),
            new XAttribute("opacity", "0.65"));

        rectangle.Add(new XElement(
            ns + "title",
            Describe(decision)));
        group.Add(rectangle);
    }

    private static string Describe(FlagDecision decision)
    {
        var candidate = decision.Candidate;
        var match = decision.Match;
        var stem = match?.Stem.StemShapeId ?? "-";
        var tip = match is null
            ? "-"
            : $"({match.TipX:F2},{match.TipY:F2})";
        var horizontal = match is null
            ? "-"
            : match.LeftEdgeDistanceInSpacings.ToString("F3", CultureInfo.InvariantCulture);
        var vertical = match is null
            ? "-"
            : match.VerticalDistanceInSpacings.ToString("F3", CultureInfo.InvariantCulture);

        return $"{candidate.Shape.ShapeId}: {candidate.ClassificationLabel}; {decision.Decision}; "
            + $"m{candidate.MeasureNumber}; level={candidate.Level}; "
            + $"classifier={candidate.ClassificationConfidence:P1}; stem={stem}; tip={tip}; "
            + $"left-edge-distance={horizontal}sp; vertical-distance={vertical}sp; "
            + $"confidence={decision.Confidence:P1}; {decision.Reason}";
    }

    private static void AddLegend(
        XElement group,
        XNamespace ns,
        XElement root,
        double unit,
        FlagAnalysisResult analysis)
    {
        var viewBox = ReadViewBox(root);
        if (viewBox is null)
        {
            return;
        }

        var x = viewBox.Value.MinX + 6 * unit;
        var y = viewBox.Value.MinY + 35 * unit;
        var fontSize = Math.Max(5 * unit, 2.8);
        var text = $"flags={analysis.Accepted.Count}/{analysis.Decisions.Count}; "
            + "blue=attached; gray=unmatched classified flag; red=ambiguous";

        group.Add(new XElement(
            ns + "text",
            new XAttribute("x", F(x)),
            new XAttribute("y", F(y)),
            new XAttribute("font-family", "Arial, sans-serif"),
            new XAttribute("font-size", F(fontSize)),
            new XAttribute("font-weight", "700"),
            new XAttribute("fill", "#1565c0"),
            new XAttribute("stroke", "white"),
            new XAttribute("stroke-width", F(0.8 * unit)),
            new XAttribute("paint-order", "stroke fill"),
            text));
    }

    private static double DebugUnit(XElement root)
    {
        var viewBox = ReadViewBox(root);

        if (viewBox is not null
            && viewBox.Value.Width > 0
            && viewBox.Value.Height > 0)
        {
            return Math.Max(
                Math.Min(viewBox.Value.Width, viewBox.Value.Height) / 1000.0,
                0.05);
        }

        return 1.0;
    }

    private static ViewBox? ReadViewBox(XElement root)
    {
        var values = ((string?)root.Attribute("viewBox"))?.Split(
            [' ', ',', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries);

        if (values is not { Length: 4 })
        {
            return null;
        }

        if (!double.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var minX)
            || !double.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var minY)
            || !double.TryParse(values[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var width)
            || !double.TryParse(values[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var height))
        {
            return null;
        }

        return new ViewBox(
            minX,
            minY,
            width,
            height);
    }

    private static string F(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private readonly record struct ViewBox(
        double MinX,
        double MinY,
        double Width,
        double Height);
}
