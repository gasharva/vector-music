using SvgMusic.Canonical;

namespace SvgMusic.Semantics;

/// <summary>
/// Repairs rests that are physically drawn in the gap between the two staves of a
/// piano grand staff. Geometric ownership alone is ambiguous there: an upper-voice
/// rest may be closer to the lower staff than to the upper one.
///
/// The repair is deliberately conservative. It tries the opposite staff only when
/// moving the rest makes the total recognized rhythmic material on BOTH staves fit
/// integer measure lengths substantially better.
/// </summary>
public sealed class InterstaffRestPass : ISemanticPass
{
    private const double MinimumImprovementInMeasureLengths = 0.12;

    public string Name => nameof(InterstaffRestPass);

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
        var corrections = new List<(RestFact Original, RestFact Replacement)>();

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

            var rests = facts
                .OfType<RestFact>()
                .Where(rest =>
                    rest.MeasureNumber == measure.Number)
                .ToArray();

            if (rests.Length == 0)
            {
                continue;
            }

            var totals = MeasureTotals(
                measure.Number,
                facts);

            foreach (var rest in rests)
            {
                if (!IsInterstaff(
                        rest,
                        measure))
                {
                    continue;
                }

                var otherStaff = rest.Staff == 1
                    ? 2
                    : 1;
                var duration = EffectiveRestDuration(
                    rest,
                    facts);
                var before = FitError(
                    totals.GetValueOrDefault(1),
                    currentMeasureLength)
                    + FitError(
                        totals.GetValueOrDefault(2),
                        currentMeasureLength);

                var movedTotals = new Dictionary<int, Fraction>(totals)
                {
                    [rest.Staff] =
                        totals.GetValueOrDefault(rest.Staff)
                        - duration,
                    [otherStaff] =
                        totals.GetValueOrDefault(otherStaff)
                        + duration
                };

                var after = FitError(
                    movedTotals.GetValueOrDefault(1),
                    currentMeasureLength)
                    + FitError(
                        movedTotals.GetValueOrDefault(2),
                        currentMeasureLength);
                var measureLength = ToDouble(currentMeasureLength);
                var improvement = ToDouble(before - after)
                    / Math.Max(measureLength, 1e-9);

                if (improvement
                    < MinimumImprovementInMeasureLengths)
                {
                    continue;
                }

                var replacement = rest with
                {
                    Staff = otherStaff,
                    Confidence = Math.Min(
                        rest.Confidence,
                        0.94),
                    Reason = rest.Reason
                        + $"; interstaff repair: moving s{rest.Staff}->s{otherStaff} "
                        + $"improves measure-fit error by {improvement:F3} measure lengths"
                };

                corrections.Add((rest, replacement));
                totals = movedTotals;

                facts.AddTrace(
                    $"InterstaffRestPass: m{measure.Number} {rest.ShapeId} "
                    + $"s{rest.Staff}->s{otherStaff}; "
                    + $"fit-error {before}->{after}; improvement={improvement:F3}");
            }
        }

        foreach (var (original, replacement) in corrections)
        {
            facts.Replace(
                original,
                replacement);

            var attachments = facts
                .OfType<RestDotAttachmentFact>()
                .Where(dot =>
                    dot.MeasureNumber == original.MeasureNumber
                    && dot.Staff == original.Staff
                    && string.Equals(
                        dot.TargetRestShapeId,
                        original.ShapeId,
                        StringComparison.Ordinal))
                .ToArray();

            foreach (var attachment in attachments)
            {
                facts.Replace(
                    attachment,
                    attachment with
                    {
                        Staff = replacement.Staff,
                        Reason = attachment.Reason
                            + $"; rest moved to staff {replacement.Staff} by InterstaffRestPass"
                    });
            }
        }

        facts.AddTrace(
            $"InterstaffRestPass: corrected={corrections.Count}");
    }

    private static bool IsInterstaff(
        RestFact rest,
        MeasureScene measure)
    {
        var upperBottom =
            measure.Upper.StaffBounds.MaxY;
        var lowerTop =
            measure.Lower.StaffBounds.MinY;

        return rest.CenterY > upperBottom
            && rest.CenterY < lowerTop;
    }

    private static Dictionary<int, Fraction> MeasureTotals(
        int measureNumber,
        SemanticFacts facts)
    {
        var result = new Dictionary<int, Fraction>
        {
            [1] = Fraction.Zero,
            [2] = Fraction.Zero
        };

        var durations = facts
            .OfType<DurationFact>()
            .Where(duration =>
                duration.MeasureNumber == measureNumber)
            .ToDictionary(
                duration => duration.NoteheadId,
                StringComparer.Ordinal);
        var noteheads = facts
            .OfType<NoteheadFact>()
            .Where(note =>
                note.MeasureNumber == measureNumber)
            .ToDictionary(
                note => note.ShapeId,
                StringComparer.Ordinal);
        var consumed = new HashSet<string>(
            StringComparer.Ordinal);

        foreach (var chord in facts
                     .OfType<ChordFact>()
                     .Where(chord =>
                         chord.MeasureNumber == measureNumber))
        {
            var members = chord.NoteheadIds
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

            var duration = members
                .Where(durations.ContainsKey)
                .Select(id => durations[id])
                .OrderByDescending(item =>
                    item.Confidence)
                .FirstOrDefault();

            if (duration is null)
            {
                continue;
            }

            var staff = members
                .Select(id => noteheads[id].Staff)
                .GroupBy(value => value)
                .OrderByDescending(group =>
                    group.Count())
                .ThenBy(group => group.Key)
                .Select(group => group.Key)
                .First();

            result[staff] =
                result.GetValueOrDefault(staff)
                + Fraction.Parse(
                    duration.EffectiveDuration);
        }

        foreach (var note in noteheads.Values)
        {
            if (consumed.Contains(note.ShapeId)
                || !durations.TryGetValue(
                    note.ShapeId,
                    out var duration))
            {
                continue;
            }

            result[note.Staff] =
                result.GetValueOrDefault(note.Staff)
                + Fraction.Parse(
                    duration.EffectiveDuration);
        }

        foreach (var rest in facts
                     .OfType<RestFact>()
                     .Where(rest =>
                         rest.MeasureNumber == measureNumber))
        {
            result[rest.Staff] =
                result.GetValueOrDefault(rest.Staff)
                + EffectiveRestDuration(
                    rest,
                    facts);
        }

        return result;
    }

    private static Fraction EffectiveRestDuration(
        RestFact rest,
        SemanticFacts facts)
    {
        var dots = facts
            .OfType<RestDotAttachmentFact>()
            .Where(dot =>
                dot.MeasureNumber == rest.MeasureNumber
                && dot.Staff == rest.Staff
                && string.Equals(
                    dot.TargetRestShapeId,
                    rest.ShapeId,
                    StringComparison.Ordinal))
            .OrderByDescending(dot =>
                dot.Confidence)
            .Select(dot => dot.Count)
            .FirstOrDefault();

        return Fraction.Parse(
            DurationMath.ApplyDots(
                rest.Duration,
                dots));
    }

    private static Fraction FitError(
        Fraction total,
        Fraction measureLength)
    {
        var totalValue = ToDouble(total);
        var measureValue = ToDouble(
            measureLength);

        if (totalValue <= 1e-9
            || measureValue <= 1e-9)
        {
            return Fraction.Zero;
        }

        var multiple = Math.Max(
            1,
            (int)Math.Round(
                totalValue / measureValue,
                MidpointRounding.AwayFromZero));
        var target = new Fraction(
            measureLength.Numerator * multiple,
            measureLength.Denominator)
            .Reduce();
        var delta = total - target;

        if (delta.Numerator < 0)
        {
            delta = new Fraction(
                -delta.Numerator,
                delta.Denominator);
        }

        return delta.Reduce();
    }

    private static double ToDouble(
        Fraction value) =>
        value.Numerator
        / (double)value.Denominator;
}
