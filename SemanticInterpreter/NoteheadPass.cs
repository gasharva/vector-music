namespace SvgMusic.Semantics;

public sealed class NoteheadPass : ISemanticPass
{
    private readonly NoteheadAnalyzer _analyzer;

    public NoteheadPass(NoteheadAnalyzer? analyzer = null)
    {
        _analyzer = analyzer ?? new NoteheadAnalyzer();
    }

    public string Name => nameof(NoteheadPass);

    public NoteheadAnalysisResult? LastAnalysis { get; private set; }

    public void Run(
        SemanticDocument document,
        SemanticFacts facts)
    {
        var analysis = _analyzer.Analyze(document);
        LastAnalysis = analysis;

        facts.AddTrace(FormatSizeProfile(analysis.SizeProfile));

        foreach (var decision in analysis.Accepted)
        {
            var candidate = decision.Candidate;
            var ellipse = candidate.Ellipse;

            facts.Add(new NoteheadFact(
                candidate.MeasureNumber,
                candidate.StaffNumber,
                ellipse.ShapeId,
                ellipse.CenterX,
                ellipse.CenterY,
                ellipse.Source.MajorRadius,
                ellipse.Source.MinorRadius,
                decision.FillKind,
                candidate.NormalizedSize,
                decision.Grid.NearestStep,
                decision.Grid.ErrorInHalfSteps,
                decision.Confidence,
                decision.Reason,
                [ellipse.ShapeId]));
        }

        var rejectedBySize = analysis.Decisions.Count(decision =>
            decision.Decision == "small-dot-size-cluster");
        var rejectedByGrid = analysis.Decisions.Count(decision =>
            decision.Decision == "off-staff-grid");
        var rejectedByLedger = analysis.Decisions.Count(decision =>
            decision.Decision == "unsupported-ledger-position");

        facts.AddTrace(
            $"NoteheadPass decisions: total={analysis.Decisions.Count}; "
            + $"accepted={analysis.Accepted.Count}; "
            + $"small-dots={rejectedBySize}; "
            + $"off-grid={rejectedByGrid}; "
            + $"unsupported-ledger={rejectedByLedger}");
    }

    private static string FormatSizeProfile(EllipseSizeProfile profile)
    {
        if (!profile.HasSmallDotCluster)
        {
            return $"NoteheadPass size profile: ellipses={profile.RankedCandidates.Count}; "
                + $"no separate small-dot cluster; all ellipse sizes remain eligible; "
                + $"median={Format(profile.NoteheadMedian)}sp";
        }

        return $"NoteheadPass size profile: ellipses={profile.RankedCandidates.Count}; "
            + $"small-dot cluster={profile.SmallCount}; notehead-size band={profile.NoteheadBandCount}; "
            + $"threshold={Format(profile.Threshold)}sp; "
            + $"small-median={Format(profile.SmallMedian)}sp; "
            + $"notehead-median={Format(profile.NoteheadMedian)}sp";
    }

    private static string Format(double? value) =>
        value is null
            ? "-"
            : value.Value.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
}
