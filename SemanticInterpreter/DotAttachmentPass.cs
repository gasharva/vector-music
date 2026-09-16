namespace SvgMusic.Semantics;

public sealed class DotAttachmentPass : ISemanticPass
{
    private const double MinimumRestDotCenterGapInSpacings = 0.25;
    private const double MaximumRestDotCenterGapInSpacings = 1.80;
    private const double ExpectedRestDotCenterGapInSpacings = 1.15;
    private const double MaximumRestDotVerticalOffsetInSpacings = 0.75;

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

        var restMatches = MatchRejectedDotsToRests(
            analysis,
            facts.OfType<RestFact>().ToArray());

        foreach (var group in restMatches
                     .GroupBy(match => new
                     {
                         match.Rest.MeasureNumber,
                         match.Rest.Staff,
                         match.Rest.ShapeId
                     }))
        {
            var ordered = group
                .OrderBy(match => match.Decision.Candidate.Ellipse.CenterX)
                .ToArray();
            var rest = ordered[0].Rest;
            var dotIds = ordered
                .Select(match => match.Decision.Candidate.Ellipse.ShapeId)
                .ToArray();
            var confidence = ordered.Average(match => match.Confidence);

            facts.Add(new RestDotAttachmentFact(
                rest.MeasureNumber,
                rest.Staff,
                rest.ShapeId,
                dotIds,
                dotIds.Length,
                confidence,
                $"{dotIds.Length} augmentation dot(s) attached to rest {rest.ShapeId}; "
                + string.Join(
                    "; ",
                    ordered.Select(match => match.Reason)),
                [rest.ShapeId, .. dotIds]));
        }

        var attachedNoteheads = analysis.Accepted
            .Where(decision => decision.Match is not null)
            .Select(decision => decision.Match!.Notehead.ShapeId)
            .Distinct(StringComparer.Ordinal)
            .Count();
        var attachedRests = restMatches
            .Select(match => match.Rest.ShapeId)
            .Distinct(StringComparer.Ordinal)
            .Count();

        facts.AddTrace(
            $"DotAttachmentPass decisions: candidates={analysis.Decisions.Count}; "
            + $"accepted-note-dots={analysis.Accepted.Count}; "
            + $"accepted-rest-dots={restMatches.Count}; "
            + $"attached-noteheads={attachedNoteheads}; attached-rests={attachedRests}; "
            + $"rejected={analysis.Decisions.Count(decision => !decision.Accepted) - restMatches.Count}; "
            + $"dot-size-band={analysis.MinimumDotSize:F3}..{analysis.MaximumDotSize:F3}sp");
    }

    private static IReadOnlyList<RestDotMatch> MatchRejectedDotsToRests(
        DotAnalysisResult analysis,
        IReadOnlyList<RestFact> rests)
    {
        var result = new List<RestDotMatch>();

        foreach (var decision in analysis.Decisions.Where(decision => !decision.Accepted))
        {
            var candidate = decision.Candidate;
            var spacing = Math.Max(candidate.LineSpacing, 0.001);
            var dot = candidate.Ellipse;

            var best = rests
                .Where(rest =>
                    rest.MeasureNumber == candidate.MeasureNumber
                    && rest.Staff == candidate.StaffNumber)
                .Select(rest =>
                {
                    var horizontal = (dot.CenterX - rest.CenterX) / spacing;
                    var vertical = Math.Abs(dot.CenterY - rest.CenterY) / spacing;
                    return new
                    {
                        Rest = rest,
                        Horizontal = horizontal,
                        Vertical = vertical
                    };
                })
                .Where(item =>
                    item.Horizontal >= MinimumRestDotCenterGapInSpacings
                    && item.Horizontal <= MaximumRestDotCenterGapInSpacings
                    && item.Vertical <= MaximumRestDotVerticalOffsetInSpacings)
                .Select(item =>
                {
                    var horizontalError = Math.Abs(
                        item.Horizontal - ExpectedRestDotCenterGapInSpacings)
                        / Math.Max(
                            MaximumRestDotCenterGapInSpacings - MinimumRestDotCenterGapInSpacings,
                            0.001);
                    var verticalError = item.Vertical
                        / MaximumRestDotVerticalOffsetInSpacings;
                    var score = Math.Clamp(
                        1.0 - 0.35 * horizontalError - 0.65 * verticalError,
                        0,
                        1);
                    return new RestDotMatch(
                        decision,
                        item.Rest,
                        score,
                        $"rest-dot geometry horizontal={item.Horizontal:F3}sp; vertical={item.Vertical:F3}sp");
                })
                .OrderByDescending(match => match.Confidence)
                .ThenBy(match => Math.Abs(
                    match.Decision.Candidate.Ellipse.CenterX - match.Rest.CenterX))
                .FirstOrDefault();

            if (best is not null)
            {
                result.Add(best);
            }
        }

        return result;
    }

    private sealed record RestDotMatch(
        DotDecision Decision,
        RestFact Rest,
        double Confidence,
        string Reason);
}
