using SvgMusic.Canonical;
using SvgMusic.Scene;

namespace SvgMusic.Semantics;

/// <summary>
/// Reconstructs ties split by a system break into two disconnected SVG curve
/// fragments: one fragment runs from a real note to the end of the old system,
/// the other starts at the beginning of the new system and runs into the tied note.
///
/// The pass runs only after ordinary slur/tie recovery and only considers still
/// unclaimed curves on adjacent measures where the second measure has BreakBefore.
/// </summary>
public sealed class CrossSystemTieRecoveryPass : ISemanticPass
{
    private const double MaximumNoteEndpointDistanceInSpacings = 2.25;
    private const double MaximumBoundaryEndpointDistanceInSpacings = 2.50;
    private const double MinimumHorizontalSpanInSpacings = 0.75;
    private const double FractionTolerance = 1e-9;

    public string Name => nameof(CrossSystemTieRecoveryPass);

    public void Run(
        SemanticDocument document,
        SemanticFacts facts)
    {
        var measures = document.Measures
            .OrderBy(measure => measure.Number)
            .ToArray();

        if (measures.Length < 2)
        {
            facts.AddTrace(
                "CrossSystemTieRecoveryPass: fewer than two measures");
            return;
        }

        var claimedCurves = facts
            .OfType<SlurFact>()
            .Select(slur => slur.CurveShapeId)
            .Concat(
                facts.OfType<TieFact>()
                    .SelectMany(tie =>
                        tie.SourceShapeIds))
            .ToHashSet(StringComparer.Ordinal);

        var existingPairs = facts
            .OfType<TieFact>()
            .Select(tie => PairKey(
                tie.StartMeasureNumber,
                tie.FromNoteheadId,
                tie.EndMeasureNumber,
                tie.ToNoteheadId))
            .ToHashSet(StringComparer.Ordinal);

        var anchors = BuildAnchors(facts);
        var measureLengths = BuildMeasureLengths(
            measures,
            facts);
        var recovered = new List<RecoveryCandidate>();

        for (var index = 1;
             index < measures.Length;
             index++)
        {
            var next = measures[index];
            var previous = measures[index - 1];

            if (!next.BreakBefore
                || next.Number != previous.Number + 1)
            {
                continue;
            }

            foreach (var staffNumber in new[] { 1, 2 })
            {
                var previousStaff = staffNumber == 1
                    ? previous.Upper
                    : previous.Lower;
                var nextStaff = staffNumber == 1
                    ? next.Upper
                    : next.Lower;

                var previousFragments = BuildOutgoingFragments(
                    previous,
                    previousStaff,
                    anchors,
                    measureLengths[previous.Number],
                    claimedCurves);
                var nextFragments = BuildIncomingFragments(
                    next,
                    nextStaff,
                    anchors,
                    claimedCurves);

                var candidates = previousFragments
                    .SelectMany(left =>
                        nextFragments
                            .Where(right =>
                                Compatible(left, right))
                            .Select(right =>
                                new RecoveryCandidate(
                                    left,
                                    right,
                                    left.Score + right.Score)))
                    .OrderBy(candidate => candidate.Score)
                    .ThenBy(candidate =>
                        candidate.Left.Curve.ShapeId,
                        StringComparer.Ordinal)
                    .ThenBy(candidate =>
                        candidate.Right.Curve.ShapeId,
                        StringComparer.Ordinal)
                    .ToArray();

                foreach (var candidate in candidates)
                {
                    var pairKey = PairKey(
                        previous.Number,
                        candidate.Left.Anchor.Notehead.ShapeId,
                        next.Number,
                        candidate.Right.Anchor.Notehead.ShapeId);

                    if (existingPairs.Contains(pairKey)
                        || claimedCurves.Contains(
                            candidate.Left.Curve.ShapeId)
                        || claimedCurves.Contains(
                            candidate.Right.Curve.ShapeId))
                    {
                        continue;
                    }

                    recovered.Add(candidate);
                    existingPairs.Add(pairKey);
                    claimedCurves.Add(
                        candidate.Left.Curve.ShapeId);
                    claimedCurves.Add(
                        candidate.Right.Curve.ShapeId);
                    break;
                }
            }
        }

        foreach (var candidate in recovered)
        {
            var from = candidate.Left.Anchor;
            var to = candidate.Right.Anchor;
            var placement = candidate.Left.Placement
                ?? candidate.Right.Placement;
            var curveId =
                $"{candidate.Left.Curve.ShapeId}+{candidate.Right.Curve.ShapeId}";
            var confidence = Math.Clamp(
                0.97 - 0.08 * candidate.Score,
                0.72,
                0.97);
            var reason =
                $"{curveId}: recovered split cross-system tie "
                + $"{from.Notehead.ShapeId}->{to.Notehead.ShapeId} at {from.Pitch.Pitch}; "
                + $"previous fragment {candidate.Left.Curve.ShapeId} attaches to note "
                + $"at {candidate.Left.NoteDistance:F2}sp and system end "
                + $"at {candidate.Left.BoundaryDistance:F2}sp; "
                + $"next fragment {candidate.Right.Curve.ShapeId} starts at system edge "
                + $"{candidate.Right.BoundaryDistance:F2}sp and attaches to note "
                + $"at {candidate.Right.NoteDistance:F2}sp; "
                + $"placement={placement ?? "unspecified"}";

            facts.Add(new TieFact(
                curveId,
                from.Notehead.MeasureNumber,
                to.Notehead.MeasureNumber,
                from.Notehead.Staff,
                from.Notehead.ShapeId,
                to.Notehead.ShapeId,
                from.Pitch.Pitch,
                placement,
                candidate.Left.NoteDistance,
                candidate.Right.NoteDistance,
                confidence,
                reason,
                [
                    candidate.Left.Curve.ShapeId,
                    candidate.Right.Curve.ShapeId,
                    from.Notehead.ShapeId,
                    to.Notehead.ShapeId
                ]));

            facts.AddTrace(
                $"CrossSystemTieRecoveryPass: recovered {curveId}; "
                + $"m{from.Notehead.MeasureNumber}->m{to.Notehead.MeasureNumber}; "
                + $"staff={from.Notehead.Staff}; pitch={from.Pitch.Pitch}");
        }

        facts.AddTrace(
            $"CrossSystemTieRecoveryPass: recovered={recovered.Count}");
    }

