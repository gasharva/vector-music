namespace SvgMusic.Semantics;

public enum VoiceTargetKind
{
    Notehead,
    Chord,
    Rest
}

public sealed record VoiceFact(
    int MeasureNumber,
    int Staff,
    VoiceTargetKind TargetKind,
    string TargetId,
    int LocalVoice,
    bool IsPolyphonicStaff,
    double AnchorX,
    double AnchorY,
    double Confidence,
    string Reason,
    IReadOnlyList<string> SourceShapeIds)
    : SemanticFact(
        "VoicePass",
        Reason,
        SourceShapeIds);

public sealed record VoiceAnalysisResult(
    IReadOnlyList<VoiceFact> Voices,
    IReadOnlySet<(int MeasureNumber, int Staff)> PolyphonicStaffs)
{
    public int Voice1Count => Voices.Count(voice => voice.LocalVoice == 1);
    public int Voice2Count => Voices.Count(voice => voice.LocalVoice == 2);
}

/// <summary>
/// Assigns local voices inside one staff. The current pass deliberately supports
/// at most two voices and keeps the semantic numbering local to the staff:
/// voice 1 is the upper/up-stem voice, voice 2 is the lower/down-stem voice.
///
/// Stem direction becomes a voice signal only after independent evidence says the
/// measure/staff is polyphonic. In a monophonic passage stems may legitimately flip
/// direction with pitch, so every event remains local voice 1.
/// </summary>
public sealed class VoicePass : ISemanticPass
{
    private const double OppositeStemAlignmentInSpacings = 0.72;
    private const double RestPairAlignmentInSpacings = 0.68;
    private const double RestPairMinimumSeparationInSpacings = 0.52;
    private const double NoteRestAlignmentInSpacings = 0.58;
    private const double DisplacedRestThresholdInSpacings = 0.52;
    private const double VerticalOutsideMarginInSpacings = 0.12;
    private const double StemlessAlignmentInSpacings = 0.72;
    private const double AmbiguousRestCenterBandInSpacings = 0.16;

    public string Name => nameof(VoicePass);

    public VoiceAnalysisResult? LastAnalysis { get; private set; }

    public void Run(
        SemanticDocument document,
        SemanticFacts facts)
    {
        var noteheads = facts.OfType<NoteheadFact>().ToArray();
        var stems = facts.OfType<StemAttachmentFact>().ToArray();
        var chords = facts.OfType<ChordFact>().ToArray();
        var rests = facts.OfType<RestFact>().ToArray();

        if (noteheads.Length == 0 && rests.Length == 0)
        {
            LastAnalysis = new VoiceAnalysisResult(
                Array.Empty<VoiceFact>(),
                new HashSet<(int, int)>());
            return;
        }

        var events = BuildEvents(
            noteheads,
            stems,
            chords,
            rests);
        var voices = new List<VoiceFact>();
        var polyphonicStaffs = new HashSet<(int MeasureNumber, int Staff)>();

        foreach (var measure in document.Measures)
        {
            foreach (var staffNumber in new[] { 1, 2 })
            {
                var staff = staffNumber == 1
                    ? measure.Upper
                    : measure.Lower;
                var group = events
                    .Where(item =>
                        item.MeasureNumber == measure.Number
                        && item.Staff == staffNumber)
                    .OrderBy(item => item.AnchorX)
                    .ThenBy(item => item.AnchorY)
                    .ThenBy(item => item.TargetId, StringComparer.Ordinal)
                    .ToArray();

                if (group.Length == 0)
                {
                    continue;
                }

                var spacing = Math.Max(staff.LineSpacing, 0.001);
                var staffCenterY = staff.StaffBounds.CenterY;
                var isPolyphonic = DetectPolyphony(
                    group,
                    spacing,
                    staffCenterY);

                if (isPolyphonic)
                {
                    polyphonicStaffs.Add((measure.Number, staffNumber));
                    voices.AddRange(AssignPolyphonic(
                        group,
                        spacing,
                        staffCenterY));
                }
                else
                {
                    voices.AddRange(group.Select(item => CreateVoiceFact(
                        item,
                        1,
                        false,
                        0.99,
                        "staff has no simultaneous two-voice evidence; stem direction is ignored in monophonic context")));
                }
            }
        }

        foreach (var voice in voices)
        {
            facts.Add(voice);
        }

        LastAnalysis = new VoiceAnalysisResult(
            voices,
            polyphonicStaffs);

        facts.AddTrace(
            $"VoicePass: assignments={voices.Count}; "
            + $"polyphonic-staff-measures={polyphonicStaffs.Count}; "
            + $"voice1={voices.Count(voice => voice.LocalVoice == 1)}; "
            + $"voice2={voices.Count(voice => voice.LocalVoice == 2)}; "
            + $"low-confidence={voices.Count(voice => voice.Confidence < 0.70)}");
    }

