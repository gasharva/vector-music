using System.Diagnostics;
using SvgMusic.Canonical;
using SvgMusic.Scene;
using SvgMusic.Semantics;

if (args.Length < 2)
{
    Console.Error.WriteLine(
        "Usage: dotnet run -- <input.svg> <output-directory> "
        + "[--title <title>] [--composer <composer>] [--model <basic-classifier.zip>] "
        + "[--initial-time <beats/beat-type>] [--initial-key <fifths>] "
        + "[--wait-for-debugger] [--layout-only]");
    return 2;
}

var input = args[0];
var outputDirectory = args[1];
var title = ReadOption(args, "--title");
var composer = ReadOption(args, "--composer");
var modelPath = ReadOption(args, "--model");
var inheritedTimeSignature = ParseTimeSignatureOption(
    ReadOption(args, "--initial-time"));
var inheritedKeySignature = ParseKeySignatureOption(
    ReadOption(args, "--initial-key"));
var waitForDebugger = args.Any(argument =>
    argument.Equals(
        "--wait-for-debugger",
        StringComparison.OrdinalIgnoreCase));
var layoutOnly = args.Any(argument =>
    argument.Equals(
        "--layout-only",
        StringComparison.OrdinalIgnoreCase));

if (waitForDebugger && !Debugger.IsAttached)
{
    Console.WriteLine(
        "Waiting for debugger: requesting Visual Studio attach...");
    Debugger.Launch();
}

if (waitForDebugger && Debugger.IsAttached)
{
    Debugger.Break();
}

Directory.CreateDirectory(outputDirectory);

Console.WriteLine("1. Building geometric and notation scenes...");
var settings = SvgMusicSettings.Default;
var clusterer = new ShapeClusterer();
var scenePipeline = new ScenePipeline(
    new SvgNormalizer(),
    clusterer);
var (geometry, notation) = scenePipeline.Run(input);
var layoutAnalyzer = new ScoreLayoutAnalyzer();
var layout = layoutAnalyzer.Analyze(notation);

if (layoutAnalyzer.LastDiagnostics is { } layoutDiagnostics)
{
    Console.WriteLine(
        $"   layout: strokes={layoutDiagnostics.StrokeCount}; "
        + $"horizontal={layoutDiagnostics.HorizontalStrokeCount}; "
        + $"logical-horizontal={layoutDiagnostics.LogicalHorizontalCount}; "
        + $"vertical={layoutDiagnostics.VerticalStrokeCount}; "
        + $"staffs={layoutDiagnostics.StaffCount}; "
        + $"systems={layoutDiagnostics.SystemCount}; "
        + $"boundaries={layoutDiagnostics.BoundaryCount}; "
        + $"measures={layoutDiagnostics.MeasureCount}");

    if (layoutDiagnostics.StaffCount == 0
        || layoutDiagnostics.BoundaryCount == 0)
    {
        Console.WriteLine("   long horizontal layout candidates:");
        foreach (var candidate in layoutDiagnostics.LongHorizontalCandidates)
        {
            Console.WriteLine($"     {candidate}");
        }
    }

    if (layoutDiagnostics.StaffCount > 0)
    {
        Console.WriteLine("   detected staffs:");
        foreach (var staff in layoutDiagnostics.StaffDescriptions)
        {
            Console.WriteLine($"     {staff}");
        }
    }

    if (layoutDiagnostics.StaffCount > 0
        && layoutDiagnostics.BoundaryCount == 0)
    {
        Console.WriteLine("   vertical layout candidates:");
        foreach (var candidate in layoutDiagnostics.VerticalCandidates)
        {
            Console.WriteLine($"     {candidate}");
        }
    }

    Console.WriteLine("   measure-boundary diagnostics:");
    foreach (var detail in layoutDiagnostics.MeasureBoundaryDetails)
    {
        Console.WriteLine($"     {detail}");
    }
}

if (layoutOnly)
{
    Console.WriteLine("Layout-only probe complete.");
    return layout.Systems
        .SelectMany(system => system.StaffPairs)
        .Any(pair => pair.Measures.Count > 0)
        ? 0
        : 4;
}

if (scenePipeline.LastCompositeDecisions.Count > 0)
{
    Console.WriteLine("   composite candidate decisions:");
    foreach (var decision in scenePipeline.LastCompositeDecisions)
    {
        Console.WriteLine(
            $"     {decision.Kind}/{decision.Id}: "
            + $"{(decision.Accepted ? "ACCEPT" : "REJECT")}; "
            + $"{decision.Reason}; "
            + $"sources=[{string.Join(",", decision.SourceShapeIds)}]");
    }
}

Console.WriteLine("2. Classifying reusable contour prototypes...");
var classifier = await AudiverisSymbolClassifier.CreateAsync(modelPath);
var stopwatch = Stopwatch.StartNew();
var prototypeClassifier = new PrototypeSymbolClassifier(
    classifier,
    settings: settings);
notation = prototypeClassifier.Classify(
    geometry,
    notation,
    layout);
Console.WriteLine(
    $"   prototype classification: {stopwatch.Elapsed.TotalSeconds:F3}s");

