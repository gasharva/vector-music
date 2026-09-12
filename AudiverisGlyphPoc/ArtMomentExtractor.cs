namespace AudiverisGlyphPoc;

/// <summary>
/// CPU-only port of Audiveris BasicARTExtractor.
/// The implementation deliberately keeps the same 101x101 lookup-table
/// interpolation so that a later golden test can compare feature vectors
/// against Audiveris itself rather than merely approximate them.
/// </summary>
public static class ArtMomentExtractor
{
    public const int Angular = 20;
    public const int Radial = 5;
    public const int MomentCount = Angular * Radial - 1;

    private const int LutRadius = 50;
    private const int LutSize = 1 + 2 * LutRadius;

    private static readonly double[][][] RealLuts = BuildLuts(imaginary: false);
    private static readonly double[][][] ImaginaryLuts = BuildLuts(imaginary: true);

    public static double[] Extract(BinaryGlyph glyph)
    {
        var mass = glyph.Mass;
        var xx = glyph.X;
        var yy = glyph.Y;

        var centerX = xx.Sum(value => (double)value) / mass;
        var centerY = yy.Sum(value => (double)value) / mass;

        var dxMax = double.MinValue;
        var dyMax = double.MinValue;

        for (var i = 0; i < mass; i++)
        {
            dxMax = Math.Max(dxMax, Math.Abs(xx[i] - centerX));
            dyMax = Math.Max(dyMax, Math.Abs(yy[i] - centerY));
        }

        var radius = Math.Hypot(dxMax, dyMax);

        if (radius <= 0)
        {
            throw new ArgumentException(
                "ART moments cannot be extracted from a zero-radius glyph.",
                nameof(glyph));
        }

        var coefficientReal = new double[Angular, Radial];
        var coefficientImaginary = new double[Angular, Radial];

        for (var i = 0; i < mass; i++)
        {
            var x = xx[i] - centerX;
            var y = yy[i] - centerY;

            var lutX = x * LutRadius / radius + LutRadius;
            var lutY = y * LutRadius / radius + LutRadius;

            if (!Contains(lutX, lutY))
            {
                continue;
            }

            for (var angular = 0; angular < Angular; angular++)
            {
                for (var radial = 0; radial < Radial; radial++)
                {
                    coefficientReal[angular, radial] +=
                        Interpolate(RealLuts[angular][radial], lutX, lutY);

                    coefficientImaginary[angular, radial] -=
                        Interpolate(ImaginaryLuts[angular][radial], lutX, lutY);
                }
            }
        }

        var features = new double[MomentCount];
        var featureIndex = 0;

        for (var angular = 0; angular < Angular; angular++)
        {
            for (var radial = 0; radial < Radial; radial++)
            {
                if (angular == 0 && radial == 0)
                {
                    continue;
                }

                var real = coefficientReal[angular, radial] / mass;
                var imaginary = coefficientImaginary[angular, radial] / mass;

                features[featureIndex++] = Math.Hypot(imaginary, real);
            }
        }

        return features;
    }

    public static string[] CreateLabels()
    {
        var labels = new string[MomentCount];
        var index = 0;

        for (var angular = 0; angular < Angular; angular++)
        {
            for (var radial = 0; radial < Radial; radial++)
            {
                if (angular == 0 && radial == 0)
                {
                    continue;
                }

                labels[index++] = $"F{angular:00}{radial}";
            }
        }

        return labels;
    }

    private static double[][][] BuildLuts(bool imaginary)
    {
        var result = new double[Angular][][];

        for (var angular = 0; angular < Angular; angular++)
        {
            result[angular] = new double[Radial][];

            for (var radial = 0; radial < Radial; radial++)
            {
                result[angular][radial] = new double[LutSize * LutSize];
            }
        }

        for (var x = 0; x < LutSize; x++)
        {
            var normalizedX = (x - LutRadius) / (double)LutRadius;

            for (var y = 0; y < LutSize; y++)
            {
                var normalizedY = (y - LutRadius) / (double)LutRadius;
                var radius = Math.Hypot(normalizedX, normalizedY);

                if (radius >= 1)
                {
                    continue;
                }

                var angle = Math.Atan2(normalizedY, normalizedX);
                var offset = Offset(x, y);

                for (var angular = 0; angular < Angular; angular++)
                {
                    for (var radial = 0; radial < Radial; radial++)
                    {
                        var radialTerm = Math.Cos(radius * Math.PI * radial);
                        var angularTerm = imaginary
                            ? Math.Sin(angle * angular)
                            : Math.Cos(angle * angular);

                        result[angular][radial][offset] = radialTerm * angularTerm;
                    }
                }
            }
        }

        return result;
    }

    private static bool Contains(double x, double y) =>
        x >= 0 && x < LutSize && y >= 0 && y < LutSize;

    private static double Interpolate(
        IReadOnlyList<double> table,
        double preciseX,
        double preciseY)
    {
        var x = (int)preciseX;
        var y = (int)preciseY;

        var incrementX = preciseX - x;
        var incrementY = preciseY - y;
        var max = LutSize - 1;

        var valueXY = table[Offset(x, y)];

        if (x == max)
        {
            if (y == max)
            {
                return valueXY;
            }

            return valueXY
                + incrementY * (table[Offset(x, y + 1)] - valueXY);
        }

        var valuePreciseXY = valueXY
            + incrementX * (table[Offset(x + 1, y)] - valueXY);

        if (y == max)
        {
            return valuePreciseXY;
        }

        var valueXY1 = table[Offset(x, y + 1)];
        var valuePreciseXY1 = valueXY1
            + incrementX * (table[Offset(x + 1, y + 1)] - valueXY1);

        return valuePreciseXY
            + incrementY * (valuePreciseXY1 - valuePreciseXY);
    }

    private static int Offset(int x, int y) => x * LutSize + y;
}
