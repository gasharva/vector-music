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

Console.WriteLine("5. Writing SVG parser diagnostics...");
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
var semanticPipeline = new SemanticPipeline(
    [
        new ClefPass(),
        new TimeSignaturePass(),
        new KeySignaturePass(),
        noteheadPass,
        accidentalPass,
        new PitchPass(),
        stemPass,
        flagPass,
        beamPass
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

Console.WriteLine("13. Building CanonicalNotation v0.3 skeleton...");
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

Console.WriteLine("14. Writing MuseScore-compatible MusicXML...");
var musicXmlPath = Path.Combine(
    outputDirectory,
    musicXmlFileName);
new MuseScoreCompatibleMusicXmlWriter().Write(
    canonical,
    musicXmlPath);

Console.WriteLine("15. Writing compressed MusicXML (.mxl)...");
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
        $"semantic.noteheads={facts.OfType<NoteheadFact>().Count()}",
        $"semantic.accidentals={facts.OfType<AccidentalFact>().Count()}",
        $"semantic.pitches={facts.OfType<PitchFact>().Count()}",
        $"semantic.stems={facts.OfType<StemAttachmentFact>().Count()}",
        $"semantic.flags={facts.OfType<FlagAttachmentFact>().Count()}",
        $"semantic.beams={facts.OfType<BeamAttachmentFact>().Count()}",
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
        $"parser.strokes={Path.GetFullPath(Path.Combine(outputDirectory, parserBaseName + ".strokes.svg"))}",
        $"parser.arcs={Path.GetFullPath(Path.Combine(outputDirectory, parserBaseName + ".arcs.svg"))}",
        $"parser.ellipses={Path.GetFullPath(Path.Combine(outputDirectory, parserBaseName + ".ellipses.svg"))}",
        $"parser.contours={Path.GetFullPath(Path.Combine(outputDirectory, parserBaseName + ".contours.svg"))}",
        $"parser.classified={Path.GetFullPath(Path.Combine(outputDirectory, classifiedSymbolsFileName))}",
        $"parser.ownership={Path.GetFullPath(Path.Combine(outputDirectory, ownershipSvgFileName))}",
        $"parser.ownershipDiagnostics={Path.GetFullPath(Path.Combine(outputDirectory, ownershipDiagnosticsFileName))}"
    ]);

Console.WriteLine("16. Writing artifact index...");
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
Console.WriteLine($"  canonical  : {Path.GetFullPath(canonicalPath)}");
Console.WriteLine($"  MusicXML   : {Path.GetFullPath(musicXmlPath)}");
Console.WriteLine($"  MXL        : {Path.GetFullPath(compressedMusicXmlPath)}");
Console.WriteLine($"  noteheads  : {Path.GetFullPath(noteheadsSvgPath)}");
Console.WriteLine($"  accidentals: {Path.GetFullPath(accidentalsSvgPath)}");
Console.WriteLine($"  stems      : {Path.GetFullPath(stemsSvgPath)}");
Console.WriteLine($"  flags      : {Path.GetFullPath(flagsSvgPath)}");
Console.WriteLine($"  beams      : {Path.GetFullPath(beamsSvgPath)}");
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
