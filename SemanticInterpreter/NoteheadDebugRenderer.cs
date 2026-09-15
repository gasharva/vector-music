using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace SvgMusic.Semantics;

public sealed class NoteheadDebugRenderer
{
    public void Render(
        string inputSvgPath,
        NoteheadAnalysisResult analysis,
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
            new XAttribute("id", "semantic-noteheads"),
            new XAttribute("pointer-events", "all"));

        foreach (var decision in analysis.Decisions)
        {
            var ellipse = decision.Candidate.Ellipse.Source;
            var degrees = ellipse.Rotation * 180.0 / Math.PI;
            var style = StyleFor(decision);

            var overlay = new XElement(
                ns + "ellipse",
                new XAttribute("cx", F(ellipse.Center.X)),
                new XAttribute("cy", F(ellipse.Center.Y)),
                new XAttribute("rx", F(ellipse.MajorRadius)),
                new XAttribute("ry", F(ellipse.MinorRadius)),
                new XAttribute(
                    "transform",
                    $"rotate({F(degrees)} {F(ellipse.Center.X)} {F(ellipse.Center.Y)})"),
                new XAttribute("fill", style.Fill),
                new XAttribute("fill-opacity", style.FillOpacity),
                new XAttribute("stroke", style.Stroke),
                new XAttribute("stroke-width", F(style.StrokeWidth * unit)),
                new XAttribute("opacity", style.Opacity),
                new XAttribute("data-notehead-decision", decision.Decision),
                new XAttribute("data-shape-id", decision.Candidate.Ellipse.ShapeId));

            overlay.Add(new XElement(
                ns + "title",
                Describe(decision)));

            group.Add(overlay);
        }

