using SvgMusic.Canonical;
using SvgMusic.Scene;

namespace SvgMusic.Semantics;

public sealed record SlurFact(
    string CurveShapeId,
    int StartMeasureNumber,
    int EndMeasureNumber,
    int StartStaff,
    int EndStaff,
    string FromNoteheadId,
    string ToNoteheadId,
    string? Placement,
    double StartDistanceInSpacings,
    double EndDistanceInSpacings,
    double Confidence,
    string Reason,
    IReadOnlyList<string> SourceShapeIds)
    : SemanticFact(
        "SlurPass",
        Reason,
        SourceShapeIds);

public sealed record SlurDecision(
    string CurveShapeId,
    CurvedStroke? Curve,
    bool Accepted,
    string Decision,
    NoteheadFact? FromNotehead,
    NoteheadFact? ToNotehead,
    string? Placement,
    double? StartDistanceInSpacings,
    double? EndDistanceInSpacings,
    double Confidence,
    string Reason);

public sealed record SlurAnalysisResult(
    IReadOnlyList<SlurDecision> Decisions)
{
    public IReadOnlyList<SlurDecision> Accepted =>
        Decisions.Where(decision => decision.Accepted).ToArray();
}

/// <summary>
/// Converts already extracted CurvedStroke geometry into semantic slurs.
///
/// ArcExtractor deliberately does only geometry. This pass attaches each horizontal
/// arch to pitched endpoints, using both noteheads and their stems. A same-pitch
/// hypothesis is tested first because ties and slurs share the same raw arc geometry;
/// plausible ties are reserved for a later TiePass instead of leaking into SlurPass.
/// </summary>
public sealed class SlurPass : ISemanticPass
{
    private const double MinimumHorizontalSpanInSpacings = 1.20;
    private const double MaximumSlurEndpointDistanceInSpacings = 2.20;
    private const double MaximumTieEndpointDistanceInSpacings = 3.25;
    private const double TiePreferenceMarginInSpacings = 0.80;
    private const double MaximumUnopposedTieScoreInSpacings = 5.00;
    private const double DifferentVoicePenaltyInSpacings = 0.35;
    private const double DifferentStaffPenaltyInSpacings = 0.45;
    private const int EndpointCandidateLimit = 14;

    public string Name => nameof(SlurPass);

    public SlurAnalysisResult? LastAnalysis { get; private set; }

    public void Run(
        SemanticDocument document,
        SemanticFacts facts)
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

