using SvgMusic.Scene;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class FallbackTextRecognitionAnalyzerTests
{
    [Fact]
    public void Analyze_GrowsFromPoorResidualThroughUnclassifiedRawNeighbours()
    {
        var geometry = new GeometricScene([
            Shape("left-primitive", 0, 0, 5, 10),
            Shape("seed", 7, 0, 12, 10),
            Shape("right-ellipse", 14, 0, 20, 10)
        ]);
        var notation = Notation(
            "seed",
            new SymbolClassification("CLUTTER", 0.99, 30, []));
        var recognizer = new RecordingRecognizer("ABC", 0.98);

        var analysis = new FallbackTextRecognitionAnalyzer(recognizer).Analyze(
            geometry,
            notation,
            Layout(10));

        var observation = Assert.Single(analysis.Observations);
        Assert.Equal(TextCandidateKind.HorizontalRun, observation.Kind);
        Assert.Equal(
            ["left-primitive", "seed", "right-ellipse"],
            observation.SourceShapeIds);
        Assert.Equal("ABC", observation.Recognition?.Text);

        var candidate = Assert.Single(recognizer.Candidates);
        Assert.Equal(TextCandidateKind.HorizontalRun, candidate.Kind);
    }

    [Fact]
    public void Analyze_UsesSingletonWhenNoHorizontalTrainExists()
    {
        var geometry = new GeometricScene([
            Shape("seed", 20, 10, 25, 20),
            Shape("far", 100, 10, 105, 20)
        ]);
        var notation = Notation(
            "seed",
            new SymbolClassification("ACCENT", 0.30, 30, []));
        var recognizer = new RecordingRecognizer("x", 0.91);

        var analysis = new FallbackTextRecognitionAnalyzer(recognizer).Analyze(
            geometry,
            notation,
            Layout(10));

        var observation = Assert.Single(analysis.Observations);
        Assert.Equal(TextCandidateKind.Prototype, observation.Kind);
        Assert.Equal(["seed"], observation.SourceShapeIds);

        var candidate = Assert.Single(recognizer.Candidates);
        Assert.Equal(["seed"], candidate.SourceShapeIds);
    }

    [Fact]
    public void Analyze_DoesNotRunOcrForConfidentMusicGlyph()
    {
        var geometry = new GeometricScene([
            Shape("music", 0, 0, 5, 10),
            Shape("neighbour", 7, 0, 12, 10)
        ]);
        var notation = Notation(
            "music",
            new SymbolClassification("SHARP", 0.99, 30, []));
        var recognizer = new RecordingRecognizer("ignored", 0.99);

        var analysis = new FallbackTextRecognitionAnalyzer(recognizer).Analyze(
            geometry,
            notation,
            Layout(10));

        Assert.Empty(analysis.Observations);
        Assert.Empty(recognizer.Candidates);
    }

    [Fact]
    public void Analyze_DoesNotUseProvenanceToFormTrain()
    {
        var geometry = new GeometricScene([
            Shape("completely-unrelated-a", 0, 0, 5, 10),
            Shape("seed", 7, 0, 12, 10),
            Shape("another-unrelated-z", 14, 0, 20, 10)
        ]);
        var notation = Notation("seed", null);
        var recognizer = new RecordingRecognizer("word", 0.97);

        var analysis = new FallbackTextRecognitionAnalyzer(recognizer).Analyze(
            geometry,
            notation,
            Layout(10));

        var run = Assert.Single(analysis.Observations);
        Assert.Equal(TextCandidateKind.HorizontalRun, run.Kind);
        Assert.Equal(3, run.SourceShapeIds.Count);
    }

    [Fact]
    public void Analyze_DoesNotPullGiantStaffLineIntoTrain()
    {
        var geometry = new GeometricScene([
            Shape("seed", 0, 0, 5, 10),
            Shape("staff-line", 6, 4, 200, 5)
        ]);
        var notation = Notation("seed", null);
        var recognizer = new RecordingRecognizer("x", 0.90);

        var analysis = new FallbackTextRecognitionAnalyzer(recognizer).Analyze(
            geometry,
            notation,
            Layout(10));

        var single = Assert.Single(analysis.Observations);
        Assert.Equal(TextCandidateKind.Prototype, single.Kind);
        Assert.Equal(["seed"], single.SourceShapeIds);
    }

    private static NotationScene Notation(
        string shapeId,
        SymbolClassification? classification)
    {
        var descriptor = new ShapeDescriptor(1, 1, []);
        return new NotationScene(
            [new ShapePrototype("p", shapeId, descriptor, classification)],
            [new ShapeInstance(
                shapeId,
                "p",
                0,
                0,
                5,
                10,
                "path",
                null,
                classification)],
            [],
            [],
            []);
    }

    private static GeometricShape Shape(
        string id,
        double minX,
        double minY,
        double maxX,
        double maxY)
    {
        var points = new[]
        {
            new PointD(minX, minY),
            new PointD(maxX, minY),
            new PointD(maxX, maxY),
            new PointD(minX, maxY)
        };

        return new GeometricShape(
            id,
            "path",
            points,
            new BoundsD(minX, minY, maxX, maxY),
            IsClosed: true,
            Contours: [new GeometricContour(points, true)],
            HasFill: true);
    }

    private static ScoreLayout Layout(double spacing) =>
        new(
            [],
            [new StaffLayout(
                "staff-1",
                "system-1",
                new BoundsD(0, 0, 300, 50),
                [],
                spacing,
                [])]);

    private sealed class RecordingRecognizer(
        string text,
        double confidence) : ITextRecognizer
    {
        public List<TextRecognitionCandidate> Candidates { get; } = [];

        public TextRecognition? Recognize(TextRecognitionCandidate candidate)
        {
            Candidates.Add(candidate);
            return new TextRecognition(text, confidence, "test");
        }
    }
}