    private static IReadOnlyList<FragmentCandidate> BuildOutgoingFragments(
        MeasureScene measure,
        StaffMeasureScene staff,
        IReadOnlyList<EndpointAnchor> anchors,
        Fraction measureLength,
        IReadOnlySet<string> claimedCurves)
    {
        var spacing = Math.Max(
            staff.LineSpacing,
            0.001);

        return staff.Elements
            .OfType<CurveElement>()
            .Where(curve =>
                !claimedCurves.Contains(curve.ShapeId))
            .Select(curve =>
                TryBuildOutgoing(
                    curve.Source,
                    measure,
                    staff.StaffNumber,
                    spacing,
                    anchors,
                    measureLength))
            .Where(candidate =>
                candidate is not null)
            .Select(candidate =>
                candidate!)
            .OrderBy(candidate =>
                candidate.Score)
            .ToArray();
    }

    private static IReadOnlyList<FragmentCandidate> BuildIncomingFragments(
        MeasureScene measure,
        StaffMeasureScene staff,
        IReadOnlyList<EndpointAnchor> anchors,
        IReadOnlySet<string> claimedCurves)
    {
        var spacing = Math.Max(
            staff.LineSpacing,
            0.001);

        return staff.Elements
            .OfType<CurveElement>()
            .Where(curve =>
                !claimedCurves.Contains(curve.ShapeId))
            .Select(curve =>
                TryBuildIncoming(
                    curve.Source,
                    measure,
                    staff.StaffNumber,
                    spacing,
                    anchors))
            .Where(candidate =>
                candidate is not null)
            .Select(candidate =>
                candidate!)
            .OrderBy(candidate =>
                candidate.Score)
            .ToArray();
    }

    private static FragmentCandidate? TryBuildOutgoing(
        CurvedStroke curve,
        MeasureScene measure,
        int staff,
        double spacing,
        IReadOnlyList<EndpointAnchor> anchors,
        Fraction measureLength)
    {
        if (!TryEndpoints(
                curve,
                spacing,
                out var left,
                out var right))
        {
            return null;
        }

        var boundaryDistance =
            Math.Abs(right.X - measure.XEnd)
            / spacing;

        if (boundaryDistance
            > MaximumBoundaryEndpointDistanceInSpacings)
        {
            return null;
        }

        var anchor = anchors
            .Where(item =>
                item.Notehead.MeasureNumber == measure.Number
                && item.Notehead.Staff == staff
                && EndsAtMeasureBoundary(
                    item,
                    measureLength))
            .Select(item => new
            {
                Anchor = item,
                Distance = NoteDistance(
                    left,
                    item.Notehead)
                    / spacing
            })
            .Where(item =>
                item.Distance
                <= MaximumNoteEndpointDistanceInSpacings)
            .OrderBy(item => item.Distance)
            .ThenBy(item =>
                item.Anchor.Notehead.ShapeId,
                StringComparer.Ordinal)
            .FirstOrDefault();

        if (anchor is null)
        {
            return null;
        }

        return new FragmentCandidate(
            curve,
            anchor.Anchor,
            anchor.Distance,
            boundaryDistance,
            Placement(curve, left, right),
            anchor.Distance + boundaryDistance);
    }

