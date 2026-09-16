using SvgMusic.Canonical;

namespace SvgMusic.Semantics;

/// <summary>
/// Reconstructs event start times inside each measure/staff/voice.
///
/// X is used only for ordering and cross-voice alignment. Exact time comes from
/// semantic durations: one voice becomes a local rhythmic backbone and aligned
/// events in the other voice become anchors. Missing visual events are represented
/// as time gaps rather than invented rests.
/// </summary>
public sealed class OnsetPass : ISemanticPass
{
    private const double CrossVoiceAlignmentInSpacings = 0.72;
    private const double AnchorConflictTolerance = 1e-9;

    public string Name => nameof(OnsetPass);

    public OnsetAnalysisResult? LastAnalysis { get; private set; }

    public void Run(
        SemanticDocument document,
        SemanticFacts facts)
    {
        var voices = facts.OfType<VoiceFact>().ToArray();
        if (voices.Length == 0)
        {
            LastAnalysis = new OnsetAnalysisResult(Array.Empty<OnsetFact>());
            facts.AddTrace("OnsetPass: no VoiceFact input; no onsets inferred");
            return;
        }

        var events = BuildEvents(facts, voices);
        var result = new List<OnsetFact>();
        var currentMeasureLength = new Fraction(1);
        var timeChanges = facts
            .OfType<TimeSignatureFact>()
            .ToDictionary(time => time.MeasureNumber);

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
                var staff = staffNumber == 1
                    ? measure.Upper
                    : measure.Lower;
                var spacing = Math.Max(staff.LineSpacing, 0.001);
                var staffEvents = events
                    .Where(ev =>
                        ev.MeasureNumber == measure.Number
                        && ev.Staff == staffNumber)
                    .OrderBy(ev => ev.AnchorX)
                    .ThenBy(ev => ev.TargetId, StringComparer.Ordinal)
                    .ToArray();

                if (staffEvents.Length == 0)
                {
                    continue;
                }

                var byVoice = staffEvents
                    .GroupBy(ev => ev.LocalVoice)
                    .OrderBy(group => group.Key)
                    .ToArray();

                if (byVoice.Length == 1)
                {
                    result.AddRange(Sequential(
                        byVoice[0].ToArray(),
                        Fraction.Zero,
                        0.95,
                        "single local voice: onset is cumulative semantic duration in x-order"));
                    continue;
                }

                var backboneGroup = byVoice
                    .OrderByDescending(group => group.Count())
                    .ThenBy(group => group.Key)
                    .First();
                var backbone = backboneGroup
                    .OrderBy(ev => ev.AnchorX)
                    .ThenBy(ev => ev.TargetId, StringComparer.Ordinal)
                    .ToArray();
                var backboneOnsets = Sequential(
                    backbone,
                    Fraction.Zero,
                    0.95,
                    $"local voice {backboneGroup.Key} chosen as rhythmic backbone")
                    .ToArray();
                result.AddRange(backboneOnsets);

                var backboneByTarget = backboneOnsets.ToDictionary(
                    onset => new TargetKey(onset.TargetKind, onset.TargetId));
                var backboneTimeline = backbone
                    .Select((ev, index) => new AnchorEvent(
                        ev,
                        Fraction.Parse(backboneOnsets[index].At),
                        index))
                    .ToArray();

                foreach (var voiceGroup in byVoice.Where(group => group.Key != backboneGroup.Key))
                {
                    var secondary = voiceGroup
                        .OrderBy(ev => ev.AnchorX)
                        .ThenBy(ev => ev.TargetId, StringComparer.Ordinal)
                        .ToArray();
                    var anchors = BuildAnchors(
                        secondary,
                        backboneTimeline,
                        spacing);

                    result.AddRange(AssignSecondary(
                        secondary,
                        anchors,
                        backboneGroup.Key));
                }

                var staffOnsets = result
                    .Where(onset =>
                        onset.MeasureNumber == measure.Number
                        && onset.Staff == staffNumber)
                    .ToArray();
                var eventByKey = staffEvents.ToDictionary(
                    ev => new TargetKey(ev.TargetKind, ev.TargetId));
                var maxEnd = staffOnsets
                    .Select(onset =>
                    {
                        var ev = eventByKey[new TargetKey(onset.TargetKind, onset.TargetId)];
                        return ToDouble(Fraction.Parse(onset.At) + Fraction.Parse(ev.Duration));
                    })
                    .DefaultIfEmpty(0)
                    .Max();
                var measureEnd = ToDouble(currentMeasureLength);

                if (maxEnd > measureEnd + 1e-8)
                {
                    facts.AddTrace(
                        $"OnsetPass warning: m{measure.Number} staff {staffNumber} "
                        + $"events extend to {maxEnd:F4} beyond measure length {measureEnd:F4}");
                }
            }
        }

        foreach (var onset in result)
        {
            facts.Add(onset);
        }

        LastAnalysis = new OnsetAnalysisResult(result);
        facts.AddTrace(
            $"OnsetPass: assignments={result.Count}; "
            + $"anchored={result.Count(onset => onset.Reason.Contains("aligned", StringComparison.OrdinalIgnoreCase))}; "
            + $"low-confidence={result.Count(onset => onset.Confidence < 0.70)}");
    }

    private static IReadOnlyList<OnsetEvent> BuildEvents(
        SemanticFacts facts,
        IReadOnlyList<VoiceFact> voices)
    {
        var noteheads = facts.OfType<NoteheadFact>()
            .ToDictionary(
                note => new NoteKey(note.MeasureNumber, note.ShapeId));
        var durations = facts.OfType<DurationFact>()
            .ToDictionary(
                duration => new NoteKey(duration.MeasureNumber, duration.NoteheadId));
        var chords = facts.OfType<ChordFact>().ToArray();
        var rests = facts.OfType<RestFact>()
            .ToDictionary(
                rest => new RestKey(rest.MeasureNumber, rest.Staff, rest.ShapeId));
        var restDots = facts.OfType<RestDotAttachmentFact>()
            .ToDictionary(
                dot => new RestKey(dot.MeasureNumber, dot.Staff, dot.TargetRestShapeId));
        var voiceMap = voices
            .GroupBy(voice => new VoiceKey(
                voice.MeasureNumber,
                voice.Staff,
                voice.TargetKind,
                voice.TargetId))
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(voice => voice.Confidence).First());

        var consumed = new HashSet<NoteKey>();
        var result = new List<OnsetEvent>();

        foreach (var chord in chords)
        {
            var members = chord.NoteheadIds
                .Select(id => new NoteKey(chord.MeasureNumber, id))
                .Where(noteheads.ContainsKey)
                .ToArray();
            if (members.Length == 0)
            {
                continue;
            }

            foreach (var member in members)
            {
                consumed.Add(member);
            }

            var voice = voices
                .Where(item =>
                    item.MeasureNumber == chord.MeasureNumber
                    && item.TargetKind == VoiceTargetKind.Chord
                    && item.TargetId == chord.ChordId)
                .OrderByDescending(item => item.Confidence)
                .FirstOrDefault();
            if (voice is null)
            {
                continue;
            }

            var duration = members
                .Where(durations.ContainsKey)
                .Select(key => durations[key])
                .OrderByDescending(item => item.Confidence)
                .ThenBy(item => item.NoteheadId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (duration is null)
            {
                continue;
            }

            result.Add(new OnsetEvent(
                chord.MeasureNumber,
                voice.Staff,
                VoiceTargetKind.Chord,
                chord.ChordId,
                voice.LocalVoice,
                chord.AnchorX,
                duration.EffectiveDuration,
                Math.Min(chord.Confidence, voice.Confidence),
                chord.SourceShapeIds));
        }

        foreach (var pair in noteheads)
        {
            if (consumed.Contains(pair.Key)
                || !durations.TryGetValue(pair.Key, out var duration))
            {
                continue;
            }

            var note = pair.Value;
            var voice = voices
                .Where(item =>
                    item.MeasureNumber == note.MeasureNumber
                    && item.Staff == note.Staff
                    && item.TargetKind == VoiceTargetKind.Notehead
                    && item.TargetId == note.ShapeId)
                .OrderByDescending(item => item.Confidence)
                .FirstOrDefault();
            if (voice is null)
            {
                continue;
            }

            result.Add(new OnsetEvent(
                note.MeasureNumber,
                note.Staff,
                VoiceTargetKind.Notehead,
                note.ShapeId,
                voice.LocalVoice,
                note.CenterX,
                duration.EffectiveDuration,
                Math.Min(note.Confidence, voice.Confidence),
                note.SourceShapeIds));
        }

        foreach (var pair in rests)
        {
            var rest = pair.Value;
            if (!voiceMap.TryGetValue(
                    new VoiceKey(
                        rest.MeasureNumber,
                        rest.Staff,
                        VoiceTargetKind.Rest,
                        rest.ShapeId),
                    out var voice))
            {
                continue;
            }

            var dots = restDots.TryGetValue(pair.Key, out var dot)
                ? dot.Count
                : 0;
            var duration = DurationMath.ApplyDots(
                rest.Duration,
                dots);

            result.Add(new OnsetEvent(
                rest.MeasureNumber,
                rest.Staff,
                VoiceTargetKind.Rest,
                rest.ShapeId,
                voice.LocalVoice,
                rest.CenterX,
                duration,
                Math.Min(rest.Confidence, voice.Confidence),
                rest.SourceShapeIds));
        }

        return result;
    }

    private static IReadOnlyList<OnsetFact> Sequential(
        IReadOnlyList<OnsetEvent> events,
        Fraction start,
        double confidence,
        string reason)
    {
        var result = new List<OnsetFact>(events.Count);
        var cursor = start;

        foreach (var ev in events
                     .OrderBy(item => item.AnchorX)
                     .ThenBy(item => item.TargetId, StringComparer.Ordinal))
        {
            result.Add(Fact(
                ev,
                cursor,
                Math.Min(confidence, ev.Confidence),
                reason));
            cursor += Fraction.Parse(ev.Duration);
        }

        return result;
    }

    private static IReadOnlyDictionary<int, Anchor> BuildAnchors(
        IReadOnlyList<OnsetEvent> secondary,
        IReadOnlyList<AnchorEvent> backbone,
        double spacing)
    {
        var result = new Dictionary<int, Anchor>();
        var minimumBackboneIndex = 0;
        var tolerance = spacing * CrossVoiceAlignmentInSpacings;

        for (var i = 0; i < secondary.Count; i++)
        {
            var candidate = backbone
                .Where(item => item.Index >= minimumBackboneIndex)
                .Select(item => new
                {
                    Item = item,
                    Dx = Math.Abs(item.Event.AnchorX - secondary[i].AnchorX)
                })
                .Where(item => item.Dx <= tolerance)
                .OrderBy(item => item.Dx)
                .ThenBy(item => item.Item.Index)
                .FirstOrDefault();

            if (candidate is null)
            {
                continue;
            }

            result[i] = new Anchor(
                candidate.Item.Onset,
                candidate.Item.Index,
                candidate.Dx / spacing);
            minimumBackboneIndex = candidate.Item.Index;
        }

        return result;
    }

    private static IReadOnlyList<OnsetFact> AssignSecondary(
        IReadOnlyList<OnsetEvent> events,
        IReadOnlyDictionary<int, Anchor> anchors,
        int backboneVoice)
    {
        if (events.Count == 0)
        {
            return Array.Empty<OnsetFact>();
        }

        if (anchors.Count == 0)
        {
            return Sequential(
                events,
                Fraction.Zero,
                0.55,
                $"no cross-voice x anchor to local voice {backboneVoice}; secondary voice provisionally starts at measure origin");
        }

        var firstAnchorIndex = anchors.Keys.Min();
        var firstAnchor = anchors[firstAnchorIndex];
        var preceding = Fraction.Zero;
        for (var i = 0; i < firstAnchorIndex; i++)
        {
            preceding += Fraction.Parse(events[i].Duration);
        }

        var start = firstAnchor.Onset - preceding;
        if (ToDouble(start) < -AnchorConflictTolerance)
        {
            start = Fraction.Zero;
        }

        var result = new List<OnsetFact>(events.Count);
        var cursor = start;

        for (var i = 0; i < events.Count; i++)
        {
            var ev = events[i];
            var confidence = Math.Min(ev.Confidence, 0.88);
            var reason = $"secondary voice propagated from x-aligned backbone voice {backboneVoice}";

            if (anchors.TryGetValue(i, out var anchor))
            {
                var current = ToDouble(cursor);
                var anchored = ToDouble(anchor.Onset);

                if (anchored >= current - AnchorConflictTolerance)
                {
                    if (anchored > current + AnchorConflictTolerance)
                    {
                        reason = $"x-aligned to backbone voice {backboneVoice}; implicit silence gap preserved before event";
                    }
                    else
                    {
                        reason = $"x-aligned to backbone voice {backboneVoice}";
                    }

                    cursor = anchor.Onset;
                    confidence = Math.Min(ev.Confidence, 0.97 - Math.Min(anchor.DistanceInSpacings, 0.5) * 0.15);
                }
                else
                {
                    reason = $"x-aligned anchor to backbone voice {backboneVoice} conflicts with accumulated durations; kept monotonic duration cursor";
                    confidence = Math.Min(ev.Confidence, 0.48);
                }
            }

            result.Add(Fact(ev, cursor, confidence, reason));
            cursor += Fraction.Parse(ev.Duration);
        }

        return result;
    }

    private static OnsetFact Fact(
        OnsetEvent ev,
        Fraction onset,
        double confidence,
        string reason)
    {
        return new OnsetFact(
            ev.MeasureNumber,
            ev.Staff,
            ev.TargetKind,
            ev.TargetId,
            ev.LocalVoice,
            onset.ToString(),
            ev.AnchorX,
            Math.Clamp(confidence, 0, 1),
            reason,
            ev.SourceShapeIds);
    }

    private static double ToDouble(Fraction value) =>
        value.Numerator / (double)value.Denominator;

    private readonly record struct NoteKey(
        int MeasureNumber,
        string ShapeId);

    private readonly record struct RestKey(
        int MeasureNumber,
        int Staff,
        string ShapeId);

    private readonly record struct VoiceKey(
        int MeasureNumber,
        int Staff,
        VoiceTargetKind TargetKind,
        string TargetId);

    private readonly record struct TargetKey(
        VoiceTargetKind TargetKind,
        string TargetId);

    private sealed record OnsetEvent(
        int MeasureNumber,
        int Staff,
        VoiceTargetKind TargetKind,
        string TargetId,
        int LocalVoice,
        double AnchorX,
        string Duration,
        double Confidence,
        IReadOnlyList<string> SourceShapeIds);

    private sealed record AnchorEvent(
        OnsetEvent Event,
        Fraction Onset,
        int Index);

    private sealed record Anchor(
        Fraction Onset,
        int BackboneIndex,
        double DistanceInSpacings);
}
