using SvgMusic.Canonical;

namespace SvgMusic.Semantics;

public sealed record WrittenDurationClassification(
    Fraction Duration,
    string NoteType,
    int SubdivisionLevel,
    bool FallbackWithoutStem);

/// <summary>
/// Shared musical rule for turning notehead/stem/subdivision evidence into a written value.
/// Geometry-specific passes should only extract the evidence and delegate the musical
/// interpretation here.
/// </summary>
public static class WrittenDurationClassifier
{
    public static WrittenDurationClassification Classify(
        bool isHollow,
        bool hasStem,
        int subdivisionLevel)
    {
        subdivisionLevel = Math.Max(0, subdivisionLevel);

        if (isHollow)
        {
            return hasStem
                ? new WrittenDurationClassification(
                    new Fraction(1, 2),
                    "half",
                    0,
                    false)
                : new WrittenDurationClassification(
                    new Fraction(1, 1),
                    "whole",
                    0,
                    false);
        }

        var denominator = checked(4L * Pow2(subdivisionLevel));
        return new WrittenDurationClassification(
            new Fraction(1, denominator).Reduce(),
            NoteTypeForDenominator(denominator),
            subdivisionLevel,
            !hasStem);
    }

    private static long Pow2(int exponent)
    {
        if (exponent < 0 || exponent > 30)
        {
            throw new ArgumentOutOfRangeException(nameof(exponent));
        }

        return 1L << exponent;
    }

    private static string NoteTypeForDenominator(long denominator) =>
        denominator switch
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
