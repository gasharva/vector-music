using SvgMusic.Scene;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class FallbackTextRecognitionAnalyzerTests
{
    [Fact]
    public void Analyze_BuildsGreedyMaximalTrainFromPoorGlyphsOnly()
    {
        var geometry = new GeometricScene([
            Shape("bad-a", 0, 0, 5, 10),
            Shape("confident-middle", 7, 0, 12, 10),
            Shape("bad-b", 14, 0, 19, 10),
            Shape("bad-c", 28, 0, 33, 10)
        ]);
        var notation = Notation(
            ("bad-a", Poor()),
            ("confident-middle", Clear("SHARP")),
            ("bad-b", Poor()),
            ("bad-c", Poor()));
        var recognizer = new RecordingRecognizer("ABC", 0.98);

        var analysis = new FallbackTextRecognitionAnalyzer(recognizer).Analyze(
            geometry,
            notation,
            Layout(10));

        var observation = Assert.Single(analysis.Observations);
        Assert.Equal(TextCandidateKind.HorizontalRun, observation.Kind);
        Assert.Equal(["bad-a", "bad-b", "bad-c"], observation.SourceShapeIds);
        Assert.DoesNotContain("confident-middle", observation.SourceShapeIds);
        Assert.Equal("ABC", observation.Recognition?.Text);

        var candidate = Assert.Single(recognizer.Candidates);
        Assert.Equal(["bad-a", "bad-b", "bad-c"], candidate.SourceShapeIds);
    }

    [Fact]
    public void Analyze_AllowsGapOfTwoAverageGlyphWidths()
    {
        var geometry = new GeometricScene([
            Shape("a", 0, 0, 5, 10),
            Shape("b", 15, 0, 20, 10),
            Shape("c", 30, 0, 35, 10)
        ]);
        var notation = Notation(
            ("a", Poor()),
            ("b", Poor()),
            ("c", Poor()));
        var recognizer = new RecordingRecognizer("abc", 0.99);

        var analysis = new FallbackTextRecognitionAnalyzer(recognizer).Analyze(
            geometry,
            notation,
            Layout(10));

        var train = Assert.Single(analysis.Observations);
        Assert.Equal(TextCandidateKind.HorizontalRun, train.Kind);
        Assert.Equal(["a", "b", "c"], train.SourceShapeIds);
    }

    [Fact]
    public void Analyze_EmitsSingletonOnlyWhenGlyphWasNotConsumedByLargerTrain()
    {
        var geometry = new GeometricScene([
            Shape("a", 0, 0, 5, 10),
            Shape("b", 12, 0, 17, 10),
            Shape("lonely", 100, 0, 105, 10)
        ]);
        var notation = Notation(
            ("a", Poor()),
            ("b", Poor()),
            ("lonely", Poor()));
        var recognizer = new RecordingRecognizer("text", 0.95);

        var analysis = new FallbackTextRecognitionAnalyzer(recognizer).Analyze(
            geometry,
            notation,
            Layout(10));

        Assert.Equal(2, analysis.Observations.Count);

        var train = Assert.Single(
            analysis.Observations,
            item => item.Kind == TextCandidateKind.HorizontalRun);
        Assert.Equal(["a", "b"], train.SourceShapeIds);

        var singleton = Assert.Single(
            analysis.Observations,
            item => item.Kind == TextCandidateKind.Prototype);
        Assert.Equal(["lonely"], singleton.SourceShapeIds);

        var allShapeIds = analysis.Observations
            .SelectMany(item => item.SourceShapeIds)
            .ToArray();
        Assert.Equal(3, allShapeIds.Length);
        Assert.Equal(3, allShapeIds.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Analyze_DoesNotRunOcrForConfidentMusicGlyph()
    {
        var geometry = new GeometricScene([
            Shape("music", 0, 0, 5, 10)
        ]);
        var notation = Notation(("music", Clear("SHARP")));
        var recognizer = new RecordingRecognizer("ignored", 0.99);

        var analysis = new FallbackTextRecognitionAnalyzer(recognizer).Analyze(
            geometry,
            notation,
            Layout(10));

        Assert.Empty(analysis.Observations);
        Assert.Empty(recognizer.Candidates);
    }

    [Fact]
    public void Analyze_DoesNotJoinPoorGlyphsFromDifferentRows()
    {
        var geometry = new GeometricScene([
            Shape("a", 0, 0, 5, 10),
            Shape("b", 8, 0, 13, 10),
            Shape("other-row", 16, 40, 21, 50)
        ]);
        var notation = Notation(
            ("a", Poor()),
            ("b", Poor()),
            ("other-row", Poor()));
        var recognizer = new RecordingRecognizer("x", 0.90);

        var analysis = new FallbackTextRecognitionAnalyzer(recognizer).Analyze(
            geometry,
            notation,
            Layout(10));

        Assert.Equal(2, analysis.Observations.Count);
        Assert.Contains(
            analysis.Observations,
            item => item.Kind == TextCandidateKind.HorizontalRun
                && item.SourceShapeIds.SequenceEqual(["a", "b"]));
        Assert.Contains(
            analysis.Observations,
            item => item.Kind == TextCandidateKind.Prototype
                && item.SourceShapeIds.SequenceEqual(["other-row"]));
    }

    [Fact]
    public void Analyze_DoesNotUseGiantPoorGlyphAsTrainWagon()
    {
        var geometry = new GeometricScene([
            Shape("seed", 0, 0, 5, 10),
            Shape("staff-line", 6, 4, 200, 5)
        ]);
        var notation = Notation(
            ("seed", Poor()),
            ("staff-line", Poor()));
        var recognizer = new RecordingRecognizer("x", 0.90);

        var analysis = new FallbackTextRecognitionAnalyzer(recognizer).Analyze(
            geometry,
            notation,
            Layout(10));

        var single = Assert.Single(analysis.Observations);
        Assert.Equal(TextCandidateKind.Prototype, single.Kind);
        Assert.Equal(["seed"], single.SourceShapeIds);
    }

    [Fact]
    public void Analyze_TreatsConfidentClutterAsPoorRecognition()
    {
        var geometry = new GeometricScene([
            Shape("clutter", 0, 0, 5, 10)
        ]);
        var notation = Notation((
            "clutter",
            new SymbolClassification("CLUTTER", 0.99, 30, [])));
        var recognizer = new RecordingRecognizer("A", 0.99);

        var analysis = new FallbackTextRecognitionAnalyzer(recognizer).Analyze(
            geometry,
            notation,
            Layout(10));

        var single = Assert.Single(analysis.Observations);
        Assert.Equal(["clutter"], single.SourceShapeIds);
    }

    private static SymbolClassification Poor() =>
        new("ACCENT", 0.30, 30, []);

    private static SymbolClassification Clear(string label) =>
        new(label, 0.99, 30, []);

    private static NotationScene Notation(
        params (string ShapeId, SymbolClassification? Classification)[] glyphs)
    {
        var descriptor = new ShapeDescriptor(1, 1, []);
        var prototypes = glyphs
            .Select((glyph, index) => new ShapePrototype(
                $"p{index}",
                glyph.ShapeId,
                descriptor,
                glyph.Classification))
            .ToArray();
        var instances = glyphs
            .Select((glyph, index) => new ShapeInstance(
                glyph.ShapeId,
                $"p{index}",
                0,
                0,
                5,
                10,
                "path",
                null,
                glyph.Classification))
            .ToArray();

        return new NotationScene(
            prototypes,
            instances,
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
