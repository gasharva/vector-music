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
    Console.WriteLine("OCR-probing raw glyphs, then recognizing uncertain horizontal runs...");
    var analysis = new RawTextRecognitionAnalyzer(recognizer).Analyze(
        geometry,
        layout);

    var rawGlyphObservations = analysis.Observations
        .Where(item => item.Kind == TextCandidateKind.Prototype)
        .ToArray();
    var runObservations = analysis.Observations
        .Where(item => item.Kind == TextCandidateKind.HorizontalRun)
        .ToArray();
    var uncertainRawGlyphs = rawGlyphObservations
        .Where(item => RawTextRecognitionAnalyzer.IsUncertainSingleton(item.Recognition))
        .ToArray();
    var recognizedRuns = runObservations
        .Where(item => !string.IsNullOrWhiteSpace(item.Recognition?.Text))
        .ToArray();

    var jsonPath = Path.Combine(outputDirectory, "parser.ocr.json");
    var svgPath = Path.Combine(outputDirectory, "parser.ocr.svg");
    var runsSvgPath = Path.Combine(outputDirectory, "parser.ocr.runs.svg");

    // Keep JSON useful rather than gigantic: retain every run plus only the raw
    // singleton probes that actually triggered the uncertainty rule.
    var reportObservations = uncertainRawGlyphs
        .Concat(runObservations)
        .ToArray();

    var report = new
    {
        Engine = useOcr ? "PP-OCRv5-Latin" : "disabled",
        ClearSingletonConfidence = RawTextRecognitionAnalyzer.ClearSingletonConfidence,
        Summary = new
        {
            RawGlyphsProbed = rawGlyphObservations.Length,
            UncertainRawGlyphs = uncertainRawGlyphs.Length,
            HorizontalRuns = runObservations.Length,
            RecognizedRuns = recognizedRuns.Length
        },
        Observations = reportObservations.Select(item => new
        {
            item.Id,
            Kind = item.Kind == TextCandidateKind.HorizontalRun ? "HorizontalRun" : "RawGlyph",
            Bounds = new
            {
                MinX = Math.Round(item.Bounds.MinX, 3),
                MinY = Math.Round(item.Bounds.MinY, 3),
                MaxX = Math.Round(item.Bounds.MaxX, 3),
                MaxY = Math.Round(item.Bounds.MaxY, 3)
            },
            item.SourceShapeIds,
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

    var renderer = new TextRecognitionDebugRenderer();
    renderer.Render(
        input,
        analysis,
        svgPath);

    var runsOnly = new TextRecognitionAnalysisResult(runObservations);
    renderer.Render(
        input,
        runsOnly,
        runsSvgPath);

    Console.WriteLine($"Raw glyphs OCR-probed : {rawGlyphObservations.Length}");
    Console.WriteLine($"Uncertain raw glyphs  : {uncertainRawGlyphs.Length}");
    Console.WriteLine($"Horizontal runs       : {runObservations.Length}");
    Console.WriteLine($"Recognized runs       : {recognizedRuns.Length}");
    Console.WriteLine($"JSON     : {jsonPath}");
    Console.WriteLine($"SVG all  : {svgPath}");
    Console.WriteLine($"SVG runs : {runsSvgPath}");
}
finally
{
    recognizerLifetime?.Dispose();
}

return 0;
