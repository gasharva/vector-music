namespace AudiverisGlyphPoc;

/// <summary>
/// Port of Audiveris MixGlyphDescriptor:
/// 99 ART modules + 10 geometric moments + vertical aspect = 110 features.
/// </summary>
public static class MixGlyphDescriptor
{
    public const int FeatureCount =
        ArtMomentExtractor.MomentCount
        + GeometricMomentExtractor.FeatureCount
        + 1;

    public static double[] Extract(
        BinaryGlyph glyph,
        int interline)
    {
        var art = ArtMomentExtractor.Extract(glyph);
        var geometric = GeometricMomentExtractor.Extract(glyph, interline);
        var result = new double[FeatureCount];

        var index = 0;

        Array.Copy(art, 0, result, index, art.Length);
        index += art.Length;

        Array.Copy(geometric, 0, result, index, geometric.Length);
        index += geometric.Length;

        result[index] = glyph.Height / (double)glyph.Width;

        return result;
    }

    public static string[] CreateLabels()
    {
        var result = new string[FeatureCount];
        var index = 0;

        foreach (var label in ArtMomentExtractor.CreateLabels())
        {
            result[index++] = label;
        }

        foreach (var label in GeometricMomentExtractor.Labels)
        {
            result[index++] = label;
        }

        result[index] = "aspect";

        return result;
    }

    public static void ValidateAgainst(AudiverisModel model)
    {
        var labels = CreateLabels();

        if (model.InputSize != FeatureCount)
        {
            throw new InvalidDataException(
                $"Audiveris model expects {model.InputSize} inputs, "
                + $"but MixGlyphDescriptor produces {FeatureCount}.");
        }

        if (!labels.SequenceEqual(model.InputLabels, StringComparer.Ordinal))
        {
            var mismatch = labels
                .Zip(model.InputLabels, (expected, actual) => (expected, actual))
                .Select((pair, index) => (pair.expected, pair.actual, index))
                .FirstOrDefault(item => !string.Equals(
                    item.expected,
                    item.actual,
                    StringComparison.Ordinal));

            throw new InvalidDataException(
                "MixGlyphDescriptor labels do not match model inputs. "
                + $"First mismatch at {mismatch.index}: "
                + $"expected '{mismatch.expected}', model '{mismatch.actual}'.");
        }
    }
}
