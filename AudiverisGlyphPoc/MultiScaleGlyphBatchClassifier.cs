using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace AudiverisGlyphPoc;

public sealed class MultiScaleGlyphBatchClassifier
{
    public MultiScaleGlyphBatchResult ClassifyDirectory(
        AudiverisModel model,
        string directory,
        int top = 8)
    {
        var manifestPath = Path.Combine(directory, "multiscale-manifest.json");

        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException(
                "Multi-scale glyph export manifest was not found.",
                manifestPath);
        }

        var manifest = JsonSerializer.Deserialize<MultiScaleGlyphExportManifest>(
            File.ReadAllText(manifestPath),
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            })
            ?? throw new InvalidDataException("Could not deserialize multi-scale glyph manifest.");

        var results = new List<MultiScaleGlyphClassification>();

        foreach (var item in manifest.Glyphs)
        {
            var scaleResults = new List<ScaleClassification>();
            string? error = null;

            foreach (var variant in item.Variants.OrderBy(variant => variant.Interline))
            {
                try
                {
                    var pngPath = Path.Combine(directory, variant.FileName);
                    var image = SimplePngReader.Read(pngPath);
                    var glyph = image.ToBinaryGlyph();
                    var features = MixGlyphDescriptor.Extract(glyph, variant.Interline);
                    var predictions = model.Evaluate(features, top);

                    scaleResults.Add(new ScaleClassification(
                        variant.Interline,
                        predictions,
                        null));
                }
                catch (Exception ex)
                {
                    scaleResults.Add(new ScaleClassification(
                        variant.Interline,
                        Array.Empty<Prediction>(),
                        ex.Message));
                    error ??= ex.Message;
                }
            }

            results.Add(new MultiScaleGlyphClassification(
                item.ShapeId,
                item.PrototypeId,
                item.PrototypeInstanceCount,
                item.PreviewFileName,
                scaleResults,
                error));
        }

        var result = new MultiScaleGlyphBatchResult(
            manifest.PreviewInterline,
            results);

        WriteOutputs(directory, result);

        return result;
    }

    private static void WriteOutputs(
        string directory,
        MultiScaleGlyphBatchResult result)
    {
        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        File.WriteAllText(
            Path.Combine(directory, "multiscale-predictions.json"),
            JsonSerializer.Serialize(result, jsonOptions));

        var csv = new List<string>
        {
            "prototypeId,shapeId,instances,label20,score20,label30,score30,label40,score40,bestInterline,bestLabel,bestScore"
        };

        foreach (var item in OrderByBestConfidence(result.Glyphs))
        {
            var at20 = TopAt(item, 20);
            var at30 = TopAt(item, 30);
            var at40 = TopAt(item, 40);
            var best = Best(item);

            csv.Add(string.Join(
                ",",
                Csv(item.PrototypeId),
                Csv(item.ShapeId),
                item.PrototypeInstanceCount.ToString(CultureInfo.InvariantCulture),
                Csv(at20?.Label ?? string.Empty),
                ScoreCsv(at20),
                Csv(at30?.Label ?? string.Empty),
                ScoreCsv(at30),
                Csv(at40?.Label ?? string.Empty),
                ScoreCsv(at40),
                best?.Interline.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                Csv(best?.Prediction.Label ?? string.Empty),
                best is null
                    ? string.Empty
                    : best.Prediction.Score.ToString("G17", CultureInfo.InvariantCulture)));
        }

        File.WriteAllLines(
            Path.Combine(directory, "multiscale-predictions.csv"),
            csv);

        File.WriteAllText(
            Path.Combine(directory, "report.html"),
            BuildHtmlReport(result),
            Encoding.UTF8);
    }

    private static string BuildHtmlReport(MultiScaleGlyphBatchResult result)
    {
        var html = new StringBuilder();

        html.AppendLine("<!doctype html>");
        html.AppendLine("<html lang=\"en\">");
        html.AppendLine("<head>");
        html.AppendLine("<meta charset=\"utf-8\">");
        html.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        html.AppendLine("<title>Audiveris multi-scale glyph report</title>");
        html.AppendLine("<style>");
        html.AppendLine("body { font-family: system-ui, sans-serif; margin: 24px; color: #222; }");
        html.AppendLine("table { border-collapse: collapse; width: 100%; }");
        html.AppendLine("th, td { border-bottom: 1px solid #ddd; padding: 10px 12px; text-align: left; vertical-align: middle; }");
        html.AppendLine("th { position: sticky; top: 0; background: white; z-index: 1; }");
        html.AppendLine("tr:hover { background: #f7f7f7; }");
        html.AppendLine(".glyph { width: 150px; height: 110px; object-fit: contain; image-rendering: pixelated; border: 1px solid #eee; background: white; }");
        html.AppendLine(".score { font-variant-numeric: tabular-nums; }");
        html.AppendLine(".best { font-weight: 800; }");
        html.AppendLine(".high { color: #147a35; }");
        html.AppendLine(".medium { color: #9a6400; }");
        html.AppendLine(".low { color: #9b2c2c; }");
        html.AppendLine(".meta { color: #666; margin-bottom: 18px; }");
        html.AppendLine(".variant { white-space: nowrap; line-height: 1.55; }");
        html.AppendLine("code { white-space: nowrap; }");
        html.AppendLine("</style>");
        html.AppendLine("</head>");
        html.AppendLine("<body>");
        html.AppendLine("<h1>Audiveris multi-scale glyph report</h1>");
        html.AppendLine($"<p class=\"meta\">One representative contour per ShapePrototype. Each contour is rasterized independently from vector geometry at interline 20, 30 and 40 px. The preview image is {result.PreviewInterline}px. Rows are sorted by the best confidence across the three scales.</p>");
        html.AppendLine("<table>");
        html.AppendLine("<thead><tr><th>Prototype</th><th>Instances</th><th>Contour</th><th>Winner</th><th>Confidence</th><th>Shape</th></tr></thead>");
        html.AppendLine("<tbody>");

        foreach (var item in OrderByBestConfidence(result.Glyphs))
        {
            var best = Best(item);

            html.AppendLine("<tr>");
            html.AppendLine($"<td><code>{H(item.PrototypeId)}</code></td>");
            html.AppendLine($"<td>{item.PrototypeInstanceCount}</td>");
            html.AppendLine($"<td><img class=\"glyph\" src=\"{H(item.PreviewFileName)}\" alt=\"{H(item.ShapeId)}\"></td>");
            html.AppendLine($"<td>{BuildWinnerCell(item, best)}</td>");
            html.AppendLine($"<td>{BuildConfidenceCell(item, best)}</td>");
            html.AppendLine($"<td><code>{H(item.ShapeId)}</code></td>");
            html.AppendLine("</tr>");
        }

        html.AppendLine("</tbody>");
        html.AppendLine("</table>");
        html.AppendLine("</body>");
        html.AppendLine("</html>");

        return html.ToString();
    }

    private static string BuildWinnerCell(
        MultiScaleGlyphClassification item,
        BestScalePrediction? best)
    {
        var lines = new List<string>();

        foreach (var scale in item.Scales.OrderBy(scale => scale.Interline))
        {
            var top = scale.Predictions.FirstOrDefault();
            var label = top?.Label ?? "ERROR";
            var isBest = best is not null && best.Interline == scale.Interline;
            var value = isBest
                ? $"<strong>{H(label)}</strong>"
                : H(label);

            lines.Add($"<div class=\"variant\">{value} · {scale.Interline}</div>");
        }

        return string.Join(string.Empty, lines);
    }

    private static string BuildConfidenceCell(
        MultiScaleGlyphClassification item,
        BestScalePrediction? best)
    {
        var lines = new List<string>();

        foreach (var scale in item.Scales.OrderBy(scale => scale.Interline))
        {
            var top = scale.Predictions.FirstOrDefault();
            var score = top?.Score ?? 0;
            var scoreClass = score >= 0.8
                ? "high"
                : score >= 0.5
                    ? "medium"
                    : "low";
            var bestClass = best is not null && best.Interline == scale.Interline
                ? " best"
                : string.Empty;

            lines.Add(
                $"<div class=\"variant score {scoreClass}{bestClass}\">"
                + $"{score:P1} · {scale.Interline}</div>");
        }

        return string.Join(string.Empty, lines);
    }

    private static IEnumerable<MultiScaleGlyphClassification> OrderByBestConfidence(
        IEnumerable<MultiScaleGlyphClassification> glyphs)
    {
        return glyphs
            .OrderByDescending(item => Best(item)?.Prediction.Score ?? double.MinValue)
            .ThenBy(item => PrototypeSortKey(item.PrototypeId))
            .ThenBy(item => item.PrototypeId, StringComparer.Ordinal);
    }

    private static BestScalePrediction? Best(MultiScaleGlyphClassification item)
    {
        return item.Scales
            .Select(scale => new
            {
                scale.Interline,
                Prediction = scale.Predictions.FirstOrDefault()
            })
            .Where(value => value.Prediction is not null)
            .OrderByDescending(value => value.Prediction!.Score)
            .Select(value => new BestScalePrediction(
                value.Interline,
                value.Prediction!))
            .FirstOrDefault();
    }

    private static Prediction? TopAt(
        MultiScaleGlyphClassification item,
        int interline)
    {
        return item.Scales
            .FirstOrDefault(scale => scale.Interline == interline)?
            .Predictions
            .FirstOrDefault();
    }

    private static string ScoreCsv(Prediction? prediction)
    {
        return prediction is null
            ? string.Empty
            : prediction.Score.ToString("G17", CultureInfo.InvariantCulture);
    }

    private static int PrototypeSortKey(string prototypeId)
    {
        const string prefix = "prototype-";

        if (prototypeId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(
                prototypeId[prefix.Length..],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var number))
        {
            return number;
        }

        return int.MaxValue;
    }

    private static string H(string value) => WebUtility.HtmlEncode(value);

    private static string Csv(string value)
    {
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private sealed record BestScalePrediction(
        int Interline,
        Prediction Prediction);
}

public sealed record MultiScaleGlyphExportManifest(
    int PreviewInterline,
    double SourceInterline,
    IReadOnlyList<MultiScaleGlyphExportEntry> Glyphs);

public sealed record MultiScaleGlyphExportEntry(
    string ShapeId,
    string PrototypeId,
    int PrototypeInstanceCount,
    string PreviewFileName,
    IReadOnlyList<GlyphRasterVariant> Variants);

public sealed record GlyphRasterVariant(
    int Interline,
    string FileName,
    int Width,
    int Height,
    int ForegroundPixels);

public sealed record ScaleClassification(
    int Interline,
    IReadOnlyList<Prediction> Predictions,
    string? Error);

public sealed record MultiScaleGlyphClassification(
    string ShapeId,
    string PrototypeId,
    int PrototypeInstanceCount,
    string PreviewFileName,
    IReadOnlyList<ScaleClassification> Scales,
    string? Error);

public sealed record MultiScaleGlyphBatchResult(
    int PreviewInterline,
    IReadOnlyList<MultiScaleGlyphClassification> Glyphs);
