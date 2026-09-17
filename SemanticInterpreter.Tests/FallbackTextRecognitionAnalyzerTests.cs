using SvgMusic.Scene;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class FallbackTextRecognitionAnalyzerTests
{
    [Fact]
    public void Analyze_PoorGlyphActivatesWholeHorizontalRowIncludingConfidentNeighbours()
    {
        var geometry = new GeometricScene([
            Shape("bad-y", 0, 0, 5, 10),
            Shape("bad-e", 7, 1, 12, 11),
            Shape("primitive-stem", 14, 0, 16, 10),
            Shape("primitive-oval", 18, 1, 23, 11),
            Shape("confident-marcato", 25, 0, 30, 10)
        ]);
        var notation = Notation(
            ("bad-y", Poor()),
            ("bad-e", Poor()),
            ("primitive-stem", Clear("STEM")),
            ("primitive-oval", Clear("NOTEHEAD_BLACK")),
            ("confident-marcato", Clear("MARCATO")));
        var recognizer = new RecordingRecognizer("YELLOW", 0.99);

        var analysis = new FallbackTextRecognitionAnalyzer(recognizer).Analyze(
            geometry,
            notation,
            Layout(10));

        var train = Assert.Single(analysis.Observations);
        Assert.Equal(TextCandidateKind.HorizontalRun, train.Kind);
        Assert.Equal(
            ["bad-y", "bad-e", "primitive-stem", "primitive-oval", "confident-marcato"],
            train.SourceShapeIds);
        Assert.Equal("YELLOW", train.Recognition?.Text);
    }

    [Fact]
    public void Analyze_SplitsActivatedRowAtGapOverTwoAverageWidths_AndOcrsEveryPiece()
    {
        var geometry = new GeometricScene([
            Shape("seed", 0, 0, 5, 10),
            Shape("left-good", 7, 0, 12, 10),
            Shape("right-good-a", 30, 0, 35, 10),
            Shape("right-good-b", 37, 0, 42, 10)
        ]);
        var notation = Notation(
            ("seed", Poor()),
            ("left-good", Clear("SHARP")),
            ("right-good-a", Clear("FLAT")),
            ("right-good-b", Clear("MARCATO")));
        var recognizer = new RecordingRecognizer("text", 0.98);

        var analysis = new FallbackTextRecognitionAnalyzer(recognizer).Analyze(
            geometry,
            notation,
            Layout(10));

        Assert.Equal(2, analysis.Observations.Count);
        Assert.Contains(
            analysis.Observations,
            item => item.SourceShapeIds.SequenceEqual(["seed", "left-good"]));
        Assert.Contains(
            analysis.Observations,
            item => item.SourceShapeIds.SequenceEqual(["right-good-a", "right-good-b"]));
        Assert.Equal(2, recognizer.Candidates.Count);
    }

    [Fact]
    public void Analyze_AllowsVerticalJitterWhenBuildingActivatedRow()
    {
        var geometry = new GeometricScene([
            Shape("seed", 0, 0, 5, 10),
            Shape("jittered", 7, 6, 12, 16)
        ]);
        var notation = Notation(
            ("seed", Poor()),
            ("jittered", Clear("SHARP")));
        var recognizer = new RecordingRecognizer("ye", 0.95);

        var analysis = new FallbackTextRecognitionAnalyzer(recognizer).Analyze(
            geometry,
            notation,
            Layout(10));

        var train = Assert.Single(analysis.Observations);
        Assert.Equal(TextCandidateKind.HorizontalRun, train.Kind);
        Assert.Equal(["seed", "jittered"], train.SourceShapeIds);
    }

    [Fact]
    public void Analyze_EmitsSingletonWhenActivatedPieceContainsOneGlyph()
    {
        var geometry = new GeometricScene([
            Shape("seed", 20, 10, 25, 20)
        ]);
        var notation = Notation(("seed", Poor()));
        var recognizer = new RecordingRecognizer("x", 0.91);

        var analysis = new FallbackTextRecognitionAnalyzer(recognizer).Analyze(
            geometry,
            notation,
            Layout(10));

        var singleton = Assert.Single(analysis.Observations);
        Assert.Equal(TextCandidateKind.Prototype, singleton.Kind);
        Assert.Equal(["seed"], singleton.SourceShapeIds);
    }

    [Fact]
    public void Analyze_DoesNotRunOcrWhenThereIsNoPoorSeed()
    {
        var geometry = new GeometricScene([
            Shape("music", 0, 0, 5, 10),
            Shape("neighbour", 7, 0, 12, 10)
        ]);
        var notation = Notation(
            ("music", Clear("SHARP")),
            ("neighbour", Clear("FLAT")));
        var recognizer = new RecordingRecognizer("ignored", 0.99);

        var analysis = new FallbackTextRecognitionAnalyzer(recognizer).Analyze(
            geometry,
            notation,
            Layout(10));

        Assert.Empty(analysis.Observations);
        Assert.Empty(recognizer.Candidates);
    }

    [Fact]
    public void Analyze_DoesNotPullGlyphsFromDifferentRows()
    {
        var geometry = new GeometricScene([
            Shape("seed", 0, 0, 5, 10),
            Shape("same-row", 8, 0, 13, 10),
            Shape("other-row", 16, 40, 21, 50)
        ]);
        var notation = Notation(
            ("seed", Poor()),
            ("same-row", Clear("SHARP")),
            ("other-row", Clear("FLAT")));
        var recognizer = new RecordingRecognizer("x", 0.90);

        var analysis = new FallbackTextRecognitionAnalyzer(recognizer).Analyze(
            geometry,
            notation,
            Layout(10));

        var train = Assert.Single(analysis.Observations);
        Assert.Equal(["seed", "same-row"], train.SourceShapeIds);
        Assert.DoesNotContain("other-row", train.SourceShapeIds);
    }

    [Fact]
    public void Analyze_DoesNotPullGiantStaffLineIntoActivatedRow()
    {
        var geometry = new GeometricScene([
            Shape("seed", 0, 0, 5, 10),
            Shape("staff-line", 6, 4, 200, 5)
        ]);
        var notation = Notation(
            ("seed", Poor()),
            ("staff-line", Clear("LEDGER")));
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
    public void Analyze_TreatsConfidentClutterAsPoorRecognitionSeed()
    {
        var geometry = new GeometricScene([
            Shape("clutter", 0, 0, 5, 10),
            Shape("good-neighbour", 7, 0, 12, 10)
        ]);
        var notation = Notation(
            ("clutter", new SymbolClassification("CLUTTER", 0.99, 30, [])),
            ("good-neighbour", Clear("SHARP")));
        var recognizer = new RecordingRecognizer("AB", 0.99);

        var analysis = new FallbackTextRecognitionAnalyzer(recognizer).Analyze(
            geometry,
            notation,
            Layout(10));

        var train = Assert.Single(analysis.Observations);
        Assert.Equal(["clutter", "good-neighbour"], train.SourceShapeIds);
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