if (prototypeClassifier.LastDiagnostics.Count > 0)
{
    Console.WriteLine("   slowest prototype classifications:");

    foreach (var item in prototypeClassifier.LastDiagnostics.Take(12))
    {
        Console.WriteLine(
            $"     {item.PrototypeId}/{item.ShapeId}: "
            + $"shape={item.ShapeWidth:F2}x{item.ShapeHeight:F2}; "
            + $"points={item.PointCount}; total={item.TotalMilliseconds:F1}ms; "
            + (item.Skipped ? $"SKIPPED; {item.Reason}" : item.Reason));

        foreach (var scale in item.Scales)
        {
            Console.WriteLine(
                $"       i{scale.Interline}: "
                + $"{scale.RasterWidth}x{scale.RasterHeight}; "
                + $"fg={scale.ForegroundPixels}; "
                + $"raster={scale.RasterizeMilliseconds:F1}ms; "
                + $"classify={scale.ClassifyMilliseconds:F1}ms");
        }
    }
}

Console.WriteLine("3. Repairing composite bass clefs...");
stopwatch.Restart();
var bassClefRepair = new BassClefCompositeRepair(classifier);
notation = bassClefRepair.Repair(
    geometry,
    notation,
    layout);
Console.WriteLine(
    $"   bass-clef repair: {stopwatch.Elapsed.TotalSeconds:F3}s; "
    + $"candidates={bassClefRepair.LastStatistics.DotPairCandidates}; "
    + $"classifier-runs={bassClefRepair.LastStatistics.CompositeClassifications}; "
    + $"reused-F-clefs={bassClefRepair.LastStatistics.ReusedConfidentFClefs}");

Console.WriteLine("4. Assigning logical staff/measure ownership...");
stopwatch.Restart();
var ownershipResult = new LogicalOwnershipAnalyzer().AnalyzeAndApply(
    geometry,
    notation,
    layout);
Console.WriteLine(
    $"   logical ownership: {stopwatch.Elapsed.TotalSeconds:F3}s");
notation = ownershipResult.Scene;
var ownership = ownershipResult.Ownership;

stopwatch.Restart();
var ledgerLadderOwnership = new LedgerLadderEllipseOwnershipAssigner().AssignAndApply(
    notation,
    layout,
    ownership);
Console.WriteLine(
    $"   ledger-ladder ownership: {stopwatch.Elapsed.TotalSeconds:F3}s; "
    + $"corrections={ledgerLadderOwnership.Adjustments.Count}");
notation = ledgerLadderOwnership.Scene;
ownership = ledgerLadderOwnership.Ownership;

stopwatch.Restart();
var fourthGeneration = new FourthGenerationOuterBandAssigner().AssignAndApply(
    geometry,
    notation,
    layout,
    ownership);
Console.WriteLine(
    $"   fourth-generation ownership: {stopwatch.Elapsed.TotalSeconds:F3}s");
notation = fourthGeneration.Scene;
ownership = fourthGeneration.Ownership;

Console.WriteLine("5. Running OCR fallback for semantic text...");
using var textRecognizer = new RapidOcrTextRecognizer();
var textAnalysis = new FallbackTextRecognitionAnalyzer(textRecognizer).Analyze(
    geometry,
    notation,
    layout);
Console.WriteLine(
    $"   OCR candidates: {textAnalysis.Observations.Count}; recognized: {textAnalysis.Recognized.Count}");

Console.WriteLine("5b. Writing SVG parser diagnostics...");
const string parserBaseName = "parser";
const string classifiedSymbolsFileName = "parser.classified-symbols.svg";
const string ownershipSvgFileName = "parser.ownership.svg";
const string ownershipDiagnosticsFileName = "parser.ownership.render-diagnostics.txt";

new DebugSceneRenderer().RenderAll(
    input,
    notation,
    outputDirectory,
    parserBaseName);

new SymbolClassificationDebugRenderer().Render(
    input,
    geometry,
    notation,
    Path.Combine(
        outputDirectory,
        classifiedSymbolsFileName));

new LogicalOwnershipDebugRenderer().Render(
    input,
    geometry,
    notation,
    layout,
    ownership,
    Path.Combine(
        outputDirectory,
        ownershipSvgFileName));

Console.WriteLine("6. Building measure-oriented semantic scene...");
var semanticDocument = new MeasureSceneBuilder().Build(
    geometry,
    notation,
    layout);

Console.WriteLine("7. Running granular semantic passes...");
var noteheadPass = new NoteheadPass();
var accidentalPass = new AccidentalPass();
var stemPass = new StemAttachmentPass();
var flagPass = new FlagAttachmentPass();
var beamPass = new BeamAttachmentPass();
var tupletPass = new TupletPass();
var dotPass = new DotAttachmentPass();
var durationPass = new DurationPass();
var chordPass = new ChordPass();
var textPass = new TextPass(
    textAnalysis,
    geometry,
    layout,
    ownership,
    notation);
var semanticPipeline = new SemanticPipeline(
    [
        new ClefPass(),
        new TimeSignaturePass(
            inheritedTimeSignature,
            settings),
        new KeySignaturePass(inheritedKeySignature),
        noteheadPass,
        accidentalPass,
        new PitchPass(),
        stemPass,
        flagPass,
        beamPass,
        tupletPass,
        dotPass,
        durationPass,
        chordPass,
        textPass
    ]);
var facts = semanticPipeline.Run(semanticDocument);

