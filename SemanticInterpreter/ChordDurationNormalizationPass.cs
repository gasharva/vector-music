using SvgMusic.Canonical;

namespace SvgMusic.Semantics;

/// <summary>
/// Normalizes rhythmic duration across members of an already recognized chord.
///
/// Augmentation dots are visually attached to individual heads, but rhythmically
/// belong to the chord event. In dense/ledger notation one printed dot can be missed
/// while another member of the same chord is recognized correctly. If all members
/// agree on the undotted value and tuplet ratio, the strongest observed dot count is
/// therefore propagated to every member before voice/onset reconstruction.
/// </summary>
public sealed class ChordDurationNormalizationPass : ISemanticPass
{
    public string Name => nameof(ChordDurationNormalizationPass);

    public void Run(
        SemanticDocument document,
        SemanticFacts facts)
    {
        var durations = facts.OfType<DurationFact>()
            .ToDictionary(
                duration => new DurationKey(
                    duration.MeasureNumber,
                    duration.NoteheadId));
        var dotFacts = facts.OfType<DotAttachmentFact>().ToArray();
        var normalized = 0;
        var chords = 0;

        foreach (var chord in facts.OfType<ChordFact>())
        {
            var members = chord.NoteheadIds
                .Select(noteheadId => new DurationKey(
                    chord.MeasureNumber,
                    noteheadId))
                .Where(durations.ContainsKey)
                .Select(key => durations[key])
                .ToArray();

            if (members.Length < 2)
            {
                continue;
            }

            var compatible = members
                .Select(duration => new
                {
                    duration.BaseDuration,
                    duration.NoteType,
                    duration.TupletActual,
                    duration.TupletNormal
                })
                .Distinct()
                .Count() == 1;

            if (!compatible)
            {
                facts.AddTrace(
                    $"ChordDurationNormalizationPass: {chord.ChordId} kept unchanged; "
                    + "members disagree before augmentation dots");
                continue;
            }

            var maximumDots = members.Max(duration => duration.Dots);
            var minimumDots = members.Min(duration => duration.Dots);
            if (maximumDots == minimumDots)
            {
                continue;
            }

            chords++;
            var peerDotShapeIds = dotFacts
                .Where(dot =>
                    dot.MeasureNumber == chord.MeasureNumber
                    && chord.NoteheadIds.Contains(
                        dot.TargetNoteheadId,
                        StringComparer.Ordinal)
                    && dot.Count == maximumDots)
                .SelectMany(dot => dot.DotShapeIds)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            foreach (var original in members.Where(duration => duration.Dots != maximumDots))
            {
                var effective = Fraction.Parse(
                    DurationMath.ApplyDots(
                        original.BaseDuration,
                        maximumDots));

                if (original.TupletActual is int actual
                    && original.TupletNormal is int normal)
                {
                    effective = new Fraction(
                        checked(effective.Numerator * normal),
                        checked(effective.Denominator * actual))
                        .Reduce();
                }

                var sourceShapeIds = original.SourceShapeIds
                    .Concat(peerDotShapeIds)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var replacement = original with
                {
                    EffectiveDuration = effective.ToString(),
                    Dots = maximumDots,
                    Confidence = Math.Min(original.Confidence, chord.Confidence),
                    Reason = original.Reason
                        + $"; chord normalization: dots={maximumDots} propagated from "
                        + $"another member of {chord.ChordId}",
                    SourceShapeIds = sourceShapeIds
                };

                facts.Replace(original, replacement);
                durations[new DurationKey(
                    original.MeasureNumber,
                    original.NoteheadId)] = replacement;
                normalized++;
            }
        }

        facts.AddTrace(
            $"ChordDurationNormalizationPass: chords-normalized={chords}; "
            + $"member-durations-updated={normalized}");
    }

    private readonly record struct DurationKey(
        int MeasureNumber,
        string NoteheadId);
}
