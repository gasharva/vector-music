namespace SvgMusic.Semantics;

public sealed class DotAttachmentPass : ISemanticPass
{
    private readonly DotAttachmentAnalyzer _analyzer;

    public DotAttachmentPass(DotAttachmentAnalyzer? analyzer = null)
    {
        _analyzer = analyzer ?? new DotAttachmentAnalyzer();
    }

    public string Name => nameof(DotAttachmentPass);

    public DotAnalysisResult? LastAnalysis { get; private set; }

    public void Run(
        SemanticDocument document,
        SemanticFacts facts)
    {
        var analysis = _analyzer.Analyze(
            document,
            facts);
        LastAnalysis = analysis;

        foreach (var group in analysis.Accepted
                     .Where(decision => decision.Match is not null)
                     .GroupBy(decision => decision.Match!.Notehead.ShapeId, StringComparer.Ordinal))
        {
            var ordered = group
                .OrderBy(decision => decision.Candidate.Ellipse.CenterX)
                .ToArray();
            var target = ordered[0].Match!.Notehead;
            var dotShapeIds = ordered
                .Select(decision => decision.Candidate.Ellipse.ShapeId)
                .ToArray();
            var confidence = ordered.Average(decision => decision.Confidence);

            facts.Add(new DotAttachmentFact(
                target.MeasureNumber,
                target.Staff,
                target.ShapeId,
                dotShapeIds,
                dotShapeIds.Length,
                confidence,
                $"{dotShapeIds.Length} augmentation dot(s) attached to notehead {target.ShapeId}; "
                + string.Join(
                    "; ",
                    ordered.Select(decision => decision.Reason)),
                [target.ShapeId, .. dotShapeIds]));
        }

        var attachedNoteheads = analysis.Accepted
            .Where(decision => decision.Match is not null)
            .Select(decision => decision.Match!.Notehead.ShapeId)
            .Distinct(StringComparer.Ordinal)
            .Count();

        facts.AddTrace(
            $"DotAttachmentPass decisions: candidates={analysis.Decisions.Count}; "
            + $"accepted-dots={analysis.Accepted.Count}; attached-noteheads={attachedNoteheads}; "
            + $"rejected={analysis.Decisions.Count(decision => !decision.Accepted)}; "
            + $"dot-size-band={analysis.MinimumDotSize:F3}..{analysis.MaximumDotSize:F3}sp");
    }
}
