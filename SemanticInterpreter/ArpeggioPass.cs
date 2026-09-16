using SvgMusic.Scene;

namespace SvgMusic.Semantics;

/// <summary>
/// Semantic-scene wrapper for a geometric vertical zigzag. The primitive remains
/// musically neutral until ArpeggioPass proves an attachment to one or more chords.
/// </summary>
public sealed record VerticalZigZagElement : SemanticElement
{
    public required VerticalZigZagPrimitive Source { get; init; }
}

public sealed record ArpeggioFact(
    int MeasureNumber,
    string ZigZagShapeId,
    IReadOnlyList<string> ChordIds,
    IReadOnlyList<string> NoteheadIds,
    IReadOnlyList<int> Staffs,
    string At,
    double AnchorX,
    double MinY,
    double MaxY,
    string? Direction,
    double Confidence,
    string Reason,
    IReadOnlyList<string> SourceShapeIds)
    : SemanticFact(
        "ArpeggioPass",
        Reason,
        SourceShapeIds);

public sealed record ArpeggioDecision(
    string ZigZagShapeId,
    bool Accepted,
    string Decision,
    int MeasureNumber,
    IReadOnlyList<string> ChordIds,
    string? At,
    double Confidence,
    string Reason);

public sealed record ArpeggioAnalysisResult(
    IReadOnlyList<ArpeggioDecision> Decisions)
{
    public IReadOnlyList<ArpeggioDecision> Accepted =>
        Decisions.Where(decision => decision.Accepted).ToArray();
}

/// <summary>
/// Converts already-proven VerticalZigZag geometry into arpeggio semantics.
///
/// The pass deliberately attaches only to ChordFact objects, never arbitrary nearby
/// noteheads. Cross-staff arpeggios are assembled from chord facts that share the same
/// rhythmic onset and x-column. Thus an unrelated simultaneous melodic singleton is
/// not accidentally absorbed by a tall zigzag.
/// </summary>
public sealed class ArpeggioPass : ISemanticPass
{
    private const double MaximumHorizontalGapInSpacings = 3.0;
    private const double MaximumChordColumnDeltaInSpacings = 1.25;
    private const double AllowedHorizontalOverlapInSpacings = 0.50;
    private const double VerticalPaddingInSpacings = 0.75;

    public string Name => nameof(ArpeggioPass);

    public ArpeggioAnalysisResult? LastAnalysis { get; private set; }

    public void Run(
        SemanticDocument document,
        SemanticFacts facts)
    {
        var noteheads = facts.OfType<NoteheadFact>().ToArray();
        var chords = facts.OfType<ChordFact>().ToArray();
        var onsets = facts
            .OfType<OnsetFact>()
            .Where(onset => onset.TargetKind == VoiceTargetKind.Chord)
            .GroupBy(onset => (onset.MeasureNumber, onset.TargetId))
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(onset => onset.Confidence)
                    .ThenBy(onset => onset.Staff)
                    .First());
        var noteheadsByKey = noteheads.ToDictionary(
            notehead => (notehead.MeasureNumber, notehead.ShapeId));
        var decisions = new List<ArpeggioDecision>();
        var seenZigZags = new HashSet<string>(StringComparer.Ordinal);

