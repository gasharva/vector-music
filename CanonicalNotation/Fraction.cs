using System.Globalization;

namespace SvgMusic.Canonical;

public readonly record struct Fraction(long Numerator, long Denominator)
{
    public static Fraction Zero => new(0, 1);

    public Fraction(long numerator) : this(numerator, 1) { }

    public Fraction Reduce()
    {
        if (Denominator == 0) throw new DivideByZeroException();
        var n = Numerator;
        var d = Denominator;
        if (d < 0) { n = -n; d = -d; }
        var g = Gcd(Math.Abs(n), d);
        return new Fraction(n / g, d / g);
    }

    public static Fraction Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Zero;
        var parts = value.Split('/', StringSplitOptions.TrimEntries);
        return parts.Length switch
        {
            1 => new Fraction(long.Parse(parts[0], CultureInfo.InvariantCulture), 1),
            2 => new Fraction(
                    long.Parse(parts[0], CultureInfo.InvariantCulture),
                    long.Parse(parts[1], CultureInfo.InvariantCulture))
                .Reduce(),
            _ => throw new FormatException($"Invalid fraction: {value}")
        };
    }

    public static Fraction operator +(Fraction a, Fraction b) =>
        new Fraction(a.Numerator * b.Denominator + b.Numerator * a.Denominator,
            a.Denominator * b.Denominator).Reduce();

    public static Fraction operator -(Fraction a, Fraction b) =>
        new Fraction(a.Numerator * b.Denominator - b.Numerator * a.Denominator,
            a.Denominator * b.Denominator).Reduce();

    public override string ToString()
    {
        var r = Reduce();
        if (r.Numerator == 0) return "0";
        return r.Denominator == 1 ? r.Numerator.ToString(CultureInfo.InvariantCulture) : $"{r.Numerator}/{r.Denominator}";
    }

    private static long Gcd(long a, long b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a == 0 ? 1 : a;
    }
}
