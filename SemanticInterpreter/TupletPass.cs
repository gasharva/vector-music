namespace SvgMusic.Semantics;

public sealed class TupletPass : ISemanticPass
{
    private readonly TupletAnalyzer _analyzer;

    public TupletPass(TupletAnalyzer? analyzer = null)
    {
        _analyzer = analyzer ?? new TupletAnalyzer();
    }

    public string Name => nameof(TupletPass);

    public TupletAnalysisResult? LastAnalysis { get; private set; }

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
                    $"Accepted tuplet {decision.Candidate.Shape.ShapeId} has no beam match.");
            var actualNotes = decision.ActualNotes
                ?? throw new InvalidOperationException(
                    $"Accepted tuplet {decision.Candidate.Shape.ShapeId} has no actual note count.");
            var normalNotes = decision.NormalNotes
                ?? throw new InvalidOperationException(
                    $"Accepted tuplet {decision.Candidate.Shape.ShapeId} has no normal note count.");

            facts.Add(new TupletFact(
                decision.Candidate.MeasureNumber,
                decision.Candidate.Shape.ShapeId,
                decision.Candidate.ClassificationLabel,
                decision.Candidate.DisplayedNumber,
                actualNotes,
                normalNotes,
                decision.CorrectedFromStemCount,
                match.Beam.BeamShapeId,
                decision.AttachedStemIds,
                decision.AttachedNoteheadIds,
                decision.Candidate.ClassificationConfidence,
                decision.Confidence,
                decision.Reason,
                [
                    decision.Candidate.Shape.ShapeId,
                    match.Beam.BeamShapeId,
                    .. decision.AttachedStemIds
                ]));
        }

        var corrected = analysis.Accepted.Count(decision =>
            decision.CorrectedFromStemCount);
        var unmatched = analysis.Decisions.Count(decision =>
            decision.Decision == "no-nearby-primary-beam");

        facts.AddTrace(
            $"TupletPass decisions: candidates={analysis.Decisions.Count}; "
            + $"accepted={analysis.Accepted.Count}; corrected-from-stem-count={corrected}; "
            + $"unmatched={unmatched}");
    }
}
