using System.Text.Json;
using SvgMusic.Scene;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: dotnet run -- <input.svg> <output.json>");
    return 2;
}

var input = args[0];
var output = args[1];

var pipeline = new ScenePipeline(
    new SvgNormalizer(),
    new ShapeClusterer());

var (_, notation) = pipeline.Run(input);

var compact = new
{
    verticalZigZags = notation.VerticalZigZags
};

Directory.CreateDirectory(
    Path.GetDirectoryName(Path.GetFullPath(output))
    ?? Environment.CurrentDirectory);

File.WriteAllText(
    output,
    JsonSerializer.Serialize(
        compact,
        new JsonSerializerOptions { WriteIndented = true }));

Console.WriteLine($"vertical zigzags: {notation.VerticalZigZags.Count}");
return 0;
