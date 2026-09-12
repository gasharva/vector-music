using AudiverisGlyphPoc;

var modelPath = args.Length > 0
    ? args[0]
    : null;

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

    // A deliberately tiny synthetic foreground glyph.
    // It is not meant to be musically meaningful; the purpose is to exercise
    // foreground pixels -> ART + geometric moments -> normalization -> network.
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
    Console.WriteLine();
    Console.WriteLine("Next correctness gate: compare a real glyph feature vector with Audiveris Java output.");
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 1;
}

return 0;