        AddLegend(group, ns, root, unit, analysis);
        root.Add(group);
        document.Save(
            outputSvgPath,
            SaveOptions.DisableFormatting);
    }

    public void WriteReport(
        NoteheadAnalysisResult analysis,
        string outputTextPath)
    {
        var profile = analysis.SizeProfile;
        var decisionsByShape = analysis.Decisions.ToDictionary(
            decision => decision.Candidate.Ellipse.ShapeId,
            StringComparer.Ordinal);
        var lines = new List<string>
        {
            "NOTEHEAD ANALYSIS",
            string.Empty,
            "SIZE PROFILE",
            $"ellipses={profile.RankedCandidates.Count}",
            $"small-dot-cluster={profile.HasSmallDotCluster}",
            $"threshold={FormatNullable(profile.Threshold)} staff-spaces",
            $"small-count={profile.SmallCount}",
            $"notehead-band-count={profile.NoteheadBandCount}",
            $"small-median={FormatNullable(profile.SmallMedian)} staff-spaces",
            $"notehead-median={FormatNullable(profile.NoteheadMedian)} staff-spaces",
            string.Empty,
            "RANKED BY NORMALIZED ELLIPSE SIZE",
            "rank  size(sp)  decision                     measure staff step  grid-error ledger fill    shape"
        };

        for (var index = 0; index < profile.RankedCandidates.Count; index++)
        {
            var candidate = profile.RankedCandidates[index];
            var decision = decisionsByShape[candidate.Ellipse.ShapeId];
            var ledger = decision.Grid.RequiredLedgerLevels == 0
                ? "-"
                : decision.Grid.HasLedgerSupport
                    ? $"ok:{decision.Grid.RequiredLedgerLevels}"
                    : $"no:{decision.Grid.RequiredLedgerLevels}";

            lines.Add(
                $"{index + 1,4}  "
                + $"{candidate.NormalizedSize,8:F3}  "
                + $"{decision.Decision,-28}  "
                + $"{candidate.MeasureNumber,7} "
                + $"{candidate.StaffNumber,5} "
                + $"{decision.Grid.NearestStep,4}  "
                + $"{decision.Grid.ErrorInHalfSteps,10:F3} "
                + $"{ledger,-6} "
                + $"{decision.FillKind,-6}  "
                + candidate.Ellipse.ShapeId);
        }

        lines.Add(string.Empty);
        lines.Add("ACCEPTED NOTEHEADS");

        foreach (var decision in analysis.Accepted)
        {
            lines.Add(Describe(decision));
        }

        lines.Add(string.Empty);
        lines.Add("REJECTED: UNSUPPORTED LEDGER POSITIONS");

        foreach (var decision in analysis.Decisions.Where(decision =>
                     decision.Decision == "unsupported-ledger-position"))
        {
            lines.Add(Describe(decision));
        }

        File.WriteAllLines(
            outputTextPath,
            lines,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static DebugStyle StyleFor(NoteheadDecision decision)
    {
        if (decision.Accepted && decision.FillKind == "hollow")
        {
            return new DebugStyle(
                "#1565c0",
                "#1565c0",
                "0.14",
                2.1,
                "0.95");
        }

        if (decision.Accepted)
        {
            return new DebugStyle(
                "#2e7d32",
                "#2e7d32",
                "0.18",
                2.1,
                "0.95");
        }

        if (decision.Decision == "small-dot-size-cluster")
        {
            return new DebugStyle(
                "#ef6c00",
                "#ef6c00",
                "0.08",
                1.3,
                "0.72");
        }

        if (decision.Decision == "unsupported-ledger-position")
        {
            return new DebugStyle(
                "#c2185b",
                "none",
                "0",
                1.5,
                "0.80");
        }

        return new DebugStyle(
            "#757575",
            "none",
            "0",
            1.1,
            "0.55");
    }

    private static string Describe(NoteheadDecision decision)
    {
        var candidate = decision.Candidate;

        return $"{candidate.Ellipse.ShapeId}: {decision.Decision}; "
            + $"m{candidate.MeasureNumber} staff={candidate.StaffNumber}; "
            + $"size={candidate.NormalizedSize:F3}sp; "
            + $"step={decision.Grid.NearestStep}; "
            + $"grid-error={decision.Grid.ErrorInHalfSteps:F3}; "
            + $"ledger-levels={decision.Grid.RequiredLedgerLevels}; "
            + $"ledger-support={decision.Grid.HasLedgerSupport}; "
            + $"fill={decision.FillKind}; "
            + $"confidence={decision.Confidence:P1}; "
            + decision.Reason;
    }

    private static void AddLegend(
        XElement group,
        XNamespace ns,
        XElement root,
        double unit,
        NoteheadAnalysisResult analysis)
    {
        var viewBox = ReadViewBox(root);
        if (viewBox is null)
        {
            return;
        }

        var x = viewBox.Value.MinX + 6 * unit;
        var y = viewBox.Value.MinY + 10 * unit;
        var fontSize = Math.Max(5 * unit, 2.8);
        var accepted = analysis.Accepted.Count;
        var small = analysis.Decisions.Count(decision =>
            decision.Decision == "small-dot-size-cluster");
        var offGrid = analysis.Decisions.Count(decision =>
            decision.Decision == "off-staff-grid");
        var unsupportedLedger = analysis.Decisions.Count(decision =>
            decision.Decision == "unsupported-ledger-position");
        var text = $"noteheads={accepted}; small dots={small}; "
            + $"off-grid={offGrid}; unsupported-ledger={unsupportedLedger}";

        group.Add(new XElement(
            ns + "text",
            new XAttribute("x", F(x)),
            new XAttribute("y", F(y)),
            new XAttribute("font-family", "Arial, sans-serif"),
            new XAttribute("font-size", F(fontSize)),
            new XAttribute("font-weight", "700"),
            new XAttribute("fill", "#111111"),
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

    private static string FormatNullable(double? value) =>
        value is null
            ? "-"
            : value.Value.ToString("F3", CultureInfo.InvariantCulture);

    private static string F(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private sealed record DebugStyle(
        string Stroke,
        string Fill,
        string FillOpacity,
        double StrokeWidth,
        string Opacity);

    private readonly record struct ViewBox(
        double MinX,
        double MinY,
        double Width,
        double Height);
}
