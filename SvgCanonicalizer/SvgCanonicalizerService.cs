using SvgMusic.Scene;

namespace SvgMusic.Canonicalization;

public sealed record SvgCanonicalizationResult(
    string InputPath,
    string OutputPath,
    int ShapeCount,
    int ContourCount,
    BoundsD Bounds);

public sealed class SvgCanonicalizerService
{
    private readonly ISvgNormalizer _normalizer;
    private readonly CanonicalSvgWriter _writer;

    public SvgCanonicalizerService(
        ISvgNormalizer? normalizer = null,
        CanonicalSvgWriter? writer = null)
    {
        _normalizer = normalizer ?? new SvgNormalizer();
        _writer = writer ?? new CanonicalSvgWriter();
    }

    public SvgCanonicalizationResult Canonicalize(
        string inputPath,
        string outputPath)
    {
        var scene = _normalizer.Normalize(inputPath);
        var written = _writer.Write(scene, outputPath);

        return new SvgCanonicalizationResult(
            Path.GetFullPath(inputPath),
            Path.GetFullPath(outputPath),
            written.ShapeCount,
            written.ContourCount,
            written.Bounds);
    }
}
