using System.Globalization;
using System.Net;
using System.Text;
using RapidOcrNet;
using SkiaSharp;
using Svg.Skia;

if (args.Length == 0)
{
    Console.WriteLine("Usage: dotnet run --project Experiments/GlyphOcrPoc -- <glyph-svg-dir> [output-dir]");
    Console.WriteLine("Example: dotnet run --project Experiments/GlyphOcrPoc -- artifacts/glyphs-svg artifacts/glyph-ocr");
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

var tsvPath = Path.Combine(outputDir, "results.tsv");
using (var writer = new StreamWriter(tsvPath, false, Encoding.UTF8))
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

var htmlPath = Path.Combine(outputDir, "results.html");
WriteHtmlReport(rows, outputDir, htmlPath);

var recognized = rows.Count(x => !string.IsNullOrWhiteSpace(x.Predicted));
Console.WriteLine();
Console.WriteLine($"Recognized non-empty: {recognized}/{rows.Count}");
Console.WriteLine($"Total: {swAll.Elapsed.TotalSeconds:0.00} s");
Console.WriteLine($"TSV:   {tsvPath}");
Console.WriteLine($"HTML:  {htmlPath}");

static void WriteHtmlReport(
    IReadOnlyList<Row> rows,
    string outputDir,
    string htmlPath)
{
    var recognized = rows.Count(row => !string.IsNullOrWhiteSpace(row.Predicted));
    var highConfidence = rows.Count(row => row.Confidence >= 0.95);
    var averageMs = rows.Count == 0 ? 0 : rows.Average(row => row.Milliseconds);

    var html = new StringBuilder();
    html.AppendLine("<!doctype html>");
    html.AppendLine("<html lang=\"en\"><head><meta charset=\"utf-8\">");
    html.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
    html.AppendLine("<title>Glyph OCR results</title>");
    html.AppendLine("""
<style>
:root { color-scheme: light; font-family: Arial, Helvetica, sans-serif; }
body { margin: 20px; background: #f5f6f8; color: #202124; }
h1 { margin: 0 0 6px; }
.summary { margin: 0 0 16px; color: #5f6368; }
.grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(190px, 1fr)); gap: 10px; }
.card { background: white; border: 1px solid #dadce0; border-left-width: 5px; border-radius: 8px; padding: 9px; min-width: 0; }
.card.high { border-left-color: #34a853; }
.card.mid { border-left-color: #f9ab00; }
.card.low { border-left-color: #ea4335; }
.card.empty { border-left-color: #9aa0a6; }
.preview { height: 92px; display: flex; align-items: center; justify-content: center; background: white; border: 1px solid #eee; border-radius: 5px; margin-bottom: 7px; }
.preview img { max-width: 100%; max-height: 88px; }
.prediction { font-size: 22px; font-weight: 700; min-height: 27px; margin: 3px 0; overflow-wrap: anywhere; }
.prediction.none { color: #9aa0a6; }
.filename { font: 11px Consolas, monospace; color: #5f6368; overflow-wrap: anywhere; }
.meta { display: flex; gap: 8px; margin-top: 5px; font-size: 12px; color: #3c4043; }
.error { margin-top: 5px; font-size: 11px; color: #b3261e; overflow-wrap: anywhere; }
</style>
""");
    html.AppendLine("</head><body>");
    html.AppendLine("<h1>Glyph OCR results</h1>");
    html.AppendLine($"<div class=\"summary\">{rows.Count} glyphs · {recognized} non-empty · {highConfidence} with confidence ≥ 0.95 · average {averageMs:0.0} ms</div>");
    html.AppendLine("<div class=\"grid\">");

    foreach (var row in rows)
    {
        var relativePng = Path.GetRelativePath(outputDir, row.PngPath).Replace('\\', '/');
        var prediction = string.IsNullOrWhiteSpace(row.Predicted) ? "∅" : row.Predicted;
        var cardClass = string.IsNullOrWhiteSpace(row.Predicted)
            ? "empty"
            : row.Confidence >= 0.95
                ? "high"
                : row.Confidence >= 0.70
                    ? "mid"
                    : "low";

        html.AppendLine($"<div class=\"card {cardClass}\">");
        html.AppendLine($"<div class=\"preview\"><img src=\"{Html(relativePng)}\" alt=\"{Html(row.Glyph)}\"></div>");
        html.AppendLine($"<div class=\"prediction{(prediction == "∅" ? " none" : "")}\">{Html(prediction)}</div>");
        html.AppendLine($"<div class=\"filename\">{Html(row.Glyph)}</div>");
        html.AppendLine($"<div class=\"meta\"><span>conf {row.Confidence:0.000}</span><span>{row.Milliseconds:0.0} ms</span></div>");
        if (!string.IsNullOrWhiteSpace(row.Error))
            html.AppendLine($"<div class=\"error\">{Html(row.Error)}</div>");
        html.AppendLine("</div>");
    }

    html.AppendLine("</div></body></html>");
    File.WriteAllText(htmlPath, html.ToString(), Encoding.UTF8);
}

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
static string Html(string value) => WebUtility.HtmlEncode(value);

internal sealed record Row(
    string Glyph,
    string Predicted,
    double Confidence,
    double Milliseconds,
    string PngPath,
    string? Error);
