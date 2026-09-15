namespace SvgMusic.Semantics;

public sealed class BeamAttachmentPass : ISemanticPass
{
    private readonly BeamAttachmentAnalyzer _analyzer;
    private readonly ResidualBeamHookRecovery _hookRecovery;

    public BeamAttachmentPass(
        BeamAttachmentAnalyzer? analyzer = null,
        ResidualBeamHookRecovery? hookRecovery = null)
    {
        _analyzer = analyzer ?? new BeamAttachmentAnalyzer();
        _hookRecovery = hookRecovery ?? new ResidualBeamHookRecovery();
    }

    public string Name => nameof(BeamAttachmentPass);

    public BeamAnalysisResult? LastAnalysis { get; private set; }

    public void Run(
        SemanticDocument document,
        SemanticFacts facts)
    {
        var recovery = _hookRecovery.Recover(document);
        var analysis = _analyzer.Analyze(
            recovery.Document,
            facts);
        LastAnalysis = analysis;

        foreach (var decision in analysis.Accepted)
        {
            if (decision.Level is null)
            {
                throw new InvalidOperationException(
                    $"Accepted beam {decision.Candidate.Stroke.ShapeId} has no level.");
            }

            var source = decision.Candidate.Stroke.Source;
            var attachedStemIds = decision.Matches
                .Select(match => match.Stem.StemShapeId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var attachedStaffs = decision.Matches
                .SelectMany(match => match.Stem.AttachedStaffs)
                .Distinct()
                .OrderBy(staff => staff)
                .ToArray();

            facts.Add(new BeamAttachmentFact(
                decision.Candidate.MeasureNumber,
                source.ShapeId,
                decision.Level.Value,
                attachedStemIds,
                attachedStaffs,
                decision.LeftEndSupported,
                decision.RightEndSupported,
                decision.IsHook,
                decision.IsCrossStaff,
                source.Start.X,
                source.Start.Y,
                source.End.X,
                source.End.Y,
                decision.Candidate.LengthInSpacings,
                decision.Candidate.WidthInSpacings,
                decision.Candidate.Slope,
                decision.Confidence,
                decision.Reason,
                [source.ShapeId, .. attachedStemIds]));
        }

        var hooks = analysis.Accepted.Count(decision =>
            decision.IsHook);
        var crossStaff = analysis.Accepted.Count(decision =>
            decision.IsCrossStaff);
        var levelSummary = string.Join(
            ",",
            analysis.Accepted
                .Where(decision => decision.Level is not null)
                .GroupBy(decision => decision.Level!.Value)
                .OrderBy(group => group.Key)
                .Select(group => $"L{group.Key}={group.Count()}"));

        facts.AddTrace(
            $"BeamAttachmentPass decisions: candidates={analysis.Decisions.Count}; "
            + $"accepted={analysis.Accepted.Count}; hooks={hooks}; "
            + $"recovered-compact-hooks={recovery.RecoveredCount}; "
            + $"cross-staff={crossStaff}; levels=[{levelSummary}]");
    }
}
