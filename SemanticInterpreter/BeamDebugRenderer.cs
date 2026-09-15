using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace SvgMusic.Semantics;

public sealed class BeamDebugRenderer
{
    public void Render(
        string inputSvgPath,
        BeamAnalysisResult analysis,
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
            new XAttribute("id", "semantic-beams"),
            new XAttribute("pointer-events", "all"));

        foreach (var decision in analysis.Decisions)
        {
            if (decision.Accepted)
            {
                AddAcceptedBeam(
                    group,
                    ns,
                    unit,
                    decision);
                AddStemIntersections(
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
        BeamAnalysisResult analysis,
        string outputTextPath)
    {
        var lines = new List<string>
        {
            "BEAM ATTACHMENT ANALYSIS",
            string.Empty,
            $"candidates={analysis.Decisions.Count}",
            $"accepted={analysis.Accepted.Count}",
            $"rejected={analysis.Decisions.Count(decision => !decision.Accepted)}",
            $"hooks={analysis.Accepted.Count(decision => decision.IsHook)}",
            $"cross-staff={analysis.Accepted.Count(decision => decision.IsCrossStaff)}",
            $"level-1={analysis.Accepted.Count(decision => decision.Level == 1)}",
            $"level-2={analysis.Accepted.Count(decision => decision.Level == 2)}",
            $"level-3={analysis.Accepted.Count(decision => decision.Level == 3)}",
            $"level-4+={analysis.Accepted.Count(decision => decision.Level >= 4)}",
            string.Empty,
            "ACCEPTED BEAMS"
        };

        foreach (var decision in analysis.Accepted)
        {
            lines.Add(Describe(decision));
        }

        lines.Add(string.Empty);
        lines.Add("REJECTED BEAM-LIKE STROKES");

        foreach (var decision in analysis.Decisions.Where(decision => !decision.Accepted))
        {
            lines.Add(Describe(decision));
        }

        File.WriteAllLines(
            outputTextPath,
            lines,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void AddAcceptedBeam(
        XElement group,
        XNamespace ns,
        double unit,
        BeamDecision decision)
    {
        var source = decision.Candidate.Stroke.Source;
        var color = BeamColor(
            decision.Level ?? 0,
            decision.IsCrossStaff);
        var width = Math.Max(
            source.Width * 0.55,
            2.6 * unit);
        var line = new XElement(
            ns + "line",
            new XAttribute("x1", F(source.Start.X)),
            new XAttribute("y1", F(source.Start.Y)),
            new XAttribute("x2", F(source.End.X)),
            new XAttribute("y2", F(source.End.Y)),
            new XAttribute("stroke", color),
            new XAttribute("stroke-width", F(width)),
            new XAttribute("stroke-linecap", "butt"),
            new XAttribute("opacity", "0.78"),
            new XAttribute("data-beam-shape-id", source.ShapeId),
            new XAttribute("data-beam-level", decision.Level ?? 0),
            new XAttribute("data-beam-hook", decision.IsHook),
            new XAttribute("data-cross-staff", decision.IsCrossStaff));

        line.Add(new XElement(
            ns + "title",
            Describe(decision)));
        group.Add(line);

        var left = source.Start.X <= source.End.X
            ? source.Start
            : source.End;
        var right = source.Start.X <= source.End.X
            ? source.End
            : source.Start;

        AddEndpoint(
            group,
            ns,
            unit,
            left.X,
            left.Y,
            decision.LeftEndSupported,
            color,
            "left");
        AddEndpoint(
            group,
            ns,
            unit,
            right.X,
            right.Y,
            decision.RightEndSupported,
            color,
            "right");

        var labelX = (source.Start.X + source.End.X) / 2.0;
        var labelY = (source.Start.Y + source.End.Y) / 2.0 - 4.0 * unit;
        var label = $"B{decision.Level}"
            + (decision.IsHook ? "H" : string.Empty)
            + (decision.IsCrossStaff ? "X" : string.Empty);

        group.Add(new XElement(
            ns + "text",
            new XAttribute("x", F(labelX)),
            new XAttribute("y", F(labelY)),
            new XAttribute("font-family", "Arial, sans-serif"),
            new XAttribute("font-size", F(Math.Max(6.0 * unit, 3.2))),
            new XAttribute("font-weight", "700"),
            new XAttribute("text-anchor", "middle"),
            new XAttribute("fill", color),
            new XAttribute("stroke", "white"),
            new XAttribute("stroke-width", F(0.8 * unit)),
            new XAttribute("paint-order", "stroke fill"),
            label));
    }

    private static void AddStemIntersections(
        XElement group,
        XNamespace ns,
        double unit,
        BeamDecision decision)
    {
        var color = BeamColor(
            decision.Level ?? 0,
            decision.IsCrossStaff);

        foreach (var match in decision.Matches)
        {
            var circle = new XElement(
                ns + "circle",
                new XAttribute("cx", F(match.StemX)),
                new XAttribute("cy", F(match.BeamY)),
                new XAttribute("r", F(2.0 * unit)),
                new XAttribute("fill", color),
                new XAttribute("stroke", "white"),
                new XAttribute("stroke-width", F(0.7 * unit)),
                new XAttribute("data-beam-source", decision.Candidate.Stroke.ShapeId),
                new XAttribute("data-stem-id", match.Stem.StemShapeId));

            circle.Add(new XElement(
                ns + "title",
                $"beam {decision.Candidate.Stroke.ShapeId} level={decision.Level} "
                + $"intersects stem {match.Stem.StemShapeId}; "
                + $"distance-from-free-tip={match.DistanceFromFreeTipInSpacings:F3}sp"));
            group.Add(circle);
        }
    }

    private static void AddEndpoint(
        XElement group,
        XNamespace ns,
        double unit,
        double x,
        double y,
        bool supported,
        string color,
        string side)
    {
        var circle = new XElement(
            ns + "circle",
            new XAttribute("cx", F(x)),
            new XAttribute("cy", F(y)),
            new XAttribute("r", F(2.8 * unit)),
            new XAttribute("fill", supported ? color : "white"),
            new XAttribute("stroke", supported ? "white" : "#e53935"),
            new XAttribute("stroke-width", F(1.0 * unit)),
            new XAttribute("data-beam-end", side),
            new XAttribute("data-supported", supported));

        circle.Add(new XElement(
            ns + "title",
            supported
                ? $"{side} beam end supported by stem"
                : $"{side} beam end is free (hook)"));
        group.Add(circle);
    }

    private static void AddRejectedCandidate(
        XElement group,
        XNamespace ns,
        double unit,
        BeamDecision decision)
    {
        var source = decision.Candidate.Stroke.Source;
        var line = new XElement(
            ns + "line",
            new XAttribute("x1", F(source.Start.X)),
            new XAttribute("y1", F(source.Start.Y)),
            new XAttribute("x2", F(source.End.X)),
            new XAttribute("y2", F(source.End.Y)),
            new XAttribute("stroke", "#757575"),
            new XAttribute("stroke-width", F(Math.Max(1.3 * unit, 0.7))),
            new XAttribute("stroke-dasharray", $"{F(3.0 * unit)} {F(3.0 * unit)}"),
            new XAttribute("opacity", "0.25"));

        line.Add(new XElement(
            ns + "title",
            Describe(decision)));
        group.Add(line);
    }

    private static string Describe(BeamDecision decision)
    {
        var candidate = decision.Candidate;
        var source = candidate.Stroke.Source;
        var stems = decision.Matches.Count == 0
            ? "-"
            : string.Join(
                ",",
                decision.Matches.Select(match =>
                    $"{match.Stem.StemShapeId}@{match.DistanceFromFreeTipInSpacings:F2}sp"));

        return $"{source.ShapeId}: {decision.Decision}; m{candidate.MeasureNumber}; "
            + $"level={decision.Level?.ToString() ?? "-"}; "
            + $"from=({source.Start.X:F2},{source.Start.Y:F2}) "
            + $"to=({source.End.X:F2},{source.End.Y:F2}); "
            + $"length={candidate.LengthInSpacings:F2}sp; "
            + $"width={candidate.WidthInSpacings:F3}sp; slope={candidate.Slope:F3}; "
            + $"left={decision.LeftEndSupported}; right={decision.RightEndSupported}; "
            + $"hook={decision.IsHook}; cross-staff={decision.IsCrossStaff}; "
            + $"stems=[{stems}]; confidence={decision.Confidence:P1}; "
            + decision.Reason;
    }

    private static string BeamColor(
        int level,
        bool crossStaff)
    {
        if (crossStaff)
        {
            return "#6a1b9a";
        }

        return level switch
        {
            1 => "#00897b",
            2 => "#039be5",
            3 => "#3949ab",
            4 => "#5e35b1",
            _ => "#7e57c2"
        };
    }

    private static void AddLegend(
        XElement group,
        XNamespace ns,
        XElement root,
        double unit,
        BeamAnalysisResult analysis)
    {
        var viewBox = ReadViewBox(root);
        if (viewBox is null)
        {
            return;
        }

        var x = viewBox.Value.MinX + 6 * unit;
        var y = viewBox.Value.MinY + 43 * unit;
        var fontSize = Math.Max(5 * unit, 2.8);
        var text = $"beams={analysis.Accepted.Count}; "
            + $"L1={analysis.Accepted.Count(decision => decision.Level == 1)}; "
            + $"L2={analysis.Accepted.Count(decision => decision.Level == 2)}; "
            + $"L3={analysis.Accepted.Count(decision => decision.Level == 3)}; "
            + $"hooks={analysis.Accepted.Count(decision => decision.IsHook)}; "
            + "red hollow endpoint=free hook end";

        group.Add(new XElement(
            ns + "text",
            new XAttribute("x", F(x)),
            new XAttribute("y", F(y)),
            new XAttribute("font-family", "Arial, sans-serif"),
            new XAttribute("font-size", F(fontSize)),
            new XAttribute("font-weight", "700"),
            new XAttribute("fill", "#00897b"),
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
