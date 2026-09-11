using System.Text.Json;
using SvgMusic.Scene;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: dotnet run -- <input.svg> [notation-scene.json]");
    return 2;
}

var input = args[0];
var output = args.Length > 1 ? args[1] : "notation-scene.json";

var pipeline = new ScenePipeline(
    new SvgNormalizer(),
    new ShapeClusterer());

var (geometry, notation) = pipeline.Run(input);

Console.WriteLine($"GeometricScene shapes : {geometry.Shapes.Count}");
Console.WriteLine($"NotationScene prototypes: {notation.Prototypes.Count}");
Console.WriteLine($"NotationScene instances : {notation.Instances.Count}");

foreach (var prototype in notation.Prototypes)
{
    var count = notation.Instances.Count(x => x.PrototypeId == prototype.Id);
    Console.WriteLine($"  {prototype.Id}: {count} instances; representative={prototype.RepresentativeShapeId}");
}

var json = JsonSerializer.Serialize(notation, new JsonSerializerOptions
{
    WriteIndented = true
});
File.WriteAllText(output, json);
Console.WriteLine($"Written: {Path.GetFullPath(output)}");

if (Path.GetFileName(input).Equals("shape-clustering-noteheads.svg", StringComparison.OrdinalIgnoreCase))
{
    var ok = geometry.Shapes.Count == 100
        && notation.Prototypes.Count == 1
        && notation.Instances.Count == 100;

    Console.WriteLine(ok
        ? "Fixture check: PASS (1 prototype, 100 instances)"
        : "Fixture check: FAIL");

    return ok ? 0 : 1;
}

return 0;
