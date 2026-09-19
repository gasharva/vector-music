namespace SvgMusic.Semantics;

public sealed record TieFact(
    string CurveShapeId,
    int StartMeasureNumber,
    int EndMeasureNumber,
    int Staff,
    string FromNoteheadId,
    string ToNoteheadId,
    string Pitch,
    string? Placement,
    double StartDistanceInSpacings,
    double EndDistanceInSpacings,
    double Confidence,
    string Reason,
    IReadOnlyList<string> SourceShapeIds)
    : SemanticFact(
        "TiePass",
        Reason,
        SourceShapeIds);

public sealed record TieDecision(
    string CurveShapeId,
    bool Accepted,
    string Decision,
    NoteheadFact? FromNotehead,
    NoteheadFact? ToNotehead,
    string? Pitch,
    string? Placement,
    double? StartDistanceInSpacings,
    double? EndDistanceInSpacings,
    double Confidence,
    string Reason);

public sealed record TieAnalysisResult(
    IReadOnlyList<TieDecision> Decisions)
{
    public IReadOnlyList<TieDecision> Accepted =>
        Decisions.Where(decision => decision.Accepted).ToArray();
}

/// <summary>
/// Converts the same-pitch curved-stroke hypotheses deliberately reserved by
/// SlurPass into semantic ties.
///
/// SlurPass decides which raw curves are tie-like. TieCandidateMatcher then performs
/// a global one-curve/one-note-pair assignment, which is important for tied chords:
/// independent nearest-neighbour matching can otherwise attach neighbouring curves
/// to the same pitch twice.
/// </summary>
public sealed class TiePass : ISemanticPass
{
    public string Name => nameof(TiePass);

    public TieAnalysisResult? LastAnalysis { get; private set; }

