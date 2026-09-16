using SvgMusic.Scene;

namespace SvgMusic.Semantics;

/// <summary>
/// Recovers same-pitch ties whose curve was geometrically assigned to the neighbouring
/// staff ownership band. This happens naturally for ties attached to high ledger notes
/// in the lower staff (and symmetrically for low ledger notes in the upper staff).
///
/// Normal SlurPass/TiePass remains the primary classifier. This recovery only sees
/// still-unclaimed horizontal curves and requires a close, chronological, same-pitch,
/// same-staff, same-local-voice note pair. Curve ownership itself is deliberately not
/// used as a staff constraint.
/// </summary>
public sealed class TieOwnershipRecoveryPass : ISemanticPass
{
    private const double MinimumHorizontalSpanInSpacings = 1.20;
    private const double MaximumEndpointDistanceInSpacings = 3.25;
    private const int EndpointCandidateLimit = 18;

    public string Name => nameof(TieOwnershipRecoveryPass);

    public void Run(
        SemanticDocument document,
        SemanticFacts facts)
    {
        var claimedCurves = facts.OfType<SlurFact>()
            .Select(slur => slur.CurveShapeId)
            .Concat(facts.OfType<TieFact>().Select(tie => tie.CurveShapeId))
            .ToHashSet(StringComparer.Ordinal);
        var existingPairs = facts.OfType<TieFact>()
            .Select(tie => PairKey(
                tie.StartMeasureNumber,
                tie.FromNoteheadId,
                tie.EndMeasureNumber,
                tie.ToNoteheadId))
            .ToHashSet(StringComparer.Ordinal);
        var anchors = BuildAnchors(facts);
        var observations = CollectCurves(document)
            .Where(observation => !claimedCurves.Contains(observation.Curve.ShapeId))
            .ToArray();
        var candidates = new List<RecoveryCandidate>();

        foreach (var observation in observations)
        {
            candidates.AddRange(BuildCandidates(
                observation,
                anchors,
                existingPairs));
        }

        var assignedCurves = new HashSet<string>(StringComparer.Ordinal);
        var assignedPairs = new HashSet<string>(existingPairs, StringComparer.Ordinal);
        var accepted = new List<RecoveryCandidate>();

        foreach (var candidate in candidates
                     .OrderBy(candidate => candidate.Score)
                     .ThenBy(candidate => candidate.CurveShapeId, StringComparer.Ordinal)
                     .ThenBy(candidate => candidate.PairKey, StringComparer.Ordinal))
        {
            if (!assignedCurves.Add(candidate.CurveShapeId))
            {
                continue;
            }

            if (!assignedPairs.Add(candidate.PairKey))
            {
                assignedCurves.Remove(candidate.CurveShapeId);
                continue;
            }

            accepted.Add(candidate);
        }

        foreach (var candidate in accepted)
        {
            var ownershipMismatch = !candidate.ObservedStaffs.Contains(candidate.From.Notehead.Staff);
            var confidence = Math.Clamp(
                0.96 - 0.10 * candidate.Score,
                0.58,
                0.95);
            var reason = $"{candidate.CurveShapeId}: recovered same-pitch tie "
                + $"{candidate.From.Notehead.ShapeId} -> {candidate.To.Notehead.ShapeId} "
                + $"at {candidate.From.Pitch.Pitch}; endpoint distances="
                + $"{candidate.StartDistance:F2}/{candidate.EndDistance:F2}sp; "
                + $"curve-staffs=[{string.Join(',', candidate.ObservedStaffs.OrderBy(staff => staff))}]; "
                + $"endpoint-staff={candidate.From.Notehead.Staff}; ownership-mismatch={ownershipMismatch}";

            facts.Add(new TieFact(
                candidate.CurveShapeId,
                candidate.From.Notehead.MeasureNumber,
                candidate.To.Notehead.MeasureNumber,
                candidate.From.Notehead.Staff,
                candidate.From.Notehead.ShapeId,
                candidate.To.Notehead.ShapeId,
                candidate.From.Pitch.Pitch,
                candidate.Placement,
                candidate.StartDistance,
                candidate.EndDistance,
                confidence,
                reason,
                [
                    candidate.CurveShapeId,
                    candidate.From.Notehead.ShapeId,
                    candidate.To.Notehead.ShapeId
                ]));
        }

        facts.AddTrace(
            $"TieOwnershipRecoveryPass: unclaimed-curves={observations.Length}; "
            + $"same-pitch-candidates={candidates.Count}; recovered={accepted.Count}; "
            + $"ownership-mismatch={accepted.Count(candidate => !candidate.ObservedStaffs.Contains(candidate.From.Notehead.Staff))}");
    }