const string noteheadsSvgFileName = "semantic.noteheads.svg";
const string noteheadsDiagnosticsFileName = "semantic.noteheads.txt";
const string accidentalsSvgFileName = "semantic.accidentals.svg";
const string accidentalsDiagnosticsFileName = "semantic.accidentals.txt";
const string stemsSvgFileName = "semantic.stems.svg";
const string stemsDiagnosticsFileName = "semantic.stems.txt";
const string flagsSvgFileName = "semantic.flags.svg";
const string flagsDiagnosticsFileName = "semantic.flags.txt";
const string beamsSvgFileName = "semantic.beams.svg";
const string beamsDiagnosticsFileName = "semantic.beams.txt";
const string tupletsSvgFileName = "semantic.tuplets.svg";
const string tupletsDiagnosticsFileName = "semantic.tuplets.txt";
const string dotsSvgFileName = "semantic.dots.svg";
const string dotsDiagnosticsFileName = "semantic.dots.txt";
const string textSvgFileName = "semantic.text.svg";

var noteheadsSvgPath = Path.Combine(
    outputDirectory,
    noteheadsSvgFileName);
var accidentalsSvgPath = Path.Combine(
    outputDirectory,
    accidentalsSvgFileName);
var stemsSvgPath = Path.Combine(
    outputDirectory,
    stemsSvgFileName);
var flagsSvgPath = Path.Combine(
    outputDirectory,
    flagsSvgFileName);
var beamsSvgPath = Path.Combine(
    outputDirectory,
    beamsSvgFileName);
var tupletsSvgPath = Path.Combine(
    outputDirectory,
    tupletsSvgFileName);
var dotsSvgPath = Path.Combine(
    outputDirectory,
    dotsSvgFileName);
var textSvgPath = Path.Combine(
    outputDirectory,
    textSvgFileName);

if (noteheadPass.LastAnalysis is not null)
{
    Console.WriteLine("8. Writing notehead diagnostics...");
    var noteheadDebugRenderer = new NoteheadDebugRenderer();

    noteheadDebugRenderer.Render(
        input,
        noteheadPass.LastAnalysis,
        noteheadsSvgPath);

    noteheadDebugRenderer.WriteReport(
        noteheadPass.LastAnalysis,
        Path.Combine(
            outputDirectory,
            noteheadsDiagnosticsFileName));
}

if (accidentalPass.LastAnalysis is not null)
{
    Console.WriteLine("9. Writing accidental diagnostics...");
    var accidentalDebugRenderer = new AccidentalDebugRenderer();
    var accidentalBaseSvg = File.Exists(noteheadsSvgPath)
        ? noteheadsSvgPath
        : input;

    accidentalDebugRenderer.Render(
        accidentalBaseSvg,
        accidentalPass.LastAnalysis,
        accidentalsSvgPath);

    accidentalDebugRenderer.WriteReport(
        accidentalPass.LastAnalysis,
        Path.Combine(
            outputDirectory,
            accidentalsDiagnosticsFileName));
}

if (stemPass.LastAnalysis is not null)
{
    Console.WriteLine("10. Writing stem attachment diagnostics...");
    var stemDebugRenderer = new StemDebugRenderer();
    var stemBaseSvg = File.Exists(accidentalsSvgPath)
        ? accidentalsSvgPath
        : File.Exists(noteheadsSvgPath)
            ? noteheadsSvgPath
            : input;

    stemDebugRenderer.Render(
        stemBaseSvg,
        stemPass.LastAnalysis,
        stemsSvgPath);

    stemDebugRenderer.WriteReport(
        stemPass.LastAnalysis,
        Path.Combine(
            outputDirectory,
            stemsDiagnosticsFileName));
}

if (flagPass.LastAnalysis is not null)
{
    Console.WriteLine("11. Writing flag attachment diagnostics...");
    var flagDebugRenderer = new FlagDebugRenderer();
    var flagBaseSvg = File.Exists(stemsSvgPath)
        ? stemsSvgPath
        : File.Exists(accidentalsSvgPath)
            ? accidentalsSvgPath
            : input;

    flagDebugRenderer.Render(
        flagBaseSvg,
        flagPass.LastAnalysis,
        flagsSvgPath);

    flagDebugRenderer.WriteReport(
        flagPass.LastAnalysis,
        Path.Combine(
            outputDirectory,
            flagsDiagnosticsFileName));
}

if (beamPass.LastAnalysis is not null)
{
    Console.WriteLine("12. Writing beam attachment diagnostics...");
    var beamDebugRenderer = new BeamDebugRenderer();
    var beamBaseSvg = File.Exists(flagsSvgPath)
        ? flagsSvgPath
        : File.Exists(stemsSvgPath)
            ? stemsSvgPath
            : input;

    beamDebugRenderer.Render(
        beamBaseSvg,
        beamPass.LastAnalysis,
        beamsSvgPath);

    beamDebugRenderer.WriteReport(
        beamPass.LastAnalysis,
        Path.Combine(
            outputDirectory,
            beamsDiagnosticsFileName));
}

