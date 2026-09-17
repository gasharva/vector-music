using System.Text.Json;
using SvgMusic.Scene;

if (args.Length == 0)
{
    Console.Error.WriteLine(
        "Usage: dotnet run -- <input.svg> [notation-scene.json] "
        + "[--debug] [--verbose-json] [--export-glyphs <directory>] "
        + "[--export-glyphs-svg <directory>] "
        + "[--classify-symbols] [--model <basic-classifier.zip>]");
    return 2;
}

var input = args[0];
var exportGlyphsIndex = FindOption(args, "--export-glyphs");
var exportGlyphsSvgIndex = FindOption(args, "--export-glyphs-svg");
var modelIndex = FindOption(args, "--model");

var exportGlyphsDirectory = ReadOptionValue(
    args,
    exportGlyphsIndex,
    "--export-glyphs");
var exportGlyphsSvgDirectory = ReadOptionValue(
    args,
    exportGlyphsSvgIndex,
    "--export-glyphs-svg");
var modelPath = ReadOptionValue(
    args,
    modelIndex,
    "--model");

var positional = args
    .Skip(1)
    .Where((argument, index) =>
    {
        var actualIndex = index + 1;

        if (actualIndex == exportGlyphsIndex + 1
            || actualIndex == exportGlyphsSvgIndex + 1
            || actualIndex == modelIndex + 1)
        {
            return false;
        }

        return !argument.StartsWith("--", StringComparison.Ordinal);
    })
    .ToArray();

var output = positional.Length > 0
    ? positional[0]
    : "notation-scene.json";
var debug = HasOption(args, "--debug");
var verbose = HasOption(args, "--verbose-json");
var classifySymbols = HasOption(args, "--classify-symbols");

var clusterer = new ShapeClusterer();
var pipeline = new ScenePipeline(
    new SvgNormalizer(),
    clusterer);

var (geometry, notation) = pipeline.Run(input);
var layout = new ScoreLayoutAnalyzer().Analyze(notation);
IReadOnlyList<string> bassClefRepairDiagnostics = Array.Empty<string>();

if (classifySymbols)
{
    Console.WriteLine("Classifying contour prototypes with Audiveris BasicClassifier...");

    var classifier = await AudiverisSymbolClassifier.CreateAsync(modelPath);
    notation = new PrototypeSymbolClassifier(classifier).Classify(
        geometry,
        notation,
        layout);

    var bassClefRepair = new BassClefCompositeRepair(classifier);
    notation = bassClefRepair.Repair(
        geometry,
        notation,
        layout);
    bassClefRepairDiagnostics = bassClefRepair.Diagnostics.ToArray();

    foreach (var diagnostic in bassClefRepairDiagnostics)
    {
        Console.WriteLine($"[bass-clef-repair] {diagnostic}");
    }

    var classifiedPrototypes = notation.Prototypes.Count(prototype =>
        prototype.Classification is not null);
    var confidentPrototypes = notation.Prototypes.Count(prototype =>
        prototype.Classification is { Confidence: >= 0.75 });

    Console.WriteLine(
        $"Classified prototypes        : {classifiedPrototypes}");
    Console.WriteLine(
        $"Confident prototypes >= 0.75: {confidentPrototypes}");
}

var ownershipResult = new LogicalOwnershipAnalyzer().AnalyzeAndApply(
    geometry,
    notation,
    layout);

ownershipResult = new FourthGenerationOuterBandAssigner().AssignAndApply(
    geometry,
    ownershipResult.Scene,
    layout,
    ownershipResult.Ownership);

notation = ownershipResult.Scene;
var logicalOwnership = ownershipResult.Ownership;

Console.WriteLine($"GeometricScene shapes       : {geometry.Shapes.Count}");
Console.WriteLine($"NotationScene contours      : {notation.Instances.Count}");
Console.WriteLine($"NotationScene prototypes    : {notation.Prototypes.Count}");
Console.WriteLine($"NotationScene strokes       : {notation.Strokes.Count}");
Console.WriteLine($"NotationScene curved strokes: {notation.CurvedStrokes.Count}");
Console.WriteLine($"NotationScene vertical zigzags: {notation.VerticalZigZags.Count}");
Console.WriteLine(
    $"NotationScene ellipses      : {notation.Ellipses.Count} "
    + $"(hollow: {notation.Ellipses.Count(ellipse => ellipse.IsHollow)})");
Console.WriteLine($"ScoreLayout staffs          : {layout.Staffs.Count}");
Console.WriteLine($"ScoreLayout systems         : {layout.Systems.Count}");
Console.WriteLine(
    $"ScoreLayout measures        : "
    + $"{layout.Systems.SelectMany(system => system.StaffPairs).Sum(pair => pair.Measures.Count)}");
Console.WriteLine(
    $"Logical ownership assigned  : {logicalOwnership.Assignments.Count}");
Console.WriteLine(
    $"  generation 1              : {logicalOwnership.Assignments.Count(item => item.Ownership.Generation == 1)}");
Console.WriteLine(
    $"  generation 2              : {logicalOwnership.Assignments.Count(item => item.Ownership.Generation == 2)}");
Console.WriteLine(
    $"  generation 3              : {logicalOwnership.Assignments.Count(item => item.Ownership.Generation == 3)}");
Console.WriteLine(
    $"  generation 4              : {logicalOwnership.Assignments.Count(item => item.Ownership.Generation == 4)}");
Console.WriteLine(
    $"  coordinate spans          : {logicalOwnership.Assignments.Count(item => item.Ownership.IsSpan)}");

