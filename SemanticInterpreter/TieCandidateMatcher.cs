using SvgMusic.Scene;

namespace SvgMusic.Semantics;

internal sealed record TieMatchCandidate(
    string CurveShapeId,
    NoteheadFact FromNotehead,
    NoteheadFact ToNotehead,
    string Pitch,
    string? Placement,
    double StartDistanceInSpacings,
    double EndDistanceInSpacings,
    double Score)
{
    public string PairKey => $"{FromNotehead.ShapeId}\u001f{ToNotehead.ShapeId}";
}

/// <summary>
/// Builds same-pitch endpoint alternatives for tie-like curves and solves a global
/// one-curve/one-note-pair assignment. Independent nearest-neighbour attachment is
/// not sufficient for tied chords: neighbouring arcs can otherwise collapse onto
/// the same pitch pair.
/// </summary>
internal static class TieCandidateMatcher
{
    private const double MaximumEndpointDistanceInSpacings = 3.25;
    private const int EndpointCandidateLimit = 16;
    private const int PairCandidateLimit = 32;

    public static IReadOnlyDictionary<string, TieMatchCandidate> Match(
        SemanticDocument document,
        SemanticFacts facts,
        IReadOnlySet<string> curveShapeIds)
    {
        if (curveShapeIds.Count == 0)
        {
            return new Dictionary<string, TieMatchCandidate>(StringComparer.Ordinal);
        }

        var anchors = BuildAnchors(facts);
        var observations = CollectCurves(document)
            .Where(observation => curveShapeIds.Contains(observation.Curve.ShapeId))
            .ToDictionary(
                observation => observation.Curve.ShapeId,
                StringComparer.Ordinal);

        var candidatesByCurve = new Dictionary<string, TieMatchCandidate[]>(StringComparer.Ordinal);

        foreach (var curveId in curveShapeIds.OrderBy(id => id, StringComparer.Ordinal))
        {
            if (!observations.TryGetValue(curveId, out var observation))
            {
                candidatesByCurve[curveId] = [];
                continue;
            }

            candidatesByCurve[curveId] = BuildCandidates(observation, anchors)
                .GroupBy(candidate => candidate.PairKey, StringComparer.Ordinal)
                .Select(group => group.OrderBy(candidate => candidate.Score).First())
                .OrderBy(candidate => candidate.Score)
                .ThenBy(candidate => candidate.FromNotehead.MeasureNumber)
                .ThenBy(candidate => candidate.FromNotehead.CenterY)
                .ThenBy(candidate => candidate.PairKey, StringComparer.Ordinal)
                .Take(PairCandidateLimit)
                .ToArray();
        }

        // Maximum-cardinality bipartite matching. Candidate lists and curve order are
        // deterministic and cost-sorted, so conflicts are pushed toward the next best
        // geometrically plausible pitch rather than producing duplicate tie markers.
        var pairOwner = new Dictionary<string, string>(StringComparer.Ordinal);
        var assignment = new Dictionary<string, TieMatchCandidate>(StringComparer.Ordinal);
        var curveOrder = candidatesByCurve
            .OrderBy(item => item.Value.Length == 0 ? int.MaxValue : item.Value.Length)
            .ThenBy(item => item.Value.Length == 0 ? double.MaxValue : item.Value[0].Score)
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => item.Key)
            .ToArray();

        foreach (var curveId in curveOrder)
        {
            TryAssign(
                curveId,
                candidatesByCurve,
                pairOwner,
                assignment,
                new HashSet<string>(StringComparer.Ordinal),
                new HashSet<string>(StringComparer.Ordinal));
        }

