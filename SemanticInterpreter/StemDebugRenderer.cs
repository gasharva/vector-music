using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace SvgMusic.Semantics;

public sealed class StemDebugRenderer
{
    public void Render(
        string inputSvgPath,
        StemAnalysisResult analysis,
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
            new XAttribute("id", "semantic-stems"),
            new XAttribute("pointer-events", "all"));

        foreach (var decision in analysis.Decisions)
        {
            if (decision.Accepted)
            {
                AddAcceptedStem(
                    group,
                    ns,
                    unit,
                    decision);
                AddAttachedNoteheads(
                    group,
                    ns,
                    unit,
                    decision);
            }
            else
            {
                AddRejectedCandidate(
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
        StemAnalysisResult analysis,
        string outputTextPath)
    {
        var lines = new List<string>
        {
            "STEM ATTACHMENT ANALYSIS",
            string.Empty,
            $"candidates={analysis.Decisions.Count}",
            $"accepted={analysis.Accepted.Count}",
            $"unmatched={analysis.Decisions.Count(decision => !decision.Accepted)}",
            $"cross-staff={analysis.Accepted.Count(decision => decision.IsCrossStaff)}",
            $"ambiguous-direction={analysis.Accepted.Count(decision => decision.Direction == StemDirection.Ambiguous)}",
            $"noteheads-attached={analysis.AttachedNoteheads}/{analysis.TotalNoteheads}",
            string.Empty,
            "ACCEPTED STEMS"
        };

        foreach (var decision in analysis.Accepted)
        {
            lines.Add(Describe(decision));
        }

        lines.Add(string.Empty);
        lines.Add("UNMATCHED VERTICAL STROKE CANDIDATES");

        foreach (var decision in analysis.Decisions.Where(decision => !decision.Accepted))
        {
            lines.Add(Describe(decision));
        }

        File.WriteAllLines(
            outputTextPath,
            lines,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void AddAcceptedStem(
        XElement group,
        XNamespace ns,
        double unit,
        StemDecision decision)
    {
        var source = decision.Candidate.Stroke.Source;
        var color = decision.IsCrossStaff
            ? "#6a1b9a"
            : "#d81b60";
        var line = new XElement(
            ns + "line",
            new XAttribute("x1", F(source.Start.X)),
            new XAttribute("y1", F(source.Start.Y)),
            new XAttribute("x2", F(source.End.X)),
            new XAttribute("y2", F(source.End.Y)),
            new XAttribute("stroke", color),
            new XAttribute("stroke-width", F(Math.Max(3.0 * unit, 1.2))),
            new XAttribute("stroke-linecap", "round"),
            new XAttribute("opacity", "0.88"),
            new XAttribute("data-stem-shape-id", source.ShapeId),
            new XAttribute("data-stem-direction", decision.Direction),
            new XAttribute("data-cross-staff", decision.IsCrossStaff));

        line.Add(new XElement(
            ns + "title",
            Describe(decision)));
        group.Add(line);

        var labelX = (source.Start.X + source.End.X) / 2.0 + 3.0 * unit;
        var labelY = Math.Min(source.Start.Y, source.End.Y) - 2.0 * unit;
        var label = decision.Direction switch
        {
            StemDirection.Up => "↑",
            StemDirection.Down => "↓",
            _ => "?"
        };

        group.Add(new XElement(
            ns + "text",
            new XAttribute("x", F(labelX)),
            new XAttribute("y", F(labelY)),
            new XAttribute("font-family", "Arial, sans-serif"),
            new XAttribute("font-size", F(Math.Max(6.5 * unit, 3.4))),
            new XAttribute("font-weight", "700"),
            new XAttribute("fill", color),
            new XAttribute("stroke", "white"),
            new XAttribute("stroke-width", F(0.8 * unit)),
            new XAttribute("paint-order", "stroke fill"),
            label));
    }

    private static void AddAttachedNoteheads(
        XElement group,
        XNamespace ns,
        double unit,
        StemDecision decision)
    {
        var color = decision.IsCrossStaff
            ? "#6a1b9a"
            : "#d81b60";

        foreach (var match in decision.Matches)
        {
            var notehead = match.Notehead;
            var rx = Math.Max(notehead.MajorRadius + 2.5 * unit, 2.5 * unit);
            var ry = Math.Max(notehead.MinorRadius + 2.5 * unit, 2.5 * unit);
            var ring = new XElement(
                ns + "ellipse",
                new XAttribute("cx", F(notehead.CenterX)),
                new XAttribute("cy", F(notehead.CenterY)),
                new XAttribute("rx", F(rx)),
                new XAttribute("ry", F(ry)),
                new XAttribute("fill", "none"),
                new XAttribute("stroke", color),
                new XAttribute("stroke-width", F(1.8 * unit)),
                new XAttribute("data-stem-source", decision.Candidate.Stroke.ShapeId),
                new XAttribute("data-notehead-id", notehead.ShapeId));

            ring.Add(new XElement(
                ns + "title",
                $"{notehead.ShapeId} attached to stem {decision.Candidate.Stroke.ShapeId}; "
                + $"edge-distance={match.EdgeDistanceInSpacings:F3}sp; score={match.Score:P1}"));
            group.Add(ring);
        }
    }

    private static void AddRejectedCandidate(
        XElement group,
        XNamespace ns,
        double unit,
        StemDecision decision)
    {
        var source = decision.Candidate.Stroke.Source;
        var line = new XElement(
            ns + "line",
            new XAttribute("x1", F(source.Start.X)),
            new XAttribute("y1", F(source.Start.Y)),
            new XAttribute("x2", F(source.End.X)),
            new XAttribute("y2", F(source.End.Y)),
            new XAttribute("stroke", "#757575"),
            new XAttribute("stroke-width", F(Math.Max(1.2 * unit, 0.6))),
            new XAttribute("stroke-dasharray", $"{F(3.0 * unit)} {F(3.0 * unit)}"),
            new XAttribute("opacity", "0.35"));

        line.Add(new XElement(
            ns + "title",
            Describe(decision)));
        group.Add(line);
    }

    private static string Describe(StemDecision decision)
    {
        var candidate = decision.Candidate;
        var source = candidate.Stroke.Source;
        var noteheads = decision.Matches.Count == 0
            ? "-"
            : string.Join(
                ",",
                decision.Matches.Select(match =>
                    $"{match.Notehead.ShapeId}/s{match.Notehead.Staff}"));

        return $"{source.ShapeId}: {decision.Decision}; m{candidate.MeasureNumber}; "
            + $"from=({source.Start.X:F2},{source.Start.Y:F2}) "
            + $"to=({source.End.X:F2},{source.End.Y:F2}); "
            + $"length={candidate.Length / candidate.LineSpacing:F2}sp; "
            + $"width={candidate.NormalizedWidth:F3}sp; "
            + $"vertical-ratio={candidate.VerticalRatio:F3}; "
            + $"direction={decision.Direction}; cross-staff={decision.IsCrossStaff}; "
            + $"noteheads=[{noteheads}]; confidence={decision.Confidence:P1}; "
            + decision.Reason;
    }

    private static void AddLegend(
        XElement group,
        XNamespace ns,
        XElement root,
        double unit,
        StemAnalysisResult analysis)
    {
        var viewBox = ReadViewBox(root);
        if (viewBox is null)
        {
            return;
        }

        var x = viewBox.Value.MinX + 6 * unit;
        var y = viewBox.Value.MinY + 27 * unit;
        var fontSize = Math.Max(5 * unit, 2.8);
        var text = $"stems={analysis.Accepted.Count}; "
            + $"attached-noteheads={analysis.AttachedNoteheads}/{analysis.TotalNoteheads}; "
            + $"purple=cross-staff; gray=unmatched vertical candidate";

        group.Add(new XElement(
            ns + "text",
            new XAttribute("x", F(x)),
            new XAttribute("y", F(y)),
            new XAttribute("font-family", "Arial, sans-serif"),
            new XAttribute("font-size", F(fontSize)),
            new XAttribute("font-weight", "700"),
            new XAttribute("fill", "#d81b60"),
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
