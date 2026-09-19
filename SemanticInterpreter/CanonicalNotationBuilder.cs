using SvgMusic.Canonical;

namespace SvgMusic.Semantics;

/// <summary>
/// Projects accepted semantic facts into CanonicalNotation.
///
/// Chord membership, local voice identity and rhythmic onset are supplied explicitly
/// by ChordPass, VoicePass and OnsetPass. Geometry is no longer re-interpreted here.
/// Rest augmentation dots are also projected as real dotted rest durations.
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
            .Where(fact => !fact.IsInherited)
            .ToDictionary(
                fact => fact.MeasureNumber);
        var keyFacts = facts
            .OfType<KeySignatureFact>()
            .Where(fact => !fact.IsInherited)
            .ToDictionary(
                fact => fact.MeasureNumber);
        var noteheads = facts.OfType<NoteheadFact>().ToArray();
        var pitches = facts.OfType<PitchFact>().ToArray();
        var durations = facts.OfType<DurationFact>().ToArray();
        var stems = facts.OfType<StemAttachmentFact>().ToArray();
        var chords = facts.OfType<ChordFact>().ToArray();
        var rests = facts.OfType<RestFact>().ToArray();
        var restDots = facts.OfType<RestDotAttachmentFact>().ToArray();
        var voices = facts.OfType<VoiceFact>().ToArray();
        var onsets = facts.OfType<OnsetFact>().ToArray();
        var graceNoteheadIds = facts
            .OfType<GraceNoteFact>()
            .Select(grace => grace.NoteheadId)
            .ToHashSet(StringComparer.Ordinal);

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
                rests,
                restDots,
                voices,
                onsets,
                graceNoteheadIds,
                facts,
                noteheadToEventId,
                stemToEventId,
                eventX);

            events.AddRange(BuildClassifiedDynamicEvents(
                measure.Number,
                facts));
            events.AddRange(BuildTextDirectionEvents(
                measure.Number,
                facts));

            measures.Add(new Measure(
                measure.Number,
                events,
                attributes,
                RightBarline: measure.RightBarline,
                Layout: measure.BreakBefore
                    ? new LayoutHint("system")
                    : null));
        }

        var beamRelations = BuildBeamRelations(
            facts,
            stemToEventId,
            eventX);
        var tieRelations = TieRelationProjector.Build(
            facts,
            noteheadToEventId);
        var slurRelations = BuildSlurRelations(
            facts,
            noteheadToEventId);
        var tupletRelations = BuildTupletRelations(
            facts,
            noteheadToEventId,
            eventX);
        var arpeggioRelations = BuildArpeggioRelations(
            facts,
            noteheadToEventId,
            eventX);
        var hairpinRelations = BuildHairpinRelations(facts);
        var pedalRelations = BuildPedalRelations(facts);
        var octaveShiftRelations = BuildOctaveShiftRelations(facts);

        facts.AddTrace(
            $"CanonicalBuilder: events={measures.Sum(measure => measure.Events.Count)}; "
            + $"notes={measures.Sum(measure => measure.Events.Sum(ev => ev.Notes?.Count ?? 0))}; "
            + $"rests={measures.Sum(measure => measure.Events.Count(ev => ev.Type == "rest"))}; "
            + $"chord-events={measures.Sum(measure => measure.Events.Count(ev => (ev.Notes?.Count ?? 0) > 1))}; "
            + $"beam-relations={beamRelations.Count}; tie-relations={tieRelations.Count}; "
            + $"slur-relations={slurRelations.Count}; tuplet-relations={tupletRelations.Count}; "
            + $"arpeggios={arpeggioRelations.Count}; "
            + $"hairpins={hairpinRelations.Count}; pedals={pedalRelations.Count}; "
            + $"octave-shifts={octaveShiftRelations.Count}; "
            + $"grace-events={measures.Sum(measure => measure.Events.Count(ev => ev.Grace == true))}; "
            + $"onsets={onsets.Length}; dotted-rests={restDots.Length}");

        var detectedTitle = BestText(facts, SemanticTextRole.Title);
        var detectedSubtitle = BestText(facts, SemanticTextRole.Subtitle);
        var detectedComposer = BestText(facts, SemanticTextRole.Composer);

        return new CanonicalNotation(
            "CanonicalNotation",
            "0.4",
            new Metadata(
                title ?? detectedTitle,
                composer ?? detectedComposer,
                detectedSubtitle),
            [new Part("P1", "Piano", measures)],
            new Relations(
                beamRelations,
                tieRelations,
                slurRelations,
                tupletRelations,
                arpeggioRelations,
                hairpinRelations,
                pedalRelations,
                octaveShiftRelations));
    }

    private static List<CanonicalEvent> BuildRawNoteEvents(
        MeasureScene measure,
        IReadOnlyDictionary<string, NoteheadFact> noteheadsById,
        IReadOnlyDictionary<string, PitchFact> pitchesByNotehead,
        IReadOnlyDictionary<string, DurationFact> durationsByNotehead,
        IReadOnlyList<StemAttachmentFact> allStems,
        IReadOnlyList<ChordFact> allChords,
        IReadOnlyList<RestFact> allRests,
        IReadOnlyList<RestDotAttachmentFact> allRestDots,
        IReadOnlyList<VoiceFact> allVoices,
        IReadOnlyList<OnsetFact> allOnsets,
        IReadOnlySet<string> graceNoteheadIds,
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
        var measureRests = allRests
            .Where(rest => rest.MeasureNumber == measure.Number)
            .OrderBy(rest => rest.CenterX)
            .ThenBy(rest => rest.Staff)
            .ThenBy(rest => rest.CenterY)
            .ThenBy(rest => rest.ShapeId, StringComparer.Ordinal)
            .ToArray();
        var measureVoices = allVoices
            .Where(voice => voice.MeasureNumber == measure.Number)
            .GroupBy(voice => new VoiceKey(
                voice.TargetKind,
                voice.TargetId))
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(voice => voice.Confidence)
                    .First());
        var measureOnsets = allOnsets
            .Where(onset => onset.MeasureNumber == measure.Number)
            .GroupBy(onset => new VoiceKey(
                onset.TargetKind,
                onset.TargetId))
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(onset => onset.Confidence)
                    .First());
        var measureRestDots = allRestDots
            .Where(dot => dot.MeasureNumber == measure.Number)
            .GroupBy(dot => new RestKey(dot.Staff, dot.TargetRestShapeId))
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(dot => dot.Confidence)
                    .First());

        if (measureNoteheads.Length == 0 && measureRests.Length == 0)
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
            var fallbackStaff = chord.Staffs.Count > 0
                ? chord.Staffs[0]
                : chordNoteheads[0].Staff;
            var voice = ResolveCanonicalVoice(
                measureVoices,
                VoiceTargetKind.Chord,
                chord.ChordId,
                fallbackStaff);
            var draft = BuildDraft(
                eventId,
                chordNoteheads,
                stem,
                voice,
                VoiceTargetKind.Chord,
                chord.ChordId,
                pitchesByNotehead,
                durationsByNotehead,
                graceNoteheadIds,
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

            var voice = ResolveCanonicalVoice(
                measureVoices,
                VoiceTargetKind.Notehead,
                notehead.ShapeId,
                notehead.Staff);
            drafts.Add(BuildDraft(
                eventId,
                [notehead],
                stem,
                voice,
                VoiceTargetKind.Notehead,
                notehead.ShapeId,
                pitchesByNotehead,
                durationsByNotehead,
                graceNoteheadIds,
                facts));
            noteheadToEventId[notehead.ShapeId] = eventId;

            if (stem is not null && !stemToEventId.ContainsKey(stem.StemShapeId))
            {
                stemToEventId[stem.StemShapeId] = eventId;
            }
        }

        foreach (var rest in measureRests)
        {
            var voice = ResolveCanonicalVoice(
                measureVoices,
                VoiceTargetKind.Rest,
                rest.ShapeId,
                rest.Staff);
            var eventId = $"m{measure.Number}-rest-{rest.ShapeId}";
            var dotCount = measureRestDots.TryGetValue(
                    new RestKey(rest.Staff, rest.ShapeId),
                    out var restDot)
                ? restDot.Count
                : 0;
            var duration = DurationMath.ApplyDots(
                rest.Duration,
                dotCount);
            var restEvent = new CanonicalEvent
            {
                Id = eventId,
                Type = "rest",
                At = "0",
                Staff = rest.Staff,
                Voice = voice,
                Duration = duration,
                Notation = new EventNotation(
                    NoteType: rest.NoteType,
                    Dots: dotCount > 0 ? dotCount : null)
            };

            drafts.Add(new RawEventDraft(
                restEvent,
                rest.CenterX,
                voice,
                VoiceTargetKind.Rest,
                rest.ShapeId,
                null));
        }

        var assigned = new List<CanonicalEvent>(drafts.Count);

        foreach (var voiceGroup in drafts
                     .GroupBy(draft => draft.Voice)
                     .OrderBy(group => group.Key))
        {
            var fallbackCursor = Fraction.Zero;

            foreach (var draft in voiceGroup
                         .OrderBy(item => item.X)
                         .ThenBy(item => item.Event.Id, StringComparer.Ordinal))
            {
                var onsetKey = new VoiceKey(
                    draft.TargetKind,
                    draft.TargetId);
                var at = measureOnsets.TryGetValue(onsetKey, out var onset)
                    ? Fraction.Parse(onset.At)
                    : fallbackCursor;
                var ev = draft.Event with
                {
                    At = at.ToString()
                };

                assigned.Add(ev);
                eventX[ev.Id] = draft.X;

                var end = at + Fraction.Parse(ev.Duration ?? "0");
                fallbackCursor = Max(
                    fallbackCursor,
                    end);
            }
        }

        facts.AddTrace(
            $"CanonicalBuilder: m{measure.Number} events={assigned.Count}; "
            + $"projected-noteheads={measureNoteheads.Length}; "
            + $"rests={measureRests.Length}; "
            + $"dotted-rests={measureRests.Count(rest => measureRestDots.ContainsKey(new RestKey(rest.Staff, rest.ShapeId)))}; "
            + $"chord-events={drafts.Count(draft => (draft.Event.Notes?.Count ?? 0) > 1)}; "
            + $"single-events={drafts.Count(draft => (draft.Event.Notes?.Count ?? 0) == 1)}; "
            + $"semantic-onsets={drafts.Count(draft => measureOnsets.ContainsKey(new VoiceKey(draft.TargetKind, draft.TargetId)))}; "
            + $"voices=[{string.Join(',', drafts.Select(draft => draft.Voice).Distinct().OrderBy(voice => voice))}]");

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
        int voice,
        VoiceTargetKind targetKind,
        string targetId,
        IReadOnlyDictionary<string, PitchFact> pitchesByNotehead,
        IReadOnlyDictionary<string, DurationFact> durationsByNotehead,
        IReadOnlySet<string> graceNoteheadIds,
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
                var fingerings = facts
                    .OfType<TextFact>()
                    .Where(text =>
                        text.Role == SemanticTextRole.Fingering
                        && string.Equals(
                            text.AnchorShapeId,
                            note.ShapeId,
                            StringComparison.Ordinal))
                    .OrderByDescending(text => text.Confidence)
                    .Select(text => new TechnicalMark(
                        "fingering",
                        text.Text,
                        text.Placement ?? "above"))
                    .Distinct()
                    .ToList();

                return new CanonicalNote(
                    pitch.Pitch,
                    note.Staff,
                    ExplicitAccidental(pitch),
                    fingerings.Count > 0
                        ? fingerings
                        : null);
            })
            .ToList();

        var x = noteheads.Average(note => note.CenterX);
        var graceCount = noteheads.Count(note => graceNoteheadIds.Contains(note.ShapeId));
        var isGrace = graceCount > 0 && graceCount == noteheads.Count;
        if (graceCount > 0 && !isGrace)
        {
            facts.AddTrace(
                $"CanonicalBuilder: {eventId} mixes grace and metric noteheads; "
                + "keeping the event metric");
        }

        var classifiedMarks = facts
            .OfType<ClassifiedNotationMarkFact>()
            .Where(mark => noteheads.Any(note =>
                note.ShapeId == mark.TargetNoteheadId))
            .OrderBy(mark => mark.CenterX)
            .ThenBy(mark => mark.ShapeId, StringComparer.Ordinal)
            .ToArray();

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
                },
            Articulations: BuildClassifiedMarks(
                classifiedMarks,
                ClassifiedNotationFamily.Articulation),
            Ornaments: BuildClassifiedMarks(
                classifiedMarks,
                ClassifiedNotationFamily.Ornament),
            Fermatas: BuildClassifiedMarks(
                classifiedMarks,
                ClassifiedNotationFamily.Fermata));

        var ev = new CanonicalEvent
        {
            Id = eventId,
            Type = "chord",
            At = "0",
            Voice = voice,
            Duration = isGrace ? null : selectedDuration.EffectiveDuration,
            Grace = isGrace ? true : null,
            Notes = notes,
            Notation = notation
        };

        return new RawEventDraft(
            ev,
            x,
            voice,
            targetKind,
            targetId,
            stem?.StemShapeId);
    }

    private static List<NotationMark>? BuildClassifiedMarks(
        IReadOnlyList<ClassifiedNotationMarkFact> marks,
        ClassifiedNotationFamily family)
    {
        var result = marks
            .Where(mark => mark.Family == family)
            .Select(mark => new NotationMark(
                mark.Type,
                Placement: mark.Placement))
            .Distinct()
            .ToList();

        return result.Count > 0
            ? result
            : null;
    }

    private static IEnumerable<CanonicalEvent> BuildTextDirectionEvents(
        int measureNumber,
        SemanticFacts facts)
    {
        var metronomeByObservation = facts
            .OfType<MetronomeMarkFact>()
            .Where(mark => mark.MeasureNumber == measureNumber)
            .GroupBy(mark => mark.ObservationId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(mark => mark.Confidence)
                    .First(),
                StringComparer.Ordinal);

        foreach (var fact in facts
                     .OfType<TextFact>()
                     .Where(fact =>
                         fact.MeasureNumber == measureNumber
                         && fact.Role is SemanticTextRole.Instruction or SemanticTextRole.Tempo)
                     .OrderBy(fact => Fraction.Parse(fact.At ?? "0").Numerator
                         / (double)Fraction.Parse(fact.At ?? "0").Denominator)
                     .ThenBy(fact => fact.Bounds.MinX)
                     .ThenBy(fact => fact.ObservationId, StringComparer.Ordinal))
        {
            if (fact.Role == SemanticTextRole.Tempo
                && metronomeByObservation.TryGetValue(
                    fact.ObservationId,
                    out var mark))
            {
                if (!string.IsNullOrWhiteSpace(mark.InstructionText))
                {
                    yield return new CanonicalEvent
                    {
                        Id = $"m{measureNumber}-text-{fact.ObservationId}",
                        Type = "text",
                        At = fact.At ?? "0",
                        Staff = fact.Staff ?? 1,
                        Text = mark.InstructionText,
                        TextRole = fact.Role.ToString(),
                        Placement = fact.Placement
                    };
                }

                yield return new CanonicalEvent
                {
                    Id = $"m{measureNumber}-tempo-{fact.ObservationId}",
                    Type = "tempo",
                    At = mark.At,
                    Staff = mark.Staff,
                    Placement = fact.Placement ?? "above",
                    BeatUnit = mark.BeatUnit,
                    Bpm = mark.Bpm
                };
                continue;
            }

            yield return new CanonicalEvent
            {
                Id = $"m{measureNumber}-text-{fact.ObservationId}",
                Type = "text",
                At = fact.At ?? "0",
                Staff = fact.Staff ?? 1,
                Text = fact.Text,
                TextRole = fact.Role.ToString(),
                Placement = fact.Placement
            };
        }
    }

    private static string? BestText(
        SemanticFacts facts,
        SemanticTextRole role)
    {
        return facts
            .OfType<TextFact>()
            .Where(fact => fact.Role == role)
            .OrderByDescending(fact => fact.Confidence)
            .ThenBy(fact => fact.Bounds.MinY)
            .ThenBy(fact => fact.Bounds.MinX)
            .Select(fact => fact.Text)
            .FirstOrDefault();
    }

    private static IEnumerable<CanonicalEvent> BuildClassifiedDynamicEvents(
        int measureNumber,
        SemanticFacts facts)
    {
        return facts
            .OfType<DynamicDirectionFact>()
            .Where(fact => fact.MeasureNumber == measureNumber)
            .OrderBy(fact => Fraction.Parse(fact.At).Numerator
                / (double)Fraction.Parse(fact.At).Denominator)
            .ThenBy(fact => fact.CenterX)
            .ThenBy(fact => fact.ShapeId, StringComparer.Ordinal)
            .Select(fact => new CanonicalEvent
            {
                Id = $"m{measureNumber}-dynamic-{fact.ShapeId}",
                Type = "dynamic",
                At = fact.At,
                Staff = fact.Staff,
                Value = fact.Value,
                Placement = fact.Placement
            });
    }

    private static int ResolveCanonicalVoice(
        IReadOnlyDictionary<VoiceKey, VoiceFact> measureVoices,
        VoiceTargetKind targetKind,
        string targetId,
        int fallbackStaff)
    {
        if (measureVoices.TryGetValue(
                new VoiceKey(targetKind, targetId),
                out var voice))
        {
            return CanonicalVoice(
                voice.Staff,
                voice.LocalVoice);
        }

        return CanonicalVoice(
            fallbackStaff,
            1);
    }

    private static int CanonicalVoice(
        int staff,
        int localVoice)
    {
        var normalizedStaff = Math.Max(1, staff);
        var normalizedVoice = Math.Clamp(localVoice, 1, 4);
        return (normalizedStaff - 1) * 4 + normalizedVoice;
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

    private static List<SlurRelation> BuildSlurRelations(
        SemanticFacts facts,
        IReadOnlyDictionary<string, string> noteheadToEventId)
    {
        var result = new List<SlurRelation>();

        foreach (var slur in facts
                     .OfType<SlurFact>()
                     .OrderBy(slur => slur.StartMeasureNumber)
                     .ThenBy(slur => slur.CurveShapeId, StringComparer.Ordinal))
        {
            if (!noteheadToEventId.TryGetValue(slur.FromNoteheadId, out var fromEvent)
                || !noteheadToEventId.TryGetValue(slur.ToNoteheadId, out var toEvent)
                || string.Equals(fromEvent, toEvent, StringComparison.Ordinal))
            {
                continue;
            }

            result.Add(new SlurRelation(
                $"slur-{slur.CurveShapeId}",
                fromEvent,
                toEvent,
                slur.Placement));
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

    private static List<ArpeggioRelation> BuildArpeggioRelations(
        SemanticFacts facts,
        IReadOnlyDictionary<string, string> noteheadToEventId,
        IReadOnlyDictionary<string, double> eventX)
    {
        var result = new List<ArpeggioRelation>();

        foreach (var arpeggio in facts
                     .OfType<ArpeggioFact>()
                     .OrderBy(fact => fact.MeasureNumber)
                     .ThenBy(fact => fact.AnchorX)
                     .ThenBy(fact => fact.ZigZagShapeId, StringComparer.Ordinal))
        {
            var events = arpeggio.NoteheadIds
                .Where(noteheadToEventId.ContainsKey)
                .Select(noteheadId => noteheadToEventId[noteheadId])
                .Distinct(StringComparer.Ordinal)
                .OrderBy(eventId => eventX.TryGetValue(eventId, out var x)
                    ? x
                    : double.MaxValue)
                .ThenBy(eventId => eventId, StringComparer.Ordinal)
                .ToList();

            if (events.Count == 0)
            {
                continue;
            }

            result.Add(new ArpeggioRelation(
                $"arpeggio-{arpeggio.ZigZagShapeId}",
                events,
                arpeggio.Direction));
        }

        return result;
    }

    private static List<SpanRelation> BuildHairpinRelations(
        SemanticFacts facts)
    {
        return facts
            .OfType<HairpinFact>()
            .OrderBy(fact => fact.StartMeasureNumber)
            .ThenBy(fact => Fraction.Parse(fact.StartAt).Numerator / (double)Fraction.Parse(fact.StartAt).Denominator)
            .ThenBy(fact => fact.Staff)
            .ThenBy(fact => fact.ShapeId, StringComparer.Ordinal)
            .Select((fact, index) => new SpanRelation
            {
                Id = $"hairpin-{index + 1}",
                Kind = "hairpin",
                From = new TimeAnchor(
                    fact.StartMeasureNumber,
                    fact.StartAt,
                    fact.Staff),
                To = new TimeAnchor(
                    fact.EndMeasureNumber,
                    fact.EndAt,
                    fact.Staff),
                Type = fact.Type,
                Placement = fact.Placement
            })
            .ToList();
    }

    private static List<SpanRelation> BuildPedalRelations(
        SemanticFacts facts)
    {
        return facts
            .OfType<PedalFact>()
            .OrderBy(fact => fact.StartMeasureNumber)
            .ThenBy(fact => Fraction.Parse(fact.StartAt).Numerator / (double)Fraction.Parse(fact.StartAt).Denominator)
            .ThenBy(fact => fact.Staff)
            .ThenBy(fact => fact.BracketId, StringComparer.Ordinal)
            .Select((fact, index) => new SpanRelation
            {
                Id = $"pedal-{index + 1}",
                Kind = "pedal",
                From = new TimeAnchor(
                    fact.StartMeasureNumber,
                    fact.StartAt,
                    fact.Staff),
                To = new TimeAnchor(
                    fact.EndMeasureNumber,
                    fact.EndAt,
                    fact.Staff),
                Line = fact.Line,
                StartMark = fact.StartMark,
                Placement = fact.Placement
            })
            .ToList();
    }

    private static List<SpanRelation> BuildOctaveShiftRelations(
        SemanticFacts facts)
    {
        return facts
            .OfType<OttavaFact>()
            .OrderBy(fact => fact.StartMeasureNumber)
            .ThenBy(fact => fact.StartX)
            .ThenBy(fact => fact.BracketId, StringComparer.Ordinal)
            .Select((fact, index) => new SpanRelation
            {
                Id = $"octaveShift-{index + 1}",
                Kind = "octaveShift",
                From = new TimeAnchor(
                    fact.StartMeasureNumber,
                    fact.StartAt,
                    fact.Staff),
                To = new TimeAnchor(
                    fact.EndMeasureNumber,
                    fact.EndAt,
                    fact.Staff),
                Direction = fact.Direction,
                Size = fact.Size,
                Placement = fact.Placement
            })
            .ToList();
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

    private static Fraction Max(
        Fraction first,
        Fraction second)
    {
        var firstValue = first.Numerator / (double)first.Denominator;
        var secondValue = second.Numerator / (double)second.Denominator;
        return firstValue >= secondValue
            ? first
            : second;
    }

    private readonly record struct VoiceKey(
        VoiceTargetKind TargetKind,
        string TargetId);

    private readonly record struct RestKey(
        int Staff,
        string ShapeId);

    private sealed record RawEventDraft(
        CanonicalEvent Event,
        double X,
        int Voice,
        VoiceTargetKind TargetKind,
        string TargetId,
        string? StemShapeId);
}
