using System.Text.Json;
using SvgMusic.Scene;

if (args.Length < 2)
{
    Console.Error.WriteLine(
        "Usage: dotnet run --project Tools/TextOcrDiagnostics -- <input.svg> <output-directory> [--no-ocr]");
    return 2;
}

var input = Path.GetFullPath(args[0]);
var outputDirectory = Path.GetFullPath(args[1]);
var useOcr = !args.Any(argument =>
    argument.Equals("--no-ocr", StringComparison.OrdinalIgnoreCase));

Directory.CreateDirectory(outputDirectory);

Console.WriteLine("Building current SvgScene...");
var pipeline = new ScenePipeline(
    new SvgNormalizer(),
    new ShapeClusterer());
var (geometry, notation) = pipeline.Run(input);
var layout = new ScoreLayoutAnalyzer().Analyze(notation);

ITextRecognizer recognizer;
IDisposable? recognizerLifetime = null;

if (useOcr)
{
    Console.WriteLine("Loading PP-OCRv5 Latin...");
    var rapidOcr = new RapidOcrTextRecognizer();
    recognizer = rapidOcr;
    recognizerLifetime = rapidOcr;
}
else
{
    Console.WriteLine("OCR disabled; using NullTextRecognizer.");
    recognizer = NullTextRecognizer.Instance;
}

try
{
    Console.WriteLine("Recognizing prototype glyphs and horizontal text runs...");
    var analysis = new TextRecognitionAnalyzer(recognizer).Analyze(
        geometry,
        notation,
        layout);

    var jsonPath = Path.Combine(outputDirectory, "parser.ocr.json");
    var svgPath = Path.Combine(outputDirectory, "parser.ocr.svg");

    File.WriteAllText(
        jsonPath,
        JsonSerializer.Serialize(
            analysis,
            new JsonSerializerOptions { WriteIndented = true }));

    new TextRecognitionDebugRenderer().Render(
        input,
        analysis,
        svgPath);

    var prototypeCount = analysis.Observations.Count(item =>
        item.Kind == TextCandidateKind.Prototype);
    var runCount = analysis.Observations.Count(item =>
        item.Kind == TextCandidateKind.HorizontalRun);
    var recognizedPrototypeCount = analysis.Recognized.Count(item =>
        item.Kind == TextCandidateKind.Prototype);
    var recognizedRunCount = analysis.Recognized.Count(item =>
        item.Kind == TextCandidateKind.HorizontalRun);

    Console.WriteLine($"Prototype observations : {prototypeCount}");
    Console.WriteLine($"Horizontal runs        : {runCount}");
    Console.WriteLine($"Recognized prototypes  : {recognizedPrototypeCount}");
    Console.WriteLine($"Recognized runs        : {recognizedRunCount}");
    Console.WriteLine($"JSON: {jsonPath}");
    Console.WriteLine($"SVG : {svgPath}");
}
finally
{
    recognizerLifetime?.Dispose();
}

return 0;
