namespace SvgMusic.Semantics;

public sealed class FlagAttachmentPass : ISemanticPass
{
    private readonly FlagAttachmentAnalyzer _analyzer;

    public FlagAttachmentPass(
        FlagAttachmentAnalyzer? analyzer = null)
    {
        _analyzer = analyzer ?? new FlagAttachmentAnalyzer();
    }

    public string Name => nameof(FlagAttachmentPass);

    public FlagAnalysisResult? LastAnalysis { get; private set; }

    public void Run(
        SemanticDocument document,
        SemanticFacts facts)
    {
        var analysis = _analyzer.Analyze(
            document,
            facts);
        LastAnalysis = analysis;

        foreach (var decision in analysis.Accepted)
        {
            var match = decision.Match
                ?? throw new InvalidOperationException(
                    $"Accepted flag {decision.Candidate.Shape.ShapeId} has no stem match.");

            facts.Add(new FlagAttachmentFact(
                decision.Candidate.MeasureNumber,
                decision.Candidate.Shape.ShapeId,
                match.Stem.StemShapeId,
                decision.Candidate.Level,
                decision.Candidate.ClassificationLabel,
                decision.Candidate.ClassificationConfidence,
                match.TipX,
                match.TipY,
                match.Stem.IsCrossStaff,
                decision.Confidence,
                decision.Reason,
                [
                    decision.Candidate.Shape.ShapeId,
                    match.Stem.StemShapeId
                ]));
        }

        var unmatched = analysis.Decisions.Count(decision =>
            decision.Decision == "no-compatible-stem-tip");
        var ambiguous = analysis.Decisions.Count(decision =>
            decision.Decision == "ambiguous-stem-tip");
        var conflicts = analysis.Decisions.Count(decision =>
            decision.Decision == "stem-already-has-better-flag");

        facts.AddTrace(
            $"FlagAttachmentPass decisions: candidates={analysis.Decisions.Count}; "
            + $"accepted={analysis.Accepted.Count}; unmatched={unmatched}; "
            + $"ambiguous={ambiguous}; stem-conflicts={conflicts}; "
            + $"levels=[{string.Join(',', analysis.Accepted.Select(decision => decision.Candidate.Level).OrderBy(level => level))}]");
    }
}
