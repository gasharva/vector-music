using SvgMusic.Canonical;

namespace SvgMusic.Semantics;

/// <summary>
/// Infers written and effective note durations from already accepted semantic facts.
///
/// The pass intentionally does not try to recover voices or rhythmic positions. It answers
/// the smaller question we can now answer reliably for each notehead:
/// - hollow + no stem => whole;
/// - hollow + stem => half;
/// - filled + stem => quarter, shortened by the deepest flag/beam level;
/// - augmentation dots lengthen the written value;
/// - tuplets scale the performed/effective value by normal/actual.
///
/// A filled notehead without an accepted stem is kept as a low-confidence quarter fallback
/// so the raw MusicXML preview can still show it instead of silently dropping the pitch.
/// </summary>
public sealed class DurationPass : ISemanticPass
{
    public string Name => nameof(DurationPass);

    public void Run(
        SemanticDocument document,
        SemanticFacts facts)
    {
        var noteheads = facts
            .OfType<NoteheadFact>()
            .OrderBy(note => note.MeasureNumber)
            .ThenBy(note => note.Staff)
            .ThenBy(note => note.CenterX)
            .ThenBy(note => note.CenterY)
            .ToArray();

        if (noteheads.Length == 0)
        {
            throw new InvalidDataException(
                "DurationPass requires NoteheadPass to run first.");
        }

        var stems = facts.OfType<StemAttachmentFact>().ToArray();
        var flags = facts.OfType<FlagAttachmentFact>().ToArray();
        var beams = facts.OfType<BeamAttachmentFact>().ToArray();
        var tuplets = facts.OfType<TupletFact>().ToArray();
        var dots = facts.OfType<DotAttachmentFact>().ToArray();
        var graceNoteheadIds = facts
            .OfType<GraceNoteFact>()
            .Select(grace => grace.NoteheadId)
            .ToHashSet(StringComparer.Ordinal);

        var decisions = new List<DurationFact>(noteheads.Length);

        foreach (var notehead in noteheads)
        {
            var stem = stems
                .Where(candidate =>
                    candidate.MeasureNumber == notehead.MeasureNumber
                    && candidate.AttachedNoteheadIds.Contains(
                        notehead.ShapeId,
                        StringComparer.Ordinal))
                .OrderByDescending(candidate => candidate.Confidence)
                .ThenBy(candidate => candidate.StemShapeId, StringComparer.Ordinal)
                .FirstOrDefault();

            var matchingFlags = stem is null
                ? Array.Empty<FlagAttachmentFact>()
                : flags
                    .Where(flag =>
                        flag.MeasureNumber == notehead.MeasureNumber
                        && flag.StemShapeId == stem.StemShapeId)
                    .ToArray();

            var matchingBeams = stem is null
                ? Array.Empty<BeamAttachmentFact>()
                : beams
                    .Where(beam =>
                        beam.MeasureNumber == notehead.MeasureNumber
                        && beam.AttachedStemIds.Contains(
                            stem.StemShapeId,
                            StringComparer.Ordinal))
                    .ToArray();

            var flagLevel = matchingFlags.Length == 0
                ? 0
                : matchingFlags.Max(flag => flag.Level);
            var beamLevel = matchingBeams.Length == 0
                ? 0
                : matchingBeams.Max(beam => beam.Level);
            var subdivisionLevel = Math.Max(flagLevel, beamLevel);

            var isHollow = notehead.FillKind.Equals(
                "hollow",
                StringComparison.OrdinalIgnoreCase);
            var written = WrittenDurationClassifier.Classify(
                isHollow,
                stem is not null,
                subdivisionLevel);
            var baseDuration = written.Duration;
            var noteType = written.NoteType;
            subdivisionLevel = written.SubdivisionLevel;
            var fallbackWithoutStem = written.FallbackWithoutStem;

            var matchingDots = dots
                .Where(dot =>
                    dot.MeasureNumber == notehead.MeasureNumber
                    && dot.TargetNoteheadId == notehead.ShapeId)
                .ToArray();
            var dotCount = matchingDots.Sum(dot => dot.Count);
            var dottedDuration = ApplyDots(baseDuration, dotCount);

            var tuplet = tuplets
                .Where(candidate =>
                    candidate.MeasureNumber == notehead.MeasureNumber
                    && (candidate.AttachedNoteheadIds.Contains(
                            notehead.ShapeId,
                            StringComparer.Ordinal)
                        || (stem is not null
                            && candidate.AttachedStemIds.Contains(
                                stem.StemShapeId,
                                StringComparer.Ordinal))))
                .OrderByDescending(candidate => candidate.Confidence)
                .ThenBy(candidate => candidate.TupletShapeId, StringComparer.Ordinal)
                .FirstOrDefault();

            var nominalEffectiveDuration = tuplet is null
                ? dottedDuration
                : Multiply(
                    dottedDuration,
                    tuplet.NormalNotes,
                    tuplet.ActualNotes);
            var isGrace = graceNoteheadIds.Contains(notehead.ShapeId);
            var effectiveDuration = isGrace
                ? Fraction.Zero
                : nominalEffectiveDuration;

            var confidenceValues = new List<double>
            {
                notehead.Confidence
            };

            if (stem is not null)
            {
                confidenceValues.Add(stem.Confidence);
            }

            confidenceValues.AddRange(matchingFlags.Select(flag => flag.Confidence));
            confidenceValues.AddRange(matchingBeams.Select(beam => beam.Confidence));
            confidenceValues.AddRange(matchingDots.Select(dot => dot.Confidence));

            if (tuplet is not null)
            {
                confidenceValues.Add(tuplet.Confidence);
            }

            var confidence = confidenceValues.Min();
            if (fallbackWithoutStem)
            {
                confidence *= 0.65;
            }

            if (isHollow && (flagLevel > 0 || beamLevel > 0))
            {
                confidence *= 0.60;
            }

            var sourceShapeIds = new List<string>
            {
                notehead.ShapeId
            };

            if (stem is not null)
            {
                sourceShapeIds.Add(stem.StemShapeId);
            }

            sourceShapeIds.AddRange(matchingFlags.Select(flag => flag.FlagShapeId));
            sourceShapeIds.AddRange(matchingBeams.Select(beam => beam.BeamShapeId));
            sourceShapeIds.AddRange(matchingDots.SelectMany(dot => dot.DotShapeIds));

            if (tuplet is not null)
            {
                sourceShapeIds.Add(tuplet.TupletShapeId);
            }

            sourceShapeIds = sourceShapeIds
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var evidence = isHollow
                ? stem is null
                    ? "hollow notehead without stem -> whole"
                    : "hollow notehead with stem -> half"
                : stem is null
                    ? "filled notehead without accepted stem -> quarter fallback"
                    : subdivisionLevel == 0
                        ? "filled notehead with stem and no flag/beam -> quarter"
                        : $"filled notehead with stem; deepest flag/beam level={subdivisionLevel}";

            var dotText = dotCount == 0
                ? "no augmentation dots"
                : $"dots={dotCount}";
            var tupletText = tuplet is null
                ? "no tuplet scaling"
                : $"tuplet={tuplet.ActualNotes}:{tuplet.NormalNotes}";
            var graceText = isGrace
                ? "grace note -> zero metrical duration"
                : "metric note";

            decisions.Add(new DurationFact(
                notehead.MeasureNumber,
                notehead.Staff,
                notehead.ShapeId,
                stem?.StemShapeId,
                baseDuration.ToString(),
                effectiveDuration.ToString(),
                noteType,
                dotCount,
                subdivisionLevel,
                tuplet?.ActualNotes,
                tuplet?.NormalNotes,
                confidence,
                $"{evidence}; {dotText}; {tupletText}; {graceText}; "
                + $"base={baseDuration}; effective={effectiveDuration}",
                sourceShapeIds));
        }

        foreach (var decision in decisions)
        {
            facts.Add(decision);
        }

        var byType = decisions
            .GroupBy(decision => decision.NoteType)
            .OrderBy(group => NoteTypeOrder(group.Key))
            .Select(group => $"{group.Key}={group.Count()}");

        facts.AddTrace(
            $"DurationPass decisions: noteheads={decisions.Count}; "
            + $"types=[{string.Join(',', byType)}]; "
            + $"dotted={decisions.Count(decision => decision.Dots > 0)}; "
            + $"tuplets={decisions.Count(decision => decision.TupletActual is not null)}; "
            + $"subdivided={decisions.Count(decision => decision.SubdivisionLevel > 0)}; "
            + $"grace={decisions.Count(decision => graceNoteheadIds.Contains(decision.NoteheadId))}; "
            + $"filled-without-stem-fallback={decisions.Count(decision => decision.Reason.Contains("quarter fallback", StringComparison.Ordinal))}");
    }

