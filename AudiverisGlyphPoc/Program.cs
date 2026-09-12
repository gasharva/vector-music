using AudiverisGlyphPoc;

var modelPath = args.Length > 0
    ? args[0]
    : null;

try
{
    var archivePath = await ModelCache.EnsureAsync(modelPath);
    var model = AudiverisModel.Load(archivePath);

    Console.WriteLine($"Model: {archivePath}");
    Console.WriteLine($"Inputs : {model.InputSize}");
    Console.WriteLine($"Hidden : {model.HiddenSize}");
    Console.WriteLine($"Outputs: {model.OutputSize}");
    Console.WriteLine();
    Console.WriteLine("First output labels:");

    foreach (var label in model.OutputLabels.Take(40))
    {
        Console.WriteLine($"  {label}");
    }

    Console.WriteLine();
    Console.WriteLine("Model loader + CPU forward pass are ready.");
    Console.WriteLine("Next step: port Audiveris MixGlyphDescriptor (ART + geometric moments) and feed real glyph pixels.");
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 1;
}

return 0;
