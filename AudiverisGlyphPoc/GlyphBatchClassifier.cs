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
                    item.FileName,
                    item.Interline,
                    0,
                    Array.Empty<Prediction>(),
                    ex.Message));
            }
        }

        var prototypeSummaries = results
            .Where(result => result.Error is null && result.Predictions.Count > 0)
            .GroupBy(result => result.PrototypeId, StringComparer.Ordinal)
            .Select(group =>
            {
                var votes = group
                    .GroupBy(
                        result => result.Predictions[0].Label,
                        StringComparer.Ordinal)
                    .Select(labelGroup => new
                    {
                        Label = labelGroup.Key,
                        Count = labelGroup.Count(),
                        AverageScore = labelGroup.Average(result => result.Predictions[0].Score)
                    })
                    .OrderByDescending(item => item.Count)
                    .ThenByDescending(item => item.AverageScore)
                    .ToArray();

                var winner = votes[0];

                return new PrototypeClassificationSummary(
                    group.Key,
                    group.Count(),
                    winner.Label,
                    winner.Count,
                    winner.AverageScore);
            })
            .OrderBy(summary => summary.PrototypeId, StringComparer.Ordinal)
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
            "shapeId,prototypeId,fileName,topLabel,topScore,error"
        };

        foreach (var item in result.Glyphs)
        {
            var top = item.Predictions.FirstOrDefault();

            csv.Add(string.Join(
                ",",
                Csv(item.ShapeId),
                Csv(item.PrototypeId),
                Csv(item.FileName),
                Csv(top?.Label ?? string.Empty),
                top is null ? string.Empty : top.Score.ToString("G17", System.Globalization.CultureInfo.InvariantCulture),
                Csv(item.Error ?? string.Empty)));
        }

        File.WriteAllLines(
            Path.Combine(directory, "predictions.csv"),
            csv);
    }

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
