using SvgMusic.Canonical;

namespace SvgMusic.Semantics;

/// <summary>
/// Recovers implicit silence at the beginning of a voice by using rhythmic columns
/// already proven on the other staff of the same piano system.
///
/// Only non-zero anchors that have an earlier event in the same source voice are
/// trusted. This prevents a provisional voice start at 0 on one staff from dragging
/// a correct timeline on the other staff back to the measure origin.
/// </summary>
public sealed class CrossStaffOnsetRefinementPass : ISemanticPass
{
    private const double AlignmentToleranceInSpacings = 0.90;
    private const double FractionTolerance = 1e-9;

    public string Name => nameof(CrossStaffOnsetRefinementPass);

    public void Run(
        SemanticDocument document,
        SemanticFacts facts)
    {
        var voices = facts
            .OfType<VoiceFact>()
            .ToArray();
        var onsets = facts
            .OfType<OnsetFact>()
            .ToArray();

        if (voices.Length == 0
            || onsets.Length == 0)
        {
            facts.AddTrace(
                "CrossStaffOnsetRefinementPass: missing voice/onset input");
            return;
        }

        var timeChanges = facts
            .OfType<TimeSignatureFact>()
            .GroupBy(time => time.MeasureNumber)
            .ToDictionary(
                group => group.Key,
                group => group.Last());
        var currentMeasureLength = new Fraction(1);
        var events = BuildEvents(
            facts,
            voices,
            onsets);
        var corrections = new List<
            (OnsetFact Original, OnsetFact Replacement)>();

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

            var measureEvents = events
                .Where(ev =>
                    ev.Voice.MeasureNumber
                        == measure.Number)
                .ToArray();

            foreach (var staffNumber in new[] { 1, 2 })
            {
                var staff = staffNumber == 1
                    ? measure.Upper
                    : measure.Lower;
                var target = measureEvents
                    .Where(ev =>
                        ev.Voice.Staff == staffNumber)
                    .ToArray();
                var other = measureEvents
                    .Where(ev =>
                        ev.Voice.Staff != staffNumber)
                    .ToArray();

                if (target.Length == 0
                    || other.Length == 0)
                {
                    continue;
                }

                var reliableAnchors =
                    ReliableInternalAnchors(other)
                        .ToArray();

                if (reliableAnchors.Length == 0)
                {
                    continue;
                }

                foreach (var voiceGroup in target
                             .GroupBy(ev =>
                                 ev.Voice.LocalVoice))
                {
                    var ordered = voiceGroup
                        .OrderBy(ev =>
                            ev.Voice.AnchorX)
                        .ThenBy(ev =>
                            ev.Voice.TargetId,
                            StringComparer.Ordinal)
                        .ToArray();
                    var first = ordered[0];
                    var firstAt = Fraction.Parse(
                        first.Onset.At);

                    // This pass repairs an implicit leading gap. A voice that
                    // already has a non-zero start has stronger local evidence.
                    if (Math.Abs(ToDouble(firstAt))
                        > FractionTolerance)
                    {
                        continue;
                    }

                    var tolerance =
                        Math.Max(
                            staff.LineSpacing,
                            0.001)
                        * AlignmentToleranceInSpacings;
                    var anchor = reliableAnchors
                        .Select(ev => new
                        {
                            Event = ev,
                            Dx = Math.Abs(
                                ev.Voice.AnchorX
                                - first.Voice.AnchorX)
                        })
                        .Where(item =>
                            item.Dx <= tolerance)
                        .OrderBy(item => item.Dx)
                        .ThenBy(item =>
                            ToDouble(
                                Fraction.Parse(
                                    item.Event.Onset.At)))
                        .FirstOrDefault();

                    if (anchor is null)
                    {
                        continue;
                    }

                    var anchoredAt = Fraction.Parse(
                        anchor.Event.Onset.At);
                    var delta = anchoredAt - firstAt;

                    if (ToDouble(delta)
                        <= FractionTolerance)
                    {
                        continue;
                    }

                    var maxEnd = ordered
                        .Select(ev =>
                            Fraction.Parse(
                                ev.Onset.At)
                            + delta
                            + ev.Duration)
                        .OrderByDescending(ToDouble)
                        .First();

                    if (ToDouble(maxEnd)
                        > ToDouble(currentMeasureLength)
                            + FractionTolerance)
                    {
                        continue;
                    }

                    foreach (var ev in ordered)
                    {
                        var shifted =
                            Fraction.Parse(
                                ev.Onset.At)
                            + delta;

                        corrections.Add((
                            ev.Onset,
                            ev.Onset with
                            {
                                At = shifted
                                    .Reduce()
                                    .ToString(),
                                Confidence = Math.Min(
                                    Math.Max(
                                        ev.Onset.Confidence,
                                        0.88),
                                    0.97),
                                Reason = ev.Onset.Reason
                                    + $"; cross-staff refinement: voice starts at "
                                    + $"{anchoredAt} from aligned internal anchor "
                                    + $"on staff {anchor.Event.Voice.Staff} "
                                    + $"(dx={anchor.Dx / Math.Max(staff.LineSpacing, 0.001):F2}sp)"
                            }));
                    }

                    facts.AddTrace(
                        $"CrossStaffOnsetRefinementPass: "
                        + $"m{measure.Number}/s{staffNumber}/v{voiceGroup.Key} "
                        + $"shifted by {delta}; anchor=s{anchor.Event.Voice.Staff}"
                        + $"/{anchor.Event.Voice.TargetId}@{anchor.Event.Onset.At}");
                }
            }
        }

