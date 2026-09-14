using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace SvgMusic.Semantics;

public sealed class TupletDebugRenderer
{
    public void Render(
        string inputSvgPath,
        TupletAnalysisResult analysis,
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
            new XAttribute("id", "semantic-tuplets"),
            new XAttribute("pointer-events", "all"));

        foreach (var decision in analysis.Decisions)
        {
            if (decision.Accepted)
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
        TupletAnalysisResult analysis,
        string outputTextPath)
    {
        var lines = new List<string>
        {
            "TUPLET ANALYSIS",
            string.Empty,
            $"candidates={analysis.Decisions.Count}",
            $"accepted={analysis.Accepted.Count}",
            $"corrected-from-stem-count={analysis.Accepted.Count(decision => decision.CorrectedFromStemCount)}",
            $"rejected={analysis.Decisions.Count(decision => !decision.Accepted)}",
            string.Empty,
            "ACCEPTED TUPLETS"
        };

        foreach (var decision in analysis.Accepted)
        {
            lines.Add(Describe(decision));
        }

        lines.Add(string.Empty);
        lines.Add("REJECTED TUPLET CANDIDATES");

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
        TupletDecision decision)
    {
        var bounds = decision.Candidate.Shape.Bounds;
        var padding = Math.Max(1.8 * unit, 0.6);
        var color = decision.CorrectedFromStemCount
            ? "#ff8f00"
            : "#f9a825";

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
            new XAttribute("rx", F(1.8 * unit)));

        rectangle.Add(new XElement(
            ns + "title",
            Describe(decision)));
        group.Add(rectangle);

        if (decision.Match is not null)
        {
            var beam = decision.Match.Beam;
            var beamCenterX = (beam.StartX + beam.EndX) / 2.0;
            var beamCenterY = (beam.StartY + beam.EndY) / 2.0;

            group.Add(new XElement(
                ns + "line",
                new XAttribute("x1", F(bounds.CenterX)),
                new XAttribute("y1", F(bounds.CenterY)),
                new XAttribute("x2", F(beamCenterX)),
                new XAttribute("y2", F(beamCenterY)),
                new XAttribute("stroke", color),
                new XAttribute("stroke-width", F(1.2 * unit)),
                new XAttribute("stroke-dasharray", $"{F(3.0 * unit)} {F(2.0 * unit)}"),
                new XAttribute("opacity", "0.85")));
        }

        var actual = decision.ActualNotes?.ToString() ?? "?";
        var normal = decision.NormalNotes?.ToString() ?? "?";
        var classifierText = decision.CorrectedFromStemCount
            ? $" cls={decision.Candidate.DisplayedNumber}"
            : string.Empty;
        var label = $"T {actual}:{normal}{classifierText}";

        group.Add(new XElement(
            ns + "text",
            new XAttribute("x", F(bounds.MinX - padding)),
            new XAttribute("y", F(bounds.MinY - 3.0 * unit)),
            new XAttribute("font-family", "Arial, sans-serif"),
            new XAttribute("font-size", F(Math.Max(7.0 * unit, 3.6))),
            new XAttribute("font-weight", "700"),
            new XAttribute("fill", color),
            new XAttribute("stroke", "white"),
            new XAttribute("stroke-width", F(0.8 * unit)),
            new XAttribute("paint-order", "stroke fill"),
            label));
    }

    private static void AddRejected(
        XElement group,
        XNamespace ns,
        double unit,
        TupletDecision decision)
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
            new XAttribute("stroke", "#8d6e63"),
            new XAttribute("stroke-width", F(1.2 * unit)),
            new XAttribute("stroke-dasharray", $"{F(4.0 * unit)} {F(3.0 * unit)}"),
            new XAttribute("opacity", "0.75"));

        rectangle.Add(new XElement(
            ns + "title",
            Describe(decision)));
        group.Add(rectangle);
    }

    private static string Describe(TupletDecision decision)
    {
        var candidate = decision.Candidate;
        var beam = decision.Match?.Beam.BeamShapeId ?? "-";
        var actual = decision.ActualNotes?.ToString() ?? "-";
        var normal = decision.NormalNotes?.ToString() ?? "-";
        var horizontal = decision.Match is null
            ? "-"
            : decision.Match.HorizontalCenterErrorInSpacings.ToString("F3", CultureInfo.InvariantCulture);
        var vertical = decision.Match is null
            ? "-"
            : decision.Match.VerticalDistanceInSpacings.ToString("F3", CultureInfo.InvariantCulture);

        return $"{candidate.Shape.ShapeId}: {candidate.ClassificationLabel}; {decision.Decision}; "
            + $"m{candidate.MeasureNumber} staff={candidate.StaffNumber}; "
            + $"classifier={candidate.ClassificationConfidence:P1}; displayed={candidate.DisplayedNumber}; "
            + $"ratio={actual}:{normal}; corrected={decision.CorrectedFromStemCount}; "
            + $"beam={beam}; stems=[{string.Join(',', decision.AttachedStemIds)}]; "
            + $"center-error={horizontal}sp; vertical-distance={vertical}sp; "
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
