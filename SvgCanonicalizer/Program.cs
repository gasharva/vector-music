using SvgMusic.Canonicalization;

if (args.Length != 2)
{
    Console.Error.WriteLine(
        "Usage: SvgCanonicalizer <input.svg|input-directory> <output.svg|output-directory>");
    return 2;
}

var input = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
var service = new SvgCanonicalizerService();

if (File.Exists(input))
{
    var target = Directory.Exists(output)
        || string.IsNullOrEmpty(Path.GetExtension(output))
            ? Path.Combine(output, Path.GetFileName(input))
            : output;

    var result = service.Canonicalize(input, target);
    Print(result);
    return 0;
}

if (!Directory.Exists(input))
{
    Console.Error.WriteLine($"Input does not exist: {input}");
    return 2;
}

Directory.CreateDirectory(output);

var files = Directory
    .EnumerateFiles(input, "*.svg", SearchOption.TopDirectoryOnly)
    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
    .ToArray();

if (files.Length == 0)
{
    Console.Error.WriteLine($"No SVG files found in: {input}");
    return 3;
}

Console.WriteLine($"Canonicalizing {files.Length} SVG file(s)...");

foreach (var file in files)
{
    var target = Path.Combine(output, Path.GetFileName(file));
    var result = service.Canonicalize(file, target);
    Print(result);
}

return 0;

static void Print(SvgCanonicalizationResult result)
{
    Console.WriteLine(
        $"{Path.GetFileName(result.InputPath)} -> {result.OutputPath}");
    Console.WriteLine(
        $"  paths={result.PathCount}; "
        + $"bounds={result.Bounds.MinX:F3},{result.Bounds.MinY:F3}.."
        + $"{result.Bounds.MaxX:F3},{result.Bounds.MaxY:F3}");
}