        foreach (var correction in corrections
                     .GroupBy(item => new OnsetKey(
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

        facts.AddTrace(
            $"CrossStaffOnsetRefinementPass: corrected={corrections.Count}");
    }

    private static IEnumerable<RhythmEvent> ReliableInternalAnchors(
        IReadOnlyList<RhythmEvent> events)
    {
        foreach (var group in events.GroupBy(ev =>
                     (ev.Voice.Staff,
                      ev.Voice.LocalVoice)))
        {
            var ordered = group
                .OrderBy(ev => ev.Voice.AnchorX)
                .ThenBy(ev => ev.Voice.TargetId, StringComparer.Ordinal)
                .ToArray();

            for (var index = 1;
                 index < ordered.Length;
                 index++)
            {
                if (ToDouble(
                        Fraction.Parse(
                            ordered[index].Onset.At))
                    > FractionTolerance)
                {
                    yield return ordered[index];
                }
            }
        }
    }

    private static IReadOnlyList<RhythmEvent> BuildEvents(
        SemanticFacts facts,
        IReadOnlyList<VoiceFact> voices,
        IReadOnlyList<OnsetFact> onsets)
    {
        var onsetMap = onsets
            .GroupBy(onset => new OnsetKey(
                onset.MeasureNumber,
                onset.Staff,
                onset.TargetKind,
                onset.TargetId))
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(item =>
                        item.Confidence)
                    .First());
        var durations = facts
            .OfType<DurationFact>()
            .ToDictionary(
                duration => new NoteKey(
                    duration.MeasureNumber,
                    duration.NoteheadId));
        var chords = facts
            .OfType<ChordFact>()
            .ToDictionary(
                chord => new ChordKey(
                    chord.MeasureNumber,
                    chord.ChordId));
        var rests = facts
            .OfType<RestFact>()
            .ToDictionary(
                rest => new RestKey(
                    rest.MeasureNumber,
                    rest.Staff,
                    rest.ShapeId));

        var result = new List<RhythmEvent>();

        foreach (var voice in voices)
        {
            if (!onsetMap.TryGetValue(
                    new OnsetKey(
                        voice.MeasureNumber,
                        voice.Staff,
                        voice.TargetKind,
                        voice.TargetId),
                    out var onset))
            {
                continue;
            }

            string? duration = null;

            if (voice.TargetKind
                    == VoiceTargetKind.Chord
                && chords.TryGetValue(
                    new ChordKey(
                        voice.MeasureNumber,
                        voice.TargetId),
                    out var chord))
            {
                duration = chord.NoteheadIds
                    .Select(id =>
                        new NoteKey(
                            chord.MeasureNumber,
                            id))
                    .Where(durations.ContainsKey)
                    .Select(key =>
                        durations[key])
                    .OrderByDescending(item =>
                        item.Confidence)
                    .Select(item =>
                        item.EffectiveDuration)
                    .FirstOrDefault();
            }
            else if (voice.TargetKind
                         == VoiceTargetKind.Notehead
                     && durations.TryGetValue(
                         new NoteKey(
                             voice.MeasureNumber,
                             voice.TargetId),
                         out var noteDuration))
            {
                duration =
                    noteDuration.EffectiveDuration;
            }
            else if (voice.TargetKind
                         == VoiceTargetKind.Rest
                     && rests.TryGetValue(
                         new RestKey(
                             voice.MeasureNumber,
                             voice.Staff,
                             voice.TargetId),
                         out var rest))
            {
                var dots = facts
                    .OfType<RestDotAttachmentFact>()
                    .Where(dot =>
                        dot.MeasureNumber
                            == rest.MeasureNumber
                        && dot.Staff == rest.Staff
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
                onset,
                Fraction.Parse(duration)));
        }

        return result;
    }

    private static double ToDouble(
        Fraction value) =>
        value.Numerator
        / (double)value.Denominator;

    private sealed record RhythmEvent(
        VoiceFact Voice,
        OnsetFact Onset,
        Fraction Duration);

    private readonly record struct NoteKey(
        int MeasureNumber,
        string NoteheadId);

    private readonly record struct ChordKey(
        int MeasureNumber,
        string ChordId);

    private readonly record struct RestKey(
        int MeasureNumber,
        int Staff,
        string RestId);

    private readonly record struct OnsetKey(
        int MeasureNumber,
        int Staff,
        VoiceTargetKind TargetKind,
        string TargetId);
}
