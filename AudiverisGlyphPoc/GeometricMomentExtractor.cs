namespace AudiverisGlyphPoc;

/// <summary>
/// Port of the first ten values produced by Audiveris GeometricMoments.
/// Hu moments and centroid coordinates are intentionally omitted because
/// MixGlyphDescriptor uses only values 0..9.
/// </summary>
public static class GeometricMomentExtractor
{
    public const int FeatureCount = 10;

    public static double[] Extract(
        BinaryGlyph glyph,
        int interline)
    {
        if (interline <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(interline),
                "Audiveris geometric moments require a positive interline value.");
        }

        var dim = glyph.Mass;
        var xx = glyph.X;
        var yy = glyph.Y;

        var xMin = int.MaxValue;
        var xMax = int.MinValue;
        var yMin = int.MaxValue;
        var yMax = int.MinValue;

        var n00 = dim / (double)(interline * interline);
        var n01 = 0.0;
        var n02 = 0.0;
        var n03 = 0.0;
        var n10 = 0.0;
        var n11 = 0.0;
        var n12 = 0.0;
        var n20 = 0.0;
        var n21 = 0.0;
        var n30 = 0.0;

        var weight = (double)dim;
        var weight2 = weight * weight;
        var weight3 = Math.Sqrt(weight * weight * weight * weight * weight);

        for (var i = dim - 1; i >= 0; i--)
        {
            var x = xx[i];
            var y = yy[i];

            n10 += x;
            n01 += y;

            xMin = Math.Min(xMin, x);
            xMax = Math.Max(xMax, x);
            yMin = Math.Min(yMin, y);
            yMax = Math.Max(yMax, y);
        }

        n10 /= dim;
        n01 /= dim;

        for (var i = dim - 1; i >= 0; i--)
        {
            var x = xx[i] - n10;
            var y = yy[i] - n01;

            n11 += x * y;
            n12 += x * y * y;
            n21 += x * x * y;
            n20 += x * x;
            n02 += y * y;
            n30 += x * x * x;
            n03 += y * y * y;
        }

        n11 /= weight2;
        n20 /= weight2;
        n02 /= weight2;

        n12 /= weight3;
        n21 /= weight3;
        n30 /= weight3;
        n03 /= weight3;

        return
        [
            n00,
            (xMax - xMin + 1) / (double)interline,
            (yMax - yMin + 1) / (double)interline,
            n20,
            n11,
            n02,
            n30,
            n21,
            n12,
            n03
        ];
    }

    public static readonly string[] Labels =
    [
        "weight",
        "width",
        "height",
        "n20",
        "n11",
        "n02",
        "n30",
        "n21",
        "n12",
        "n03"
    ];
}