        foreach (var measure in document.Measures.OrderBy(item => item.Number))
        {
            var spacing = Math.Max(
                0.001,
                (measure.Upper.LineSpacing + measure.Lower.LineSpacing) / 2.0);
            var zigZags = measure.Upper.Elements
                .Concat(measure.Lower.Elements)
                .OfType<VerticalZigZagElement>()
                .Where(element => seenZigZags.Add(element.ShapeId))
                .OrderBy(element => element.Bounds.MinX)
                .ThenBy(element => element.Bounds.MinY)
                .ThenBy(element => element.ShapeId, StringComparer.Ordinal)
                .ToArray();

            foreach (var zigZag in zigZags)
            {
                var candidates = chords
                    .Where(chord => chord.MeasureNumber == measure.Number)
                    .Select(chord => BuildCandidate(
                        chord,
                        noteheadsByKey,
                        onsets))
                    .Where(candidate => candidate is not null)
                    .Select(candidate => candidate!)
                    .Where(candidate => SpatiallyMatches(
                        zigZag,
                        candidate,
                        spacing))
                    .OrderBy(candidate => HorizontalGap(
                        zigZag,
                        candidate))
                    .ThenBy(candidate => candidate.Chord.AnchorX)
                    .ThenBy(candidate => candidate.Chord.ChordId, StringComparer.Ordinal)
                    .ToArray();

                if (candidates.Length == 0)
                {
                    decisions.Add(Reject(
                        zigZag,
                        measure.Number,
                        "no-chord-anchor",
                        "vertical zigzag has no nearby chord with a stable onset"));
                    continue;
                }

                var seed = candidates[0];
                var sameOnset = candidates
                    .Where(candidate => string.Equals(
                        candidate.Onset.At,
                        seed.Onset.At,
                        StringComparison.Ordinal))
                    .Where(candidate =>
                        Math.Abs(candidate.Chord.AnchorX - seed.Chord.AnchorX)
                        <= spacing * MaximumChordColumnDeltaInSpacings)
                    .OrderBy(candidate => candidate.Chord.AnchorX)
                    .ThenBy(candidate => candidate.Chord.ChordId, StringComparer.Ordinal)
                    .ToArray();

                if (sameOnset.Length == 0)
                {
                    decisions.Add(Reject(
                        zigZag,
                        measure.Number,
                        "no-onset-group",
                        "nearby chord candidates do not form a rhythmic onset group"));
                    continue;
                }

                var chordIds = sameOnset
                    .Select(candidate => candidate.Chord.ChordId)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var attachedNoteheads = sameOnset
                    .SelectMany(candidate => candidate.Noteheads)
                    .GroupBy(notehead => notehead.ShapeId, StringComparer.Ordinal)
                    .Select(group => group.First())
                    .OrderBy(notehead => notehead.Staff)
                    .ThenBy(notehead => notehead.CenterY)
                    .ThenBy(notehead => notehead.ShapeId, StringComparer.Ordinal)
                    .ToArray();

                if (attachedNoteheads.Length < 2)
                {
                    decisions.Add(Reject(
                        zigZag,
                        measure.Number,
                        "insufficient-chord-notes",
                        "arpeggio attachment must contain at least two chord noteheads"));
                    continue;
                }

                var staffs = attachedNoteheads
                    .Select(notehead => notehead.Staff)
                    .Distinct()
                    .OrderBy(staff => staff)
                    .ToArray();
                var gap = sameOnset.Min(candidate =>
                    HorizontalGap(zigZag, candidate));
                var proximity = 1.0 - Math.Clamp(
                    Math.Max(0, gap)
                    / (spacing * MaximumHorizontalGapInSpacings),
                    0,
                    1);
                var confidence = new[]
                {
                    zigZag.Source.Confidence,
                    sameOnset.Average(candidate => candidate.Chord.Confidence),
                    sameOnset.Average(candidate => candidate.Onset.Confidence),
                    proximity
                }.Average();
                var sourceIds = zigZag.Source.SourceShapeIds.Count > 0
                    ? zigZag.Source.SourceShapeIds
                    : [zigZag.ShapeId];
                var reason =
                    $"{zigZag.ShapeId}: vertical zigzag -> arpeggio; "
                    + $"at={seed.Onset.At}; chords=[{string.Join(',', chordIds)}]; "
                    + $"staffs=[{string.Join(',', staffs)}]; notes={attachedNoteheads.Length}; "
                    + $"gap={gap / spacing:F2}sp";

                facts.Add(new ArpeggioFact(
                    measure.Number,
                    zigZag.ShapeId,
                    chordIds,
                    attachedNoteheads.Select(notehead => notehead.ShapeId).ToArray(),
                    staffs,
                    seed.Onset.At,
                    sameOnset.Average(candidate => candidate.Chord.AnchorX),
                    attachedNoteheads.Min(notehead => NoteTop(notehead)),
                    attachedNoteheads.Max(notehead => NoteBottom(notehead)),
                    null,
                    confidence,
                    reason,
                    sourceIds));
                decisions.Add(new ArpeggioDecision(
                    zigZag.ShapeId,
                    true,
                    "arpeggio",
                    measure.Number,
                    chordIds,
                    seed.Onset.At,
                    confidence,
                    reason));
            }
        }

