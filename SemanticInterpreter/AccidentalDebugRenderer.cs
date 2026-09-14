using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace SvgMusic.Semantics;

public sealed class AccidentalDebugRenderer
{
    public void Render(
        string inputSvgPath,
        AccidentalAnalysisResult analysis,
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
            new XAttribute("id", "semantic-accidentals"),
            new XAttribute("pointer-events", "all"));

        foreach (var decision in analysis.Decisions)
        {
            if (decision.Accepted)
            {
                AddAcceptedAccidental(
                    group,
                    ns,
                    unit,
                    decision);
                AddAffectedNoteheads(
                    group,
                    ns,
                    unit,
                    decision);
            }
            else if (decision.Decision == "no-right-notehead-on-anchor-line")
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
        AccidentalAnalysisResult analysis,
        string outputTextPath)
    {
        var lines = new List<string>
        {
            "ACCIDENTAL ANALYSIS",
            string.Empty,
            $"candidates={analysis.Decisions.Count}",
            $"accepted={analysis.Accepted.Count}",
            $"key-signature={analysis.Decisions.Count(decision => decision.Decision == "key-signature-symbol")}",
            $"no-right-notehead={analysis.Decisions.Count(decision => decision.Decision == "no-right-notehead-on-anchor-line")}",
            string.Empty,
            "ACCEPTED LOCAL ACCIDENTALS"
        };

        foreach (var decision in analysis.Accepted)
        {
            lines.Add(Describe(decision));
        }

        lines.Add(string.Empty);
        lines.Add("REJECTED CLASSIFIED ACCIDENTALS");

        foreach (var decision in analysis.Decisions.Where(decision => !decision.Accepted))
        {
            lines.Add(Describe(decision));
        }

        File.WriteAllLines(
            outputTextPath,
            lines,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void AddAcceptedAccidental(
        XElement group,
        XNamespace ns,
        double unit,
        AccidentalDecision decision)
    {
        var bounds = decision.Candidate.Shape.Bounds;
        var padding = Math.Max(1.8 * unit, 0.6);
        var label = KindToken(decision.Candidate.Kind);

        var rectangle = new XElement(
            ns + "rect",
            new XAttribute("x", F(bounds.MinX - padding)),
            new XAttribute("y", F(bounds.MinY - padding)),
            new XAttribute("width", F(bounds.Width + padding * 2)),
            new XAttribute("height", F(bounds.Height + padding * 2)),
            new XAttribute("fill", "#ef6c00"),
            new XAttribute("fill-opacity", "0.10"),
            new XAttribute("stroke", "#ef6c00"),
            new XAttribute("stroke-width", F(2.0 * unit)),
            new XAttribute("rx", F(1.8 * unit)),
            new XAttribute("data-accidental-kind", decision.Candidate.Kind),
            new XAttribute("data-shape-id", decision.Candidate.Shape.ShapeId));

        rectangle.Add(new XElement(
            ns + "title",
            Describe(decision)));
        group.Add(rectangle);

        group.Add(new XElement(
            ns + "circle",
            new XAttribute("cx", F(decision.Candidate.Anchor.X)),
            new XAttribute("cy", F(decision.Candidate.Anchor.Y)),
            new XAttribute("r", F(2.4 * unit)),
            new XAttribute("fill", "#ef6c00"),
            new XAttribute("stroke", "white"),
            new XAttribute("stroke-width", F(0.9 * unit))));

        group.Add(new XElement(
            ns + "text",
            new XAttribute("x", F(bounds.MinX - padding)),
            new XAttribute("y", F(bounds.MinY - 3.0 * unit)),
            new XAttribute("font-family", "Arial, sans-serif"),
            new XAttribute("font-size", F(Math.Max(7.0 * unit, 3.6))),
            new XAttribute("font-weight", "700"),
            new XAttribute("fill", "#ef6c00"),
            new XAttribute("stroke", "white"),
            new XAttribute("stroke-width", F(0.8 * unit)),
            new XAttribute("paint-order", "stroke fill"),
            label));
    }

    private static void AddAffectedNoteheads(
        XElement group,
        XNamespace ns,
        double unit,
        AccidentalDecision decision)
    {
        if (decision.ExplicitTarget is null)
        {
            return;
        }

        foreach (var notehead in decision.AffectedNoteheads)
        {
            var explicitTarget = notehead.ShapeId == decision.ExplicitTarget.ShapeId;
            var rx = Math.Max(notehead.MajorRadius + 3.0 * unit, 3.0 * unit);
            var ry = Math.Max(notehead.MinorRadius + 3.0 * unit, 3.0 * unit);

            var ring = new XElement(
                ns + "ellipse",
                new XAttribute("cx", F(notehead.CenterX)),
                new XAttribute("cy", F(notehead.CenterY)),
                new XAttribute("rx", F(rx)),
                new XAttribute("ry", F(ry)),
                new XAttribute("fill", "none"),
                new XAttribute("stroke", "#ef6c00"),
                new XAttribute(
                    "stroke-width",
                    F((explicitTarget ? 2.7 : 1.5) * unit)),
                new XAttribute("data-accidental-source", decision.Candidate.Shape.ShapeId),
                new XAttribute("data-notehead-id", notehead.ShapeId),
                new XAttribute(
                    "data-accidental-effect",
                    explicitTarget ? "explicit" : "implicit"));

            if (!explicitTarget)
            {
                ring.SetAttributeValue(
                    "stroke-dasharray",
                    $"{F(4.0 * unit)} {F(3.0 * unit)}");
            }

            ring.Add(new XElement(
                ns + "title",
                explicitTarget
                    ? $"explicit target of {KindToken(decision.Candidate.Kind)} {decision.Candidate.Shape.ShapeId}"
                    : $"implicitly affected by {KindToken(decision.Candidate.Kind)} {decision.Candidate.Shape.ShapeId}"));

            group.Add(ring);
        }
    }

    private static void AddRejectedCandidate(
        XElement group,
        XNamespace ns,
        double unit,
        AccidentalDecision decision)
    {
        var bounds = decision.Candidate.Shape.Bounds;
        var padding = Math.Max(1.4 * unit, 0.5);

        var rectangle = new XElement(
            ns + "rect",
            new XAttribute("x", F(bounds.MinX - padding)),
            new XAttribute("y", F(bounds.MinY - padding)),
            new XAttribute("width", F(bounds.Width + padding * 2)),
            new XAttribute("height", F(bounds.Height + padding * 2)),
            new XAttribute("fill", "none"),
            new XAttribute("stroke", "#8e24aa"),
            new XAttribute("stroke-width", F(1.2 * unit)),
            new XAttribute("stroke-dasharray", $"{F(4.0 * unit)} {F(3.0 * unit)}"),
            new XAttribute("opacity", "0.70"));

        rectangle.Add(new XElement(
            ns + "title",
            Describe(decision)));
        group.Add(rectangle);
    }

    private static string Describe(AccidentalDecision decision)
    {
        var candidate = decision.Candidate;
        var explicitTarget = decision.ExplicitTarget?.ShapeId ?? "-";
        var affected = decision.AffectedNoteheads.Count == 0
            ? "-"
            : string.Join(",", decision.AffectedNoteheads.Select(notehead => notehead.ShapeId));
        var error = double.IsFinite(decision.VerticalErrorInHalfSteps)
            ? decision.VerticalErrorInHalfSteps.ToString("F3", CultureInfo.InvariantCulture)
            : "-";

        return $"{candidate.Shape.ShapeId}: {candidate.Kind}; {decision.Decision}; "
            + $"m{candidate.MeasureNumber} staff={candidate.StaffNumber}; "
            + $"anchor=({candidate.Anchor.X:F2},{candidate.Anchor.Y:F2}); "
            + $"classifier={candidate.ClassificationConfidence:P1}; "
            + $"staff-step={decision.StaffStep?.ToString() ?? "-"}; "
            + $"vertical-error={error}; explicit={explicitTarget}; affected={affected}; "
            + $"confidence={decision.Confidence:P1}; {decision.Reason}";
    }

    private static string KindToken(AccidentalKind kind)
    {
        return kind switch
        {
            AccidentalKind.Flat => "b",
            AccidentalKind.Sharp => "#",
            AccidentalKind.Natural => "n",
            AccidentalKind.DoubleFlat => "bb",
            AccidentalKind.DoubleSharp => "x",
            _ => "?"
        };
    }

    private static void AddLegend(
        XElement group,
        XNamespace ns,
        XElement root,
        double unit,
        AccidentalAnalysisResult analysis)
    {
        var viewBox = ReadViewBox(root);
        if (viewBox is null)
        {
            return;
        }

        var x = viewBox.Value.MinX + 6 * unit;
        var y = viewBox.Value.MinY + 19 * unit;
        var fontSize = Math.Max(5 * unit, 2.8);
        var noTarget = analysis.Decisions.Count(decision =>
            decision.Decision == "no-right-notehead-on-anchor-line");
        var text = $"accidentals={analysis.Accepted.Count}; rejected-no-target={noTarget}; "
            + "orange ring=explicit, dashed orange=implicit";

        group.Add(new XElement(
            ns + "text",
            new XAttribute("x", F(x)),
            new XAttribute("y", F(y)),
            new XAttribute("font-family", "Arial, sans-serif"),
            new XAttribute("font-size", F(fontSize)),
            new XAttribute("font-weight", "700"),
            new XAttribute("fill", "#ef6c00"),
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