    private static IReadOnlyList<RecoveryCandidate> BuildCandidates(
        CurveObservation observation,
        IReadOnlyList<EndpointAnchor> anchors,
        IReadOnlySet<string> existingPairs)
    {
        var curve = observation.Curve;
        if (curve.Centerline.Count < 2)
        {
            return [];
        }

        var spacing = Math.Max(observation.Spacing, 0.001);
        var first = curve.Centerline[0];
        var last = curve.Centerline[^1];
        var left = first.X <= last.X ? first : last;
        var right = first.X <= last.X ? last : first;
        var horizontalSpan = Math.Abs(right.X - left.X) / spacing;
        if (horizontalSpan < MinimumHorizontalSpanInSpacings)
        {
            return [];
        }

        var starts = EndpointCandidates(
            left,
            observation,
            anchors,
            spacing);
        var ends = EndpointCandidates(
            right,
            observation,
            anchors,
            spacing);
        var placement = Placement(curve, left, right);
        var result = new List<RecoveryCandidate>();

        foreach (var start in starts)
        {
            foreach (var end in ends)
            {
                if (SameTarget(start.Anchor, end.Anchor)
                    || ComesAfter(start.Anchor.Notehead, end.Anchor.Notehead)
                    || (start.Anchor.Notehead.MeasureNumber == end.Anchor.Notehead.MeasureNumber
                        && start.Anchor.Notehead.ShapeId == end.Anchor.Notehead.ShapeId)
                    || start.Anchor.Notehead.Staff != end.Anchor.Notehead.Staff
                    || start.Anchor.LocalVoice != end.Anchor.LocalVoice
                    || !string.Equals(
                        start.Anchor.Pitch.Pitch,
                        end.Anchor.Pitch.Pitch,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                var pairKey = PairKey(
                    start.Anchor.Notehead.MeasureNumber,
                    start.Anchor.Notehead.ShapeId,
                    end.Anchor.Notehead.MeasureNumber,
                    end.Anchor.Notehead.ShapeId);
                if (existingPairs.Contains(pairKey))
                {
                    continue;
                }

                result.Add(new RecoveryCandidate(
                    curve.ShapeId,
                    start.Anchor,
                    end.Anchor,
                    start.DistanceInSpacings,
                    end.DistanceInSpacings,
                    start.DistanceInSpacings + end.DistanceInSpacings,
                    placement,
                    observation.Staffs,
                    pairKey));
            }
        }

        return result
            .OrderBy(candidate => candidate.Score)
            .ThenBy(candidate => candidate.PairKey, StringComparer.Ordinal)
            .Take(32)
            .ToArray();
    }

    private static IReadOnlyList<EndpointChoice> EndpointCandidates(
        PointD endpoint,
        CurveObservation observation,
        IReadOnlyList<EndpointAnchor> anchors,
        double spacing)
    {
        return anchors
            .Where(anchor => observation.Measures.Contains(anchor.Notehead.MeasureNumber))
            .Select(anchor => new EndpointChoice(
                anchor,
                EndpointDistance(endpoint, anchor) / spacing))
            .Where(candidate => candidate.DistanceInSpacings <= MaximumEndpointDistanceInSpacings)
            .OrderBy(candidate => candidate.DistanceInSpacings)
            .ThenBy(candidate => candidate.Anchor.Notehead.MeasureNumber)
            .ThenBy(candidate => candidate.Anchor.Notehead.CenterX)
            .ThenBy(candidate => candidate.Anchor.Notehead.CenterY)
            .Take(EndpointCandidateLimit)
            .ToArray();
    }

    private static IReadOnlyList<EndpointAnchor> BuildAnchors(SemanticFacts facts)
    {
        var pitches = facts.OfType<PitchFact>()
            .GroupBy(pitch => new NoteKey(
                pitch.MeasureNumber,
                pitch.NoteheadId))
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(pitch => pitch.Confidence).First());
        var stems = facts.OfType<StemAttachmentFact>().ToArray();
        var chords = facts.OfType<ChordFact>().ToArray();
        var voices = facts.OfType<VoiceFact>().ToArray();
        var chordByNotehead = chords
            .SelectMany(chord => chord.NoteheadIds.Select(noteheadId => new
            {
                Key = new NoteKey(chord.MeasureNumber, noteheadId),
                Chord = chord
            }))
            .GroupBy(item => item.Key)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(item => item.Chord.Confidence).First().Chord);
        var stemByNotehead = stems
            .SelectMany(stem => stem.AttachedNoteheadIds.Select(noteheadId => new
            {
                Key = new NoteKey(stem.MeasureNumber, noteheadId),
                Stem = stem
            }))
            .GroupBy(item => item.Key)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(item => item.Stem.Confidence)
                    .ThenBy(item => item.Stem.StemShapeId, StringComparer.Ordinal)
                    .First().Stem);
        var voiceByTarget = voices
            .GroupBy(voice => new VoiceKey(
                voice.MeasureNumber,
                voice.Staff,
                voice.TargetKind,
                voice.TargetId))
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(voice => voice.Confidence).First());

        return facts.OfType<NoteheadFact>()
            .Where(notehead => pitches.ContainsKey(new NoteKey(
                notehead.MeasureNumber,
                notehead.ShapeId)))
            .Select(notehead =>
            {
                var noteKey = new NoteKey(
                    notehead.MeasureNumber,
                    notehead.ShapeId);
                chordByNotehead.TryGetValue(noteKey, out var chord);
                stemByNotehead.TryGetValue(noteKey, out var stem);
                var targetKind = chord is null
                    ? VoiceTargetKind.Notehead
                    : VoiceTargetKind.Chord;
                var targetId = chord?.ChordId ?? notehead.ShapeId;
                var localVoice = voiceByTarget.TryGetValue(
                        new VoiceKey(
                            notehead.MeasureNumber,
                            notehead.Staff,
                            targetKind,
                            targetId),
                        out var voice)
                    ? voice.LocalVoice
                    : 1;

                return new EndpointAnchor(
                    notehead,
                    pitches[noteKey],
                    stem,
                    targetKind,
                    targetId,
                    localVoice);
            })
            .ToArray();
    }

    private static double EndpointDistance(
        PointD point,
        EndpointAnchor anchor)
    {
        var note = anchor.Notehead;
        var dx = point.X - note.CenterX;
        var dy = point.Y - note.CenterY;
        var centerDistance = Math.Sqrt(dx * dx + dy * dy);
        var noteDistance = Math.Max(
            0,
            centerDistance - Math.Max(note.MajorRadius, note.MinorRadius));
        if (anchor.Stem is null)
        {
            return noteDistance;
        }

        return Math.Min(
            noteDistance,
            DistanceToSegment(
                point,
                new PointD(anchor.Stem.StartX, anchor.Stem.StartY),
                new PointD(anchor.Stem.EndX, anchor.Stem.EndY)));
    }

    private static double DistanceToSegment(
        PointD point,
        PointD a,
        PointD b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var lengthSquared = dx * dx + dy * dy;
        if (lengthSquared <= 1e-12)
        {
            return Distance(point, a);
        }

        var t = ((point.X - a.X) * dx + (point.Y - a.Y) * dy) / lengthSquared;
        t = Math.Clamp(t, 0, 1);
        return Distance(
            point,
            new PointD(
                a.X + t * dx,
                a.Y + t * dy));
    }

    private static double Distance(PointD a, PointD b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static bool SameTarget(
        EndpointAnchor first,
        EndpointAnchor second)
    {
        return first.TargetKind == second.TargetKind
            && string.Equals(
                first.TargetId,
                second.TargetId,
                StringComparison.Ordinal);
    }

    private static bool ComesAfter(
        NoteheadFact left,
        NoteheadFact right)
    {
        if (left.MeasureNumber != right.MeasureNumber)
        {
            return left.MeasureNumber > right.MeasureNumber;
        }

        return left.CenterX > right.CenterX + 1e-6;
    }

    private static string? Placement(
        CurvedStroke curve,
        PointD left,
        PointD right)
    {
        if (curve.Centerline.Count < 3)
        {
            return null;
        }

        var middle = curve.Centerline[curve.Centerline.Count / 2];
        var dx = right.X - left.X;
        var t = Math.Abs(dx) < 1e-9
            ? 0.5
            : Math.Clamp((middle.X - left.X) / dx, 0, 1);
        var chordY = left.Y + t * (right.Y - left.Y);
        if (Math.Abs(middle.Y - chordY) < 1e-6)
        {
            return null;
        }

        return middle.Y < chordY ? "above" : "below";
    }

    private static IReadOnlyList<CurveObservation> CollectCurves(
        SemanticDocument document)
    {
        var builders = new Dictionary<string, CurveObservationBuilder>(StringComparer.Ordinal);

        foreach (var measure in document.Measures)
        {
            foreach (var staff in new[] { measure.Upper, measure.Lower })
            {
                foreach (var curve in staff.Elements.OfType<CurveElement>())
                {
                    if (!builders.TryGetValue(curve.ShapeId, out var builder))
                    {
                        builder = new CurveObservationBuilder(curve.Source);
                        builders[curve.ShapeId] = builder;
                    }

                    builder.Measures.Add(measure.Number);
                    builder.Staffs.Add(staff.StaffNumber);
                    builder.Spacings.Add(staff.LineSpacing);
                }
            }
        }

        return builders.Values
            .Select(builder => new CurveObservation(
                builder.Curve,
                builder.Measures,
                builder.Staffs,
                builder.Spacings.Count == 0
                    ? 1.0
                    : builder.Spacings.Average()))
            .ToArray();
    }

    private static string PairKey(
        int startMeasure,
        string fromId,
        int endMeasure,
        string toId) =>
        $"{startMeasure}:{fromId}\u001f{endMeasure}:{toId}";

    private readonly record struct NoteKey(
        int MeasureNumber,
        string NoteheadId);

    private readonly record struct VoiceKey(
        int MeasureNumber,
        int Staff,
        VoiceTargetKind TargetKind,
        string TargetId);

    private sealed record EndpointAnchor(
        NoteheadFact Notehead,
        PitchFact Pitch,
        StemAttachmentFact? Stem,
        VoiceTargetKind TargetKind,
        string TargetId,
        int LocalVoice);

    private sealed record EndpointChoice(
        EndpointAnchor Anchor,
        double DistanceInSpacings);

    private sealed record RecoveryCandidate(
        string CurveShapeId,
        EndpointAnchor From,
        EndpointAnchor To,
        double StartDistance,
        double EndDistance,
        double Score,
        string? Placement,
        IReadOnlySet<int> ObservedStaffs,
        string PairKey);

    private sealed record CurveObservation(
        CurvedStroke Curve,
        IReadOnlySet<int> Measures,
        IReadOnlySet<int> Staffs,
        double Spacing);

    private sealed class CurveObservationBuilder(CurvedStroke curve)
    {
        public CurvedStroke Curve { get; } = curve;
        public HashSet<int> Measures { get; } = [];
        public HashSet<int> Staffs { get; } = [];
        public List<double> Spacings { get; } = [];
    }
}
