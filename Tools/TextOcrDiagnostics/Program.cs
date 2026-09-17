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

Console.WriteLine("Building the existing non-OCR SvgScene...");
var pipeline = new ScenePipeline(
    new SvgNormalizer(),
    new ShapeClusterer());
var (geometry, notation) = pipeline.Run(input);
var layout = new ScoreLayoutAnalyzer().Analyze(notation);

Console.WriteLine("Running the existing Audiveris symbol classifier before OCR fallback...");
var musicClassifier = await AudiverisSymbolClassifier.CreateAsync();
notation = new PrototypeSymbolClassifier(musicClassifier).Classify(
    geometry,
    notation,
    layout);
notation = new BassClefCompositeRepair(musicClassifier).Repair(
    geometry,
    notation,
    layout);

var poorResidualGlyphs = FallbackTextRecognitionAnalyzer
    .SelectPoorlyRecognizedGlyphShapes(
        geometry,
        notation,
        layout);

ITextRecognizer recognizer;
IDisposable? recognizerLifetime = null;

if (useOcr)
{
    Console.WriteLine("Loading PP-OCRv5 Latin for residual fallback only...");
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
    Console.WriteLine(
        "Expanding poor glyph seeds to complete horizontal rows, then splitting at large gaps...");
    var analysis = new FallbackTextRecognitionAnalyzer(recognizer).Analyze(
        geometry,
        notation,
        layout);

    var trainObservations = analysis.Observations
        .Where(item => item.Kind == TextCandidateKind.HorizontalRun)
        .ToArray();
    var singletonObservations = analysis.Observations
        .Where(item => item.Kind == TextCandidateKind.Prototype)
        .ToArray();
    var recognized = analysis.Observations
        .Where(item => !string.IsNullOrWhiteSpace(item.Recognition?.Text))
        .ToArray();

    var jsonPath = Path.Combine(outputDirectory, "parser.ocr.json");
    var svgPath = Path.Combine(outputDirectory, "parser.ocr.svg");
    var runsSvgPath = Path.Combine(outputDirectory, "parser.ocr.runs.svg");

    var report = new
    {
        Engine = useOcr ? "PP-OCRv5-Latin" : "disabled",
        Mode = "poor-glyph-seeded-full-horizontal-row-gap-segmentation",
        ClearMusicClassificationConfidence =
            FallbackTextRecognitionAnalyzer.ClearMusicClassificationConfidence,
        MaxGapAverageGlyphWidths =
            FallbackHorizontalTextTrainBuilder.MaxGapAverageGlyphWidths,
        Summary = new
        {
            PoorlyClassifiedResidualGlyphs = poorResidualGlyphs.Count,
            OcrCandidates = analysis.Observations.Count,
            HorizontalTrains = trainObservations.Length,
            SingletonFallbacks = singletonObservations.Length,
            RecognizedFallbacks = recognized.Length
        },
        Observations = analysis.Observations.Select(item => new
        {
            item.Id,
            Kind = item.Kind == TextCandidateKind.HorizontalRun
                ? "HorizontalTrain"
                : "SingletonFallback",
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
        svgPath,
        labelSingletons: true);

    var runsOnly = new TextRecognitionAnalysisResult(trainObservations);
    renderer.Render(
        input,
        runsOnly,
        runsSvgPath);

    Console.WriteLine($"Poor residual glyph seeds     : {poorResidualGlyphs.Count}");
    Console.WriteLine($"OCR fallback segments        : {analysis.Observations.Count}");
    Console.WriteLine($"Horizontal segments          : {trainObservations.Length}");
    Console.WriteLine($"Singleton fallbacks          : {singletonObservations.Length}");
    Console.WriteLine($"Recognized fallbacks         : {recognized.Length}");
    Console.WriteLine($"Row split gap                : {FallbackHorizontalTextTrainBuilder.MaxGapAverageGlyphWidths:0.##} average glyph widths");
    // Compatibility markers retained for the existing master workflow smoke grep.
    Console.WriteLine($"Raw glyphs OCR-probed (fallback seeds only): {poorResidualGlyphs.Count}");
    Console.WriteLine($"Horizontal runs (fallback trains): {trainObservations.Length}");
    Console.WriteLine($"JSON     : {jsonPath}");
    Console.WriteLine($"SVG all  : {svgPath}");
    Console.WriteLine($"SVG runs : {runsSvgPath}");
}
finally
{
    recognizerLifetime?.Dispose();
}

return 0;
