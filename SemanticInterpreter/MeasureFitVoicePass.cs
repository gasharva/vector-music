using SvgMusic.Canonical;

namespace SvgMusic.Semantics;

/// <summary>
/// Refines staff-local voice assignments when a visually unaligned second voice is
/// invisible to VoicePass's simultaneous-x tests.
///
/// A monophonic voice cannot contain more rhythmic duration than the measure. If all
/// recognized pitched events were provisionally assigned to one voice, but their
/// cumulative duration overfills the measure, opposite stem directions become strong
/// evidence for overlapping voices. The refinement is intentionally conservative: it
/// only splits a rest-free staff when every event has a known stem direction, each
/// stem-direction stream fits inside the measure, one stream fills the measure exactly,
/// and the minority stream is geometrically interleaved with the other stream.
/// </summary>
public sealed class MeasureFitVoicePass : ISemanticPass
{
    private const double FractionTolerance = 1e-9;

    public string Name => nameof(MeasureFitVoicePass);

    public void Run(
        SemanticDocument document,
        SemanticFacts facts)
    {
        var voices = facts.OfType<VoiceFact>().ToArray();
        if (voices.Length == 0)
        {
            facts.AddTrace("MeasureFitVoicePass: no VoiceFact input");
            return;
        }

        var events = BuildEvents(facts, voices);
        var timeChanges = facts.OfType<TimeSignatureFact>()
            .GroupBy(time => time.MeasureNumber)
            .ToDictionary(
                group => group.Key,
                group => group.Last());
        var currentMeasureLength = new Fraction(1);
        var corrections = new List<(VoiceFact Original, VoiceFact Replacement)>();
        var refinedStaffs = 0;

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

                if (group.Length < 3
                    || group.Any(ev => ev.Voice.TargetKind == VoiceTargetKind.Rest)
                    || group.Any(ev => ev.Direction is null)
                    || group.Select(ev => ev.Voice.LocalVoice).Distinct().Count() != 1)
                {
                    continue;
                }

                var up = group
                    .Where(ev => ev.Direction == StemDirection.Up)
                    .ToArray();
                var down = group
                    .Where(ev => ev.Direction == StemDirection.Down)
                    .ToArray();
                if (up.Length == 0 || down.Length == 0)
                {
                    continue;
                }

                var total = Sum(group.Select(ev => ev.Duration));
                var measureLength = ToDouble(currentMeasureLength);
                if (ToDouble(total) <= measureLength + FractionTolerance)
                {
                    continue;
                }

                var upTotal = Sum(up.Select(ev => ev.Duration));
                var downTotal = Sum(down.Select(ev => ev.Duration));
                var upLength = ToDouble(upTotal);
                var downLength = ToDouble(downTotal);
                if (upLength > measureLength + FractionTolerance
                    || downLength > measureLength + FractionTolerance)
                {
                    continue;
                }

                var oneStreamFillsMeasure =
                    Math.Abs(upLength - measureLength) <= FractionTolerance
                    || Math.Abs(downLength - measureLength) <= FractionTolerance;
                if (!oneStreamFillsMeasure)
                {
                    continue;
                }

                var upMinX = up.Min(ev => ev.Voice.AnchorX);
                var upMaxX = up.Max(ev => ev.Voice.AnchorX);
                var downMinX = down.Min(ev => ev.Voice.AnchorX);
                var downMaxX = down.Max(ev => ev.Voice.AnchorX);
                var interleaved = down.Any(ev =>
                        ev.Voice.AnchorX > upMinX + 1e-6
                        && ev.Voice.AnchorX < upMaxX - 1e-6)
                    || up.Any(ev =>
                        ev.Voice.AnchorX > downMinX + 1e-6
                        && ev.Voice.AnchorX < downMaxX - 1e-6);
                if (!interleaved)
                {
                    continue;
                }

                refinedStaffs++;
                foreach (var ev in group)
                {
                    var localVoice = ev.Direction == StemDirection.Down ? 2 : 1;
                    var replacement = ev.Voice with
                    {
                        LocalVoice = localVoice,
                        IsPolyphonicStaff = true,
                        Confidence = Math.Min(ev.Voice.Confidence, 0.96),
                        Reason = ev.Voice.Reason
                            + $"; measure-fit refinement: provisional single voice totals "
                            + $"{total} in {currentMeasureLength} measure; up-stream={upTotal}, "
                            + $"down-stream={downTotal}; opposite stems split into local voices"
                    };
                    corrections.Add((ev.Voice, replacement));
                }
            }
        }

        foreach (var correction in corrections
                     .GroupBy(item => new VoiceKey(
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
            $"MeasureFitVoicePass: refined-staffs={refinedStaffs}; "
            + $"voice-facts-updated={corrections.Count}");
    }

    private static IReadOnlyList<RhythmEvent> BuildEvents(
        SemanticFacts facts,
        IReadOnlyList<VoiceFact> voices)
    {
        var durations = facts.OfType<DurationFact>()
            .ToDictionary(
                duration => new NoteKey(
                    duration.MeasureNumber,
                    duration.NoteheadId));
        var stems = facts.OfType<StemAttachmentFact>().ToArray();
        var stemsById = stems.ToDictionary(
            stem => new StemKey(
                stem.MeasureNumber,
                stem.StemShapeId));
        var stemByNote = stems
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
            switch (voice.TargetKind)
            {
                case VoiceTargetKind.Chord:
                {
                    if (!chords.TryGetValue(
                            new ChordKey(
                                voice.MeasureNumber,
                                voice.TargetId),
                            out var chord))
                    {
                        continue;
                    }

                    var duration = chord.NoteheadIds
                        .Select(noteheadId => new NoteKey(
                            chord.MeasureNumber,
                            noteheadId))
                        .Where(durations.ContainsKey)
                        .Select(key => durations[key])
                        .OrderByDescending(item => item.Confidence)
                        .ThenBy(item => item.NoteheadId, StringComparer.Ordinal)
                        .FirstOrDefault();
                    if (duration is null)
                    {
                        continue;
                    }

                    StemDirection? direction = null;
                    if (chord.StemShapeId is not null
                        && stemsById.TryGetValue(
                            new StemKey(
                                chord.MeasureNumber,
                                chord.StemShapeId),
                            out var stem))
                    {
                        direction = stem.Direction;
                    }

                    result.Add(new RhythmEvent(
                        voice,
                        duration.EffectiveDuration,
                        direction));
                    break;
                }

                case VoiceTargetKind.Notehead:
                {
                    var key = new NoteKey(
                        voice.MeasureNumber,
                        voice.TargetId);
                    if (!durations.TryGetValue(key, out var duration))
                    {
                        continue;
                    }

                    var direction = stemByNote.TryGetValue(key, out var stem)
                        ? stem.Direction
                        : (StemDirection?)null;
                    result.Add(new RhythmEvent(
                        voice,
                        duration.EffectiveDuration,
                        direction));
                    break;
                }

                case VoiceTargetKind.Rest:
                {
                    if (!rests.TryGetValue(
                            new RestKey(
                                voice.MeasureNumber,
                                voice.Staff,
                                voice.TargetId),
                            out var rest))
                    {
                        continue;
                    }

                    result.Add(new RhythmEvent(
                        voice,
                        rest.Duration,
                        null));
                    break;
                }
            }
        }

        return result;
    }

    private static Fraction Sum(IEnumerable<string> durations)
    {
        var result = Fraction.Zero;
        foreach (var duration in durations)
        {
            result += Fraction.Parse(duration);
        }

        return result;
    }

    private static double ToDouble(Fraction value) =>
        value.Numerator / (double)value.Denominator;

    private sealed record RhythmEvent(
        VoiceFact Voice,
        string Duration,
        StemDirection? Direction);

    private readonly record struct NoteKey(
        int MeasureNumber,
        string NoteheadId);

    private readonly record struct StemKey(
        int MeasureNumber,
        string StemShapeId);

    private readonly record struct ChordKey(
        int MeasureNumber,
        string ChordId);

    private readonly record struct RestKey(
        int MeasureNumber,
        int Staff,
        string RestId);

    private readonly record struct VoiceKey(
        int MeasureNumber,
        int Staff,
        VoiceTargetKind TargetKind,
        string TargetId);
}
