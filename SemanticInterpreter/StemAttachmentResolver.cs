namespace SvgMusic.Semantics;

public sealed record StemAttachmentAlternative(
    string StemShapeId,
    StemDirection Direction,
    double MatchScore,
    double EdgeDistanceInSpacings,
    double StemConfidence);

public sealed record StemAttachmentConflict(
    int MeasureNumber,
    string NoteheadId,
    string WinnerStemShapeId,
    IReadOnlyList<StemAttachmentAlternative> Alternatives);

/// <summary>
/// Resolves notehead ownership when more than one geometrically plausible stem
/// touches the same notehead.
///
/// StemAttachmentAnalyzer intentionally remains permissive and records every
/// local geometric match. This resolver is the global arbitration step:
/// each notehead is assigned to its strongest attachment evidence, while one
/// stem may still own multiple noteheads (a normal chord).
/// </summary>
public sealed class StemAttachmentResolver
{
    public IReadOnlyList<StemAttachmentConflict> LastConflicts { get; private set; }
        = Array.Empty<StemAttachmentConflict>();

    public StemAnalysisResult Resolve(StemAnalysisResult analysis)
    {
        var accepted = analysis.Accepted;

        var choices = accepted
            .SelectMany(decision => decision.Matches.Select(match =>
                new AttachmentChoice(decision, match)))
            .GroupBy(
                choice => new NoteKey(
                    choice.Match.Notehead.MeasureNumber,
                    choice.Match.Notehead.ShapeId))
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(choice => choice.Match.Score)
                    .ThenBy(choice => choice.Match.EdgeDistanceInSpacings)
                    .ThenByDescending(choice => choice.Decision.Confidence)
                    .ThenBy(
                        choice => choice.Decision.Candidate.Stroke.ShapeId,
                        StringComparer.Ordinal)
                    .ToArray());

        var winnerByNotehead = choices.ToDictionary(
            pair => pair.Key,
            pair => pair.Value[0]);

        LastConflicts = choices
            .Where(pair => pair.Value.Length > 1)
            .Select(pair =>
            {
                var winner = pair.Value[0];
                return new StemAttachmentConflict(
                    pair.Key.MeasureNumber,
                    pair.Key.NoteheadId,
                    winner.Decision.Candidate.Stroke.ShapeId,
                    pair.Value.Select(choice =>
                        new StemAttachmentAlternative(
                            choice.Decision.Candidate.Stroke.ShapeId,
                            choice.Decision.Direction,
                            choice.Match.Score,
                            choice.Match.EdgeDistanceInSpacings,
                            choice.Decision.Confidence))
                        .ToArray());
            })
            .OrderBy(conflict => conflict.MeasureNumber)
            .ThenBy(conflict => conflict.NoteheadId, StringComparer.Ordinal)
            .ToArray();

        var resolvedDecisions = analysis.Decisions
            .Select(decision => ResolveDecision(
                decision,
                winnerByNotehead))
            .ToArray();

        var attached = resolvedDecisions
            .Where(decision => decision.Accepted)
            .SelectMany(decision => decision.Matches)
            .Select(match => match.Notehead.ShapeId)
            .Distinct(StringComparer.Ordinal)
            .Count();

        return new StemAnalysisResult(
            resolvedDecisions,
            analysis.TotalNoteheads,
            attached);
    }

    private static StemDecision ResolveDecision(
        StemDecision decision,
        IReadOnlyDictionary<NoteKey, AttachmentChoice> winnerByNotehead)
    {
        if (!decision.Accepted)
        {
            return decision;
        }

        var stemId = decision.Candidate.Stroke.ShapeId;
        var resolvedMatches = decision.Matches
            .Where(match =>
            {
                var key = new NoteKey(
                    match.Notehead.MeasureNumber,
                    match.Notehead.ShapeId);

                return winnerByNotehead.TryGetValue(key, out var winner)
                    && string.Equals(
                        winner.Decision.Candidate.Stroke.ShapeId,
                        stemId,
                        StringComparison.Ordinal);
            })
            .ToArray();

        if (resolvedMatches.Length == 0)
        {
            return decision with
            {
                Accepted = false,
                Decision = "lost-notehead-competition",
                Direction = StemDirection.Ambiguous,
                Matches = [],
                IsCrossStaff = false,
                Confidence = 0,
                Reason = decision.Reason
                    + "; all touching noteheads preferred stronger competing stems"
            };
        }

        if (resolvedMatches.Length == decision.Matches.Count)
        {
            return decision;
        }

        var direction = StemAttachmentAnalyzer.InferDirection(
            decision.Candidate,
            resolvedMatches);
        var attachedStaffs = resolvedMatches
            .Select(match => match.Notehead.Staff)
            .Distinct()
            .ToArray();
        var confidence = StemAttachmentAnalyzer.ComputeConfidence(
            decision.Candidate,
            resolvedMatches);

        return decision with
        {
            Direction = direction,
            Matches = resolvedMatches,
            IsCrossStaff = attachedStaffs.Length > 1,
            Confidence = confidence,
            Reason = decision.Reason
                + $"; resolver retained {resolvedMatches.Length}/{decision.Matches.Count} "
                + "notehead attachment(s) after global competition"
        };
    }

    private sealed record AttachmentChoice(
        StemDecision Decision,
        StemNoteheadMatch Match);

    private readonly record struct NoteKey(
        int MeasureNumber,
        string NoteheadId);
}
