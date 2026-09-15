using SvgMusic.Canonical;

namespace SvgMusic.Semantics;

/// <summary>
/// Projects accepted semantic facts into a deliberately simple CanonicalNotation score.
///
/// This is a raw preview, not the final rhythm/voice reconstruction. Chord membership is
/// supplied explicitly by ChordPass; the builder no longer guesses chords from geometry.
/// Remaining noteheads become single-note events. Within each staff we then serialize
/// events from left to right using inferred durations, which is enough to inspect pitch,
/// accidentals, durations and chord recognition in MuseScore while onset/voice/rest
/// reconstruction is still missing.
/// </summary>
public sealed class CanonicalNotationBuilder
{
    public CanonicalNotation Build(
        SemanticDocument document,
        SemanticFacts facts,
        string? title,
        string? composer)
    {
        var clefFacts = facts
            .OfType<ClefFact>()
            .GroupBy(fact => fact.MeasureNumber)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray());
        var timeFacts = facts
            .OfType<TimeSignatureFact>()
            .ToDictionary(
                fact => fact.MeasureNumber);
        var keyFacts = facts
            .OfType<KeySignatureFact>()
            .ToDictionary(
                fact => fact.MeasureNumber);
        var noteheads = facts.OfType<NoteheadFact>().ToArray();
        var pitches = facts.OfType<PitchFact>().ToArray();
        var durations = facts.OfType<DurationFact>().ToArray();
        var stems = facts.OfType<StemAttachmentFact>().ToArray();
        var chords = facts.OfType<ChordFact>().ToArray();

        if (noteheads.Length > 0 && durations.Length == 0)
        {
            throw new InvalidDataException(
                "CanonicalNotationBuilder requires DurationPass to run before raw note projection.");
        }

        var noteheadsById = noteheads.ToDictionary(
            note => note.ShapeId,
            StringComparer.Ordinal);
        var pitchesByNotehead = pitches.ToDictionary(
            pitch => pitch.NoteheadId,
            StringComparer.Ordinal);
        var durationsByNotehead = durations.ToDictionary(
            duration => duration.NoteheadId,
            StringComparer.Ordinal);

