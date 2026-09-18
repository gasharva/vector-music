using SvgMusic.Canonical;

namespace SvgMusic.Semantics;

/// <summary>
/// Refines a narrow but important onset ambiguity: one sustained secondary-voice
/// event sits between two known backbone events, but has no event at the same x from
/// which OnsetPass can obtain an exact anchor.
///
/// In that case OnsetPass deliberately starts the secondary voice at the measure
/// origin. If the event's x-position is much closer to the position implied by an
/// end-of-measure fit, the measure boundary supplies the missing rhythmic constraint.
/// This recovers patterns such as a half note entering on beat 2 of a 3/4 measure and
/// sustaining exactly to the barline.
/// </summary>
public sealed class MeasureEndOnsetRefinementPass : ISemanticPass
{
    private const double FractionTolerance = 1e-9;
    private const double MinimumImprovement = 0.04;

    public string Name => nameof(MeasureEndOnsetRefinementPass);

    public void Run(
        SemanticDocument document,
        SemanticFacts facts)
    {
        var voices = facts.OfType<VoiceFact>().ToArray();
        var onsets = facts.OfType<OnsetFact>().ToArray();
        if (voices.Length == 0 || onsets.Length == 0)
        {
            facts.AddTrace("MeasureEndOnsetRefinementPass: missing voice/onset input");
            return;
        }

        var events = BuildEvents(facts, voices, onsets);
        var timeChanges = facts.OfType<TimeSignatureFact>()
            .GroupBy(time => time.MeasureNumber)
            .ToDictionary(
                group => group.Key,
                group => group.Last());
        var currentMeasureLength = new Fraction(1);
        var corrections = new List<(OnsetFact Original, OnsetFact Replacement)>();

        foreach (var measure in document.Measures)
        {
            if (timeChanges.TryGetValue(measure.Number, out var time))
            {
                currentMeasureLength = new Fraction(
                    time.Beats,
                    time.BeatType).Reduce();
            }

            foreach (var staffNumber in new[] { 1, 2 })
            {
                var group = events
                    .Where(ev =>
                        ev.Voice.MeasureNumber == measure.Number
                        && ev.Voice.Staff == staffNumber)
                    .OrderBy(ev => ev.Voice.AnchorX)
                    .ThenBy(ev => ev.Voice.TargetId, StringComparer.Ordinal)
                    .ToArray();
                var byVoice = group
                    .GroupBy(ev => ev.Voice.LocalVoice)
                    .OrderBy(g => g.Key)
                    .ToArray();
                if (byVoice.Length < 2)
                {
                    continue;
                }

                foreach (var voiceGroup in byVoice)
                {
                    var single = voiceGroup.ToArray();
                    if (single.Length != 1)
                    {
                        continue;
                    }

                    var ev = single[0];
                    if (ev.Voice.TargetKind == VoiceTargetKind.Rest
                        || !string.Equals(ev.Onset.At, "0", StringComparison.Ordinal)
                        || !ev.Onset.Reason.Contains(
                            "no cross-voice x anchor",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var duration = Fraction.Parse(ev.Duration);
                    var endFit = currentMeasureLength - duration;
                    var endFitValue = ToDouble(endFit);
                    var measureLength = ToDouble(currentMeasureLength);
                    if (endFitValue <= FractionTolerance
                        || endFitValue >= measureLength - FractionTolerance)
                    {
                        continue;
                    }

                    var width = measure.XEnd - measure.XStart;
                    if (width <= 1e-6)
                    {
                        continue;
                    }

                    var t = Math.Clamp(
                        (ev.Voice.AnchorX - measure.XStart) / width,
                        0,
                        1);
                    var estimate = t * measureLength;
                    var currentError = Math.Abs(estimate);
                    var endFitError = Math.Abs(estimate - endFitValue);

                    if (currentError - endFitError < MinimumImprovement)
                    {
                        continue;
                    }

                    corrections.Add((
                        ev.Onset,
                        ev.Onset with
                        {
                            At = endFit.Reduce().ToString(),
                            Confidence = Math.Min(
                                ev.Onset.Confidence + 0.25,
                                0.86),
                            Reason = ev.Onset.Reason
                                + $"; measure-end fallback: page geometry estimates {estimate:F3}, "
                                + $"and duration {duration} ending at {currentMeasureLength} "
                                + $"implies onset {endFit}"
                        }));
                }

                var backbone = byVoice
                    .OrderByDescending(g => g.Count())
                    .ThenBy(g => g.Key)
                    .First()
                    .OrderBy(ev => ev.Voice.AnchorX)
                    .ThenBy(ev => ev.Voice.TargetId, StringComparer.Ordinal)
                    .ToArray();
                if (backbone.Length < 2)
                {
                    continue;
                }

                var first = backbone[0];
                var last = backbone[^1];
                var xSpan = last.Voice.AnchorX - first.Voice.AnchorX;
                var onsetSpan = ToDouble(Fraction.Parse(last.Onset.At))
                    - ToDouble(Fraction.Parse(first.Onset.At));
                if (xSpan <= 1e-6 || onsetSpan <= FractionTolerance)
                {
                    continue;
                }

                foreach (var secondaryGroup in byVoice.Where(g => g.Key != backbone[0].Voice.LocalVoice))
                {
                    var secondary = secondaryGroup.ToArray();
                    if (secondary.Length != 1)
                    {
                        continue;
                    }

                    var ev = secondary[0];
                    if (ev.Voice.TargetKind == VoiceTargetKind.Rest
                        || !ev.Onset.Reason.Contains(
                            "no cross-voice x anchor",
                            StringComparison.OrdinalIgnoreCase)
                        || ev.Voice.AnchorX <= first.Voice.AnchorX + 1e-6
                        || ev.Voice.AnchorX >= last.Voice.AnchorX - 1e-6)
                    {
                        continue;
                    }

                    var duration = Fraction.Parse(ev.Duration);
                    var endFit = currentMeasureLength - duration;
                    var endFitValue = ToDouble(endFit);
                    if (endFitValue <= FractionTolerance
                        || endFitValue >= ToDouble(currentMeasureLength) - FractionTolerance)
                    {
                        continue;
                    }

                    var t = (ev.Voice.AnchorX - first.Voice.AnchorX) / xSpan;
                    var estimate = ToDouble(Fraction.Parse(first.Onset.At))
                        + t * onsetSpan;
                    var current = ToDouble(Fraction.Parse(ev.Onset.At));
                    var currentError = Math.Abs(estimate - current);
                    var endFitError = Math.Abs(estimate - endFitValue);
                    if (currentError - endFitError < MinimumImprovement)
                    {
                        continue;
                    }

                    var replacement = ev.Onset with
                    {
                        At = endFit.ToString(),
                        Confidence = Math.Min(ev.Onset.Confidence + 0.28, 0.88),
                        Reason = ev.Onset.Reason
                            + $"; measure-end refinement: x interpolation estimates {estimate:F3}, "
                            + $"and duration {duration} ending at {currentMeasureLength} implies onset {endFit}"
                    };
                    corrections.Add((ev.Onset, replacement));
                }
            }
        }

        foreach (var correction in corrections
                     .GroupBy(item => new OnsetKey(
                         item.Original.MeasureNumber,
                         item.Original.Staff,
                         item.Original.TargetKind,
                         item.Original.TargetId))
                     .Select(group => group.First()))
        {
            facts.Replace(
                correction.Original,
                correction.Replacement);
        }

        facts.AddTrace(
            $"MeasureEndOnsetRefinementPass: corrected={corrections.Count}");
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
                group => group.OrderByDescending(item => item.Confidence).First());
        var durations = facts.OfType<DurationFact>()
            .ToDictionary(
                duration => new NoteKey(
                    duration.MeasureNumber,
                    duration.NoteheadId));
        var chords = facts.OfType<ChordFact>()
            .ToDictionary(
                chord => new ChordKey(
                    chord.MeasureNumber,
                    chord.ChordId));
        var rests = facts.OfType<RestFact>()
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
            if (voice.TargetKind == VoiceTargetKind.Chord
                && chords.TryGetValue(
                    new ChordKey(
                        voice.MeasureNumber,
                        voice.TargetId),
                    out var chord))
            {
                duration = chord.NoteheadIds
                    .Select(noteheadId => new NoteKey(
                        chord.MeasureNumber,
                        noteheadId))
                    .Where(durations.ContainsKey)
                    .Select(key => durations[key])
                    .OrderByDescending(item => item.Confidence)
                    .ThenBy(item => item.NoteheadId, StringComparer.Ordinal)
                    .Select(item => item.EffectiveDuration)
                    .FirstOrDefault();
            }
            else if (voice.TargetKind == VoiceTargetKind.Notehead
                     && durations.TryGetValue(
                         new NoteKey(
                             voice.MeasureNumber,
                             voice.TargetId),
                         out var noteDuration))
            {
                duration = noteDuration.EffectiveDuration;
            }
            else if (voice.TargetKind == VoiceTargetKind.Rest
                     && rests.TryGetValue(
                         new RestKey(
                             voice.MeasureNumber,
                             voice.Staff,
                             voice.TargetId),
                         out var rest))
            {
                duration = rest.Duration;
            }

            if (duration is not null)
            {
                result.Add(new RhythmEvent(
                    voice,
                    onset,
                    duration));
            }
        }

        return result;
    }

    private static double ToDouble(Fraction value) =>
        value.Numerator / (double)value.Denominator;

    private sealed record RhythmEvent(
        VoiceFact Voice,
        OnsetFact Onset,
        string Duration);

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