if (tupletPass.LastAnalysis is not null)
{
    Console.WriteLine("13. Writing tuplet diagnostics...");
    var tupletDebugRenderer = new TupletDebugRenderer();
    var tupletBaseSvg = File.Exists(beamsSvgPath)
        ? beamsSvgPath
        : File.Exists(flagsSvgPath)
            ? flagsSvgPath
            : input;

    tupletDebugRenderer.Render(
        tupletBaseSvg,
        tupletPass.LastAnalysis,
        tupletsSvgPath);

    tupletDebugRenderer.WriteReport(
        tupletPass.LastAnalysis,
        Path.Combine(
            outputDirectory,
            tupletsDiagnosticsFileName));
}

if (dotPass.LastAnalysis is not null)
{
    Console.WriteLine("14. Writing augmentation-dot diagnostics...");
    var dotDebugRenderer = new DotDebugRenderer();
    var dotBaseSvg = File.Exists(tupletsSvgPath)
        ? tupletsSvgPath
        : File.Exists(beamsSvgPath)
            ? beamsSvgPath
            : input;

    dotDebugRenderer.Render(
        dotBaseSvg,
        dotPass.LastAnalysis,
        dotsSvgPath);

    dotDebugRenderer.WriteReport(
        dotPass.LastAnalysis,
        Path.Combine(
            outputDirectory,
            dotsDiagnosticsFileName));
}

Console.WriteLine("14b. Writing semantic text diagnostics...");
new TextDebugRenderer().Render(
    input,
    facts.OfType<TextFact>(),
    textSvgPath);

Console.WriteLine("15. Building CanonicalNotation v0.4 raw preview...");
var canonical = new CanonicalNotationBuilder().Build(
    semanticDocument,
    facts,
    title,
    composer);

const string canonicalFileName = "kancheli.semantic.canonical.json";
const string musicXmlFileName = "kancheli.semantic.musicxml";
const string compressedMusicXmlFileName = "kancheli.semantic.mxl";
const string factsFileName = "semantic-facts.txt";
const string summaryFileName = "semantic-summary.txt";
const string runLogFileName = "semantic-run.log";

var canonicalPath = Path.Combine(
    outputDirectory,
    canonicalFileName);
File.WriteAllText(
    canonicalPath,
    CanonicalJson.Serialize(canonical));

Console.WriteLine("16. Writing MuseScore-compatible MusicXML...");
var musicXmlPath = Path.Combine(
    outputDirectory,
    musicXmlFileName);
new MuseScoreCompatibleMusicXmlWriter().Write(
    canonical,
    musicXmlPath);

Console.WriteLine("17. Writing compressed MusicXML (.mxl)...");
var compressedMusicXmlPath = Path.Combine(
    outputDirectory,
    compressedMusicXmlFileName);
new CompressedMusicXmlWriter().Write(
    canonical,
    compressedMusicXmlPath);

var factsPath = Path.Combine(
    outputDirectory,
    factsFileName);
File.WriteAllLines(
    factsPath,
    FormatFacts(facts));

var summaryPath = Path.Combine(
    outputDirectory,
    summaryFileName);
