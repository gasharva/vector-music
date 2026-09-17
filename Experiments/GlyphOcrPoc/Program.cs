using System.Globalization;
using RapidOcrNet;
using SkiaSharp;
using Svg.Skia;

if (args.Length == 0)
{
    Console.WriteLine("Usage: dotnet run --project Experiments/GlyphOcrPoc -- <glyph-svg-dir> [output-dir]");
    Console.WriteLine("Example: dotnet run --project Experiments/GlyphOcrPoc -- artifacts/step-by-step/music-symbols artifacts/glyph-ocr");
    return;
}

var inputDir = Path.GetFullPath(args[0]);
var outputDir = Path.GetFullPath(args.Length > 1 ? args[1] : Path.Combine(inputDir, "..", "glyph-ocr"));
var pngDir = Path.Combine(outputDir, "png");
Directory.CreateDirectory(pngDir);

var files = Directory.Exists(inputDir)
    ? Directory.EnumerateFiles(inputDir, "*.svg", SearchOption.AllDirectories).OrderBy(x => x).ToArray()
    : Array.Empty<string>();

if (files.Length == 0)
{
    Console.Error.WriteLine($"No SVG glyph files found under: {inputDir}");
    Environment.ExitCode = 2;
    return;
}

Console.WriteLine($"Input:  {inputDir}");
Console.WriteLine($"Glyphs: {files.Length}");
Console.WriteLine($"Output: {outputDir}");
Console.WriteLine("Loading bundled PP-OCRv5 Latin models...");

using var ocr = new RapidOcr();
ocr.InitModels(RapidOcrModelSet.PPOCRv5Latin);

var rows = new List<Row>(files.Length);
var swAll = System.Diagnostics.Stopwatch.StartNew();

for (var i = 0; i < files.Length; i++)
{
    var svgPath = files[i];
    var relative = Path.GetRelativePath(inputDir, svgPath);
    var safeName = MakeSafeFileName(Path.ChangeExtension(relative, null)!.Replace(Path.DirectorySeparatorChar, '_'));
    var pngPath = Path.Combine(pngDir, safeName + ".png");

    try
    {
        Rasterize(svgPath, pngPath, targetWidth: 256, targetHeight: 128, padding: 16);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = ocr.Detect(pngPath, RapidOcrOptions.Default);
        sw.Stop();

        var blocks = result.TextBlocks ?? [];
        var text = string.Join(" | ", blocks.Select(x => x.Text).Where(x => !string.IsNullOrWhiteSpace(x)));
        var scores = blocks
            .SelectMany(x => x.CharScores ?? [])
            .ToArray();
        var confidence = scores.Length == 0 ? 0d : scores.Average();

        rows.Add(new Row(relative, text, confidence, sw.Elapsed.TotalMilliseconds, pngPath, null));
        Console.WriteLine($"[{i + 1,4}/{files.Length}] {relative} => '{text}'  conf={confidence:0.000}  {sw.Elapsed.TotalMilliseconds:0.0} ms");
    }
    catch (Exception ex)
    {
        rows.Add(new Row(relative, "", 0, 0, pngPath, ex.Message));
        Console.WriteLine($"[{i + 1,4}/{files.Length}] {relative} => ERROR: {ex.Message}");
    }
}

swAll.Stop();

var csvPath = Path.Combine(outputDir, "results.tsv");
using (var writer = new StreamWriter(csvPath, false, System.Text.Encoding.UTF8))
{
    await writer.WriteLineAsync("glyph\tpredicted\tconfidence\tms\tpng\terror");
    foreach (var row in rows)
    {
        await writer.WriteLineAsync(string.Join('\t',
            Escape(row.Glyph),
            Escape(row.Predicted),
            row.Confidence.ToString("0.000000", CultureInfo.InvariantCulture),
            row.Milliseconds.ToString("0.000", CultureInfo.InvariantCulture),
            Escape(Path.GetRelativePath(outputDir, row.PngPath)),
            Escape(row.Error ?? "")));
    }
}

var recognized = rows.Count(x => !string.IsNullOrWhiteSpace(x.Predicted));
Console.WriteLine();
Console.WriteLine($"Recognized non-empty: {recognized}/{rows.Count}");
Console.WriteLine($"Total: {swAll.Elapsed.TotalSeconds:0.00} s");
Console.WriteLine($"Results: {csvPath}");

static void Rasterize(string svgPath, string pngPath, int targetWidth, int targetHeight, int padding)
{
    using var svg = new SKSvg();
    var picture = svg.Load(svgPath) ?? throw new InvalidOperationException("Svg.Skia returned null picture.");
    var bounds = picture.CullRect;
    if (bounds.Width <= 0 || bounds.Height <= 0)
        throw new InvalidOperationException($"Invalid SVG bounds: {bounds}");

    using var bitmap = new SKBitmap(targetWidth, targetHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
    using var canvas = new SKCanvas(bitmap);
    canvas.Clear(SKColors.White);

    var availableW = targetWidth - 2f * padding;
    var availableH = targetHeight - 2f * padding;
    var scale = Math.Min(availableW / bounds.Width, availableH / bounds.Height);
    var tx = (targetWidth - bounds.Width * scale) / 2f - bounds.Left * scale;
    var ty = (targetHeight - bounds.Height * scale) / 2f - bounds.Top * scale;

    canvas.Translate(tx, ty);
    canvas.Scale(scale);
    canvas.DrawPicture(picture);
    canvas.Flush();

    using var image = SKImage.FromBitmap(bitmap);
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    using var stream = File.Create(pngPath);
    data.SaveTo(stream);
}

static string MakeSafeFileName(string value)
{
    var invalid = Path.GetInvalidFileNameChars().ToHashSet();
    return new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
}

static string Escape(string value) => value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

internal sealed record Row(
    string Glyph,
    string Predicted,
    double Confidence,
    double Milliseconds,
    string PngPath,
    string? Error);
