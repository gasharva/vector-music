using System.Text.Json;
using SvgMusic.Scene;

if (args.Length == 0)
{
    Console.Error.WriteLine(
        "Usage: dotnet run -- <input.svg> [notation-scene.json] [--debug] [--verbose-json] [--export-glyphs <directory>]");
    return 2;
}

var input = args[0];
var exportGlyphsIndex = Array.FindIndex(
    args,
    argument => argument.Equals(
        "--export-glyphs",
        StringComparison.OrdinalIgnoreCase));

string? exportGlyphsDirectory = null;

if (exportGlyphsIndex >= 0)
{
    if (exportGlyphsIndex + 1 >= args.Length)
    {
        Console.Error.WriteLine("--export-glyphs requires an output directory.");
        return 2;
    }

    exportGlyphsDirectory = args[exportGlyphsIndex + 1];
}

var positional = args
    .Skip(1)
    .Where((argument, index) =>
    {
        var actualIndex = index + 1;

        if (actualIndex == exportGlyphsIndex + 1)
        {
            return false;
        }

        return !argument.StartsWith("--", StringComparison.Ordinal);
    })
    .ToArray();

var output = positional.Length > 0
    ? positional[0]
    : "notation-scene.json";

var debug = args.Any(argument =>
    argument.Equals("--debug", StringComparison.OrdinalIgnoreCase));

var verbose = args.Any(argument =>
    argument.Equals("--verbose-json", StringComparison.OrdinalIgnoreCase));

var clusterer = new ShapeClusterer();
var pipeline = new ScenePipeline(
    new SvgNormalizer(),
    clusterer);

var (geometry, notation) = pipeline.Run(input);
var layout = new ScoreLayoutAnalyzer().Analyze(notation);

Console.WriteLine($"GeometricScene shapes       : {geometry.Shapes.Count}");
Console.WriteLine($"NotationScene contours      : {notation.Instances.Count}");
Console.WriteLine($"NotationScene prototypes    : {notation.Prototypes.Count}");
Console.WriteLine($"NotationScene strokes       : {notation.Strokes.Count}");
Console.WriteLine($"NotationScene curved strokes: {notation.CurvedStrokes.Count}");
Console.WriteLine(
    $"NotationScene ellipses      : {notation.Ellipses.Count} "
    + $"(hollow: {notation.Ellipses.Count(ellipse => ellipse.IsHollow)})");
Console.WriteLine($"ScoreLayout staffs          : {layout.Staffs.Count}");
Console.WriteLine($"ScoreLayout systems         : {layout.Systems.Count}");
Console.WriteLine(
    $"ScoreLayout measures        : "
    + $"{layout.Systems.SelectMany(system => system.StaffPairs).Sum(pair => pair.Measures.Count)}");

foreach (var prototype in notation.Prototypes)
{
    var count = notation.Instances.Count(instance =>
        instance.PrototypeId == prototype.Id);

    Console.WriteLine(
        $"  {prototype.Id}: {count} instances; "
        + $"representative={prototype.RepresentativeShapeId}");
}

var compact = new
{
    prototypes = notation.Prototypes.Select(prototype => new
    {
        id = prototype.Id,
        representativeShapeId = prototype.RepresentativeShapeId,
        instanceCount = notation.Instances.Count(instance =>
            instance.PrototypeId == prototype.Id),
        aspectRatio = prototype.Descriptor.AspectRatio,
        relativeArea = prototype.Descriptor.RelativeArea
    }),
    instances = notation.Instances,
    strokes = notation.Strokes,
    curvedStrokes = notation.CurvedStrokes,
    ellipses = notation.Ellipses
};

var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true
};

File.WriteAllText(
    output,
    JsonSerializer.Serialize(compact, jsonOptions));

Console.WriteLine($"Written compact JSON: {Path.GetFullPath(output)}");

var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(output))
    ?? Environment.CurrentDirectory;

var layoutOutput = Path.Combine(
    outputDirectory,
    "score-layout.json");

File.WriteAllText(
    layoutOutput,
    JsonSerializer.Serialize(layout, jsonOptions));

Console.WriteLine($"Written score layout JSON: {layoutOutput}");

if (exportGlyphsDirectory is not null)
{
    var manifest = new MultiScaleGlyphExporter().Export(
        geometry,
        notation,
        layout,
        exportGlyphsDirectory);

    Console.WriteLine(
        $"Exported classifier glyph prototypes: {manifest.Glyphs.Count} "
        + $"to {Path.GetFullPath(exportGlyphsDirectory)}");
    Console.WriteLine(
        $"Raster interlines: 20, 30, 40 px; "
        + $"preview={manifest.PreviewInterline}px; "
        + $"source={manifest.SourceInterline:F3}");
}

if (verbose)
{
    var verboseOutput = Path.Combine(
        outputDirectory,
        Path.GetFileNameWithoutExtension(output) + ".verbose.json");

    File.WriteAllText(
        verboseOutput,
        JsonSerializer.Serialize(notation, jsonOptions));

    Console.WriteLine($"Written verbose JSON: {verboseOutput}");
}

if (debug)
{
    var debugDirectory = Path.GetDirectoryName(Path.GetFullPath(input))
        ?? Environment.CurrentDirectory;

    var name = Path.GetFileNameWithoutExtension(input);

    new DebugSceneRenderer().RenderAll(
        input,
        notation,
        debugDirectory,
        name);

    var layoutSvg = Path.Combine(
        debugDirectory,
        name + ".layout.svg");

    new ScoreLayoutDebugRenderer().Render(
        input,
        layout,
        layoutSvg);

    Console.WriteLine("Written debug SVGs:");

    foreach (var suffix in new[]
    {
        "strokes",
        "arcs",
        "ellipses",
        "contours",
        "layout"
    })
    {
        Console.WriteLine(
            $"  {Path.Combine(debugDirectory, name + "." + suffix + ".svg")}");
    }

    var arcDiagnostics = Path.Combine(
        debugDirectory,
        name + ".arc-diagnostics.txt");

    File.WriteAllLines(
        arcDiagnostics,
        clusterer.ArcDiagnostics.Select(diagnostic =>
            $"{diagnostic.ShapeId,-10} "
            + $"{diagnostic.Result,-6} "
            + $"{diagnostic.Reason,-36} "
            + diagnostic.Metrics));

    Console.WriteLine($"  {arcDiagnostics}");

    var strokeDiagnostics = Path.Combine(
        debugDirectory,
        name + ".stroke-diagnostics.txt");

    File.WriteAllLines(
        strokeDiagnostics,
        clusterer.StrokeDiagnostics.Select(diagnostic =>
            $"{diagnostic.ShapeId,-10} "
            + $"{diagnostic.Result,-6} "
            + $"{diagnostic.Reason,-32} "
            + diagnostic.Metrics));

    Console.WriteLine($"  {strokeDiagnostics}");
}

if (Path.GetFileName(input).Equals(
    "shape-clustering-noteheads.svg",
    StringComparison.OrdinalIgnoreCase))
{
    var fixtureIsValid = geometry.Shapes.Count == 100
        && notation.Strokes.Count == 0
        && notation.CurvedStrokes.Count == 0
        && notation.Ellipses.Count == 100;

    Console.WriteLine(
        fixtureIsValid
            ? "Fixture check: PASS (100 ellipse-like noteheads)"
            : "Fixture check: FAIL");

    return fixtureIsValid ? 0 : 1;
}

return 0;