    public void Run(
        SemanticDocument document,
        SemanticFacts facts)
    {
        var shadowFacts = new SemanticFacts();
        foreach (var fact in facts.Items)
        {
            shadowFacts.Add(fact);
        }

        var classifier = new SlurPass();
        classifier.Run(document, shadowFacts);

        var claimedBySlur = facts
            .OfType<SlurFact>()
            .Select(slur => slur.CurveShapeId)
            .ToHashSet(StringComparer.Ordinal);
        var alreadyTied = facts
            .OfType<TieFact>()
            .Select(tie => tie.CurveShapeId)
            .ToHashSet(StringComparer.Ordinal);
        var tieLike = classifier.LastAnalysis?.Decisions
            .Where(decision => decision.Decision == "tie-like")
            .Where(decision => !alreadyTied.Contains(decision.CurveShapeId))
            .OrderBy(decision => decision.CurveShapeId, StringComparer.Ordinal)
            .ToArray()
            ?? Array.Empty<SlurDecision>();
        var crossSystemById = facts
            .OfType<CrossSystemCurveFact>()
            .ToDictionary(
                curve => curve.CurveId,
                StringComparer.Ordinal);
        var tieLikeIds = tieLike
            .Where(decision => !claimedBySlur.Contains(decision.CurveShapeId))
            .Where(decision => !crossSystemById.ContainsKey(decision.CurveShapeId))
            .Select(decision => decision.CurveShapeId)
            .ToHashSet(StringComparer.Ordinal);
        var matched = TieCandidateMatcher.Match(
            document,
            facts,
            tieLikeIds);
        var decisions = new List<TieDecision>();

        foreach (var candidate in tieLike)
        {
            if (claimedBySlur.Contains(candidate.CurveShapeId))
            {
                decisions.Add(Reject(
                    candidate,
                    "claimed-by-slur",
                    $"{candidate.CurveShapeId}: curve is already owned by SlurPass"));
                continue;
            }

            if (crossSystemById.TryGetValue(
                    candidate.CurveShapeId,
                    out var crossSystem))
            {
                var from = candidate.FromNotehead!;
                var to = candidate.ToNotehead!;
                var pitch = facts
                    .OfType<PitchFact>()
                    .Where(item =>
                        item.MeasureNumber == from.MeasureNumber
                        && string.Equals(
                            item.NoteheadId,
                            from.ShapeId,
                            StringComparison.Ordinal))
                    .OrderByDescending(item => item.Confidence)
                    .Select(item => item.Pitch)
                    .FirstOrDefault();

                if (pitch is null)
                {
                    decisions.Add(Reject(
                        candidate,
                        "no-unique-pair",
                        $"{candidate.CurveShapeId}: reconstructed cross-system tie lost source pitch"));
                    continue;
                }

                decisions.Add(new TieDecision(
                    candidate.CurveShapeId,
                    true,
                    "tie",
                    from,
                    to,
                    pitch,
                    crossSystem.Placement,
                    crossSystem.StartDistanceInSpacings,
                    crossSystem.EndDistanceInSpacings,
                    crossSystem.Confidence,
                    $"{candidate.CurveShapeId}: reconstructed cross-system curve classified "
                    + $"as tie {from.ShapeId}->{to.ShapeId} at {pitch}; "
                    + $"fragments={crossSystem.OutgoingCurveShapeId}+"
                    + $"{crossSystem.IncomingCurveShapeId}; "
                    + $"placement={crossSystem.Placement ?? "unspecified"}"));
                continue;
            }

            if (!matched.TryGetValue(candidate.CurveShapeId, out var match))
            {
                decisions.Add(Reject(
                    candidate,
                    "no-unique-pair",
                    $"{candidate.CurveShapeId}: no unused same-pitch endpoint pair remains after global tie matching"));
                continue;
            }

            var confidence = Math.Clamp(
                0.99 - 0.11 * match.Score,
                0.60,
                0.99);
            var reason = $"{candidate.CurveShapeId}: globally matched same-pitch curved stroke "
                + $"{match.FromNotehead.ShapeId} -> {match.ToNotehead.ShapeId} "
                + $"at {match.Pitch}; distances="
                + $"{match.StartDistanceInSpacings:F2}/{match.EndDistanceInSpacings:F2}sp; "
                + $"placement={match.Placement ?? "unspecified"}";

            decisions.Add(new TieDecision(
                candidate.CurveShapeId,
                true,
                "tie",
                match.FromNotehead,
                match.ToNotehead,
                match.Pitch,
                match.Placement,
                match.StartDistanceInSpacings,
                match.EndDistanceInSpacings,
                confidence,
                reason));
        }

        LastAnalysis = new TieAnalysisResult(decisions);

        foreach (var decision in decisions.Where(decision => decision.Accepted))
        {
            var from = decision.FromNotehead!;
            var to = decision.ToNotehead!;

            var sourceShapeIds = crossSystemById.TryGetValue(
                    decision.CurveShapeId,
                    out var crossSystem)
                ? crossSystem.SourceShapeIds
                    .Concat(new[]
                    {
                        from.ShapeId,
                        to.ShapeId
                    })
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
                : new[]
                {
                    decision.CurveShapeId,
                    from.ShapeId,
                    to.ShapeId
                };

            facts.Add(new TieFact(
                decision.CurveShapeId,
                from.MeasureNumber,
                to.MeasureNumber,
                from.Staff,
                from.ShapeId,
                to.ShapeId,
                decision.Pitch!,
                decision.Placement,
                decision.StartDistanceInSpacings!.Value,
                decision.EndDistanceInSpacings!.Value,
                decision.Confidence,
                decision.Reason,
                sourceShapeIds));
        }

        facts.AddTrace(
            $"TiePass: candidates={decisions.Count}; accepted={decisions.Count(decision => decision.Accepted)}; "
            + $"cross-system={decisions.Count(decision => crossSystemById.ContainsKey(decision.CurveShapeId) && decision.Accepted)}; "
            + $"claimed-by-slur={decisions.Count(decision => decision.Decision == "claimed-by-slur")}; "
            + $"unmatched={decisions.Count(decision => decision.Decision == "no-unique-pair")}");
    }

    private static TieDecision Reject(
        SlurDecision candidate,
        string decision,
        string reason)
    {
        return new TieDecision(
            candidate.CurveShapeId,
            false,
            decision,
            candidate.FromNotehead,
            candidate.ToNotehead,
            null,
            candidate.Placement,
            candidate.StartDistanceInSpacings,
            candidate.EndDistanceInSpacings,
            0,
            reason);
    }
}
