using System.Text.Json;
using System.Text.Json.Serialization;
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

    var prototypeCount = analysis.Observations.Count(item =>
        item.Kind == TextCandidateKind.Prototype);
    var runCount = analysis.Observations.Count(item =>
        item.Kind == TextCandidateKind.HorizontalRun);
    var recognizedPrototypeCount = analysis.Recognized.Count(item =>
        item.Kind == TextCandidateKind.Prototype);
    var recognizedRunCount = analysis.Recognized.Count(item =>
        item.Kind == TextCandidateKind.HorizontalRun);

    var jsonPath = Path.Combine(outputDirectory, "parser.ocr.json");
    var svgPath = Path.Combine(outputDirectory, "parser.ocr.svg");

    var report = new
    {
        Engine = useOcr ? "PP-OCRv5-Latin" : "disabled",
        Summary = new
        {
            PrototypeObservations = prototypeCount,
            HorizontalRuns = runCount,
            RecognizedPrototypes = recognizedPrototypeCount,
            RecognizedRuns = recognizedRunCount
        },
        Observations = analysis.Observations.Select(item => new
        {
            item.Id,
            Kind = item.Kind.ToString(),
            Bounds = new
            {
                MinX = Math.Round(item.Bounds.MinX, 3),
                MinY = Math.Round(item.Bounds.MinY, 3),
                MaxX = Math.Round(item.Bounds.MaxX, 3),
                MaxY = Math.Round(item.Bounds.MaxY, 3)
            },
            item.SourceShapeIds,
            item.PrototypeId,
            Recognition = item.Recognition is null
                ? null
                : new
                {
                    item.Recognition.Text,
                    Confidence = Math.Round(item.Recognition.Confidence, 6)
                }
        }).ToArray()
    };

    File.WriteAllText(
        jsonPath,
        JsonSerializer.Serialize(
            report,
            new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            }));

    new TextRecognitionDebugRenderer().Render(
        input,
        analysis,
        svgPath);

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