foreach (var prototype in notation.Prototypes)
{
    var count = notation.Instances.Count(instance =>
        instance.PrototypeId == prototype.Id);
    var classification = prototype.Classification is null
        ? string.Empty
        : $"; class={prototype.Classification.Label} "
          + $"{prototype.Classification.Confidence:P1} "
          + $"@{prototype.Classification.Interline}px";

    Console.WriteLine(
        $"  {prototype.Id}: {count} instances; "
        + $"representative={prototype.RepresentativeShapeId}"
        + classification);
}

var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true
};

var compact = new
{
    prototypes = notation.Prototypes.Select(prototype => new
    {
        id = prototype.Id,
        representativeShapeId = prototype.RepresentativeShapeId,
        instanceCount = notation.Instances.Count(instance =>
            instance.PrototypeId == prototype.Id),
        aspectRatio = prototype.Descriptor.AspectRatio,
        relativeArea = prototype.Descriptor.RelativeArea,
        classification = prototype.Classification
    }),
    instances = notation.Instances,
    strokes = notation.Strokes,
    curvedStrokes = notation.CurvedStrokes,
    verticalZigZags = notation.VerticalZigZags,
    ellipses = notation.Ellipses,
    logicalOwnership = logicalOwnership.Assignments
};

File.WriteAllText(
    output,
    JsonSerializer.Serialize(compact, jsonOptions));

Console.WriteLine($"Written compact JSON: {Path.GetFullPath(output)}");

var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(output))
    ?? Environment.CurrentDirectory;
var baseName = Path.GetFileNameWithoutExtension(input);

var layoutOutput = Path.Combine(
    outputDirectory,
    "score-layout.json");
File.WriteAllText(
    layoutOutput,
    JsonSerializer.Serialize(layout, jsonOptions));
Console.WriteLine($"Written score layout JSON: {layoutOutput}");

var ownershipJson = Path.Combine(
    outputDirectory,
    baseName + ".ownership.json");
File.WriteAllText(
    ownershipJson,
    JsonSerializer.Serialize(logicalOwnership, jsonOptions));
Console.WriteLine($"Written logical ownership JSON: {ownershipJson}");

var ownershipLog = Path.Combine(
    outputDirectory,
    baseName + ".ownership.txt");
File.WriteAllLines(
    ownershipLog,
    logicalOwnership.Assignments.Select(FormatOwnership));
Console.WriteLine($"Written logical ownership log: {ownershipLog}");

var ownershipSvg = Path.Combine(
    outputDirectory,
    baseName + ".ownership.svg");
new LogicalOwnershipDebugRenderer().Render(
    input,
    geometry,
    notation,
    layout,
    logicalOwnership,
    ownershipSvg);
Console.WriteLine($"Written logical ownership SVG: {ownershipSvg}");

if (classifySymbols)
{
    var classifiedSvg = Path.Combine(
        outputDirectory,
        baseName + ".classified-symbols.svg");

    new SymbolClassificationDebugRenderer().Render(
        input,
        geometry,
        notation,
        classifiedSvg);

    Console.WriteLine($"Written classified symbols SVG: {classifiedSvg}");

    var repairLog = Path.Combine(
        outputDirectory,
        baseName + ".bass-clef-repair.txt");

    File.WriteAllLines(repairLog, bassClefRepairDiagnostics);
    Console.WriteLine($"Written bass clef repair log: {repairLog}");
}

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
        $"Raster interline: source={manifest.SourceInterline:F3}; "
        + "targets=20/30/40px");
}

if (exportGlyphsSvgDirectory is not null)
{
    var manifest = new PrototypeSvgExporter().Export(
        geometry,
        notation,
        exportGlyphsSvgDirectory);

    Console.WriteLine(
        $"Exported vector glyph prototypes: {manifest.Glyphs.Count} "
        + $"to {Path.GetFullPath(exportGlyphsSvgDirectory)}");
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

    new DebugSceneRenderer().RenderAll(
        input,
        notation,
        debugDirectory,
        baseName);

    var layoutSvg = Path.Combine(
        debugDirectory,
        baseName + ".layout.svg");

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
            $"  {Path.Combine(debugDirectory, baseName + "." + suffix + ".svg")}");
    }

    var arcDiagnostics = Path.Combine(
        debugDirectory,
        baseName + ".arc-diagnostics.txt");

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
        baseName + ".stroke-diagnostics.txt");

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

static int FindOption(string[] arguments, string option) =>
    Array.FindIndex(
        arguments,
        argument => argument.Equals(option, StringComparison.OrdinalIgnoreCase));

static string? ReadOptionValue(
    string[] arguments,
    int optionIndex,
    string option)
{
    if (optionIndex < 0)
    {
        return null;
    }

    if (optionIndex + 1 >= arguments.Length)
    {
        throw new ArgumentException($"{option} requires a value.");
    }

    return arguments[optionIndex + 1];
}

static bool HasOption(string[] arguments, string option) =>
    arguments.Any(argument =>
        argument.Equals(option, StringComparison.OrdinalIgnoreCase));

static string FormatOwnership(LogicalOwnershipAssignment assignment)
{
    var ownership = assignment.Ownership;
    var start = $"{ownership.Start.StaffId}+{ownership.Start.MeasureId}";
    var end = $"{ownership.End.StaffId}+{ownership.End.MeasureId}";
    var parent = ownership.ParentShapeId is null
        ? "-"
        : ownership.ParentShapeId;

    return $"{assignment.ShapeId,-12} "
        + $"{assignment.Kind,-13} "
        + $"g{ownership.Generation} "
        + $"{start} -> {end} "
        + $"reason={ownership.Reason}; "
        + $"parent={parent}; "
        + $"distance={ownership.Distance:F3}";
}