    private static FragmentCandidate? TryBuildIncoming(
        CurvedStroke curve,
        MeasureScene measure,
        int staff,
        double spacing,
        IReadOnlyList<EndpointAnchor> anchors)
    {
        if (!TryEndpoints(
                curve,
                spacing,
                out var left,
                out var right))
        {
            return null;
        }

        var boundaryDistance =
            Math.Abs(left.X - measure.XStart)
            / spacing;

        if (boundaryDistance
            > MaximumBoundaryEndpointDistanceInSpacings)
        {
            return null;
        }

        var anchor = anchors
            .Where(item =>
                item.Notehead.MeasureNumber == measure.Number
                && item.Notehead.Staff == staff
                && StartsAtMeasureOrigin(item))
            .Select(item => new
            {
                Anchor = item,
                Distance = NoteDistance(
                    right,
                    item.Notehead)
                    / spacing
            })
            .Where(item =>
                item.Distance
                <= MaximumNoteEndpointDistanceInSpacings)
            .OrderBy(item => item.Distance)
            .ThenBy(item =>
                item.Anchor.Notehead.ShapeId,
                StringComparer.Ordinal)
            .FirstOrDefault();

        if (anchor is null)
        {
            return null;
        }

        return new FragmentCandidate(
            curve,
            anchor.Anchor,
            anchor.Distance,
            boundaryDistance,
            Placement(curve, left, right),
            anchor.Distance + boundaryDistance);
    }

    private static bool Compatible(
        FragmentCandidate left,
        FragmentCandidate right)
    {
        if (!string.Equals(
                left.Anchor.Pitch.Pitch,
                right.Anchor.Pitch.Pitch,
                StringComparison.Ordinal)
            || left.Anchor.Notehead.Staff
                != right.Anchor.Notehead.Staff)
        {
            return false;
        }

        if (left.Anchor.Voice is not null
            && right.Anchor.Voice is not null
            && left.Anchor.Voice.LocalVoice
                != right.Anchor.Voice.LocalVoice)
        {
            return false;
        }

        return left.Placement is null
            || right.Placement is null
            || string.Equals(
                left.Placement,
                right.Placement,
                StringComparison.Ordinal);
    }

    private static bool TryEndpoints(
        CurvedStroke curve,
        double spacing,
        out PointD left,
        out PointD right)
    {
        left = default;
        right = default;

        if (curve.Centerline.Count < 2)
        {
            return false;
        }

        var first = curve.Centerline[0];
        var last = curve.Centerline[^1];

        left = first.X <= last.X
            ? first
            : last;
        right = first.X <= last.X
            ? last
            : first;

        return Math.Abs(right.X - left.X)
            / spacing
            >= MinimumHorizontalSpanInSpacings;
    }

    private static bool EndsAtMeasureBoundary(
        EndpointAnchor anchor,
        Fraction measureLength)
    {
        if (anchor.Onset is null
            || anchor.Duration is null)
        {
            return false;
        }

        var end = Fraction.Parse(anchor.Onset.At)
            + Fraction.Parse(
                anchor.Duration.EffectiveDuration);

        return Math.Abs(
            ToDouble(end - measureLength))
            <= FractionTolerance;
    }

    private static bool StartsAtMeasureOrigin(
        EndpointAnchor anchor)
    {
        if (anchor.Onset is null)
        {
            return false;
        }

        return Math.Abs(
            ToDouble(
                Fraction.Parse(anchor.Onset.At)))
            <= FractionTolerance;
    }

    private static IReadOnlyDictionary<int, Fraction> BuildMeasureLengths(
        IReadOnlyList<MeasureScene> measures,
        SemanticFacts facts)
    {
        var changes = facts
            .OfType<TimeSignatureFact>()
            .GroupBy(time => time.MeasureNumber)
            .ToDictionary(
                group => group.Key,
                group => group.Last());
        var result = new Dictionary<int, Fraction>();
        var current = new Fraction(1);

        foreach (var measure in measures)
        {
            if (changes.TryGetValue(
                    measure.Number,
                    out var time))
            {
                current = new Fraction(
                    time.Beats,
                    time.BeatType)
                    .Reduce();
            }

            result[measure.Number] = current;
        }

        return result;
    }

