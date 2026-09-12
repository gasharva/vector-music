using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace AudiverisGlyphPoc;

public sealed class GlyphBatchClassifier
{
    public GlyphBatchResult ClassifyDirectory(
        AudiverisModel model,
        string directory,
        int top = 8)
    {
        var manifestPath = Path.Combine(directory, "manifest.json");

        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException(
                "Glyph export manifest was not found.",
                manifestPath);
        }

        var manifest = JsonSerializer.Deserialize<GlyphExportManifest>(
            File.ReadAllText(manifestPath),
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            })
            ?? throw new InvalidDataException("Could not deserialize glyph manifest.");

        var results = new List<GlyphClassification>();

        foreach (var item in manifest.Glyphs)
        {
            var pngPath = Path.Combine(directory, item.FileName);

            try
            {
                var image = SimplePngReader.Read(pngPath);
                var glyph = image.ToBinaryGlyph();
                var features = MixGlyphDescriptor.Extract(
                    glyph,
                    item.Interline);
                var predictions = model.Evaluate(features, top);

                results.Add(new GlyphClassification(
                    item.ShapeId,
                    item.PrototypeId,
                    1,
                    item.FileName,
                    item.Interline,
                    glyph.Mass,
                    predictions,
                    null));
            }
            catch (Exception ex)
            {
                results.Add(new GlyphClassification(
                    item.ShapeId,
                    item.PrototypeId,
                    1,
                    item.FileName,
                    item.Interline,
                    0,
                    Array.Empty<Prediction>(),
                    ex.Message));
            }
        }

        var prototypeSummaries = results
            .Where(result => result.Error is null && result.Predictions.Count > 0)
            .Select(result => new PrototypeClassificationSummary(
                result.PrototypeId,
                result.PrototypeInstanceCount,
                result.Predictions[0].Label,
                1,
                result.Predictions[0].Score))
            .OrderByDescending(summary => summary.AverageWinningScore)
            .ThenBy(summary => PrototypeSortKey(summary.PrototypeId))
            .ThenBy(summary => summary.PrototypeId, StringComparer.Ordinal)
            .ToArray();

        var result = new GlyphBatchResult(
            manifest.TargetInterline,
            results,
            prototypeSummaries);

        WriteOutputs(directory, result);

        return result;
    }

    private static void WriteOutputs(
        string directory,
        GlyphBatchResult result)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        File.WriteAllText(
            Path.Combine(directory, "predictions.json"),
            JsonSerializer.Serialize(result, options));

        var csv = new List<string>
        {
            "shapeId,prototypeId,prototypeInstanceCount,fileName,topLabel,topScore,error"
        };

        foreach (var item in OrderByConfidence(result.Glyphs))
        {
            var top = item.Predictions.FirstOrDefault();

            csv.Add(string.Join(
                ",",
                Csv(item.ShapeId),
                Csv(item.PrototypeId),
                item.PrototypeInstanceCount.ToString(CultureInfo.InvariantCulture),
                Csv(item.FileName),
                Csv(top?.Label ?? string.Empty),
                top is null
                    ? string.Empty
                    : top.Score.ToString("G17", CultureInfo.InvariantCulture),
                Csv(item.Error ?? string.Empty)));
        }

        File.WriteAllLines(
            Path.Combine(directory, "predictions.csv"),
            csv);

        File.WriteAllText(
            Path.Combine(directory, "report.html"),
            BuildHtmlReport(result),
            Encoding.UTF8);
    }

    private static string BuildHtmlReport(GlyphBatchResult result)
    {
        var html = new StringBuilder();

        html.AppendLine("<!doctype html>");
        html.AppendLine("<html lang=\"en\">");
        html.AppendLine("<head>");
        html.AppendLine("<meta charset=\"utf-8\">");
        html.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        html.AppendLine("<title>Audiveris glyph classifier report</title>");
        html.AppendLine("<style>");
        html.AppendLine("body { font-family: system-ui, sans-serif; margin: 24px; color: #222; }");
        html.AppendLine("table { border-collapse: collapse; width: 100%; }");
        html.AppendLine("th, td { border-bottom: 1px solid #ddd; padding: 10px 12px; text-align: left; vertical-align: middle; }");
        html.AppendLine("th { position: sticky; top: 0; background: white; z-index: 1; }");
        html.AppendLine("tr:hover { background: #f7f7f7; }");
        html.AppendLine(".glyph { width: 150px; height: 110px; object-fit: contain; image-rendering: pixelated; border: 1px solid #eee; background: white; }");
        html.AppendLine(".score { font-variant-numeric: tabular-nums; font-weight: 600; }");
        html.AppendLine(".high { color: #147a35; }");
        html.AppendLine(".medium { color: #9a6400; }");
        html.AppendLine(".low { color: #9b2c2c; }");
        html.AppendLine(".alternatives { color: #666; font-size: 0.9em; line-height: 1.5; }");
        html.AppendLine(".meta { color: #666; margin-bottom: 18px; }");
        html.AppendLine("code { white-space: nowrap; }");
        html.AppendLine("</style>");
        html.AppendLine("</head>");
        html.AppendLine("<body>");
        html.AppendLine("<h1>Audiveris glyph classifier report</h1>");
        html.AppendLine($"<p class=\"meta\">One representative contour per ShapePrototype. Classified prototypes: {result.Glyphs.Count}. Raster interline: {result.Interline}px. Sorted by classifier confidence, highest first.</p>");
        html.AppendLine("<table>");
        html.AppendLine("<thead><tr><th>Prototype</th><th>Contour</th><th>Winner</th><th>Confidence</th><th>Other candidates</th><th>Shape</th></tr></thead>");
        html.AppendLine("<tbody>");

        foreach (var item in OrderByConfidence(result.Glyphs))
        {
            var top = item.Predictions.FirstOrDefault();
            var score = top?.Score ?? 0;
            var scoreClass = score >= 0.8
                ? "high"
                : score >= 0.5
                    ? "medium"
                    : "low";

            html.AppendLine("<tr>");
            html.AppendLine($"<td><code>{H(item.PrototypeId)}</code></td>");
            html.AppendLine($"<td><img class=\"glyph\" src=\"{H(item.FileName)}\" alt=\"{H(item.ShapeId)}\"></td>");

            if (item.Error is not null)
            {
                html.AppendLine($"<td colspan=\"3\" class=\"low\">{H(item.Error)}</td>");
            }
            else
            {
                html.AppendLine($"<td><strong>{H(top?.Label ?? string.Empty)}</strong></td>");
                html.AppendLine($"<td class=\"score {scoreClass}\">{score:P1}</td>");

                var alternatives = item.Predictions
                    .Skip(1)
                    .Take(5)
                    .Select(prediction =>
                        $"{H(prediction.Label)} <span class=\"score\">{prediction.Score:P1}</span>");

                html.AppendLine($"<td class=\"alternatives\">{string.Join("<br>", alternatives)}</td>");
            }

            html.AppendLine($"<td><code>{H(item.ShapeId)}</code></td>");
            html.AppendLine("</tr>");
        }

        html.AppendLine("</tbody>");
        html.AppendLine("</table>");
        html.AppendLine("</body>");
        html.AppendLine("</html>");

        return html.ToString();
    }

    private static IEnumerable<GlyphClassification> OrderByConfidence(
        IEnumerable<GlyphClassification> glyphs)
    {
        return glyphs
            .OrderBy(item => item.Error is not null)
            .ThenByDescending(item => item.Predictions.FirstOrDefault()?.Score ?? double.MinValue)
            .ThenBy(item => PrototypeSortKey(item.PrototypeId))
            .ThenBy(item => item.PrototypeId, StringComparer.Ordinal);
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
}

public sealed record GlyphExportManifest(
    int TargetInterline,
    double SourceInterline,
    IReadOnlyList<GlyphExportEntry> Glyphs);

public sealed record GlyphExportEntry(
    string ShapeId,
    string PrototypeId,
    string FileName,
    int Interline,
    int Width,
    int Height,
    int ForegroundPixels);

public sealed record GlyphClassification(
    string ShapeId,
    string PrototypeId,
    int PrototypeInstanceCount,
    string FileName,
    int Interline,
    int ForegroundPixels,
    IReadOnlyList<Prediction> Predictions,
    string? Error);

public sealed record PrototypeClassificationSummary(
    string PrototypeId,
    int InstanceCount,
    string WinningLabel,
    int WinningVotes,
    double AverageWinningScore);

public sealed record GlyphBatchResult(
    int Interline,
    IReadOnlyList<GlyphClassification> Glyphs,
    IReadOnlyList<PrototypeClassificationSummary> Prototypes);
