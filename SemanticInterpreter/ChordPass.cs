namespace SvgMusic.Semantics;

public sealed record ChordAnalysisResult(
    IReadOnlyList<ChordFact> Chords)
{
    public IReadOnlyList<ChordFact> StemBacked =>
        Chords.Where(chord => chord.StemShapeId is not null).ToArray();

    public IReadOnlyList<ChordFact> StemlessWhole =>
        Chords.Where(chord => chord.StemShapeId is null).ToArray();
}

/// <summary>
/// Turns already accepted notehead/stem/duration facts into explicit chord facts.
///
/// Stem-backed notes are a chord when two or more noteheads of the same fill kind
/// share the same accepted stem. Stemless whole notes have no common stem to use as
/// an anchor, so they are grouped by the same transient X-overlap columns used by
/// augmentation-dot matching. This naturally tolerates the left/right displacement
/// used to engrave seconds inside a whole-note chord.
/// </summary>
public sealed class ChordPass : ISemanticPass
{
    private readonly NoteheadColumnHelper _columnHelper;

    public ChordPass(NoteheadColumnHelper? columnHelper = null)
    {
        _columnHelper = columnHelper ?? new NoteheadColumnHelper();
    }

    public string Name => nameof(ChordPass);

    public ChordAnalysisResult? LastAnalysis { get; private set; }

    public void Run(
        SemanticDocument document,
        SemanticFacts facts)
    {
        var noteheads = facts.OfType<NoteheadFact>().ToArray();
        var durations = facts.OfType<DurationFact>().ToArray();
        var stems = facts.OfType<StemAttachmentFact>().ToArray();

        if (noteheads.Length == 0)
        {
            throw new InvalidDataException(
                "ChordPass requires NoteheadPass to run first.");
        }

        if (durations.Length == 0)
        {
            throw new InvalidDataException(
                "ChordPass requires DurationPass to run first.");
        }

        var durationByKey = durations.ToDictionary(
            duration => new NoteKey(
                duration.MeasureNumber,
                duration.NoteheadId));
        var noteheadByKey = noteheads.ToDictionary(
            notehead => new NoteKey(
                notehead.MeasureNumber,
                notehead.ShapeId));

        var chords = new List<ChordFact>();
        var chordIndex = 0;

        foreach (var stem in stems
                     .OrderBy(stem => stem.MeasureNumber)
                     .ThenBy(stem => Math.Min(stem.StartX, stem.EndX))
                     .ThenBy(stem => stem.StemShapeId, StringComparer.Ordinal))
        {
            var attached = stem.AttachedNoteheadIds
                .Select(id => new NoteKey(stem.MeasureNumber, id))
                .Where(noteheadByKey.ContainsKey)
                .Select(key => noteheadByKey[key])
                .GroupBy(notehead => notehead.FillKind, StringComparer.OrdinalIgnoreCase);

            foreach (var fillGroup in attached)
            {
                var members = fillGroup
                    .OrderBy(notehead => notehead.Staff)
                    .ThenBy(notehead => notehead.CenterY)
                    .ThenBy(notehead => notehead.ShapeId, StringComparer.Ordinal)
                    .ToArray();

                if (members.Length < 2)
                {
                    continue;
                }

                chordIndex++;
                chords.Add(CreateChord(
                    chordIndex,
                    members,
                    stem,
                    durationByKey,
                    "shared-stem"));
            }
        }

        var stemmedKeys = stems
            .SelectMany(stem => stem.AttachedNoteheadIds.Select(id =>
                new NoteKey(stem.MeasureNumber, id)))
            .ToHashSet();

        var stemlessWholeHeads = noteheads
            .Where(notehead => !stemmedKeys.Contains(
                new NoteKey(notehead.MeasureNumber, notehead.ShapeId)))
            .Where(notehead =>
                durationByKey.TryGetValue(
                    new NoteKey(notehead.MeasureNumber, notehead.ShapeId),
                    out var duration)
                && duration.StemShapeId is null
                && duration.NoteType.Equals(
                    "whole",
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (var column in _columnHelper.Build(stemlessWholeHeads))
        {
            if (column.Noteheads.Count < 2)
            {
                continue;
            }

            chordIndex++;
            chords.Add(CreateChord(
                chordIndex,
                column.Noteheads,
                null,
                durationByKey,
                "stemless-whole-x-component"));
        }

        foreach (var chord in chords)
        {
            facts.Add(chord);
        }

        LastAnalysis = new ChordAnalysisResult(chords);

        facts.AddTrace(
            $"ChordPass: chords={chords.Count}; "
            + $"stem-backed={chords.Count(chord => chord.StemShapeId is not null)}; "
            + $"stemless-whole={chords.Count(chord => chord.StemShapeId is null)}; "
            + $"noteheads-in-chords={chords.Sum(chord => chord.NoteheadIds.Count)}");
    }

    private static ChordFact CreateChord(
        int index,
        IReadOnlyList<NoteheadFact> noteheads,
        StemAttachmentFact? stem,
        IReadOnlyDictionary<NoteKey, DurationFact> durationByKey,
        string mode)
    {
        var first = noteheads[0];
        var durations = noteheads
            .Select(notehead => durationByKey[
                new NoteKey(notehead.MeasureNumber, notehead.ShapeId)])
            .ToArray();
        var selectedDuration = durations
            .OrderByDescending(duration => duration.Confidence)
            .ThenBy(duration => duration.NoteheadId, StringComparer.Ordinal)
            .First();
        var staffs = noteheads
            .Select(notehead => notehead.Staff)
            .Distinct()
            .OrderBy(staff => staff)
            .ToArray();
        var noteheadIds = noteheads
            .Select(notehead => notehead.ShapeId)
            .ToArray();
        var durationAgreement = durations.All(duration =>
            duration.NoteType == selectedDuration.NoteType
            && duration.EffectiveDuration == selectedDuration.EffectiveDuration);
        var confidence = new[]
            {
                noteheads.Average(notehead => notehead.Confidence),
                durations.Average(duration => duration.Confidence),
                stem?.Confidence ?? 1.0,
                durationAgreement ? 1.0 : 0.65
            }
            .Average();
        var sourceIds = noteheadIds
            .Concat(stem is null ? [] : new[] { stem.StemShapeId })
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var reason = mode == "shared-stem"
            ? $"{noteheads.Count} {first.FillKind} noteheads share accepted stem {stem!.StemShapeId}; "
                + $"staffs=[{string.Join(',', staffs)}]; duration-agreement={durationAgreement}"
            : $"{noteheads.Count} stemless whole-note heads form one transitive X-overlap component; "
                + $"staff={first.Staff}; duration-agreement={durationAgreement}";

        return new ChordFact(
            first.MeasureNumber,
            $"chord-m{first.MeasureNumber}-{index}",
            staffs,
            noteheadIds,
            stem?.StemShapeId,
            first.FillKind,
            selectedDuration.NoteType,
            noteheads.Average(notehead => notehead.CenterX),
            confidence,
            reason,
            sourceIds);
    }

    private readonly record struct NoteKey(
        int MeasureNumber,
        string NoteheadId);
}
