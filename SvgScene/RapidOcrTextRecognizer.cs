using RapidOcrNet;
using SkiaSharp;
using Svg.Skia;

namespace SvgMusic.Scene;

public sealed class RapidOcrTextRecognizer : ITextRecognizer, IDisposable
{
    private const int TargetHeight = 128;
    private const int Padding = 16;
    private const int MinimumWidth = 256;
    private const int MaximumWidth = 1024;

    private readonly RapidOcr _ocr;
    private readonly string _workingDirectory;
    private bool _disposed;

    public RapidOcrTextRecognizer(string? modelDirectory = null)
    {
        _workingDirectory = Path.Combine(
            Path.GetTempPath(),
            "vector-music-ocr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workingDirectory);

        var modelRoot = modelDirectory is null
            ? Path.Combine(AppContext.BaseDirectory, "models", "v5")
            : Path.GetFullPath(modelDirectory);
        var preset = RapidOcrModelSet.PPOCRv5Latin with
        {
            DetModelPath = Path.Combine(modelRoot, "ch_PP-OCRv5_mobile_det.onnx"),
            ClsModelPath = Path.Combine(modelRoot, "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx"),
            RecModelPath = Path.Combine(modelRoot, "latin_PP-OCRv5_rec_mobile_infer.onnx"),
            KeysPath = Path.Combine(modelRoot, "ppocrv5_latin_dict.txt")
        };

        EnsureModelExists(preset.DetModelPath, "detector");
        EnsureModelExists(preset.ClsModelPath, "angle classifier");
        EnsureModelExists(preset.RecModelPath, "recognizer");
        EnsureModelExists(preset.KeysPath, "dictionary");

        _ocr = new RapidOcr();
        _ocr.InitModels(preset);
    }

    public TextRecognition? Recognize(TextRecognitionCandidate candidate)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var token = Guid.NewGuid().ToString("N");
        var svgPath = Path.Combine(_workingDirectory, token + ".svg");
        var pngPath = Path.Combine(_workingDirectory, token + ".png");

        try
        {
            File.WriteAllText(svgPath, candidate.Svg);
            Rasterize(svgPath, pngPath);

            var options = RapidOcrOptions.Default with
            {
                // Vector-score text is always upright. The angle classifier only
                // distinguishes 0/180 degrees and can turn musical glyphs upside
                // down before recognition (for example mp -> du).
                DoAngle = false
            };
            var result = _ocr.Detect(pngPath, options);
            var blocks = result.TextBlocks ?? [];
            var text = string.Join(
                " ",
                blocks
                    .Select(block => block.Text?.Trim())
                    .Where(value => !string.IsNullOrWhiteSpace(value)));

            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var scores = blocks
                .SelectMany(block => block.CharScores ?? [])
                .ToArray();
            var confidence = scores.Length == 0
                ? 0d
                : scores.Average();

            return new TextRecognition(
                text,
                confidence,
                "PP-OCRv5-Latin");
        }
        finally
        {
            TryDelete(svgPath);
            TryDelete(pngPath);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ocr.Dispose();

        try
        {
            Directory.Delete(_workingDirectory, recursive: true);
        }
        catch
        {
            // Temporary OCR files are best-effort cleanup only.
        }
    }

    private static void EnsureModelExists(string path, string role)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"PP-OCRv5 {role} model is missing next to the executable: '{path}'.",
                path);
        }
    }

    private static void Rasterize(string svgPath, string pngPath)
    {
        using var svg = new SKSvg();
        var picture = svg.Load(svgPath)
            ?? throw new InvalidOperationException("Svg.Skia returned null picture.");
        var bounds = picture.CullRect;

        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new InvalidOperationException($"Invalid OCR candidate SVG bounds: {bounds}");
        }

        var availableHeight = TargetHeight - 2f * Padding;
        var proportionalWidth = (int)Math.Ceiling(
            bounds.Width / bounds.Height * availableHeight + 2f * Padding);
        var targetWidth = Math.Clamp(
            proportionalWidth,
            MinimumWidth,
            MaximumWidth);

        using var bitmap = new SKBitmap(
            targetWidth,
            TargetHeight,
            SKColorType.Bgra8888,
            SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);

        var availableWidth = targetWidth - 2f * Padding;
        var scale = Math.Min(
            availableWidth / bounds.Width,
            availableHeight / bounds.Height);
        var tx = (targetWidth - bounds.Width * scale) / 2f - bounds.Left * scale;
        var ty = (TargetHeight - bounds.Height * scale) / 2f - bounds.Top * scale;

        canvas.Translate(tx, ty);
        canvas.Scale(scale);
        canvas.DrawPicture(picture);
        canvas.Flush();

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(pngPath);
        data.SaveTo(stream);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Temporary OCR files are best-effort cleanup only.
        }
    }
}