        var currentClefs = new Dictionary<int, Clef>();
        var measures = new List<Measure>();
        var noteheadToEventId = new Dictionary<string, string>(StringComparer.Ordinal);
        var stemToEventId = new Dictionary<string, string>(StringComparer.Ordinal);
        var eventX = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var measure in document.Measures)
        {
            var clefChanges = new List<Clef>();
            var inspectPrintedClefs = measure.Number == 1 || measure.BreakBefore;

            if (inspectPrintedClefs
                && clefFacts.TryGetValue(measure.Number, out var measureClefs))
            {
                foreach (var staffNumber in new[] { 1, 2 })
                {
                    var selected = SelectSystemStartClef(
                        measure,
                        staffNumber,
                        measureClefs);

                    if (selected is null)
                    {
                        continue;
                    }

                    var clef = new Clef(
                        staffNumber,
                        selected.Sign,
                        selected.Line);

                    if (!currentClefs.TryGetValue(staffNumber, out var current)
                        || current.Sign != clef.Sign
                        || current.Line != clef.Line
                        || current.OctaveChange != clef.OctaveChange)
                    {
                        clefChanges.Add(clef);
                        currentClefs[staffNumber] = clef;

                        facts.AddTrace(
                            $"CanonicalBuilder: m{measure.Number} staff {staffNumber} "
                            + $"clef={clef.Sign}{clef.Line} from {selected.ShapeId}");
                    }
                }
            }

            TimeSignature? time = null;
            KeySignature? key = null;

            if (timeFacts.TryGetValue(measure.Number, out var timeFact))
            {
                time = new TimeSignature(
                    timeFact.Beats,
                    timeFact.BeatType);

                facts.AddTrace(
                    $"CanonicalBuilder: m{measure.Number} "
                    + $"time={timeFact.Beats}/{timeFact.BeatType}");
            }

            if (keyFacts.TryGetValue(measure.Number, out var keyFact))
            {
                key = new KeySignature(keyFact.Fifths);

                facts.AddTrace(
                    $"CanonicalBuilder: m{measure.Number} "
                    + $"key fifths={keyFact.Fifths}");
            }

            MeasureAttributes? attributes = null;

            if (measure.Number == 1
                || clefChanges.Count > 0
                || time is not null
                || key is not null)
            {
                attributes = new MeasureAttributes(
                    Time: time,
                    Key: key,
                    Staves: measure.Number == 1 ? 2 : null,
                    Clefs: clefChanges.Count > 0
                        ? clefChanges
                        : null);
            }

            var events = BuildRawNoteEvents(
                measure,
                noteheadsById,
                pitchesByNotehead,
                durationsByNotehead,
                stems,
                chords,
                facts,
                noteheadToEventId,
                stemToEventId,
                eventX);

            measures.Add(new Measure(
                measure.Number,
                events,
                attributes,
                Layout: measure.BreakBefore
                    ? new LayoutHint("system")
                    : null));
        }

        var beamRelations = BuildBeamRelations(
            facts,
            stemToEventId,
            eventX);
        var tupletRelations = BuildTupletRelations(
            facts,
            noteheadToEventId,
            eventX);

        facts.AddTrace(
            $"CanonicalBuilder: raw-note-preview events={measures.Sum(measure => measure.Events.Count)}; "
            + $"notes={measures.Sum(measure => measure.Events.Sum(ev => ev.Notes?.Count ?? 0))}; "
            + $"chord-events={measures.Sum(measure => measure.Events.Count(ev => (ev.Notes?.Count ?? 0) > 1))}; "
            + $"beam-relations={beamRelations.Count}; tuplet-relations={tupletRelations.Count}");

        return new CanonicalNotation(
            "CanonicalNotation",
            "0.3",
            new Metadata(title, composer),
            [new Part("P1", "Piano", measures)],
            new Relations(
                beamRelations,
                [],
                [],
                tupletRelations,
                [],
                [],
                [],
                []));
    }

    private static List<CanonicalEvent> BuildRawNoteEvents(
        MeasureScene measure,
        IReadOnlyDictionary<string, NoteheadFact> noteheadsById,
        IReadOnlyDictionary<string, PitchFact> pitchesByNotehead,
        IReadOnlyDictionary<string, DurationFact> durationsByNotehead,
        IReadOnlyList<StemAttachmentFact> allStems,
        IReadOnlyList<ChordFact> allChords,
        SemanticFacts facts,
        IDictionary<string, string> noteheadToEventId,
        IDictionary<string, string> stemToEventId,
        IDictionary<string, double> eventX)
    {
        var measureNoteheads = noteheadsById.Values
            .Where(note =>
                note.MeasureNumber == measure.Number
                && pitchesByNotehead.ContainsKey(note.ShapeId)
                && durationsByNotehead.ContainsKey(note.ShapeId))
            .OrderBy(note => note.CenterX)
            .ThenBy(note => note.Staff)
            .ThenBy(note => note.CenterY)
            .ToArray();

        if (measureNoteheads.Length == 0)
        {
            return [];
        }

        var availableIds = measureNoteheads
            .Select(note => note.ShapeId)
            .ToHashSet(StringComparer.Ordinal);
        var measureStems = allStems
            .Where(stem => stem.MeasureNumber == measure.Number)
            .ToArray();
        var preferredStemByNotehead = measureStems
            .SelectMany(stem => stem.AttachedNoteheadIds.Select(noteheadId => new
            {
                NoteheadId = noteheadId,
                Stem = stem
            }))
            .Where(item => availableIds.Contains(item.NoteheadId))
            .GroupBy(item => item.NoteheadId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(item => item.Stem.Confidence)
                    .ThenBy(item => item.Stem.StemShapeId, StringComparer.Ordinal)
                    .First()
                    .Stem,
                StringComparer.Ordinal);

        var drafts = new List<RawEventDraft>();
        var consumed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var chord in allChords
                     .Where(chord => chord.MeasureNumber == measure.Number)
                     .OrderBy(chord => chord.AnchorX)
                     .ThenBy(chord => chord.ChordId, StringComparer.Ordinal))
        {
            var chordNoteheads = chord.NoteheadIds
                .Where(availableIds.Contains)
                .Where(noteheadId => !consumed.Contains(noteheadId))
                .Select(noteheadId => noteheadsById[noteheadId])
                .OrderBy(note => note.Staff)
                .ThenBy(note => note.CenterY)
                .ToArray();

            if (chordNoteheads.Length < 2)
            {
                facts.AddTrace(
                    $"CanonicalBuilder: {chord.ChordId} ignored because only "
                    + $"{chordNoteheads.Length} projected notehead(s) remain");
                continue;
            }

            var stem = chord.StemShapeId is null
                ? null
                : measureStems.FirstOrDefault(candidate =>
                    candidate.StemShapeId == chord.StemShapeId);
            var eventId = chord.ChordId;
            var draft = BuildDraft(
                eventId,
                chordNoteheads,
                stem,
                pitchesByNotehead,
                durationsByNotehead,
                facts);
            drafts.Add(draft);

            if (stem is not null)
            {
                stemToEventId[stem.StemShapeId] = eventId;
            }

            foreach (var notehead in chordNoteheads)
            {
                consumed.Add(notehead.ShapeId);
                noteheadToEventId[notehead.ShapeId] = eventId;
            }
        }

        foreach (var notehead in measureNoteheads
                     .Where(note => !consumed.Contains(note.ShapeId))
                     .OrderBy(note => note.CenterX)
                     .ThenBy(note => note.Staff)
                     .ThenBy(note => note.CenterY))
        {
            preferredStemByNotehead.TryGetValue(
                notehead.ShapeId,
                out var stem);

            var eventId = stem is null
                ? $"m{measure.Number}-note-{notehead.ShapeId}"
                : $"m{measure.Number}-{stem.StemShapeId}";

            if (drafts.Any(draft => draft.Event.Id == eventId))
            {
                eventId += $"-{notehead.ShapeId}";
            }

            drafts.Add(BuildDraft(
                eventId,
                [notehead],
                stem,
                pitchesByNotehead,
                durationsByNotehead,
                facts));
            noteheadToEventId[notehead.ShapeId] = eventId;

            if (stem is not null && !stemToEventId.ContainsKey(stem.StemShapeId))
            {
                stemToEventId[stem.StemShapeId] = eventId;
            }
        }

        var assigned = new List<CanonicalEvent>(drafts.Count);

        foreach (var voiceGroup in drafts
                     .GroupBy(draft => draft.Voice)
                     .OrderBy(group => group.Key))
        {
            var cursor = Fraction.Zero;

            foreach (var draft in voiceGroup
                         .OrderBy(item => item.X)
                         .ThenBy(item => item.Event.Id, StringComparer.Ordinal))
            {
                var ev = draft.Event with
                {
                    At = cursor.ToString()
                };

                assigned.Add(ev);
                eventX[ev.Id] = draft.X;
                cursor += Fraction.Parse(ev.Duration ?? "0");
            }
        }

        facts.AddTrace(
            $"CanonicalBuilder: m{measure.Number} raw events={assigned.Count}; "
            + $"projected-noteheads={measureNoteheads.Length}; "
            + $"chord-events={drafts.Count(draft => (draft.Event.Notes?.Count ?? 0) > 1)}; "
            + $"single-events={drafts.Count(draft => (draft.Event.Notes?.Count ?? 0) == 1)}");

        return assigned
            .OrderBy(ev => eventX[ev.Id])
            .ThenBy(ev => ev.Voice ?? 1)
            .ThenBy(ev => ev.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static RawEventDraft BuildDraft(
        string eventId,
        IReadOnlyList<NoteheadFact> noteheads,
        StemAttachmentFact? stem,
        IReadOnlyDictionary<string, PitchFact> pitchesByNotehead,
        IReadOnlyDictionary<string, DurationFact> durationsByNotehead,
        SemanticFacts facts)
    {
        var durationFacts = noteheads
            .Select(note => durationsByNotehead[note.ShapeId])
            .ToArray();
        var selectedDuration = durationFacts
            .OrderByDescending(duration => duration.Confidence)
            .ThenBy(duration => duration.NoteheadId, StringComparer.Ordinal)
            .First();

        if (durationFacts.Any(duration =>
                duration.EffectiveDuration != selectedDuration.EffectiveDuration
                || duration.NoteType != selectedDuration.NoteType
                || duration.Dots != selectedDuration.Dots))
        {
            facts.AddTrace(
                $"CanonicalBuilder: {eventId} chord duration disagreement; "
                + $"using {selectedDuration.NoteType}/{selectedDuration.EffectiveDuration} "
                + $"from {selectedDuration.NoteheadId}");
        }

        var notes = noteheads
            .OrderBy(note => note.Staff)
            .ThenBy(note => note.CenterY)
            .Select(note =>
            {
                var pitch = pitchesByNotehead[note.ShapeId];
                return new CanonicalNote(
                    pitch.Pitch,
                    note.Staff,
                    ExplicitAccidental(pitch));
            })
            .ToList();

        var staffs = noteheads
            .Select(note => note.Staff)
            .Distinct()
            .OrderBy(staff => staff)
            .ToArray();
        var voice = staffs.Length == 1
            ? staffs[0]
            : 1;
        var x = noteheads.Average(note => note.CenterX);

        var notation = new EventNotation(
            NoteType: selectedDuration.NoteType,
            Dots: selectedDuration.Dots > 0
                ? selectedDuration.Dots
                : null,
            Stem: stem is null
                ? null
                : stem.Direction switch
                {
                    StemDirection.Up => "up",
                    StemDirection.Down => "down",
                    _ => null
                });

        var ev = new CanonicalEvent
        {
            Id = eventId,
            Type = noteheads.Count > 1 ? "chord" : "note",
            At = "0",
            Voice = voice,
            Duration = selectedDuration.EffectiveDuration,
            Notes = notes,
            Notation = notation
        };

        return new RawEventDraft(
            ev,
            x,
            voice,
            stem?.StemShapeId);
    }

    private static List<BeamRelation> BuildBeamRelations(
        SemanticFacts facts,
        IReadOnlyDictionary<string, string> stemToEventId,
        IReadOnlyDictionary<string, double> eventX)
    {
        var result = new List<BeamRelation>();

        foreach (var beam in facts
                     .OfType<BeamAttachmentFact>()
                     .OrderBy(beam => beam.MeasureNumber)
                     .ThenBy(beam => beam.Level)
                     .ThenBy(beam => beam.StartX))
        {
            var events = beam.AttachedStemIds
                .Where(stemToEventId.ContainsKey)
                .Select(stemId => stemToEventId[stemId])
                .Distinct(StringComparer.Ordinal)
                .OrderBy(eventId => eventX.TryGetValue(eventId, out var x) ? x : double.MaxValue)
                .ToList();

            if (beam.IsHook)
            {
                if (events.Count != 1)
                {
                    continue;
                }

                var hook = beam.LeftEndSupported && !beam.RightEndSupported
                    ? "forward"
                    : !beam.LeftEndSupported && beam.RightEndSupported
                        ? "backward"
                        : null;

                if (hook is null)
                {
                    continue;
                }

                result.Add(new BeamRelation(
                    $"beam-{beam.BeamShapeId}",
                    beam.Level,
                    events,
                    hook));
                continue;
            }

            if (events.Count < 2)
            {
                continue;
            }

            result.Add(new BeamRelation(
                $"beam-{beam.BeamShapeId}",
                beam.Level,
                events));
        }

        return result;
    }

    private static List<TupletRelation> BuildTupletRelations(
        SemanticFacts facts,
        IReadOnlyDictionary<string, string> noteheadToEventId,
        IReadOnlyDictionary<string, double> eventX)
    {
        var result = new List<TupletRelation>();

        foreach (var tuplet in facts
                     .OfType<TupletFact>()
                     .OrderBy(tuplet => tuplet.MeasureNumber)
                     .ThenBy(tuplet => tuplet.TupletShapeId, StringComparer.Ordinal))
        {
            var events = tuplet.AttachedNoteheadIds
                .Where(noteheadToEventId.ContainsKey)
                .Select(noteheadId => noteheadToEventId[noteheadId])
                .Distinct(StringComparer.Ordinal)
                .OrderBy(eventId => eventX.TryGetValue(eventId, out var x) ? x : double.MaxValue)
                .ToList();

            if (events.Count < 2)
            {
                continue;
            }

            result.Add(new TupletRelation(
                $"tuplet-{tuplet.TupletShapeId}",
                events,
                tuplet.ActualNotes,
                tuplet.NormalNotes));
        }

        return result;
    }

    private static Accidental? ExplicitAccidental(PitchFact pitch)
    {
        if (!pitch.IsAccidentalExplicit || pitch.ActiveAccidentalKind is null)
        {
            return null;
        }

        var type = pitch.ActiveAccidentalKind.Value switch
        {
            AccidentalKind.Flat => "flat",
            AccidentalKind.Sharp => "sharp",
            AccidentalKind.Natural => "natural",
            AccidentalKind.DoubleFlat => "flat-flat",
            AccidentalKind.DoubleSharp => "double-sharp",
            _ => throw new ArgumentOutOfRangeException()
        };

        return new Accidental(type);
    }

    private static ClefFact? SelectSystemStartClef(
        MeasureScene measure,
        int staffNumber,
        IReadOnlyList<ClefFact> facts)
    {
        var width = measure.XEnd - measure.XStart;
        var systemStartLimit = measure.XStart + width * 0.35;

        return facts
            .Where(fact =>
                fact.Staff == staffNumber
                && fact.X <= systemStartLimit)
            .OrderBy(fact => fact.X)
            .ThenByDescending(fact => fact.Confidence)
            .FirstOrDefault();
    }

    private sealed record RawEventDraft(
        CanonicalEvent Event,
        double X,
        int Voice,
        string? StemShapeId);
}
