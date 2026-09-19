using SvgMusic.Scene;

namespace SvgMusic.Semantics;

public enum CrossSystemCurveFragmentKind
{
    ToSystemEnd,
    FromSystemStart
}

/// <summary>
/// One half of a curve that MuseScore split at a system break.
///
/// The fragment is still a perfectly valid CurvedStroke geometrically, but only one
/// of its endpoints belongs to a real note. The other endpoint deliberately terminates
/// at the old system edge or emerges from the notation-start band of the new system.
/// </summary>
public sealed record CrossSystemCurveFragmentFact(
    string CurveShapeId,
    int MeasureNumber,
    int Staff,
    CrossSystemCurveFragmentKind Kind,
    string AttachedNoteheadId,
    double NoteDistanceInSpacings,
    double EdgeDistanceInSpacings,
    string? Placement,
    double Confidence,
    string Reason,
    IReadOnlyList<string> SourceShapeIds)
    : SemanticFact(
        "CrossSystemCurvePass",
        Reason,
        SourceShapeIds);

/// <summary>
/// A logical complete curve reconstructed from two system-break fragments.
///
/// This fact deliberately does not decide tie versus slur. SlurPass applies the same
/// pitch/voice/event semantics that it applies to ordinary complete curves; TiePass
/// then consumes a tie-like decision in the normal way.
/// </summary>
public sealed record CrossSystemCurveFact(
    string CurveId,
    int StartMeasureNumber,
    int EndMeasureNumber,
    int StartStaff,
    int EndStaff,
    string FromNoteheadId,
    string ToNoteheadId,
    string OutgoingCurveShapeId,
    string IncomingCurveShapeId,
    string? Placement,
    double StartDistanceInSpacings,
    double EndDistanceInSpacings,
    double Confidence,
    string Reason,
    IReadOnlyList<string> SourceShapeIds)
    : SemanticFact(
        "CrossSystemCurvePass",
        Reason,
        SourceShapeIds);

/// <summary>
/// Reconstructs curves split by a system break before SlurPass/TiePass classify them.
///
/// Recognition is intentionally semantic rather than SVG-class based:
/// - old-system fragment: one endpoint attaches to a real note, the other terminates
///   at the final measure boundary;
/// - new-system fragment: one endpoint emerges from the notation-start band after
///   the new system's clef/key area, the other attaches to a real note;
/// - the two fragments must straddle an actual BreakBefore and have compatible
///   placement.
///
/// The output is still an untyped curve. Same-pitch/same-voice becomes a tie later;
/// other valid pitched endpoint pairs become slurs later.
/// </summary>
public sealed class CrossSystemCurvePass : ISemanticPass
{
    private const double MinimumHorizontalSpanInSpacings = 0.75;
    private const double MaximumNoteEndpointDistanceInSpacings = 3.25;
    private const double MaximumSystemEndDistanceInSpacings = 2.50;

    // At the start of a new system the visible musical content starts after the
    // left barline because clef/key/time occupy several staff spacings. Yellow
    // Leaves has the continuation fragment at ~4.55sp from the barline.
    private const double MaximumSystemStartBandInSpacings = 7.00;

    // An endpoint that is this close to a real pitched target is not an open
    // system-edge endpoint; it belongs to an ordinary complete curve instead.
    private const double NoteAttachmentExclusionInSpacings = 2.20;

