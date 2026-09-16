using SvgMusic.Canonical;

namespace SvgMusic.Semantics;

internal static class DurationMath
{
    public static string ApplyDots(
        string baseDuration,
        int dots)
    {
        if (dots <= 0)
        {
            return Fraction.Parse(baseDuration).ToString();
        }

        var baseValue = Fraction.Parse(baseDuration);
        var result = baseValue;
        long divisor = 2;

        for (var i = 0; i < dots; i++)
        {
            result += new Fraction(
                baseValue.Numerator,
                baseValue.Denominator * divisor);
            divisor *= 2;
        }

        return result.ToString();
    }
}
