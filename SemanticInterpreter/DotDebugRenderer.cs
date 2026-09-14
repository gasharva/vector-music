using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace SvgMusic.Semantics;

public sealed class DotDebugRenderer
{
    public void Render(
        string inputSvgPath,
        DotAnalysisResult analysis,
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
            new XAttribute("id", "semantic-augmentation-dots"),
            new XAttribute("pointer-events", "all"));

        foreach (var decision in analysis.Decisions)
        {
            if (decision.Accepted && decision.Match is not null)
            {
                AddAccepted(
                    group,
                    ns,
                    unit,
                    decision);
            }
            else
            {
                AddRejected(
                    group,
                    ns,
                    unit,
                    decision);
            }
        }

        root.Add(group);
        document.Save(
            outputSvgPath,
            SaveOptions.DisableFormatting);
    }

    public void WriteReport(
        DotAnalysisResult analysis,
        string outputTextPath)
    {
        var lines = new List<string>
        {
            "AUGMENTATION DOT ANALYSIS",
            string.Empty,
            $"notehead-median-size={analysis.NoteheadMedianSize:F3}sp",
            $"dot-size-band={analysis.MinimumDotSize:F3}..{analysis.MaximumDotSize:F3}sp",
            $"candidates={analysis.Decisions.Count}",
            $"accepted={analysis.Accepted.Count}",
            $"rejected={analysis.Decisions.Count(decision => !decision.Accepted)}",
            $"attached-noteheads={analysis.Accepted.Where(decision => decision.Match is not null).Select(decision => decision.Match!.Notehead.ShapeId).Distinct(StringComparer.Ordinal).Count()}",
            string.Empty,
            "ACCEPTED AUGMENTATION DOTS"
        };

        foreach (var decision in analysis.Accepted)
        {
            lines.Add(Describe(decision));
        }

        lines.Add(string.Empty);
        lines.Add("REJECTED SMALL ELLIPSES");

        foreach (var decision in analysis.Decisions.Where(decision => !decision.Accepted))
        {
            lines.Add(Describe(decision));
        }

        File.WriteAllLines(
            outputTextPath,
            lines,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void AddAccepted(
        XElement group,
        XNamespace ns,
        double unit,
        DotDecision decision)
    {
        var dot = decision.Candidate.Ellipse;
        var match = decision.Match!;
        var target = match.Notehead;
        const string color = "#d81b60";
        var dotRadius = Math.Max(
            dot.Source.MajorRadius,
            dot.Source.MinorRadius);
        var ringRadius = Math.Max(
            dotRadius + 2.0 * unit,
            2.5 * unit);

        var dotRing = new XElement(
            ns + "circle",
            new XAttribute("cx", F(dot.CenterX)),
            new XAttribute("cy", F(dot.CenterY)),
            new XAttribute("r", F(ringRadius)),
            new XAttribute("fill", color),
            new XAttribute("fill-opacity", "0.12"),
            new XAttribute("stroke", color),
            new XAttribute("stroke-width", F(1.8 * unit)));
        dotRing.Add(new XElement(
            ns + "title",
            Describe(decision)));
        group.Add(dotRing);

        group.Add(new XElement(
            ns + "line",
            new XAttribute("x1", F(dot.CenterX)),
            new XAttribute("y1", F(dot.CenterY)),
            new XAttribute("x2", F(target.CenterX)),
            new XAttribute("y2", F(target.CenterY)),
            new XAttribute("stroke", color),
            new XAttribute("stroke-width", F(1.0 * unit)),
            new XAttribute("stroke-dasharray", $"{F(3.0 * unit)} {F(2.0 * unit)}"),
            new XAttribute("opacity", "0.75")));

        group.Add(new XElement(
            ns + "ellipse",
            new XAttribute("cx", F(target.CenterX)),
            new XAttribute("cy", F(target.CenterY)),
            new XAttribute("rx", F(Math.Max(target.MajorRadius, target.MinorRadius) + 2.2 * unit)),
            new XAttribute("ry", F(Math.Min(target.MajorRadius, target.MinorRadius) + 2.2 * unit)),
            new XAttribute("fill", "none"),
            new XAttribute("stroke", color),
            new XAttribute("stroke-width", F(1.2 * unit)),
            new XAttribute("stroke-dasharray", $"{F(3.5 * unit)} {F(2.0 * unit)}")));
    }

    private static void AddRejected(
        XElement group,
        XNamespace ns,
        double unit,
        DotDecision decision)
    {
        var dot = decision.Candidate.Ellipse;
        var dotRadius = Math.Max(
            dot.Source.MajorRadius,
            dot.Source.MinorRadius);
        var ringRadius = Math.Max(
            dotRadius + 1.4 * unit,
            2.0 * unit);

        var ring = new XElement(
            ns + "circle",
            new XAttribute("cx", F(dot.CenterX)),
            new XAttribute("cy", F(dot.CenterY)),
            new XAttribute("r", F(ringRadius)),
            new XAttribute("fill", "none"),
            new XAttribute("stroke", "#9e9e9e"),
            new XAttribute("stroke-width", F(0.9 * unit)),
            new XAttribute("stroke-dasharray", $"{F(2.5 * unit)} {F(2.0 * unit)}"),
            new XAttribute("opacity", "0.45"));
        ring.Add(new XElement(
            ns + "title",
            Describe(decision)));
        group.Add(ring);
    }

    private static string Describe(DotDecision decision)
    {
        var candidate = decision.Candidate;
        var target = decision.Match?.Notehead.ShapeId ?? "-";
        var staff = decision.Match?.Notehead.Staff.ToString() ?? "-";
        var horizontal = decision.Match is null
            ? "-"
            : decision.Match.HorizontalGapInSpacings.ToString("F3", CultureInfo.InvariantCulture);
        var vertical = decision.Match is null
            ? "-"
            : decision.Match.VerticalOffsetInSpacings.ToString("F3", CultureInfo.InvariantCulture);

        return $"{candidate.Ellipse.ShapeId}: {decision.Decision}; m{candidate.MeasureNumber}; "
            + $"size={candidate.NormalizedSize:F3}sp; target={target}; staff={staff}; "
            + $"horizontal-gap={horizontal}sp; vertical-offset={vertical}sp; "
            + $"confidence={decision.Confidence:P1}; {decision.Reason}";
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