        LastAnalysis = new ArpeggioAnalysisResult(decisions);
        facts.AddTrace(
            $"ArpeggioPass: zigzags={decisions.Count}; "
            + $"accepted={decisions.Count(decision => decision.Accepted)}; "
            + $"rejected={decisions.Count(decision => !decision.Accepted)}");
    }

    private static ChordCandidate? BuildCandidate(
        ChordFact chord,
        IReadOnlyDictionary<(int MeasureNumber, string ShapeId), NoteheadFact> noteheads,
        IReadOnlyDictionary<(int MeasureNumber, string TargetId), OnsetFact> onsets)
    {
        if (!onsets.TryGetValue(
                (chord.MeasureNumber, chord.ChordId),
                out var onset))
        {
            return null;
        }

        var members = chord.NoteheadIds
            .Where(noteheadId => noteheads.ContainsKey(
                (chord.MeasureNumber, noteheadId)))
            .Select(noteheadId => noteheads[
                (chord.MeasureNumber, noteheadId)])
            .ToArray();

        if (members.Length < 2)
        {
            return null;
        }

        return new ChordCandidate(
            chord,
            onset,
            members,
            members.Min(NoteLeft),
            members.Max(NoteRight),
            members.Min(NoteTop),
            members.Max(NoteBottom));
    }

    private static bool SpatiallyMatches(
        VerticalZigZagElement zigZag,
        ChordCandidate chord,
        double spacing)
    {
        var gap = HorizontalGap(zigZag, chord);
        if (gap < -spacing * AllowedHorizontalOverlapInSpacings
            || gap > spacing * MaximumHorizontalGapInSpacings)
        {
            return false;
        }

        var padding = spacing * VerticalPaddingInSpacings;
        return chord.MaxY >= zigZag.Bounds.MinY - padding
            && chord.MinY <= zigZag.Bounds.MaxY + padding;
    }

    private static double HorizontalGap(
        VerticalZigZagElement zigZag,
        ChordCandidate chord) =>
        chord.MinX - zigZag.Bounds.MaxX;

    private static ArpeggioDecision Reject(
        VerticalZigZagElement zigZag,
        int measureNumber,
        string decision,
        string reason) =>
        new(
            zigZag.ShapeId,
            false,
            decision,
            measureNumber,
            Array.Empty<string>(),
            null,
            zigZag.Source.Confidence,
            reason);

    private static double Radius(NoteheadFact notehead) =>
        Math.Max(notehead.MajorRadius, notehead.MinorRadius);

    private static double NoteLeft(NoteheadFact notehead) =>
        notehead.CenterX - Radius(notehead);

    private static double NoteRight(NoteheadFact notehead) =>
        notehead.CenterX + Radius(notehead);

    private static double NoteTop(NoteheadFact notehead) =>
        notehead.CenterY - Radius(notehead);

    private static double NoteBottom(NoteheadFact notehead) =>
        notehead.CenterY + Radius(notehead);

    private sealed record ChordCandidate(
        ChordFact Chord,
        OnsetFact Onset,
        IReadOnlyList<NoteheadFact> Noteheads,
        double MinX,
        double MaxX,
        double MinY,
        double MaxY);
}