    private static IReadOnlyList<VoiceEvent> BuildEvents(
        IReadOnlyList<NoteheadFact> noteheads,
        IReadOnlyList<StemAttachmentFact> stems,
        IReadOnlyList<ChordFact> chords,
        IReadOnlyList<RestFact> rests)
    {
        var noteheadsByKey = noteheads.ToDictionary(
            notehead => new NoteKey(
                notehead.MeasureNumber,
                notehead.ShapeId));
        var stemsByKey = stems.ToDictionary(
            stem => new StemKey(
                stem.MeasureNumber,
                stem.StemShapeId));
        var preferredStemByNote = stems
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
                    .First()
                    .Stem);

        var consumed = new HashSet<NoteKey>();
        var result = new List<VoiceEvent>();

        foreach (var chord in chords)
        {
            var members = chord.NoteheadIds
                .Select(id => new NoteKey(chord.MeasureNumber, id))
                .Where(noteheadsByKey.ContainsKey)
                .Select(key => noteheadsByKey[key])
                .ToArray();

            if (members.Length == 0)
            {
                continue;
            }

            foreach (var member in members)
            {
                consumed.Add(new NoteKey(
                    member.MeasureNumber,
                    member.ShapeId));
            }

            StemAttachmentFact? stem = null;
            if (chord.StemShapeId is not null)
            {
                stemsByKey.TryGetValue(
                    new StemKey(chord.MeasureNumber, chord.StemShapeId),
                    out stem);
            }

            var homeStaff = ChooseHomeStaff(
                chord,
                members);
            var homeHeads = members
                .Where(notehead => notehead.Staff == homeStaff)
                .ToArray();
            if (homeHeads.Length == 0)
            {
                homeHeads = members;
            }

            result.Add(new VoiceEvent(
                chord.MeasureNumber,
                homeStaff,
                VoiceTargetKind.Chord,
                chord.ChordId,
                chord.AnchorX,
                homeHeads.Min(notehead => notehead.CenterY),
                homeHeads.Max(notehead => notehead.CenterY),
                stem?.Direction,
                stem is null ? null : (stem.StartX + stem.EndX) / 2.0,
                chord.Confidence,
                chord.SourceShapeIds));
        }

        foreach (var notehead in noteheads)
        {
            var key = new NoteKey(
                notehead.MeasureNumber,
                notehead.ShapeId);
            if (consumed.Contains(key))
            {
                continue;
            }

            preferredStemByNote.TryGetValue(
                key,
                out var stem);

            result.Add(new VoiceEvent(
                notehead.MeasureNumber,
                notehead.Staff,
                VoiceTargetKind.Notehead,
                notehead.ShapeId,
                notehead.CenterX,
                notehead.CenterY,
                notehead.CenterY,
                stem?.Direction,
                stem is null ? null : (stem.StartX + stem.EndX) / 2.0,
                Math.Min(
                    notehead.Confidence,
                    stem?.Confidence ?? 1.0),
                stem is null
                    ? notehead.SourceShapeIds
                    : notehead.SourceShapeIds
                        .Concat(new[] { stem.StemShapeId })
                        .Distinct(StringComparer.Ordinal)
                        .ToArray()));
        }