File.WriteAllLines(
    summaryPath,
    [
        $"input={Path.GetFullPath(input)}",
        $"geometry.shapes={geometry.Shapes.Count}",
        $"layout.staffs={layout.Staffs.Count}",
        $"layout.systems={layout.Systems.Count}",
        $"layout.boundaries={layout.Systems.SelectMany(system => system.StaffPairs).Sum(pair => pair.Boundaries.Count)}",
        $"layout.measures={layout.Systems.SelectMany(system => system.StaffPairs).Sum(pair => pair.Measures.Count)}",
        $"notation.instances={notation.Instances.Count}",
        $"notation.strokes={notation.Strokes.Count}",
        $"notation.curves={notation.CurvedStrokes.Count}",
        $"notation.ellipses={notation.Ellipses.Count}",
        $"ownership.assignments={ownership.Assignments.Count}",
        $"ownership.ledgerLadderCorrections={ledgerLadderOwnership.Adjustments.Count}",
        $"semantic.measures={semanticDocument.Measures.Count}",
        $"semantic.facts={facts.Items.Count}",
        $"semantic.clefs={facts.OfType<ClefFact>().Count()}",
        $"semantic.times={facts.OfType<TimeSignatureFact>().Count()}",
        $"semantic.keys={facts.OfType<KeySignatureFact>().Count()}",
        $"semantic.noteheads={facts.OfType<NoteheadFact>().Count()}",
        $"semantic.graceNotes={facts.OfType<GraceNoteFact>().Count()}",
        $"semantic.accidentals={facts.OfType<AccidentalFact>().Count()}",
        $"semantic.pitches={facts.OfType<PitchFact>().Count()}",
        $"semantic.stems={facts.OfType<StemAttachmentFact>().Count()}",
        $"semantic.flags={facts.OfType<FlagAttachmentFact>().Count()}",
        $"semantic.beams={facts.OfType<BeamAttachmentFact>().Count()}",
        $"semantic.tuplets={facts.OfType<TupletFact>().Count()}",
        $"semantic.dotAttachments={facts.OfType<DotAttachmentFact>().Count()}",
        $"semantic.augmentationDots={facts.OfType<DotAttachmentFact>().Sum(dot => dot.Count)}",
        $"semantic.durations={facts.OfType<DurationFact>().Count()}",
        $"semantic.chords={facts.OfType<ChordFact>().Count()}",
        $"semantic.chordNoteheads={facts.OfType<ChordFact>().Sum(chord => chord.NoteheadIds.Count)}",
        $"semantic.crossSystemCurveFragments={facts.OfType<CrossSystemCurveFragmentFact>().Count()}",
        $"semantic.crossSystemCurves={facts.OfType<CrossSystemCurveFact>().Count()}",
        $"semantic.ties={facts.OfType<TieFact>().Count()}",
        $"semantic.slurs={facts.OfType<SlurFact>().Count()}",
        $"semantic.hairpins={facts.OfType<HairpinFact>().Count()}",
        $"semantic.pedals={facts.OfType<PedalFact>().Count()}",
        $"semantic.text={facts.OfType<TextFact>().Count()}",
        $"semantic.text.title={facts.OfType<TextFact>().Count(text => text.Role == SemanticTextRole.Title)}",
        $"semantic.text.subtitle={facts.OfType<TextFact>().Count(text => text.Role == SemanticTextRole.Subtitle)}",
        $"semantic.text.composer={facts.OfType<TextFact>().Count(text => text.Role == SemanticTextRole.Composer)}",
        $"semantic.text.instruction={facts.OfType<TextFact>().Count(text => text.Role == SemanticTextRole.Instruction)}",
        $"semantic.text.tempo={facts.OfType<TextFact>().Count(text => text.Role == SemanticTextRole.Tempo)}",
        $"semantic.text.measureNumber={facts.OfType<TextFact>().Count(text => text.Role == SemanticTextRole.MeasureNumber)}",
        $"semantic.text.fingering={facts.OfType<TextFact>().Count(text => text.Role == SemanticTextRole.Fingering)}",
        $"semantic.text.unknown={facts.OfType<TextFact>().Count(text => text.Role == SemanticTextRole.Unknown)}",
        $"canonical.ties={canonical.Relations.Ties.Count}",
        $"canonical.slurs={canonical.Relations.Slurs.Count}",
        $"canonical.hairpins={canonical.Relations.Hairpins.Count}",
        $"canonical.pedals={canonical.Relations.Pedals.Count}",
        $"canonical.graceEvents={canonical.Parts.Single().Measures.Sum(measure => measure.Events.Count(ev => ev.Grace == true))}",
        $"canonical.measures={canonical.Parts.Single().Measures.Count}",
        $"canonical.path={Path.GetFullPath(canonicalPath)}",
        $"musicxml.path={Path.GetFullPath(musicXmlPath)}",
        $"mxl.path={Path.GetFullPath(compressedMusicXmlPath)}",
        $"semantic.noteheadsSvg={Path.GetFullPath(noteheadsSvgPath)}",
        $"semantic.noteheadsDiagnostics={Path.GetFullPath(Path.Combine(outputDirectory, noteheadsDiagnosticsFileName))}",
        $"semantic.accidentalsSvg={Path.GetFullPath(accidentalsSvgPath)}",
        $"semantic.accidentalsDiagnostics={Path.GetFullPath(Path.Combine(outputDirectory, accidentalsDiagnosticsFileName))}",
        $"semantic.stemsSvg={Path.GetFullPath(stemsSvgPath)}",
        $"semantic.stemsDiagnostics={Path.GetFullPath(Path.Combine(outputDirectory, stemsDiagnosticsFileName))}",
        $"semantic.flagsSvg={Path.GetFullPath(flagsSvgPath)}",
        $"semantic.flagsDiagnostics={Path.GetFullPath(Path.Combine(outputDirectory, flagsDiagnosticsFileName))}",
        $"semantic.beamsSvg={Path.GetFullPath(beamsSvgPath)}",
        $"semantic.beamsDiagnostics={Path.GetFullPath(Path.Combine(outputDirectory, beamsDiagnosticsFileName))}",
        $"semantic.tupletsSvg={Path.GetFullPath(tupletsSvgPath)}",
        $"semantic.tupletsDiagnostics={Path.GetFullPath(Path.Combine(outputDirectory, tupletsDiagnosticsFileName))}",
        $"semantic.dotsSvg={Path.GetFullPath(dotsSvgPath)}",
        $"semantic.dotsDiagnostics={Path.GetFullPath(Path.Combine(outputDirectory, dotsDiagnosticsFileName))}",
        $"semantic.textSvg={Path.GetFullPath(textSvgPath)}",
        $"parser.strokes={Path.GetFullPath(Path.Combine(outputDirectory, parserBaseName + ".strokes.svg"))}",
        $"parser.arcs={Path.GetFullPath(Path.Combine(outputDirectory, parserBaseName + ".arcs.svg"))}",
        $"parser.ellipses={Path.GetFullPath(Path.Combine(outputDirectory, parserBaseName + ".ellipses.svg"))}",
        $"parser.contours={Path.GetFullPath(Path.Combine(outputDirectory, parserBaseName + ".contours.svg"))}",
        $"parser.classified={Path.GetFullPath(Path.Combine(outputDirectory, classifiedSymbolsFileName))}",
        $"parser.ownership={Path.GetFullPath(Path.Combine(outputDirectory, ownershipSvgFileName))}",
        $"parser.ownershipDiagnostics={Path.GetFullPath(Path.Combine(outputDirectory, ownershipDiagnosticsFileName))}"
    ]);

Console.WriteLine("18. Writing artifact index...");
new ArtifactIndexWriter().Write(
    outputDirectory,
    input,
    title,
    canonicalFileName,
    musicXmlFileName,
    compressedMusicXmlFileName,
    factsFileName,
    summaryFileName,
    runLogFileName);

