using AudiverisGlyphPoc;

var glyphDirectory = ReadOption(args, "--glyph-dir");
var modelPath = ReadOption(args, "--model");

try
{
    var archivePath = await ModelCache.EnsureAsync(modelPath);
    var model = AudiverisModel.Load(archivePath);

    MixGlyphDescriptor.ValidateAgainst(model);

    Console.WriteLine($"Model: {archivePath}");
    Console.WriteLine($"Inputs : {model.InputSize}");
    Console.WriteLine($"Hidden : {model.HiddenSize}");
    Console.WriteLine($"Outputs: {model.OutputSize}");
    Console.WriteLine($"MixGlyphDescriptor features: {MixGlyphDescriptor.FeatureCount}");
    Console.WriteLine("Model input labels match the ported descriptor: PASS");
    Console.WriteLine();

    if (glyphDirectory is not null)
    {
        var result = new MultiScaleGlyphBatchClassifier().ClassifyDirectory(
            model,
            glyphDirectory,
            top: 8);

        var fullDirectory = Path.GetFullPath(glyphDirectory);

        Console.WriteLine($"Glyph directory       : {fullDirectory}");
        Console.WriteLine($"Prototypes classified : {result.Glyphs.Count}");
        Console.WriteLine($"Raster interlines      : 20, 30, 40 px");
        Console.WriteLine($"Preview interline      : {result.PreviewInterline} px");
        Console.WriteLine();
        Console.WriteLine("Best result per prototype:");

        foreach (var item in result.Glyphs)
        {
            var best = item.Scales
                .Select(scale => new
                {
                    scale.Interline,
                    Prediction = scale.Predictions.FirstOrDefault()
                })
                .Where(value => value.Prediction is not null)
                .OrderByDescending(value => value.Prediction!.Score)
                .FirstOrDefault();

            Console.WriteLine(
                $"  {item.PrototypeId,-14} "
                + $"instances={item.PrototypeInstanceCount,-3} "
                + $"{best?.Prediction?.Label ?? "ERROR",-28} "
                + $"score={(best?.Prediction?.Score ?? 0):F4} "
                + $"at={best?.Interline ?? 0}px");
        }

        Console.WriteLine();
        Console.WriteLine($"Written: {Path.Combine(fullDirectory, "multiscale-predictions.json")}");
        Console.WriteLine($"Written: {Path.Combine(fullDirectory, "multiscale-predictions.csv")}");
        Console.WriteLine($"Written: {Path.Combine(fullDirectory, "report.html")}");

        return 0;
    }

    RunSyntheticSmokeTest(model);
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 1;
}

return 0;

static string? ReadOption(
    IReadOnlyList<string> arguments,
    string option)
{
    for (var index = 0; index < arguments.Count; index++)
    {
        if (!arguments[index].Equals(
                option,
                StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (index + 1 >= arguments.Count)
        {
            throw new ArgumentException($"{option} requires a value.");
        }

        return arguments[index + 1];
    }

    return null;
}

static void RunSyntheticSmokeTest(AudiverisModel model)
{
    var glyph = BinaryGlyph.FromRows(
        "   ##   ",
        "  ####  ",
        " ##  ## ",
        " ##  ## ",
        " ###### ",
        " ##  ## ",
        " ##  ## ");

    var features = MixGlyphDescriptor.Extract(glyph, interline: 8);
    var predictions = model.Evaluate(features, top: 10);

    Console.WriteLine($"Synthetic glyph foreground pixels: {glyph.Mass}");
    Console.WriteLine($"Synthetic glyph size             : {glyph.Width} x {glyph.Height}");
    Console.WriteLine($"Generated features               : {features.Length}");
    Console.WriteLine();
    Console.WriteLine("Top classifier outputs for the synthetic glyph:");

    foreach (var prediction in predictions)
    {
        Console.WriteLine($"  {prediction.Label,-28} {prediction.Score:F6}");
    }

    Console.WriteLine();
    Console.WriteLine("End-to-end CPU path is alive:");
    Console.WriteLine("foreground pixels -> ART + geometric moments -> Audiveris classifier");
}