        return assignment;
    }

    private static bool TryAssign(
        string curveId,
        IReadOnlyDictionary<string, TieMatchCandidate[]> candidatesByCurve,
        IDictionary<string, string> pairOwner,
        IDictionary<string, TieMatchCandidate> assignment,
        ISet<string> visitedCurves,
        ISet<string> visitedPairs)
    {
        if (!visitedCurves.Add(curveId)
            || !candidatesByCurve.TryGetValue(curveId, out var candidates))
        {
            return false;
        }

        foreach (var candidate in candidates)
        {
            if (!visitedPairs.Add(candidate.PairKey))
            {
                continue;
            }

            if (!pairOwner.TryGetValue(candidate.PairKey, out var displacedCurve))
            {
                pairOwner[candidate.PairKey] = curveId;
                assignment[curveId] = candidate;
                return true;
            }

            if (TryAssign(
                    displacedCurve,
                    candidatesByCurve,
                    pairOwner,
                    assignment,
                    visitedCurves,
                    visitedPairs))
            {
                pairOwner[candidate.PairKey] = curveId;
                assignment[curveId] = candidate;
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<TieMatchCandidate> BuildCandidates(
        CurveObservation observation,
        IReadOnlyList<EndpointAnchor> anchors)
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
        var starts = EndpointCandidates(left, observation, anchors, spacing);
        var ends = EndpointCandidates(right, observation, anchors, spacing);
        var result = new List<TieMatchCandidate>();

        foreach (var start in starts)
        {
            foreach (var end in ends)
            {
                if (start.Anchor.Notehead.ShapeId == end.Anchor.Notehead.ShapeId
                    || SameTarget(start.Anchor, end.Anchor)
                    || ComesAfter(start.Anchor.Notehead, end.Anchor.Notehead)
                    || start.Anchor.Notehead.Staff != end.Anchor.Notehead.Staff
                    || start.Anchor.LocalVoice != end.Anchor.LocalVoice
                    || !string.Equals(start.Anchor.Pitch.Pitch, end.Anchor.Pitch.Pitch, StringComparison.Ordinal))
                {
                    continue;
                }

                result.Add(new TieMatchCandidate(
                    curve.ShapeId,
                    start.Anchor.Notehead,
                    end.Anchor.Notehead,
                    start.Anchor.Pitch.Pitch,
                    Placement(curve, left, right),
                    start.DistanceInSpacings,
                    end.DistanceInSpacings,
                    start.DistanceInSpacings + end.DistanceInSpacings));
            }
        }

        return result;
    }

    private static IReadOnlyList<EndpointAnchor> BuildAnchors(SemanticFacts facts)
    {
        var noteheads = facts.OfType<NoteheadFact>().ToArray();
        var pitches = facts.OfType<PitchFact>().ToArray();
        var stems = facts.OfType<StemAttachmentFact>().ToArray();
        var chords = facts.OfType<ChordFact>().ToArray();
        var voices = facts.OfType<VoiceFact>().ToArray();

        var pitchByNotehead = pitches
            .GroupBy(pitch => pitch.NoteheadId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(pitch => pitch.Confidence).First(),
                StringComparer.Ordinal);
        var chordByNotehead = chords
            .SelectMany(chord => chord.NoteheadIds.Select(noteheadId => new
            {
                NoteheadId = noteheadId,
                Chord = chord
            }))
            .GroupBy(item => item.NoteheadId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(item => item.Chord.Confidence)
                    .Select(item => item.Chord)
                    .First(),
                StringComparer.Ordinal);
        var stemByNotehead = stems
            .SelectMany(stem => stem.AttachedNoteheadIds.Select(noteheadId => new
            {
                NoteheadId = noteheadId,
                Stem = stem
            }))
            .GroupBy(item => item.NoteheadId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(item => item.Stem.Confidence)
                    .ThenBy(item => item.Stem.StemShapeId, StringComparer.Ordinal)
                    .Select(item => item.Stem)
                    .First(),
                StringComparer.Ordinal);
        var voiceByTarget = voices
            .GroupBy(voice => new VoiceKey(voice.TargetKind, voice.TargetId))
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(voice => voice.Confidence).First());

        return noteheads
            .Where(notehead => pitchByNotehead.ContainsKey(notehead.ShapeId))
            .Select(notehead =>
            {
                stemByNotehead.TryGetValue(notehead.ShapeId, out var stem);
                chordByNotehead.TryGetValue(notehead.ShapeId, out var chord);
                var targetKind = chord is null
                    ? VoiceTargetKind.Notehead
                    : VoiceTargetKind.Chord;
                var targetId = chord?.ChordId ?? notehead.ShapeId;
                var localVoice = voiceByTarget.TryGetValue(
                        new VoiceKey(targetKind, targetId),
                        out var voice)
                    ? voice.LocalVoice
                    : 1;

                return new EndpointAnchor(
                    notehead,
                    pitchByNotehead[notehead.ShapeId],
                    stem,
                    targetKind,
                    targetId,
                    localVoice);
            })
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
            .Where(anchor => observation.Staffs.Contains(anchor.Notehead.Staff))
            .Select(anchor => new EndpointChoice(
                anchor,
                EndpointDistance(endpoint, anchor) / spacing))
            .Where(candidate => candidate.DistanceInSpacings <= MaximumEndpointDistanceInSpacings)
            .OrderBy(candidate => candidate.DistanceInSpacings)
            .ThenBy(candidate => candidate.Anchor.Notehead.MeasureNumber)
            .ThenBy(candidate => candidate.Anchor.Notehead.CenterX)
            .ThenBy(candidate => candidate.Anchor.Notehead.ShapeId, StringComparer.Ordinal)
            .Take(EndpointCandidateLimit)
            .ToArray();
    }

    private static double EndpointDistance(PointD point, EndpointAnchor anchor)
    {
        var note = anchor.Notehead;
        var dx = point.X - note.CenterX;
        var dy = point.Y - note.CenterY;
        var centerDistance = Math.Sqrt(dx * dx + dy * dy);
        var noteBoundaryDistance = Math.Max(
            0,
            centerDistance - Math.Max(note.MajorRadius, note.MinorRadius));

        if (anchor.Stem is null)
        {
            return noteBoundaryDistance;
        }

        var stemDistance = DistanceToSegment(
            point,
            new PointD(anchor.Stem.StartX, anchor.Stem.StartY),
            new PointD(anchor.Stem.EndX, anchor.Stem.EndY));
        return Math.Min(noteBoundaryDistance, stemDistance);
    }

    private static double DistanceToSegment(PointD point, PointD a, PointD b)
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
        return Distance(point, new PointD(a.X + t * dx, a.Y + t * dy));
    }

    private static double Distance(PointD a, PointD b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static bool SameTarget(EndpointAnchor first, EndpointAnchor second) =>
        first.TargetKind == second.TargetKind
        && string.Equals(first.TargetId, second.TargetId, StringComparison.Ordinal);

    private static bool ComesAfter(NoteheadFact left, NoteheadFact right)
    {
        if (left.MeasureNumber != right.MeasureNumber)
        {
            return left.MeasureNumber > right.MeasureNumber;
        }

        return left.CenterX > right.CenterX + 1e-6;
    }

    private static string? Placement(CurvedStroke curve, PointD left, PointD right)
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

    private static IReadOnlyList<CurveObservation> CollectCurves(SemanticDocument document)
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
                builder.Spacings.Count == 0 ? 1.0 : builder.Spacings.Average()))
            .ToArray();
    }

    private readonly record struct VoiceKey(VoiceTargetKind TargetKind, string TargetId);

    private sealed record EndpointAnchor(
        NoteheadFact Notehead,
        PitchFact Pitch,
        StemAttachmentFact? Stem,
        VoiceTargetKind TargetKind,
        string TargetId,
        int LocalVoice);

    private sealed record EndpointChoice(EndpointAnchor Anchor, double DistanceInSpacings);

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
