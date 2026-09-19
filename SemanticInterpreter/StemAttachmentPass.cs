namespace SvgMusic.Semantics;

public sealed class StemAttachmentPass : ISemanticPass
{
    private readonly StemAttachmentAnalyzer _analyzer;
    private readonly StemAttachmentResolver _resolver;

    public StemAttachmentPass(
        StemAttachmentAnalyzer? analyzer = null,
        StemAttachmentResolver? resolver = null)
    {
        _analyzer = analyzer ?? new StemAttachmentAnalyzer();
        _resolver = resolver ?? new StemAttachmentResolver();
    }

    public string Name => nameof(StemAttachmentPass);

    public StemAnalysisResult? LastAnalysis { get; private set; }

    public void Run(
        SemanticDocument document,
        SemanticFacts facts)
    {
        var rawAnalysis = _analyzer.Analyze(
            document,
            facts);
        var analysis = _resolver.Resolve(rawAnalysis);
        LastAnalysis = analysis;

        foreach (var decision in analysis.Accepted)
        {
            var source = decision.Candidate.Stroke.Source;
            var attachedNoteheads = decision.Matches
                .Select(match => match.Notehead.ShapeId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var attachedStaffs = decision.Matches
                .Select(match => match.Notehead.Staff)
                .Distinct()
                .OrderBy(staff => staff)
                .ToArray();

            facts.Add(new StemAttachmentFact(
                decision.Candidate.MeasureNumber,
                source.ShapeId,
                decision.Direction,
                attachedNoteheads,
                attachedStaffs,
                decision.IsCrossStaff,
                source.Start.X,
                source.Start.Y,
                source.End.X,
                source.End.Y,
                decision.Candidate.Length / decision.Candidate.LineSpacing,
                decision.Candidate.NormalizedWidth,
                decision.Confidence,
                decision.Reason,
                [source.ShapeId, .. attachedNoteheads]));
        }

        var unmatchedCandidates = analysis.Decisions.Count(decision =>
            !decision.Accepted);
        var crossStaff = analysis.Accepted.Count(decision =>
            decision.IsCrossStaff);
        var ambiguousDirection = analysis.Accepted.Count(decision =>
            decision.Direction == StemDirection.Ambiguous);

        facts.AddTrace(
            $"StemAttachmentPass decisions: candidates={analysis.Decisions.Count}; "
            + $"accepted={analysis.Accepted.Count}; unmatched={unmatchedCandidates}; "
            + $"cross-staff={crossStaff}; ambiguous-direction={ambiguousDirection}; "
            + $"noteheads-attached={analysis.AttachedNoteheads}/{analysis.TotalNoteheads}; "
            + $"attachment-conflicts={_resolver.LastConflicts.Count}");

        foreach (var conflict in _resolver.LastConflicts)
        {
            facts.AddTrace(
                $"StemAttachmentResolver: m{conflict.MeasureNumber} {conflict.NoteheadId} "
                + $"-> {conflict.WinnerStemShapeId}; alternatives=["
                + string.Join(
                    ", ",
                    conflict.Alternatives.Select(alternative =>
                        $"{alternative.StemShapeId}/{alternative.Direction} "
                        + $"match={alternative.MatchScore:P1} "
                        + $"edge={alternative.EdgeDistanceInSpacings:F3}sp "
                        + $"stem={alternative.StemConfidence:P1}"))
                + "]");
        }
    }
}
