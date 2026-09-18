using SvgMusic.Canonical;

namespace SvgMusic.Semantics;

/// <summary>
/// Repartitions an overfull staff into rhythmic lanes after the first voice/onset
/// passes. Stem direction is useful evidence, but it is not a stable voice identity:
/// one musical voice can flip stems. This pass therefore uses measure capacity and
/// x-order to split impossible timelines into the minimum number of non-overfull
/// lanes, while preserving existing voice assignments as a soft preference.
/// </summary>
public sealed class RhythmicLanePass : ISemanticPass
{
    private const double FractionTolerance = 1e-9;
    private const double SimultaneousXToleranceInSpacings = 0.72;
    private const int MaximumSupportedVoices = 4;

    public string Name => nameof(RhythmicLanePass);

    public void Run(
        SemanticDocument document,
        SemanticFacts facts)
    {
        var timeChanges = facts
            .OfType<TimeSignatureFact>()
            .GroupBy(time => time.MeasureNumber)
            .ToDictionary(
                group => group.Key,
                group => group.Last());
        var currentMeasureLength = new Fraction(1);
        var corrections = new List<(VoiceFact Original, VoiceFact Replacement)>();
        var repairedStaffs = 0;

        foreach (var measure in document.Measures)
        {
            if (timeChanges.TryGetValue(
                    measure.Number,
                    out var time))
            {
                currentMeasureLength = new Fraction(
                    time.Beats,
                    time.BeatType).Reduce();
            }

            foreach (var staffNumber in new[] { 1, 2 })
            {
                var staff = staffNumber == 1
                    ? measure.Upper
                    : measure.Lower;
                var events = BuildEvents(
                    measure.Number,
                    staffNumber,
                    facts)
                    .OrderBy(ev => ev.Voice.AnchorX)
                    .ThenBy(ev => ev.Voice.TargetId, StringComparer.Ordinal)
                    .ToArray();

                if (events.Length < 2)
                {
                    continue;
                }

                var total = Sum(events.Select(ev => ev.Duration));
                var measureValue = ToDouble(currentMeasureLength);
                var totalValue = ToDouble(total);

                if (totalValue
                    <= measureValue + FractionTolerance)
                {
                    continue;
                }

                var targetVoiceCount = (int)Math.Ceiling(
                    totalValue / measureValue - FractionTolerance);

                if (targetVoiceCount < 2
                    || targetVoiceCount > MaximumSupportedVoices)
                {
                    facts.AddTrace(
                        $"RhythmicLanePass: skipped m{measure.Number}/s{staffNumber}; "
                        + $"recognized duration {total} requires {targetVoiceCount} lanes");
                    continue;
                }

                var lanes = TryBuildLanes(
                    events,
                    currentMeasureLength,
                    Math.Max(
                        staff.LineSpacing,
                        0.001),
                    targetVoiceCount);

                if (lanes is null)
                {
                    facts.AddTrace(
                        $"RhythmicLanePass: no feasible lane partition for "
                        + $"m{measure.Number}/s{staffNumber}; total={total}; "
                        + $"target-lanes={targetVoiceCount}");
                    continue;
                }

                var changed = false;
                var assignedVoices = AssignVoiceNumbers(lanes);

                for (var laneIndex = 0;
                     laneIndex < lanes.Count;
                     laneIndex++)
                {
                    var localVoice = assignedVoices[laneIndex];

                    foreach (var ev in lanes[laneIndex].Events)
                    {
                        if (ev.Voice.LocalVoice == localVoice
                            && ev.Voice.IsPolyphonicStaff)
                        {
                            continue;
                        }

                        changed = true;
                        corrections.Add((
                            ev.Voice,
                            ev.Voice with
                            {
                                LocalVoice = localVoice,
                                IsPolyphonicStaff = true,
                                Confidence = Math.Min(
                                    ev.Voice.Confidence,
                                    0.94),
                                Reason = ev.Voice.Reason
                                    + $"; rhythmic-lane repair: measure total {total} "
                                    + $"in {currentMeasureLength} measure requires "
                                    + $"{targetVoiceCount} concurrent lanes; "
                                    + $"assigned preserved/conventional voice {localVoice}"
                            }));
                    }
                }

                if (!changed)
                {
                    continue;
                }

                repairedStaffs++;
                facts.AddTrace(
                    $"RhythmicLanePass: m{measure.Number}/s{staffNumber} "
                    + $"total={total}; lanes={string.Join(
                        ", ",
                        lanes.Select((lane, index) =>
                            $"v{assignedVoices[index]}:{lane.Total}[{string.Join(
                                '+',
                                lane.Events.Select(ev => ev.Voice.TargetId))}]"))}");
            }
        }

        foreach (var correction in corrections
                     .GroupBy(item => new VoiceKey(
                         item.Original.MeasureNumber,
                         item.Original.Staff,
                         item.Original.TargetKind,
                         item.Original.TargetId))
                     .Select(group => group.Last()))
        {
            facts.Replace(
                correction.Original,
                correction.Replacement);
        }

        if (corrections.Count > 0)
        {
            var removed = facts.RemoveWhere<OnsetFact>(_ => true);
            new OnsetPass().Run(
                document,
                facts);

            facts.AddTrace(
                $"RhythmicLanePass: repaired-staffs={repairedStaffs}; "
                + $"voice-facts-updated={corrections.Count}; "
                + $"old-onsets-removed={removed}; onsets-recomputed=True");
        }
        else
        {
            facts.AddTrace(
                "RhythmicLanePass: repaired-staffs=0; onsets-recomputed=False");
        }
    }

    private static IReadOnlyList<Lane>? TryBuildLanes(
        IReadOnlyList<RhythmEvent> events,
        Fraction measureLength,
        double spacing,
        int targetVoiceCount)
    {
        var lanes = new List<Lane>();
        var remaining = new List<RhythmEvent>();
        var measureValue = ToDouble(measureLength);

        foreach (var ev in events)
        {
            var durationValue = ToDouble(ev.Duration);

            if (Math.Abs(
                    durationValue - measureValue)
                <= FractionTolerance)
            {
                if (lanes.Count >= targetVoiceCount)
                {
                    return null;
                }

                lanes.Add(new Lane(ev));
            }
            else
            {
                remaining.Add(ev);
            }
        }

        foreach (var ev in remaining
                     .OrderBy(item => item.Voice.AnchorX)
                     .ThenBy(item => item.Voice.TargetId, StringComparer.Ordinal))
        {
            var candidates = lanes
                .Where(lane =>
                    Fits(
                        lane,
                        ev,
                        measureLength,
                        spacing))
                .Select(lane => new
                {
                    Lane = lane,
                    Score = LaneScore(
                        lane,
                        ev,
                        measureLength)
                })
                .OrderByDescending(item => item.Score)
                .ThenBy(item => lanes.IndexOf(item.Lane))
                .ToArray();

            if (candidates.Length > 0)
            {
                candidates[0].Lane.Add(ev);
                continue;
            }

            if (lanes.Count >= targetVoiceCount)
            {
                return null;
            }

            lanes.Add(new Lane(ev));
        }

        if (lanes.Count > targetVoiceCount)
        {
            return null;
        }

        return lanes;
    }

    private static IReadOnlyList<int> AssignVoiceNumbers(
        IReadOnlyList<Lane> lanes)
    {
        var result = Enumerable
            .Repeat(0, lanes.Count)
            .ToArray();
        var usedVoices = new HashSet<int>();
        var assignedLanes = new HashSet<int>();

        var candidates = lanes
            .SelectMany((lane, laneIndex) =>
                lane.Events
                    .GroupBy(ev => ev.Voice.LocalVoice)
                    .Select(group => new
                    {
                        LaneIndex = laneIndex,
                        Voice = group.Key,
                        Support = group.Sum(ev => ToDouble(ev.Duration)),
                        Count = group.Count(),
                        LaneTotal = ToDouble(lane.Total)
                    }))
            .OrderByDescending(candidate => candidate.Support)
            .ThenByDescending(candidate => candidate.Count)
            .ThenByDescending(candidate => candidate.LaneTotal)
            .ThenBy(candidate => candidate.LaneIndex)
            .ThenBy(candidate => candidate.Voice)
            .ToArray();

        foreach (var candidate in candidates)
        {
            if (candidate.Voice <= 0
                || assignedLanes.Contains(candidate.LaneIndex)
                || usedVoices.Contains(candidate.Voice))
            {
                continue;
            }

            result[candidate.LaneIndex] = candidate.Voice;
            assignedLanes.Add(candidate.LaneIndex);
            usedVoices.Add(candidate.Voice);
        }

        var nextVoice = 1;

        for (var laneIndex = 0;
             laneIndex < lanes.Count;
             laneIndex++)
        {
            if (result[laneIndex] != 0)
            {
                continue;
            }

            while (usedVoices.Contains(nextVoice))
            {
                nextVoice++;
            }

            result[laneIndex] = nextVoice;
            usedVoices.Add(nextVoice);
        }

        return result;
    }

    private static bool Fits(
        Lane lane,
        RhythmEvent ev,
        Fraction measureLength,
        double spacing)
    {
        if (ToDouble(lane.Total + ev.Duration)
            > ToDouble(measureLength) + FractionTolerance)
        {
            return false;
        }

        var last = lane.Events
            .OrderBy(item => item.Voice.AnchorX)
            .LastOrDefault();

        if (last is not null
            && Math.Abs(
                ev.Voice.AnchorX - last.Voice.AnchorX)
            <= spacing * SimultaneousXToleranceInSpacings)
        {
            return false;
        }

        return true;
    }

    private static double LaneScore(
        Lane lane,
        RhythmEvent ev,
        Fraction measureLength)
    {
        var score = 0.0;

        if (lane.PreferredOriginalVoice
            == ev.Voice.LocalVoice)
        {
            score += 8.0;
        }

        if (lane.Events.Count > 0
            && lane.Events[^1].Voice.LocalVoice
                == ev.Voice.LocalVoice)
        {
            score += 4.0;
        }

        score += ToDouble(lane.Total)
            / Math.Max(
                ToDouble(measureLength),
                1e-9);

        return score;
    }

    private static IReadOnlyList<RhythmEvent> BuildEvents(
        int measureNumber,
        int staff,
        SemanticFacts facts)
    {
        var durations = facts
            .OfType<DurationFact>()
            .Where(duration =>
                duration.MeasureNumber == measureNumber)
            .ToDictionary(
                duration => duration.NoteheadId,
                StringComparer.Ordinal);
        var chords = facts
            .OfType<ChordFact>()
            .Where(chord =>
                chord.MeasureNumber == measureNumber)
            .ToDictionary(
                chord => chord.ChordId,
                StringComparer.Ordinal);
        var rests = facts
            .OfType<RestFact>()
            .Where(rest =>
                rest.MeasureNumber == measureNumber
                && rest.Staff == staff)
            .ToDictionary(
                rest => rest.ShapeId,
                StringComparer.Ordinal);

        var result = new List<RhythmEvent>();

        foreach (var voice in facts
                     .OfType<VoiceFact>()
                     .Where(voice =>
                         voice.MeasureNumber == measureNumber
                         && voice.Staff == staff))
        {
            string? duration = null;

            if (voice.TargetKind == VoiceTargetKind.Chord
                && chords.TryGetValue(
                    voice.TargetId,
                    out var chord))
            {
                duration = chord.NoteheadIds
                    .Where(durations.ContainsKey)
                    .Select(id => durations[id])
                    .OrderByDescending(item =>
                        item.Confidence)
                    .Select(item =>
                        item.EffectiveDuration)
                    .FirstOrDefault();
            }
            else if (voice.TargetKind
                         == VoiceTargetKind.Notehead
                     && durations.TryGetValue(
                         voice.TargetId,
                         out var noteDuration))
            {
                duration =
                    noteDuration.EffectiveDuration;
            }
            else if (voice.TargetKind
                         == VoiceTargetKind.Rest
                     && rests.TryGetValue(
                         voice.TargetId,
                         out var rest))
            {
                var dots = facts
                    .OfType<RestDotAttachmentFact>()
                    .Where(dot =>
                        dot.MeasureNumber == measureNumber
                        && dot.Staff == staff
                        && string.Equals(
                            dot.TargetRestShapeId,
                            rest.ShapeId,
                            StringComparison.Ordinal))
                    .OrderByDescending(dot =>
                        dot.Confidence)
                    .Select(dot => dot.Count)
                    .FirstOrDefault();

                duration = DurationMath.ApplyDots(
                    rest.Duration,
                    dots);
            }

            if (duration is null)
            {
                continue;
            }

            result.Add(new RhythmEvent(
                voice,
                Fraction.Parse(duration)));
        }

        return result;
    }

    private static Fraction Sum(
        IEnumerable<Fraction> values)
    {
        var result = Fraction.Zero;

        foreach (var value in values)
        {
            result += value;
        }

        return result;
    }

    private static double ToDouble(
        Fraction value) =>
        value.Numerator
        / (double)value.Denominator;

    private sealed class Lane
    {
        public Lane(RhythmEvent first)
        {
            PreferredOriginalVoice =
                first.Voice.LocalVoice;
            Events.Add(first);
            Total = first.Duration;
        }

        public int PreferredOriginalVoice { get; }

        public List<RhythmEvent> Events { get; } = [];

        public Fraction Total { get; private set; }

        public void Add(RhythmEvent ev)
        {
            Events.Add(ev);
            Total += ev.Duration;
        }
    }

    private sealed record RhythmEvent(
        VoiceFact Voice,
        Fraction Duration);

    private readonly record struct VoiceKey(
        int MeasureNumber,
        int Staff,
        VoiceTargetKind TargetKind,
        string TargetId);
}