Console.WriteLine();
Console.WriteLine("Semantic interpretation complete:");
Console.WriteLine($"  measures   : {semanticDocument.Measures.Count}");
Console.WriteLine($"  facts      : {facts.Items.Count}");
Console.WriteLine($"  clefs      : {facts.OfType<ClefFact>().Count()}");
Console.WriteLine($"  times      : {facts.OfType<TimeSignatureFact>().Count()}");
Console.WriteLine($"  keys       : {facts.OfType<KeySignatureFact>().Count()}");
Console.WriteLine($"  noteheads  : {facts.OfType<NoteheadFact>().Count()}");
Console.WriteLine($"  accidentals: {facts.OfType<AccidentalFact>().Count()}");
Console.WriteLine($"  pitches    : {facts.OfType<PitchFact>().Count()}");
Console.WriteLine($"  stems      : {facts.OfType<StemAttachmentFact>().Count()}");
Console.WriteLine($"  flags      : {facts.OfType<FlagAttachmentFact>().Count()}");
Console.WriteLine($"  beams      : {facts.OfType<BeamAttachmentFact>().Count()}");
Console.WriteLine($"  tuplets    : {facts.OfType<TupletFact>().Count()}");
Console.WriteLine($"  dot targets: {facts.OfType<DotAttachmentFact>().Count()}");
Console.WriteLine($"  augm. dots : {facts.OfType<DotAttachmentFact>().Sum(dot => dot.Count)}");
Console.WriteLine($"  durations  : {facts.OfType<DurationFact>().Count()}");
Console.WriteLine($"  chords     : {facts.OfType<ChordFact>().Count()}");
Console.WriteLine($"  xsys frags : {facts.OfType<CrossSystemCurveFragmentFact>().Count()}");
Console.WriteLine($"  xsys curves: {facts.OfType<CrossSystemCurveFact>().Count()}");
Console.WriteLine($"  ties       : {facts.OfType<TieFact>().Count()}");
Console.WriteLine($"  slurs      : {facts.OfType<SlurFact>().Count()}");
Console.WriteLine($"  hairpins   : {facts.OfType<HairpinFact>().Count()}");
Console.WriteLine($"  pedals     : {facts.OfType<PedalFact>().Count()}");
Console.WriteLine($"  text facts : {facts.OfType<TextFact>().Count()}");
Console.WriteLine($"  ownership ledger corrections: {ledgerLadderOwnership.Adjustments.Count}");
Console.WriteLine($"  canonical  : {Path.GetFullPath(canonicalPath)}");
Console.WriteLine($"  MusicXML   : {Path.GetFullPath(musicXmlPath)}");
Console.WriteLine($"  MXL        : {Path.GetFullPath(compressedMusicXmlPath)}");
Console.WriteLine($"  noteheads  : {Path.GetFullPath(noteheadsSvgPath)}");
Console.WriteLine($"  accidentals: {Path.GetFullPath(accidentalsSvgPath)}");
Console.WriteLine($"  stems      : {Path.GetFullPath(stemsSvgPath)}");
Console.WriteLine($"  flags      : {Path.GetFullPath(flagsSvgPath)}");
Console.WriteLine($"  beams      : {Path.GetFullPath(beamsSvgPath)}");
Console.WriteLine($"  tuplets    : {Path.GetFullPath(tupletsSvgPath)}");
Console.WriteLine($"  dots       : {Path.GetFullPath(dotsSvgPath)}");
Console.WriteLine($"  ownership  : {Path.GetFullPath(Path.Combine(outputDirectory, ownershipSvgFileName))}");
Console.WriteLine($"  index      : {Path.GetFullPath(Path.Combine(outputDirectory, "index.html"))}");

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

static int? ParseKeySignatureOption(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return null;
    }

    if (!int.TryParse(value, out var fifths)
        || fifths < -7
        || fifths > 7)
    {
        throw new ArgumentException(
            $"Invalid --initial-key value '{value}'. Expected an integer from -7 to 7.");
    }

    return fifths;
}

