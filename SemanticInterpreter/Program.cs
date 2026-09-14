using SvgMusic.Canonical;
using SvgMusic.Scene;
using SvgMusic.Semantics;

if (args.Length < 2)
{
    Console.Error.WriteLine(
        "Usage: dotnet run -- <input.svg> <output-directory> "
        + "[--title <title>] [--composer <composer>] [--model <basic-classifier.zip>]");
    return 2;
}

var input = args[0];
var outputDirectory = args[1];
var title = ReadOption(args, "--title");
var composer = ReadOption(args, "--composer");
var modelPath = ReadOption(args, "--model");

Directory.CreateDirectory(outputDirectory);

Console.WriteLine("1. Building geometric and notation scenes...");
var clusterer = new ShapeClusterer();
var scenePipeline = new ScenePipeline(
    new SvgNormalizer(),
    clusterer);
var (geometry, notation) = scenePipeline.Run(input);
var layout = new ScoreLayoutAnalyzer().Analyze(notation);

Console.WriteLine("2. Classifying reusable contour prototypes...");
var classifier = await AudiverisSymbolClassifier.CreateAsync(modelPath);
notation = new PrototypeSymbolClassifier(classifier).Classify(
    geometry,
    notation,
    layout);

Console.WriteLine("3. Repairing composite bass clefs...");
notation = new BassClefCompositeRepair(classifier).Repair(
    geometry,
    notation,
    layout);

Console.WriteLine("4. Assigning logical staff/measure ownership...");
var ownershipResult = new LogicalOwnershipAnalyzer().AnalyzeAndApply(
    geometry,
    notation,
    layout);
notation = ownershipResult.Scene;
var ownership = ownershipResult.Ownership;

var fourthGeneration = new FourthGenerationOuterBandAssigner().AssignAndApply(
    geometry,
    notation,
    layout,
    ownership);
notation = fourthGeneration.Scene;
ownership = fourthGeneration.Ownership;

Console.WriteLine("5. Building measure-oriented semantic scene...");
var semanticDocument = new MeasureSceneBuilder().Build(
    geometry,
    notation,
    layout);

Console.WriteLine("6. Running granular semantic passes...");
var semanticPipeline = new SemanticPipeline(
    [
        new ClefPass(),
        new TimeSignaturePass(),
        new KeySignaturePass()
    ]);
var facts = semanticPipeline.Run(semanticDocument);

Console.WriteLine("7. Building CanonicalNotation v0.3 skeleton...");
var canonical = new CanonicalNotationBuilder().Build(
    semanticDocument,
    facts,
    title,
    composer);

var canonicalPath = Path.Combine(
    outputDirectory,
    "kancheli.semantic.canonical.json");
File.WriteAllText(
    canonicalPath,
    CanonicalJson.Serialize(canonical));

Console.WriteLine("8. Writing MusicXML...");
var musicXmlPath = Path.Combine(
    outputDirectory,
    "kancheli.semantic.musicxml");
new MusicXmlWriter().Write(
    canonical,
    musicXmlPath);

var factsPath = Path.Combine(
    outputDirectory,
    "semantic-facts.txt");
File.WriteAllLines(
    factsPath,
    FormatFacts(facts));

var summaryPath = Path.Combine(
    outputDirectory,
    "semantic-summary.txt");
File.WriteAllLines(
    summaryPath,
    [
        $"input={Path.GetFullPath(input)}",
        $"geometry.shapes={geometry.Shapes.Count}",
        $"notation.instances={notation.Instances.Count}",
        $"notation.strokes={notation.Strokes.Count}",
        $"notation.curves={notation.CurvedStrokes.Count}",
        $"notation.ellipses={notation.Ellipses.Count}",
        $"ownership.assignments={ownership.Assignments.Count}",
        $"semantic.measures={semanticDocument.Measures.Count}",
        $"semantic.facts={facts.Items.Count}",
        $"semantic.clefs={facts.OfType<ClefFact>().Count()}",
        $"semantic.times={facts.OfType<TimeSignatureFact>().Count()}",
        $"semantic.keys={facts.OfType<KeySignatureFact>().Count()}",
        $"canonical.measures={canonical.Parts.Single().Measures.Count}",
        $"canonical.path={Path.GetFullPath(canonicalPath)}",
        $"musicxml.path={Path.GetFullPath(musicXmlPath)}"
    ]);

Console.WriteLine();
Console.WriteLine("Semantic interpretation complete:");
Console.WriteLine($"  measures : {semanticDocument.Measures.Count}");
Console.WriteLine($"  facts    : {facts.Items.Count}");
Console.WriteLine($"  clefs    : {facts.OfType<ClefFact>().Count()}");
Console.WriteLine($"  times    : {facts.OfType<TimeSignatureFact>().Count()}");
Console.WriteLine($"  keys     : {facts.OfType<KeySignatureFact>().Count()}");
Console.WriteLine($"  canonical: {Path.GetFullPath(canonicalPath)}");
Console.WriteLine($"  MusicXML : {Path.GetFullPath(musicXmlPath)}");

return 0;

static string? ReadOption(
    string[] arguments,
    string option)
{
    var index = Array.FindIndex(
        arguments,
        argument => argument.Equals(
            option,
            StringComparison.OrdinalIgnoreCase));

    if (index < 0)
    {
        return null;
    }

    if (index + 1 >= arguments.Length)
    {
        throw new ArgumentException($"{option} requires a value.");
    }

    return arguments[index + 1];
}

static IEnumerable<string> FormatFacts(SemanticFacts facts)
{
    yield return "SEMANTIC PASS TRACE";

    foreach (var trace in facts.Trace)
    {
        yield return trace;
    }

    yield return string.Empty;
    yield return "FACTS";

    foreach (var fact in facts.Items)
    {
        switch (fact)
        {
            case ClefFact clef:
                yield return $"clef m{clef.MeasureNumber} staff={clef.Staff} "
                    + $"{clef.Sign}{clef.Line} x={clef.X:F2} "
                    + $"confidence={clef.Confidence:P1} shape={clef.ShapeId}; "
                    + $"reason={clef.Reason}";
                break;

            case TimeSignatureFact time:
                yield return $"time m{time.MeasureNumber} "
                    + $"{time.Beats}/{time.BeatType} x={time.MinX:F2}..{time.MaxX:F2}; "
                    + $"shapes={string.Join(',', time.SourceShapeIds)}; "
                    + $"reason={time.Reason}";
                break;

            case KeySignatureFact key:
                yield return $"key m{key.MeasureNumber} fifths={key.Fifths} "
                    + $"kind={key.AccidentalKind} count={key.AccidentalCount} "
                    + $"x={key.MinX:F2}..{key.MaxX:F2}; "
                    + $"shapes={string.Join(',', key.SourceShapeIds)}; "
                    + $"reason={key.Reason}";
                break;

            default:
                yield return $"{fact.Pass}: {fact.Reason}";
                break;
        }
    }

    yield return string.Empty;
    yield return "BUILD TRACE";

    foreach (var trace in facts.Trace.Where(trace =>
                 trace.StartsWith(
                     "CanonicalBuilder:",
                     StringComparison.Ordinal)))
    {
        yield return trace;
    }
}
