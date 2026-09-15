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
/// The expensive/ambiguous part is deciding what a raw curve connects. Rather than
/// maintain a second nearly-identical endpoint matcher, TiePass reuses SlurPass on a
/// shadow fact set and consumes only its tie-like decisions. The current score has a
/// few dozen curves, so repeating this geometric pass is negligible and guarantees
/// that slur/tie classification cannot drift apart.
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

        var pitchByNotehead = facts
            .OfType<PitchFact>()
            .GroupBy(pitch => pitch.NoteheadId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(pitch => pitch.Confidence).First(),
                StringComparer.Ordinal);

        var claimedBySlur = facts
            .OfType<SlurFact>()
            .Select(slur => slur.CurveShapeId)
            .ToHashSet(StringComparer.Ordinal);
        var alreadyTied = facts
            .OfType<TieFact>()
            .Select(tie => tie.CurveShapeId)
            .ToHashSet(StringComparer.Ordinal);

        var decisions = new List<TieDecision>();
        var tieCandidates = classifier.LastAnalysis?.Decisions
            .Where(decision => decision.Decision == "tie-like")
            .OrderBy(decision => decision.CurveShapeId, StringComparer.Ordinal)
            .ToArray()
            ?? Array.Empty<SlurDecision>();

        foreach (var candidate in tieCandidates)
        {
            if (alreadyTied.Contains(candidate.CurveShapeId))
            {
                continue;
            }

            if (claimedBySlur.Contains(candidate.CurveShapeId))
            {
                decisions.Add(Reject(
                    candidate,
                    "claimed-by-slur",
                    $"{candidate.CurveShapeId}: curve is already owned by SlurPass"));
                continue;
            }

            if (candidate.FromNotehead is null
                || candidate.ToNotehead is null
                || candidate.StartDistanceInSpacings is null
                || candidate.EndDistanceInSpacings is null)
            {
                decisions.Add(Reject(
                    candidate,
                    "incomplete-tie-candidate",
                    $"{candidate.CurveShapeId}: tie-like decision has incomplete endpoints"));
                continue;
            }

            if (!pitchByNotehead.TryGetValue(candidate.FromNotehead.ShapeId, out var fromPitch)
                || !pitchByNotehead.TryGetValue(candidate.ToNotehead.ShapeId, out var toPitch)
                || !string.Equals(fromPitch.Pitch, toPitch.Pitch, StringComparison.Ordinal))
            {
                decisions.Add(Reject(
                    candidate,
                    "pitch-mismatch",
                    $"{candidate.CurveShapeId}: tie endpoints no longer resolve to one pitch"));
                continue;
            }

            var distanceSum = candidate.StartDistanceInSpacings.Value
                + candidate.EndDistanceInSpacings.Value;
            var confidence = Math.Clamp(
                0.99 - 0.11 * distanceSum,
                0.60,
                0.99);
            var reason = $"{candidate.CurveShapeId}: same-pitch curved stroke joins "
                + $"{candidate.FromNotehead.ShapeId} -> {candidate.ToNotehead.ShapeId} "
                + $"at {fromPitch.Pitch}; distances="
                + $"{candidate.StartDistanceInSpacings.Value:F2}/"
                + $"{candidate.EndDistanceInSpacings.Value:F2}sp; "
                + $"placement={candidate.Placement ?? "unspecified"}";

            decisions.Add(new TieDecision(
                candidate.CurveShapeId,
                true,
                "tie",
                candidate.FromNotehead,
                candidate.ToNotehead,
                fromPitch.Pitch,
                candidate.Placement,
                candidate.StartDistanceInSpacings,
                candidate.EndDistanceInSpacings,
                confidence,
                reason));
        }

        LastAnalysis = new TieAnalysisResult(decisions);

        foreach (var decision in decisions.Where(decision => decision.Accepted))
        {
            var from = decision.FromNotehead!;
            var to = decision.ToNotehead!;

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
                [decision.CurveShapeId, from.ShapeId, to.ShapeId]));
        }

        facts.AddTrace(
            $"TiePass: candidates={decisions.Count}; accepted={decisions.Count(decision => decision.Accepted)}; "
            + $"claimed-by-slur={decisions.Count(decision => decision.Decision == "claimed-by-slur")}; "
            + $"rejected={decisions.Count(decision => !decision.Accepted)}");
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