static (int Beats, int BeatType)? ParseTimeSignatureOption(
    string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return null;
    }

    var parts = value.Split(
        '/',
        StringSplitOptions.TrimEntries
        | StringSplitOptions.RemoveEmptyEntries);

    if (parts.Length != 2
        || !int.TryParse(parts[0], out var beats)
        || !int.TryParse(parts[1], out var beatType)
        || beats <= 0
        || beatType <= 0)
    {
        throw new ArgumentException(
            $"Invalid --initial-time value '{value}'. Expected beats/beat-type, for example 3/4.");
    }

    return (beats, beatType);
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
            case TextFact text:
                yield return $"text role={text.Role} value='{text.Text}' "
                    + $"m={text.MeasureNumber?.ToString() ?? "-"} staff={text.Staff?.ToString() ?? "-"} "
                    + $"at={text.At ?? "-"} anchor={text.AnchorShapeId ?? "-"} "
                    + $"avg-height={text.AverageGlyphHeight:F2} "
                    + $"ocr={text.OcrConfidence:P1} confidence={text.Confidence:P1}; "
                    + $"reason={text.Reason}";
                break;

            case ClefFact clef:
                yield return $"clef m{clef.MeasureNumber} staff={clef.Staff} "
                    + $"{clef.Sign}{clef.Line} x={clef.X:F2} "
                    + $"confidence={clef.Confidence:P1} shape={clef.ShapeId}; "
                    + $"reason={clef.Reason}";
                break;

            case TimeSignatureFact time:
                yield return $"time m{time.MeasureNumber} "
                    + $"{time.Beats}/{time.BeatType} x={time.MinX:F2}..{time.MaxX:F2}; "
                    + $"inherited={time.IsInherited}; "
                    + $"shapes={string.Join(',', time.SourceShapeIds)}; "
                    + $"reason={time.Reason}";
                break;

            case KeySignatureFact key:
                yield return $"key m{key.MeasureNumber} fifths={key.Fifths} "
                    + $"kind={key.AccidentalKind} count={key.AccidentalCount} "
                    + $"x={key.MinX:F2}..{key.MaxX:F2}; "
                    + $"inherited={key.IsInherited}; "
                    + $"shapes={string.Join(',', key.SourceShapeIds)}; "
                    + $"reason={key.Reason}";
                break;

            case NoteheadFact notehead:
                yield return $"notehead m{notehead.MeasureNumber} staff={notehead.Staff} "
                    + $"shape={notehead.ShapeId} x={notehead.CenterX:F2} y={notehead.CenterY:F2} "
                    + $"fill={notehead.FillKind} size={notehead.NormalizedSize:F3}sp "
                    + $"staff-step={notehead.StaffStep} error={notehead.StaffStepError:F3} "
                    + $"confidence={notehead.Confidence:P1}; reason={notehead.Reason}";
                break;

            case AccidentalFact accidental:
                yield return $"accidental m{accidental.MeasureNumber} staff={accidental.Staff} "
                    + $"shape={accidental.ShapeId} kind={accidental.Kind} "
                    + $"anchor=({accidental.AnchorX:F2},{accidental.AnchorY:F2}) "
                    + $"staff-step={accidental.StaffStep} "
                    + $"explicit={accidental.ExplicitTargetNoteheadId} "
                    + $"affected=[{string.Join(',', accidental.AffectedNoteheadIds)}] "
                    + $"classifier={accidental.ClassificationConfidence:P1} "
                    + $"vertical-error={accidental.VerticalErrorInHalfSteps:F3} "
                    + $"confidence={accidental.Confidence:P1}; reason={accidental.Reason}";
                break;

            case PitchFact pitch:
                var localAccidental = pitch.ActiveAccidentalKind is null
                    ? "none"
                    : $"{pitch.ActiveAccidentalKind}/{pitch.ActiveAccidentalShapeId}/"
                        + (pitch.IsAccidentalExplicit ? "explicit" : "inherited");

                yield return $"pitch m{pitch.MeasureNumber} staff={pitch.Staff} "
                    + $"notehead={pitch.NoteheadId} staff-step={pitch.StaffStep} "
                    + $"clef={pitch.ClefSign}{pitch.ClefLine}/{pitch.ClefShapeId} "
                    + $"key={pitch.KeyFifths} local={localAccidental} "
                    + $"step={pitch.Step} octave={pitch.Octave} alter={pitch.Alter} "
                    + $"pitch={pitch.Pitch} confidence={pitch.Confidence:P1}; "
                    + $"reason={pitch.Reason}";
                break;

            case StemAttachmentFact stem:
                yield return $"stem m{stem.MeasureNumber} shape={stem.StemShapeId} "
                    + $"direction={stem.Direction} cross-staff={stem.IsCrossStaff} "
                    + $"noteheads=[{string.Join(',', stem.AttachedNoteheadIds)}] "
                    + $"staffs=[{string.Join(',', stem.AttachedStaffs)}] "
                    + $"from=({stem.StartX:F2},{stem.StartY:F2}) "
                    + $"to=({stem.EndX:F2},{stem.EndY:F2}) "
                    + $"length={stem.LengthInSpacings:F2}sp width={stem.WidthInSpacings:F3}sp "
                    + $"confidence={stem.Confidence:P1}; reason={stem.Reason}";
                break;

            case FlagAttachmentFact flag:
                yield return $"flag m{flag.MeasureNumber} shape={flag.FlagShapeId} "
                    + $"label={flag.ClassificationLabel} level={flag.Level} "
                    + $"stem={flag.StemShapeId} tip=({flag.StemTipX:F2},{flag.StemTipY:F2}) "
                    + $"cross-staff-stem={flag.IsCrossStaffStem} "
                    + $"classifier={flag.ClassificationConfidence:P1} "
                    + $"confidence={flag.Confidence:P1}; reason={flag.Reason}";
                break;

            case BeamAttachmentFact beam:
                yield return $"beam m{beam.MeasureNumber} shape={beam.BeamShapeId} "
                    + $"level={beam.Level} hook={beam.IsHook} cross-staff={beam.IsCrossStaff} "
                    + $"stems=[{string.Join(',', beam.AttachedStemIds)}] "
                    + $"staffs=[{string.Join(',', beam.AttachedStaffs)}] "
                    + $"left={beam.LeftEndSupported} right={beam.RightEndSupported} "
                    + $"from=({beam.StartX:F2},{beam.StartY:F2}) "
                    + $"to=({beam.EndX:F2},{beam.EndY:F2}) "
                    + $"length={beam.LengthInSpacings:F2}sp width={beam.WidthInSpacings:F3}sp "
                    + $"slope={beam.Slope:F3} confidence={beam.Confidence:P1}; "
                    + $"reason={beam.Reason}";
                break;

            case TupletFact tuplet:
                yield return $"tuplet m{tuplet.MeasureNumber} shape={tuplet.TupletShapeId} "
                    + $"label={tuplet.ClassificationLabel} displayed={tuplet.DisplayedNumber} "
                    + $"ratio={tuplet.ActualNotes}:{tuplet.NormalNotes} "
                    + $"corrected={tuplet.CorrectedFromStemCount} beam={tuplet.PrimaryBeamShapeId} "
                    + $"stems=[{string.Join(',', tuplet.AttachedStemIds)}] "
                    + $"noteheads=[{string.Join(',', tuplet.AttachedNoteheadIds)}] "
                    + $"classifier={tuplet.ClassificationConfidence:P1} "
                    + $"confidence={tuplet.Confidence:P1}; reason={tuplet.Reason}";
                break;

            case DotAttachmentFact dot:
                yield return $"dot m{dot.MeasureNumber} staff={dot.Staff} "
                    + $"target={dot.TargetNoteheadId} count={dot.Count} "
                    + $"shapes=[{string.Join(',', dot.DotShapeIds)}] "
                    + $"confidence={dot.Confidence:P1}; reason={dot.Reason}";
                break;

            case DurationFact duration:
                yield return $"duration m{duration.MeasureNumber} staff={duration.Staff} "
                    + $"notehead={duration.NoteheadId} stem={duration.StemShapeId ?? "none"} "
                    + $"type={duration.NoteType} base={duration.BaseDuration} "
                    + $"effective={duration.EffectiveDuration} dots={duration.Dots} "
                    + $"level={duration.SubdivisionLevel} "
                    + $"tuplet={duration.TupletActual?.ToString() ?? "-"}:{duration.TupletNormal?.ToString() ?? "-"} "
                    + $"confidence={duration.Confidence:P1}; reason={duration.Reason}";
                break;

            case ChordFact chord:
                yield return $"chord m{chord.MeasureNumber} id={chord.ChordId} "
                    + $"type={chord.NoteType} fill={chord.FillKind} "
                    + $"stem={chord.StemShapeId ?? "none"} x={chord.AnchorX:F2} "
                    + $"staffs=[{string.Join(',', chord.Staffs)}] "
                    + $"noteheads=[{string.Join(',', chord.NoteheadIds)}] "
                    + $"confidence={chord.Confidence:P1}; reason={chord.Reason}";
                break;

            case CrossSystemCurveFragmentFact fragment:
                yield return $"cross-system-fragment {fragment.CurveShapeId} "
                    + $"m{fragment.MeasureNumber} staff={fragment.Staff} "
                    + $"kind={fragment.Kind} attached={fragment.AttachedNoteheadId} "
                    + $"note-distance={fragment.NoteDistanceInSpacings:F2}sp "
                    + $"edge-distance={fragment.EdgeDistanceInSpacings:F2}sp "
                    + $"placement={fragment.Placement ?? "unspecified"} "
                    + $"confidence={fragment.Confidence:P1}; reason={fragment.Reason}";
                break;

            case CrossSystemCurveFact curve:
                yield return $"cross-system-curve {curve.CurveId} "
                    + $"m{curve.StartMeasureNumber}/s{curve.StartStaff}"
                    + $"->m{curve.EndMeasureNumber}/s{curve.EndStaff} "
                    + $"noteheads={curve.FromNoteheadId}->{curve.ToNoteheadId} "
                    + $"fragments={curve.OutgoingCurveShapeId}+{curve.IncomingCurveShapeId} "
                    + $"placement={curve.Placement ?? "unspecified"} "
                    + $"distance={curve.StartDistanceInSpacings:F2}/{curve.EndDistanceInSpacings:F2}sp "
                    + $"confidence={curve.Confidence:P1}; reason={curve.Reason}";
                break;

            case TieFact tie:
                yield return $"tie {tie.CurveShapeId} m{tie.StartMeasureNumber}->m{tie.EndMeasureNumber} "
                    + $"staff={tie.Staff} pitch={tie.Pitch} "
                    + $"noteheads={tie.FromNoteheadId}->{tie.ToNoteheadId} "
                    + $"placement={tie.Placement ?? "unspecified"} "
                    + $"distance={tie.StartDistanceInSpacings:F2}/{tie.EndDistanceInSpacings:F2}sp "
                    + $"confidence={tie.Confidence:P1}; reason={tie.Reason}";
                break;

            case SlurFact slur:
                yield return $"slur {slur.CurveShapeId} m{slur.StartMeasureNumber}->m{slur.EndMeasureNumber} "
                    + $"staff={slur.StartStaff}->{slur.EndStaff} "
                    + $"noteheads={slur.FromNoteheadId}->{slur.ToNoteheadId} "
                    + $"placement={slur.Placement ?? "unspecified"} "
                    + $"distance={slur.StartDistanceInSpacings:F2}/{slur.EndDistanceInSpacings:F2}sp "
                    + $"confidence={slur.Confidence:P1}; reason={slur.Reason}";
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