    public string Name => nameof(CrossSystemCurvePass);

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
                "CrossSystemCurvePass: fewer than two measures");
            return;
        }

        var anchors = BuildAnchors(facts);
        var fragments = new List<FragmentCandidate>();
        var composites = new List<CompositeCandidate>();
        var usedCurves = new HashSet<string>(
            StringComparer.Ordinal);

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

            var outgoing = new List<FragmentCandidate>();
            var incoming = new List<FragmentCandidate>();

            foreach (var staffNumber in new[] { 1, 2 })
            {
                var previousStaff = staffNumber == 1
                    ? previous.Upper
                    : previous.Lower;
                var nextStaff = staffNumber == 1
                    ? next.Upper
                    : next.Lower;

                outgoing.AddRange(
                    BuildOutgoingFragments(
                        previous,
                        previousStaff,
                        anchors));
                incoming.AddRange(
                    BuildIncomingFragments(
                        next,
                        nextStaff,
                        anchors));
            }

            fragments.AddRange(outgoing);
            fragments.AddRange(incoming);

            var pairCandidates = outgoing
                .SelectMany(left =>
                    incoming
                        .Where(right =>
                            PlacementCompatible(
                                left.Placement,
                                right.Placement))
                        .Select(right =>
                            new CompositeCandidate(
                                left,
                                right,
                                PairScore(left, right))))
                .OrderBy(candidate => candidate.Score)
                .ThenBy(candidate =>
                    candidate.Outgoing.Curve.ShapeId,
                    StringComparer.Ordinal)
                .ThenBy(candidate =>
                    candidate.Incoming.Curve.ShapeId,
                    StringComparer.Ordinal)
                .ToArray();

            foreach (var candidate in pairCandidates)
            {
                if (usedCurves.Contains(
                        candidate.Outgoing.Curve.ShapeId)
                    || usedCurves.Contains(
                        candidate.Incoming.Curve.ShapeId))
                {
                    continue;
                }

                usedCurves.Add(
                    candidate.Outgoing.Curve.ShapeId);
                usedCurves.Add(
                    candidate.Incoming.Curve.ShapeId);
                composites.Add(candidate);
            }
        }

        foreach (var fragment in fragments
                     .GroupBy(fragment =>
                         fragment.Curve.ShapeId,
                         StringComparer.Ordinal)
                     .Select(group => group
                         .OrderBy(item => item.Score)
                         .First()))
        {
            var reason =
                $"{fragment.Curve.ShapeId}: incomplete cross-system curve fragment; "
                + $"kind={fragment.Kind}; m{fragment.MeasureNumber}; "
                + $"staff={fragment.Staff}; attached={fragment.Anchor.Notehead.ShapeId}/"
                + $"{fragment.Anchor.Pitch.Pitch}; note-distance={fragment.NoteDistance:F2}sp; "
                + $"edge-distance={fragment.EdgeDistance:F2}sp; "
                + $"placement={fragment.Placement ?? "unspecified"}";

            facts.Add(new CrossSystemCurveFragmentFact(
                fragment.Curve.ShapeId,
                fragment.MeasureNumber,
                fragment.Staff,
                fragment.Kind,
                fragment.Anchor.Notehead.ShapeId,
                fragment.NoteDistance,
                fragment.EdgeDistance,
                fragment.Placement,
                Math.Clamp(
                    0.98
                    - 0.08 * fragment.NoteDistance
                    - 0.02 * Math.Min(
                        fragment.EdgeDistance,
                        6.0),
                    0.65,
                    0.98),
                reason,
                [
                    fragment.Curve.ShapeId,
                    fragment.Anchor.Notehead.ShapeId
                ]));
        }

        foreach (var composite in composites)
        {
            var outgoing = composite.Outgoing;
            var incoming = composite.Incoming;
            var curveId =
                $"{outgoing.Curve.ShapeId}+{incoming.Curve.ShapeId}";
            var placement = outgoing.Placement
                ?? incoming.Placement;
            var confidence = Math.Clamp(
                0.98 - 0.06 * composite.Score,
                0.68,
                0.98);
            var reason =
                $"{curveId}: reconstructed complete curve across system break; "
                + $"outgoing={outgoing.Curve.ShapeId} "
                + $"m{outgoing.MeasureNumber}/s{outgoing.Staff}/"
                + $"{outgoing.Anchor.Notehead.ShapeId}/{outgoing.Anchor.Pitch.Pitch}; "
                + $"incoming={incoming.Curve.ShapeId} "
                + $"m{incoming.MeasureNumber}/s{incoming.Staff}/"
                + $"{incoming.Anchor.Notehead.ShapeId}/{incoming.Anchor.Pitch.Pitch}; "
                + $"placement={placement ?? "unspecified"}";

            facts.Add(new CrossSystemCurveFact(
                curveId,
                outgoing.MeasureNumber,
                incoming.MeasureNumber,
                outgoing.Staff,
                incoming.Staff,
                outgoing.Anchor.Notehead.ShapeId,
                incoming.Anchor.Notehead.ShapeId,
                outgoing.Curve.ShapeId,
                incoming.Curve.ShapeId,
                placement,
                outgoing.NoteDistance,
                incoming.NoteDistance,
                confidence,
                reason,
                [
                    outgoing.Curve.ShapeId,
                    incoming.Curve.ShapeId,
                    outgoing.Anchor.Notehead.ShapeId,
                    incoming.Anchor.Notehead.ShapeId
                ]));

            facts.AddTrace(
                $"CrossSystemCurvePass: reconstructed {curveId}; "
                + $"m{outgoing.MeasureNumber}/s{outgoing.Staff}"
                + $"->{incoming.MeasureNumber}/s{incoming.Staff}; "
                + $"{outgoing.Anchor.Pitch.Pitch}->{incoming.Anchor.Pitch.Pitch}; "
                + $"placement={placement ?? "unspecified"}");
        }

        facts.AddTrace(
            $"CrossSystemCurvePass: fragments={fragments
                .Select(fragment => fragment.Curve.ShapeId)
                .Distinct(StringComparer.Ordinal)
                .Count()}; reconstructed={composites.Count}");
    }

    private static IReadOnlyList<FragmentCandidate> BuildOutgoingFragments(
        MeasureScene measure,
        StaffMeasureScene staff,
        IReadOnlyList<EndpointAnchor> anchors)
    {
        var spacing = Math.Max(
            staff.LineSpacing,
            0.001);

        return staff.Elements
            .OfType<CurveElement>()
            .Select(curve =>
                TryBuildOutgoing(
                    curve.Source,
                    measure,
                    staff.StaffNumber,
                    spacing,
                    anchors))
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!)
            .OrderBy(candidate => candidate.Score)
            .ToArray();
    }

    private static IReadOnlyList<FragmentCandidate> BuildIncomingFragments(
        MeasureScene measure,
        StaffMeasureScene staff,
        IReadOnlyList<EndpointAnchor> anchors)
    {
        var spacing = Math.Max(
            staff.LineSpacing,
            0.001);

        return staff.Elements
            .OfType<CurveElement>()
            .Select(curve =>
                TryBuildIncoming(
                    curve.Source,
                    measure,
                    staff.StaffNumber,
                    spacing,
                    anchors))
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!)
            .OrderBy(candidate => candidate.Score)
            .ToArray();
    }

    private static FragmentCandidate? TryBuildOutgoing(
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

        var edgeDistance =
            Math.Abs(right.X - measure.XEnd)
            / spacing;

        if (edgeDistance
            > MaximumSystemEndDistanceInSpacings)
        {
            return null;
        }

        // A real note at the right endpoint means this is an ordinary complete
        // curve near the system edge, not an incomplete continuation fragment.
        if (ClosestAnchorDistance(
                right,
                measure.Number,
                staff,
                spacing,
                anchors)
            <= NoteAttachmentExclusionInSpacings)
        {
            return null;
        }

        var anchor = ClosestAnchor(
            left,
            measure.Number,
            staff,
            spacing,
            anchors);

        if (anchor is null
            || anchor.Distance
                > MaximumNoteEndpointDistanceInSpacings)
        {
            return null;
        }

        return new FragmentCandidate(
            curve,
            measure.Number,
            staff,
            CrossSystemCurveFragmentKind.ToSystemEnd,
            anchor.Anchor,
            anchor.Distance,
            edgeDistance,
            Placement(curve, left, right),
            anchor.Distance
            + Math.Min(edgeDistance, 2.5));
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

        var edgeDistance =
            Math.Abs(left.X - measure.XStart)
            / spacing;

        if (left.X < measure.XStart - spacing
            || edgeDistance
                > MaximumSystemStartBandInSpacings)
        {
            return null;
        }

        // Same protection as outgoing: if the left endpoint already belongs to a
        // note, this is a normal curve beginning on the first beat of the system.
        if (ClosestAnchorDistance(
                left,
                measure.Number,
                staff,
                spacing,
                anchors)
            <= NoteAttachmentExclusionInSpacings)
        {
            return null;
        }

        var anchor = ClosestAnchor(
            right,
            measure.Number,
            staff,
            spacing,
            anchors);

        if (anchor is null
            || anchor.Distance
                > MaximumNoteEndpointDistanceInSpacings)
        {
            return null;
        }

        return new FragmentCandidate(
            curve,
            measure.Number,
            staff,
            CrossSystemCurveFragmentKind.FromSystemStart,
            anchor.Anchor,
            anchor.Distance,
            edgeDistance,
            Placement(curve, left, right),
            anchor.Distance
            + 0.20 * Math.Min(edgeDistance, 7.0));
    }

    private static AnchorChoice? ClosestAnchor(
        PointD point,
        int measureNumber,
        int staff,
        double spacing,
        IReadOnlyList<EndpointAnchor> anchors) =>
        anchors
            .Where(anchor =>
                anchor.Notehead.MeasureNumber == measureNumber
                && anchor.Notehead.Staff == staff)
            .Select(anchor => new AnchorChoice(
                anchor,
                EndpointDistance(
                    point,
                    anchor)
                / spacing))
            .OrderBy(choice => choice.Distance)
            .ThenBy(choice =>
                choice.Anchor.Notehead.ShapeId,
                StringComparer.Ordinal)
            .FirstOrDefault();

    private static double ClosestAnchorDistance(
        PointD point,
        int measureNumber,
        int staff,
        double spacing,
        IReadOnlyList<EndpointAnchor> anchors)
    {
        var closest = ClosestAnchor(
            point,
            measureNumber,
            staff,
            spacing,
            anchors);

        return closest?.Distance
            ?? double.PositiveInfinity;
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

    private static double PairScore(
        FragmentCandidate outgoing,
        FragmentCandidate incoming)
    {
        var score =
            outgoing.NoteDistance
            + incoming.NoteDistance
            + 0.10 * outgoing.EdgeDistance
            + 0.05 * incoming.EdgeDistance;

        // Same staff is a useful but non-mandatory preference: a slur may be
        // cross-staff, while a tie will later require same staff/voice/pitch.
        if (outgoing.Staff != incoming.Staff)
        {
            score += 0.50;
        }

        if (string.Equals(
                outgoing.Anchor.Pitch.Pitch,
                incoming.Anchor.Pitch.Pitch,
                StringComparison.Ordinal))
        {
            score -= 0.50;
        }

        if (outgoing.Anchor.Voice is not null
            && incoming.Anchor.Voice is not null
            && outgoing.Anchor.Voice.LocalVoice
                == incoming.Anchor.Voice.LocalVoice)
        {
            score -= 0.25;
        }

        return score;
    }

    private static bool PlacementCompatible(
        string? left,
        string? right) =>
        left is null
        || right is null
        || string.Equals(
            left,
            right,
            StringComparison.Ordinal);

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
        var stems = facts
            .OfType<StemAttachmentFact>()
            .ToArray();
        var chords = facts
            .OfType<ChordFact>()
            .ToArray();
        var voices = facts
            .OfType<VoiceFact>()
            .ToArray();
        var onsets = facts
            .OfType<OnsetFact>()
            .ToArray();
        var durations = facts
            .OfType<DurationFact>()
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
        var stemByNotehead = stems
            .SelectMany(stem =>
                stem.AttachedNoteheadIds.Select(noteheadId => new
                {
                    Key = new NoteKey(
                        stem.MeasureNumber,
                        noteheadId),
                    Stem = stem
                }))
            .GroupBy(item => item.Key)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(item =>
                        item.Stem.Confidence)
                    .First()
                    .Stem);
        var voiceByTarget = voices
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
        var onsetByTarget = onsets
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
        var durationByNotehead = durations
            .GroupBy(duration => new NoteKey(
                duration.MeasureNumber,
                duration.NoteheadId))
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(duration =>
                        duration.Confidence)
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
                        out var pitch))
                {
                    return null;
                }

                chordByNotehead.TryGetValue(
                    noteKey,
                    out var chord);
                stemByNotehead.TryGetValue(
                    noteKey,
                    out var stem);
                durationByNotehead.TryGetValue(
                    noteKey,
                    out var duration);

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

                voiceByTarget.TryGetValue(
                    voiceKey,
                    out var voice);
                onsetByTarget.TryGetValue(
                    voiceKey,
                    out var onset);

                return new EndpointAnchor(
                    notehead,
                    pitch,
                    stem,
                    voice,
                    onset,
                    duration);
            })
            .Where(anchor => anchor is not null)
            .Select(anchor => anchor!)
            .ToArray();
    }

    private static double EndpointDistance(
        PointD point,
        EndpointAnchor anchor)
    {
        var note = anchor.Notehead;
        var dx = point.X - note.CenterX;
        var dy = point.Y - note.CenterY;
        var centerDistance = Math.Sqrt(
            dx * dx + dy * dy);
        var noteDistance = Math.Max(
            0,
            centerDistance
            - Math.Max(
                note.MajorRadius,
                note.MinorRadius));

        if (anchor.Stem is null)
        {
            return noteDistance;
        }

        return Math.Min(
            noteDistance,
            DistanceToSegment(
                point,
                new PointD(
                    anchor.Stem.StartX,
                    anchor.Stem.StartY),
                new PointD(
                    anchor.Stem.EndX,
                    anchor.Stem.EndY)));
    }

    private static double DistanceToSegment(
        PointD point,
        PointD a,
        PointD b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var lengthSquared =
            dx * dx + dy * dy;

        if (lengthSquared <= 1e-12)
        {
            return Distance(point, a);
        }

        var t =
            ((point.X - a.X) * dx
             + (point.Y - a.Y) * dy)
            / lengthSquared;
        t = Math.Clamp(t, 0, 1);

        return Distance(
            point,
            new PointD(
                a.X + t * dx,
                a.Y + t * dy));
    }

    private static double Distance(
        PointD a,
        PointD b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(
            dx * dx + dy * dy);
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

        if (Math.Abs(middle.Y - chordY)
            < 1e-6)
        {
            return null;
        }

        return middle.Y < chordY
            ? "above"
            : "below";
    }

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
        VoiceFact? Voice,
        OnsetFact? Onset,
        DurationFact? Duration);

    private sealed record AnchorChoice(
        EndpointAnchor Anchor,
        double Distance);

    private sealed record FragmentCandidate(
        CurvedStroke Curve,
        int MeasureNumber,
        int Staff,
        CrossSystemCurveFragmentKind Kind,
        EndpointAnchor Anchor,
        double NoteDistance,
        double EdgeDistance,
        string? Placement,
        double Score);

    private sealed record CompositeCandidate(
        FragmentCandidate Outgoing,
        FragmentCandidate Incoming,
        double Score);
}