        var preferredStemByNotehead = stems
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
        var onsetByTarget = facts
            .OfType<OnsetFact>()
            .GroupBy(onset => new VoiceKey(onset.TargetKind, onset.TargetId))
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(onset => onset.Confidence).First());
        var durationByNotehead = facts
            .OfType<DurationFact>()
            .GroupBy(duration => duration.NoteheadId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(duration => duration.Confidence).First(),
                StringComparer.Ordinal);

        var anchors = noteheads
            .Where(notehead => pitchByNotehead.ContainsKey(notehead.ShapeId))
            .Select(notehead =>
            {
                preferredStemByNotehead.TryGetValue(notehead.ShapeId, out var stem);
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

                onsetByTarget.TryGetValue(
                    new VoiceKey(targetKind, targetId),
                    out var onset);
                durationByNotehead.TryGetValue(
                    notehead.ShapeId,
                    out var duration);

                return new EndpointAnchor(
                    notehead,
                    pitchByNotehead[notehead.ShapeId],
                    stem,
                    targetKind,
                    targetId,
                    localVoice,
                    onset,
                    duration);
            })
            .ToArray();

        var crossSystemCurves = facts
            .OfType<CrossSystemCurveFact>()
            .ToArray();
        var crossSystemById = crossSystemCurves
            .ToDictionary(
                curve => curve.CurveId,
                StringComparer.Ordinal);
        var fragmentShapeIds = crossSystemCurves
            .SelectMany(curve => new[]
            {
                curve.OutgoingCurveShapeId,
                curve.IncomingCurveShapeId
            })
            .ToHashSet(StringComparer.Ordinal);

        var curveObservations = CollectCurves(document)
            .Where(observation =>
                !fragmentShapeIds.Contains(
                    observation.Curve.ShapeId))
            .ToArray();
        var decisions = new List<SlurDecision>();

        foreach (var observation in curveObservations
                     .OrderBy(item => item.Curve.ShapeId, StringComparer.Ordinal))
        {
            decisions.Add(Decide(observation, anchors));
        }

        foreach (var crossSystem in crossSystemCurves
                     .OrderBy(curve => curve.CurveId, StringComparer.Ordinal))
        {
            decisions.Add(
                DecideCrossSystem(
                    crossSystem,
                    anchors));
        }

        LastAnalysis = new SlurAnalysisResult(decisions);

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

            facts.Add(new SlurFact(
                decision.CurveShapeId,
                from.MeasureNumber,
                to.MeasureNumber,
                from.Staff,
                to.Staff,
                from.ShapeId,
                to.ShapeId,
                decision.Placement,
                decision.StartDistanceInSpacings!.Value,
                decision.EndDistanceInSpacings!.Value,
                decision.Confidence,
                decision.Reason,
                sourceShapeIds));
        }

        facts.AddTrace(
            $"SlurPass: curves={decisions.Count}; accepted={decisions.Count(decision => decision.Accepted)}; "
            + $"tie-like={decisions.Count(decision => decision.Decision == "tie-like")}; "
            + $"cross-system={crossSystemCurves.Length}; "
            + $"vertical/non-span={decisions.Count(decision => decision.Decision == "non-horizontal-arch")}; "
            + $"unattached={decisions.Count(decision => decision.Decision == "no-endpoint-pair")}");
    }

    private static SlurDecision DecideCrossSystem(
        CrossSystemCurveFact curve,
        IReadOnlyList<EndpointAnchor> anchors)
    {
        var from = anchors
            .Where(anchor =>
                anchor.Notehead.MeasureNumber
                    == curve.StartMeasureNumber
                && anchor.Notehead.Staff
                    == curve.StartStaff
                && string.Equals(
                    anchor.Notehead.ShapeId,
                    curve.FromNoteheadId,
                    StringComparison.Ordinal))
            .FirstOrDefault();
        var to = anchors
            .Where(anchor =>
                anchor.Notehead.MeasureNumber
                    == curve.EndMeasureNumber
                && anchor.Notehead.Staff
                    == curve.EndStaff
                && string.Equals(
                    anchor.Notehead.ShapeId,
                    curve.ToNoteheadId,
                    StringComparison.Ordinal))
            .FirstOrDefault();

        if (from is null || to is null)
        {
            return new SlurDecision(
                curve.CurveId,
                null,
                false,
                "no-endpoint-pair",
                from?.Notehead,
                to?.Notehead,
                curve.Placement,
                curve.StartDistanceInSpacings,
                curve.EndDistanceInSpacings,
                0,
                $"{curve.CurveId}: reconstructed cross-system curve lost a semantic endpoint");
        }

        if (SameTarget(from, to)
            || ComesAfter(
                from.Notehead,
                to.Notehead))
        {
            return new SlurDecision(
                curve.CurveId,
                null,
                false,
                "no-endpoint-pair",
                from.Notehead,
                to.Notehead,
                curve.Placement,
                curve.StartDistanceInSpacings,
                curve.EndDistanceInSpacings,
                0,
                $"{curve.CurveId}: reconstructed cross-system endpoints are not chronological distinct events");
        }

        if (IsSamePitchVoice(from, to)
            && IsTieAdjacent(from, to))
        {
            return new SlurDecision(
                curve.CurveId,
                null,
                false,
                "tie-like",
                from.Notehead,
                to.Notehead,
                curve.Placement,
                curve.StartDistanceInSpacings,
                curve.EndDistanceInSpacings,
                0,
                $"{curve.CurveId}: reconstructed cross-system curve joins same-pitch "
                + $"{from.Pitch.Pitch} events in one staff/voice; reserved for TiePass");
        }

        return new SlurDecision(
            curve.CurveId,
            null,
            true,
            "slur",
            from.Notehead,
            to.Notehead,
            curve.Placement,
            curve.StartDistanceInSpacings,
            curve.EndDistanceInSpacings,
            curve.Confidence,
            $"{curve.CurveId}: reconstructed cross-system curve endpoints attach to "
            + $"{from.Notehead.ShapeId}/{from.Pitch.Pitch} and "
            + $"{to.Notehead.ShapeId}/{to.Pitch.Pitch}; "
            + $"placement={curve.Placement ?? "unspecified"}");
    }

    private static IReadOnlyList<CurveObservation> CollectCurves(
        SemanticDocument document)
    {
        var observations = new Dictionary<string, CurveObservationBuilder>(StringComparer.Ordinal);

        foreach (var measure in document.Measures)
        {
            foreach (var staff in new[] { measure.Upper, measure.Lower })
            {
                foreach (var curve in staff.Elements.OfType<CurveElement>())
                {
                    if (!observations.TryGetValue(curve.ShapeId, out var builder))
                    {
                        builder = new CurveObservationBuilder(curve.Source);
                        observations[curve.ShapeId] = builder;
                    }

                    builder.Measures.Add(measure.Number);
                    builder.Staffs.Add(staff.StaffNumber);
                    builder.Spacings.Add(staff.LineSpacing);
                }
            }
        }

        return observations.Values
            .Select(builder => new CurveObservation(
                builder.Curve,
                builder.Measures,
                builder.Staffs,
                builder.Spacings.Count == 0
                    ? 1.0
                    : builder.Spacings.Average()))
            .ToArray();
    }

    private static SlurDecision Decide(
        CurveObservation observation,
        IReadOnlyList<EndpointAnchor> anchors)
    {
        var curve = observation.Curve;
        if (curve.Centerline.Count < 2)
        {
            return Reject(
                curve,
                "degenerate-curve",
                "curve has fewer than two centreline points");
        }

        var spacing = Math.Max(observation.Spacing, 0.001);
        var first = curve.Centerline[0];
        var last = curve.Centerline[^1];
        var left = first.X <= last.X ? first : last;
        var right = first.X <= last.X ? last : first;
        var horizontalSpan = Math.Abs(right.X - left.X) / spacing;

        if (horizontalSpan < MinimumHorizontalSpanInSpacings)
        {
            return Reject(
                curve,
                "non-horizontal-arch",
                $"{curve.ShapeId}: horizontal span {horizontalSpan:F2}sp is too small for a note-to-note slur");
        }

        // Use a wider search radius for the tie hypothesis. Engraving can place a tie
        // endpoint on the far side of a chord or shared stem; pitch identity is a much
        // stronger clue than raw nearest-neighbour distance in that case.
        var wideStartCandidates = EndpointCandidates(
            left,
            observation,
            anchors,
            spacing,
            MaximumTieEndpointDistanceInSpacings);
        var wideEndCandidates = EndpointCandidates(
            right,
            observation,
            anchors,
            spacing,
            MaximumTieEndpointDistanceInSpacings);

        var bestTie = FindBestPair(
            wideStartCandidates,
            wideEndCandidates,
            requireSamePitch: true);

        var slurStartCandidates = wideStartCandidates
            .Where(candidate => candidate.DistanceInSpacings <= MaximumSlurEndpointDistanceInSpacings)
            .ToArray();
        var slurEndCandidates = wideEndCandidates
            .Where(candidate => candidate.DistanceInSpacings <= MaximumSlurEndpointDistanceInSpacings)
            .ToArray();
        var bestSlur = FindBestPair(
            slurStartCandidates,
            slurEndCandidates,
            requireSamePitch: false);

        if (bestTie is not null
            && IsTieAdjacent(bestTie.Start.Anchor, bestTie.End.Anchor)
            && (bestSlur is null
                ? bestTie.Score <= MaximumUnopposedTieScoreInSpacings
                : bestTie.Score <= bestSlur.Score + TiePreferenceMarginInSpacings))
        {
            return TieLike(curve, left, right, bestTie);
        }

        if (bestSlur is null)
        {
            return Reject(
                curve,
                "no-endpoint-pair",
                $"{curve.ShapeId}: no distinct chronological pitched events lie close enough to both arc endpoints");
        }

        // A same-pitch pair can also win the ordinary geometric search exactly. Keep
        // the semantic boundary explicit even when the wide tie pass was unnecessary.
        if (IsSamePitchVoice(bestSlur.Start.Anchor, bestSlur.End.Anchor)
            && IsTieAdjacent(bestSlur.Start.Anchor, bestSlur.End.Anchor))
        {
            return TieLike(curve, left, right, bestSlur);
        }

        var from = bestSlur.Start.Anchor;
        var to = bestSlur.End.Anchor;
        var placement = Placement(curve, left, right);
        var meanDistance = (bestSlur.Start.DistanceInSpacings + bestSlur.End.DistanceInSpacings) / 2.0;
        var confidence = Math.Clamp(
            0.98 - 0.22 * meanDistance - 0.04 * bestSlur.Score,
            0.55,
            0.99);

        return new SlurDecision(
            curve.ShapeId,
            curve,
            true,
            "slur",
            from.Notehead,
            to.Notehead,
            placement,
            bestSlur.Start.DistanceInSpacings,
            bestSlur.End.DistanceInSpacings,
            confidence,
            $"{curve.ShapeId}: curved stroke endpoints attach to {from.Notehead.ShapeId}/{from.Pitch.Pitch} and "
            + $"{to.Notehead.ShapeId}/{to.Pitch.Pitch}; distances="
            + $"{bestSlur.Start.DistanceInSpacings:F2}/{bestSlur.End.DistanceInSpacings:F2}sp; "
            + $"placement={placement ?? "unspecified"}");
    }

    private static PairChoice? FindBestPair(
        IReadOnlyList<EndpointChoice> startCandidates,
        IReadOnlyList<EndpointChoice> endCandidates,
        bool requireSamePitch)
    {
        PairChoice? best = null;

        foreach (var start in startCandidates)
        {
            foreach (var end in endCandidates)
            {
                if (start.Anchor.Notehead.ShapeId == end.Anchor.Notehead.ShapeId
                    || SameTarget(start.Anchor, end.Anchor)
                    || ComesAfter(start.Anchor.Notehead, end.Anchor.Notehead))
                {
                    continue;
                }

                if (requireSamePitch && !IsSamePitchVoice(start.Anchor, end.Anchor))
                {
                    continue;
                }

                var score = start.DistanceInSpacings + end.DistanceInSpacings;
                if (start.Anchor.LocalVoice != end.Anchor.LocalVoice)
                {
                    score += DifferentVoicePenaltyInSpacings;
                }

                if (start.Anchor.Notehead.Staff != end.Anchor.Notehead.Staff)
                {
                    score += DifferentStaffPenaltyInSpacings;
                }

                var candidate = new PairChoice(start, end, score);
                if (best is null || candidate.Score < best.Score)
                {
                    best = candidate;
                }
            }
        }

        return best;
    }

    private static bool SameTarget(
        EndpointAnchor first,
        EndpointAnchor second)
    {
        return first.TargetKind == second.TargetKind
            && string.Equals(first.TargetId, second.TargetId, StringComparison.Ordinal);
    }

    private static bool IsSamePitchVoice(
        EndpointAnchor first,
        EndpointAnchor second)
    {
        return string.Equals(first.Pitch.Pitch, second.Pitch.Pitch, StringComparison.Ordinal)
            && first.Notehead.Staff == second.Notehead.Staff
            && first.LocalVoice == second.LocalVoice;
    }

    private static bool IsTieAdjacent(
        EndpointAnchor first,
        EndpointAnchor second)
    {
        if (!IsSamePitchVoice(first, second))
        {
            return false;
        }

        // Keep the established cross-measure tie behaviour until absolute score
        // timing is available. The ambiguity we can resolve exactly here is the
        // same-measure case: a tie must connect consecutive rhythmic events.
        if (first.Notehead.MeasureNumber != second.Notehead.MeasureNumber)
        {
            return true;
        }

        // Synthetic/unit callers may not have run DurationPass/OnsetPass yet.
        // Preserve the previous geometry-only behaviour in that incomplete state.
        if (first.Onset is null
            || second.Onset is null
            || first.Duration is null)
        {
            return true;
        }

        var expectedNextOnset = Fraction.Parse(first.Onset.At)
            + Fraction.Parse(first.Duration.EffectiveDuration);
        var actualNextOnset = Fraction.Parse(second.Onset.At);

        return expectedNextOnset == actualNextOnset;
    }

    private static SlurDecision TieLike(
        CurvedStroke curve,
        PointD left,
        PointD right,
        PairChoice pair)
    {
        var from = pair.Start.Anchor;
        var to = pair.End.Anchor;

        return new SlurDecision(
            curve.ShapeId,
            curve,
            false,
            "tie-like",
            from.Notehead,
            to.Notehead,
            Placement(curve, left, right),
            pair.Start.DistanceInSpacings,
            pair.End.DistanceInSpacings,
            0,
            $"{curve.ShapeId}: same-pitch arc {from.Pitch.Pitch} joins separate events in one staff/voice; "
            + $"distances={pair.Start.DistanceInSpacings:F2}/{pair.End.DistanceInSpacings:F2}sp; reserved for TiePass");
    }

    private static IReadOnlyList<EndpointChoice> EndpointCandidates(
        PointD endpoint,
        CurveObservation observation,
        IReadOnlyList<EndpointAnchor> anchors,
        double spacing,
        double maximumDistanceInSpacings)
    {
        return anchors
            .Where(anchor => observation.Measures.Contains(anchor.Notehead.MeasureNumber))
            .Where(anchor => observation.Staffs.Contains(anchor.Notehead.Staff))
            .Select(anchor => new EndpointChoice(
                anchor,
                EndpointDistance(endpoint, anchor) / spacing))
            .Where(candidate => candidate.DistanceInSpacings <= maximumDistanceInSpacings)
            .OrderBy(candidate => candidate.DistanceInSpacings)
            .ThenBy(candidate => candidate.Anchor.Notehead.MeasureNumber)
            .ThenBy(candidate => candidate.Anchor.Notehead.CenterX)
            .ThenBy(candidate => candidate.Anchor.Notehead.ShapeId, StringComparer.Ordinal)
            .Take(EndpointCandidateLimit)
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
            new PointD(a.X + t * dx, a.Y + t * dy));
    }

    private static double Distance(PointD a, PointD b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
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

        // SVG Y grows downward.
        return middle.Y < chordY ? "above" : "below";
    }

    private static SlurDecision Reject(
        CurvedStroke curve,
        string decision,
        string reason)
    {
        return new SlurDecision(
            curve.ShapeId,
            curve,
            false,
            decision,
            null,
            null,
            null,
            null,
            null,
            0,
            reason);
    }

    private readonly record struct VoiceKey(
        VoiceTargetKind TargetKind,
        string TargetId);

    private sealed record EndpointAnchor(
        NoteheadFact Notehead,
        PitchFact Pitch,
        StemAttachmentFact? Stem,
        VoiceTargetKind TargetKind,
        string TargetId,
        int LocalVoice,
        OnsetFact? Onset,
        DurationFact? Duration);

    private sealed record EndpointChoice(
        EndpointAnchor Anchor,
        double DistanceInSpacings);

    private sealed record PairChoice(
        EndpointChoice Start,
        EndpointChoice End,
        double Score);

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