    private static Fraction ApplyDots(
        Fraction duration,
        int dots)
    {
        if (dots <= 0)
        {
            return duration.Reduce();
        }

        if (dots > 12)
        {
            throw new InvalidDataException(
                $"Unsupported augmentation-dot count: {dots}.");
        }

        var denominator = Pow2(dots);
        var numerator = 2 * denominator - 1;
        return Multiply(duration, numerator, denominator);
    }

    private static Fraction Multiply(
        Fraction value,
        long numerator,
        long denominator)
    {
        if (denominator == 0)
        {
            throw new DivideByZeroException();
        }

        return new Fraction(
            checked(value.Numerator * numerator),
            checked(value.Denominator * denominator))
            .Reduce();
    }

    private static long Pow2(int exponent)
    {
        if (exponent < 0 || exponent > 30)
        {
            throw new ArgumentOutOfRangeException(nameof(exponent));
        }

        return 1L << exponent;
    }

    private static string NoteTypeForDenominator(long denominator)
    {
        return denominator switch
        {
            1 => "whole",
            2 => "half",
            4 => "quarter",
            8 => "eighth",
            16 => "16th",
            32 => "32nd",
            64 => "64th",
            128 => "128th",
            _ => throw new InvalidDataException(
                $"Unsupported note duration denominator: {denominator}.")
        };
    }

    private static int NoteTypeOrder(string noteType)
    {
        return noteType switch
        {
            "whole" => 1,
            "half" => 2,
            "quarter" => 3,
            "eighth" => 4,
            "16th" => 5,
            "32nd" => 6,
            "64th" => 7,
            "128th" => 8,
            _ => 100
        };
    }
}
