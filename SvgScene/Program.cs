using System.Text.Json;
using SvgMusic.Scene;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: dotnet run -- <input.svg> [notation-scene.json] [--debug] [--verbose-json]");
    return 2;
}

var input = args[0];
var positional = args.Skip(1).Where(x => !x.StartsWith("--", StringComparison.Ordinal)).ToArray();
var output = positional.Length > 0 ? positional[0] : "notation-scene.json";
var debug = args.Any(x => x.Equals("--debug", StringComparison.OrdinalIgnoreCase));
var verboseJson = args.Any(x => x.Equals("--verbose-json", StringComparison.OrdinalIgnoreCase));

var pipeline = new ScenePipeline(
    new SvgNormalizer(),
    new ShapeClusterer(new GeometryAnalyzer()));

var (geometry, notation) = pipeline.Run(input);

Console.WriteLine($"GeometricScene shapes    : {geometry.Shapes.Count}");
Console.WriteLine($"NotationScene contours   : {notation.Instances.Count}");
Console.WriteLine($"NotationScene prototypes : {notation.Prototypes.Count}");
Console.WriteLine($"NotationScene strokes    : {notation.Strokes.Count}");

foreach (var prototype in notation.Prototypes)
{
    var count = notation.Instances.Count(x => x.PrototypeId == prototype.Id);
    Console.WriteLine($"  {prototype.Id}: {count} instances; representative={prototype.RepresentativeShapeId}");
}

var compact = new
{
    prototypes = notation.Prototypes.Select(p => new
    {
        id = p.Id,
        representativeShapeId = p.RepresentativeShapeId,
        instanceCount = notation.Instances.Count(x => x.PrototypeId == p.Id),
        aspectRatio = p.Descriptor.AspectRatio,
        relativeArea = p.Descriptor.RelativeArea
    }),
    instances = notation.Instances,
    strokes = notation.Strokes
};

var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
File.WriteAllText(output, JsonSerializer.Serialize(compact, jsonOptions));
Console.WriteLine($"Written compact JSON: {Path.GetFullPath(output)}");

if (verboseJson)
{
    var verboseOutput = Path.Combine(
        Path.GetDirectoryName(Path.GetFullPath(output)) ?? Environment.CurrentDirectory,
        Path.GetFileNameWithoutExtension(output) + ".verbose.json");

    File.WriteAllText(verboseOutput, JsonSerializer.Serialize(notation, jsonOptions));
    Console.WriteLine($"Written verbose JSON: {verboseOutput}");
}

if (debug)
{
    var debugOutput = Path.Combine(
        Path.GetDirectoryName(Path.GetFullPath(input)) ?? Environment.CurrentDirectory,
        Path.GetFileNameWithoutExtension(input) + ".clusters.svg");

    new DebugSceneRenderer().Render(input, notation, debugOutput);
    Console.WriteLine($"Written debug SVG: {debugOutput}");
}

if (Path.GetFileName(input).Equals("shape-clustering-noteheads.svg", StringComparison.OrdinalIgnoreCase))
{
    var ok = geometry.Shapes.Count == 100
        && notation.Strokes.Count == 0
        && notation.Prototypes.Count == 1
        && notation.Instances.Count == 100;

    Console.WriteLine(ok
        ? "Fixture check: PASS (1 prototype, 100 instances, 0 strokes)"
        : "Fixture check: FAIL");

    return ok ? 0 : 1;
}

return 0;
