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
        var result = new GlyphBatchClassifier().ClassifyDirectory(
            model,
            glyphDirectory,
            top: 8);

        Console.WriteLine($"Glyph directory: {Path.GetFullPath(glyphDirectory)}");
        Console.WriteLine($"Glyphs classified: {result.Glyphs.Count}");
        Console.WriteLine($"Prototype groups : {result.Prototypes.Count}");
        Console.WriteLine($"Errors           : {result.Glyphs.Count(item => item.Error is not null)}");
        Console.WriteLine();
        Console.WriteLine("Prototype winners:");

        foreach (var summary in result.Prototypes)
        {
            Console.WriteLine(
                $"  {summary.PrototypeId,-8} "
                + $"{summary.WinningLabel,-28} "
                + $"votes={summary.WinningVotes}/{summary.InstanceCount} "
                + $"score={summary.AverageWinningScore:F4}");
        }

        Console.WriteLine();
        Console.WriteLine(
            $"Written: {Path.Combine(Path.GetFullPath(glyphDirectory), "predictions.json")}");
        Console.WriteLine(
            $"Written: {Path.Combine(Path.GetFullPath(glyphDirectory), "predictions.csv")}");

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