        foreach (var rest in rests)
        {
            result.Add(new VoiceEvent(
                rest.MeasureNumber,
                rest.Staff,
                VoiceTargetKind.Rest,
                rest.ShapeId,
                rest.CenterX,
                rest.CenterY,
                rest.CenterY,
                null,
                null,
                rest.Confidence,
                rest.SourceShapeIds));
        }

        return result;
    }

    private static int ChooseHomeStaff(
        ChordFact chord,
        IReadOnlyList<NoteheadFact> members)
    {
        if (chord.Staffs.Count == 1)
        {
            return chord.Staffs[0];
        }

        return members
            .GroupBy(notehead => notehead.Staff)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key)
            .Select(group => group.Key)
            .First();
    }

    private static bool DetectPolyphony(
        IReadOnlyList<VoiceEvent> events,
        double spacing,
        double staffCenterY)
    {
        var pitched = events
            .Where(item => item.TargetKind != VoiceTargetKind.Rest)
            .ToArray();
        var rests = events
            .Where(item => item.TargetKind == VoiceTargetKind.Rest)
            .ToArray();

        for (var i = 0; i < pitched.Length; i++)
        {
            if (pitched[i].StemDirection is not (StemDirection.Up or StemDirection.Down))
            {
                continue;
            }

            for (var j = i + 1; j < pitched.Length; j++)
            {
                if (pitched[j].StemDirection is not (StemDirection.Up or StemDirection.Down)
                    || pitched[i].StemDirection == pitched[j].StemDirection)
                {
                    continue;
                }

                var noteheadAligned = Math.Abs(pitched[i].AnchorX - pitched[j].AnchorX)
                    <= spacing * OppositeStemAlignmentInSpacings;
                var stemAxesAligned = pitched[i].StemAnchorX is not null
                    && pitched[j].StemAnchorX is not null
                    && Math.Abs(pitched[i].StemAnchorX!.Value - pitched[j].StemAnchorX!.Value)
                        <= spacing * OppositeStemAlignmentInSpacings;

                if (noteheadAligned || stemAxesAligned)
                {
                    return true;
                }
            }
        }

        for (var i = 0; i < rests.Length; i++)
        {
            for (var j = i + 1; j < rests.Length; j++)
            {
                if (Math.Abs(rests[i].AnchorX - rests[j].AnchorX)
                        <= spacing * RestPairAlignmentInSpacings
                    && Math.Abs(rests[i].AnchorY - rests[j].AnchorY)
                        >= spacing * RestPairMinimumSeparationInSpacings)
                {
                    return true;
                }
            }
        }

        foreach (var rest in rests)
        {
            if (Math.Abs(rest.AnchorY - staffCenterY)
                < spacing * DisplacedRestThresholdInSpacings)
            {
                continue;
            }

            foreach (var note in pitched)
            {
                if (Math.Abs(rest.AnchorX - note.AnchorX)
                    > spacing * NoteRestAlignmentInSpacings)
                {
                    continue;
                }

                var margin = spacing * VerticalOutsideMarginInSpacings;
                if (rest.AnchorY < note.TopY - margin
                    || rest.AnchorY > note.BottomY + margin)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static IReadOnlyList<VoiceFact> AssignPolyphonic(
        IReadOnlyList<VoiceEvent> events,
        double spacing,
        double staffCenterY)
    {
        var result = new Dictionary<TargetKey, VoiceFact>();
        var pitched = events
            .Where(item => item.TargetKind != VoiceTargetKind.Rest)
            .ToArray();
        var rests = events
            .Where(item => item.TargetKind == VoiceTargetKind.Rest)
            .ToArray();

        foreach (var note in pitched)
        {
            if (note.StemDirection == StemDirection.Up)
            {
                result[Key(note)] = CreateVoiceFact(
                    note,
                    1,
                    true,
                    0.99,
                    "polyphonic staff: up-stem pitched event belongs to local voice 1");
            }
            else if (note.StemDirection == StemDirection.Down)
            {
                result[Key(note)] = CreateVoiceFact(
                    note,
                    2,
                    true,
                    0.99,
                    "polyphonic staff: down-stem pitched event belongs to local voice 2");
            }
        }

        foreach (var note in pitched.Where(note => !result.ContainsKey(Key(note))))
        {
            var alignedKnown = pitched
                .Where(other => other != note)
                .Where(other => result.ContainsKey(Key(other)))
                .Where(other => Math.Abs(other.AnchorX - note.AnchorX)
                    <= spacing * StemlessAlignmentInSpacings)
                .OrderBy(other => Math.Abs(other.AnchorX - note.AnchorX))
                .ThenBy(other => Math.Abs(other.AnchorY - note.AnchorY))
                .FirstOrDefault();

            if (alignedKnown is not null)
            {
                var alignedVoice = result[Key(alignedKnown)].LocalVoice;
                var localVoice = Math.Abs(note.AnchorY - alignedKnown.AnchorY)
                        >= spacing * 0.20
                    ? (note.AnchorY < alignedKnown.AnchorY ? 1 : 2)
                    : alignedVoice;

                result[Key(note)] = CreateVoiceFact(
                    note,
                    localVoice,
                    true,
                    0.78,
                    "stemless/ambiguous pitched event assigned by vertical order against an aligned stemmed event");
                continue;
            }

            var alignedRest = rests
                .Where(rest => Math.Abs(rest.AnchorX - note.AnchorX)
                    <= spacing * StemlessAlignmentInSpacings)
                .OrderBy(rest => Math.Abs(rest.AnchorX - note.AnchorX))
                .FirstOrDefault();

            if (alignedRest is not null
                && Math.Abs(note.AnchorY - alignedRest.AnchorY) >= spacing * 0.20)
            {
                result[Key(note)] = CreateVoiceFact(
                    note,
                    note.AnchorY < alignedRest.AnchorY ? 1 : 2,
                    true,
                    0.74,
                    "stemless/ambiguous pitched event assigned by vertical order against an aligned rest");
                continue;
            }

            result[Key(note)] = CreateVoiceFact(
                note,
                1,
                true,
                0.45,
                "polyphonic staff but stemless/ambiguous pitched event has no aligned voice evidence; defaulting to local voice 1");
        }

        var pairedRests = new HashSet<TargetKey>();
        foreach (var rest in rests.OrderBy(rest => rest.AnchorX).ThenBy(rest => rest.AnchorY))
        {
            if (pairedRests.Contains(Key(rest)))
            {
                continue;
            }

            var partner = rests
                .Where(other => other != rest)
                .Where(other => !pairedRests.Contains(Key(other)))
                .Where(other => Math.Abs(other.AnchorX - rest.AnchorX)
                    <= spacing * RestPairAlignmentInSpacings)
                .Where(other => Math.Abs(other.AnchorY - rest.AnchorY)
                    >= spacing * RestPairMinimumSeparationInSpacings)
                .OrderBy(other => Math.Abs(other.AnchorX - rest.AnchorX))
                .ThenBy(other => Math.Abs(other.AnchorY - rest.AnchorY))
                .FirstOrDefault();

            if (partner is null)
            {
                continue;
            }

            var upper = rest.AnchorY <= partner.AnchorY ? rest : partner;
            var lower = rest.AnchorY <= partner.AnchorY ? partner : rest;
            result[Key(upper)] = CreateVoiceFact(
                upper,
                1,
                true,
                0.98,
                "vertically paired rests at one rhythmic x-position: upper rest is local voice 1");
            result[Key(lower)] = CreateVoiceFact(
                lower,
                2,
                true,
                0.98,
                "vertically paired rests at one rhythmic x-position: lower rest is local voice 2");
            pairedRests.Add(Key(upper));
            pairedRests.Add(Key(lower));
        }

        foreach (var rest in rests.Where(rest => !result.ContainsKey(Key(rest))))
        {
            var alignedPitched = pitched
                .Where(note => Math.Abs(note.AnchorX - rest.AnchorX)
                    <= spacing * NoteRestAlignmentInSpacings)
                .Where(note => result.ContainsKey(Key(note)))
                .OrderBy(note => Math.Abs(note.AnchorX - rest.AnchorX))
                .ThenBy(note => Math.Abs(note.AnchorY - rest.AnchorY))
                .ToArray();
            var nearbyVoices = alignedPitched
                .Select(note => result[Key(note)].LocalVoice)
                .Distinct()
                .ToArray();

            if (nearbyVoices.Length == 1 && alignedPitched.Length > 0)
            {
                var closest = alignedPitched[0];
                var margin = spacing * VerticalOutsideMarginInSpacings;
                var isClearlySeparate = rest.AnchorY < closest.TopY - margin
                    || rest.AnchorY > closest.BottomY + margin;

                if (isClearlySeparate)
                {
                    result[Key(rest)] = CreateVoiceFact(
                        rest,
                        nearbyVoices[0] == 1 ? 2 : 1,
                        true,
                        0.91,
                        "rest is vertically separated from an aligned pitched event and therefore belongs to the other local voice");
                    continue;
                }
            }

            var centerOffset = rest.AnchorY - staffCenterY;
            if (Math.Abs(centerOffset)
                > spacing * AmbiguousRestCenterBandInSpacings)
            {
                result[Key(rest)] = CreateVoiceFact(
                    rest,
                    centerOffset < 0 ? 1 : 2,
                    true,
                    0.76,
                    centerOffset < 0
                        ? "unpaired rest is displaced above staff center; assigning local voice 1"
                        : "unpaired rest is displaced below staff center; assigning local voice 2");
                continue;
            }

            result[Key(rest)] = CreateVoiceFact(
                rest,
                1,
                true,
                0.42,
                "polyphonic staff but rest is not vertically separated enough to identify a voice; defaulting to local voice 1");
        }

        return events
            .Select(item => result[Key(item)])
            .ToArray();
    }

    private static VoiceFact CreateVoiceFact(
        VoiceEvent item,
        int localVoice,
        bool isPolyphonic,
        double confidence,
        string reason)
    {
        return new VoiceFact(
            item.MeasureNumber,
            item.Staff,
            item.TargetKind,
            item.TargetId,
            localVoice,
            isPolyphonic,
            item.AnchorX,
            item.AnchorY,
            Math.Clamp(
                Math.Min(confidence, item.SourceConfidence),
                0,
                1),
            reason,
            item.SourceShapeIds);
    }

    private static TargetKey Key(VoiceEvent item) =>
        new(
            item.MeasureNumber,
            item.Staff,
            item.TargetKind,
            item.TargetId);

    private readonly record struct NoteKey(
        int MeasureNumber,
        string NoteheadId);

    private readonly record struct StemKey(
        int MeasureNumber,
        string StemShapeId);

    private readonly record struct TargetKey(
        int MeasureNumber,
        int Staff,
        VoiceTargetKind TargetKind,
        string TargetId);

    private sealed record VoiceEvent(
        int MeasureNumber,
        int Staff,
        VoiceTargetKind TargetKind,
        string TargetId,
        double AnchorX,
        double TopY,
        double BottomY,
        StemDirection? StemDirection,
        double? StemAnchorX,
        double SourceConfidence,
        IReadOnlyList<string> SourceShapeIds)
    {
        public double AnchorY => (TopY + BottomY) / 2.0;
    }
}