    private static IReadOnlyList<EndpointAnchor> BuildAnchors(
        SemanticFacts facts)
    {
        var pitches = facts
            .OfType<PitchFact>()
            .GroupBy(pitch => new NoteKey(
                pitch.MeasureNumber,
                pitch.NoteheadId))
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(pitch =>
                        pitch.Confidence)
                    .First());
        var durations = facts
            .OfType<DurationFact>()
            .GroupBy(duration => new NoteKey(
                duration.MeasureNumber,
                duration.NoteheadId))
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(duration =>
                        duration.Confidence)
                    .First());
        var chords = facts
            .OfType<ChordFact>()
            .ToArray();
        var chordByNotehead = chords
            .SelectMany(chord =>
                chord.NoteheadIds.Select(noteheadId => new
                {
                    Key = new NoteKey(
                        chord.MeasureNumber,
                        noteheadId),
                    Chord = chord
                }))
            .GroupBy(item => item.Key)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(item =>
                        item.Chord.Confidence)
                    .First()
                    .Chord);
        var voices = facts
            .OfType<VoiceFact>()
            .GroupBy(voice => new VoiceKey(
                voice.MeasureNumber,
                voice.Staff,
                voice.TargetKind,
                voice.TargetId))
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(voice =>
                        voice.Confidence)
                    .First());
        var onsets = facts
            .OfType<OnsetFact>()
            .GroupBy(onset => new VoiceKey(
                onset.MeasureNumber,
                onset.Staff,
                onset.TargetKind,
                onset.TargetId))
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(onset =>
                        onset.Confidence)
                    .First());

        return facts
            .OfType<NoteheadFact>()
            .Select(notehead =>
            {
                var noteKey = new NoteKey(
                    notehead.MeasureNumber,
                    notehead.ShapeId);

                if (!pitches.TryGetValue(
                        noteKey,
                        out var pitch)
                    || !durations.TryGetValue(
                        noteKey,
                        out var duration))
                {
                    return null;
                }

                chordByNotehead.TryGetValue(
                    noteKey,
                    out var chord);
                var targetKind = chord is null
                    ? VoiceTargetKind.Notehead
                    : VoiceTargetKind.Chord;
                var targetId = chord?.ChordId
                    ?? notehead.ShapeId;
                var voiceKey = new VoiceKey(
                    notehead.MeasureNumber,
                    notehead.Staff,
                    targetKind,
                    targetId);

                voices.TryGetValue(
                    voiceKey,
                    out var voice);
                onsets.TryGetValue(
                    voiceKey,
                    out var onset);

                return new EndpointAnchor(
                    notehead,
                    pitch,
                    duration,
                    voice,
                    onset);
            })
            .Where(anchor =>
                anchor is not null)
            .Select(anchor =>
                anchor!)
            .ToArray();
    }

    private static double NoteDistance(
        PointD point,
        NoteheadFact notehead)
    {
        var dx = point.X - notehead.CenterX;
        var dy = point.Y - notehead.CenterY;
        var centerDistance = Math.Sqrt(
            dx * dx + dy * dy);

        return Math.Max(
            0,
            centerDistance
            - Math.Max(
                notehead.MajorRadius,
                notehead.MinorRadius));
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

        var middle =
            curve.Centerline[
                curve.Centerline.Count / 2];
        var dx = right.X - left.X;
        var t = Math.Abs(dx) < 1e-9
            ? 0.5
            : Math.Clamp(
                (middle.X - left.X) / dx,
                0,
                1);
        var chordY =
            left.Y
            + t * (right.Y - left.Y);

        if (Math.Abs(
                middle.Y - chordY)
            < 1e-6)
        {
            return null;
        }

        return middle.Y < chordY
            ? "above"
            : "below";
    }

    private static double ToDouble(
        Fraction value) =>
        value.Numerator
        / (double)value.Denominator;

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
        DurationFact Duration,
        VoiceFact? Voice,
        OnsetFact? Onset);

    private sealed record FragmentCandidate(
        CurvedStroke Curve,
        EndpointAnchor Anchor,
        double NoteDistance,
        double BoundaryDistance,
        string? Placement,
        double Score);

    private sealed record RecoveryCandidate(
        FragmentCandidate Left,
        FragmentCandidate Right,
        double Score);
}
