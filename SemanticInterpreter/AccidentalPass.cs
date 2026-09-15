namespace SvgMusic.Semantics;

public sealed class AccidentalPass : ISemanticPass
{
    private readonly AccidentalAnalyzer _analyzer;

    public AccidentalPass(AccidentalAnalyzer? analyzer = null)
    {
        _analyzer = analyzer ?? new AccidentalAnalyzer();
    }

    public string Name => nameof(AccidentalPass);

    public AccidentalAnalysisResult? LastAnalysis { get; private set; }

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
            var candidate = decision.Candidate;
            var explicitTarget = decision.ExplicitTarget
                ?? throw new InvalidOperationException(
                    $"Accepted accidental {candidate.Shape.ShapeId} has no explicit target.");
            var staffStep = decision.StaffStep
                ?? throw new InvalidOperationException(
                    $"Accepted accidental {candidate.Shape.ShapeId} has no staff step.");

            facts.Add(new AccidentalFact(
                candidate.MeasureNumber,
                candidate.StaffNumber,
                candidate.Shape.ShapeId,
                candidate.Kind,
                candidate.Anchor.X,
                candidate.Anchor.Y,
                staffStep,
                explicitTarget.ShapeId,
                decision.AffectedNoteheads
                    .Select(notehead => notehead.ShapeId)
                    .ToArray(),
                candidate.ClassificationConfidence,
                decision.VerticalErrorInHalfSteps,
                decision.Confidence,
                decision.Reason,
                [candidate.Shape.ShapeId]));
        }

        var rejectedKey = analysis.Decisions.Count(decision =>
            decision.Decision == "key-signature-symbol");
        var rejectedNoTarget = analysis.Decisions.Count(decision =>
            decision.Decision == "no-right-notehead-on-anchor-line");

        facts.AddTrace(
            $"AccidentalPass decisions: total={analysis.Decisions.Count}; "
            + $"accepted={analysis.Accepted.Count}; "
            + $"key-signature={rejectedKey}; no-target={rejectedNoTarget}");
    }
}
