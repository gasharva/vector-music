using SvgMusic.Scene;
using SvgMusic.Semantics;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class TextPassTests
{
    [Fact]
    public void HeaderRolesComeFromUnownedGlyphHeightNotTextContent()
    {
        var geometry = new GeometricScene([
            Shape("composer", 10, 10, 40, 20),
            Shape("subtitle", 10, 10, 60, 30),
            Shape("title", 10, 5, 80, 35)
        ]);
        var analysis = Analysis(
            Observation("composer", "Anything A", 0.99, geometry),
            Observation("subtitle", "Anything B", 0.99, geometry),
            Observation("title", "Anything C", 0.99, geometry));
        var facts = new SemanticFacts();

        new TextPass(
            analysis,
            geometry,
            Layout(),
            new LogicalOwnershipScene([]))
            .Run(Document(), facts);

        var textFacts = facts.OfType<TextFact>().ToArray();
        Assert.Equal(
            SemanticTextRole.Composer,
            Assert.Single(textFacts, fact => fact.Text == "Anything A").Role);
        Assert.Equal(
            SemanticTextRole.Subtitle,
            Assert.Single(textFacts, fact => fact.Text == "Anything B").Role);
        Assert.Equal(
            SemanticTextRole.Title,
            Assert.Single(textFacts, fact => fact.Text == "Anything C").Role);
    }

    [Fact]
    public void MusicExplainedOcrInsideStaffBecomesUnknown()
    {
        var geometry = new GeometricScene([
            Shape("note", 45, 108, 55, 118)
        ]);
        var analysis = Analysis(
            Observation("note", "O", 0.97, geometry));
        var facts = new SemanticFacts();
        facts.Add(new NoteheadFact(
            1,
            1,
            "note",
            50,
            113,
            5,
            4,
            "filled",
            1,
            0,
            0,
            0.99,
            "test",
            ["note"]));

        new TextPass(
            analysis,
            geometry,
            Layout(),
            Ownership("note"))
            .Run(Document(), facts);

        var text = Assert.Single(facts.OfType<TextFact>());
        Assert.Equal(SemanticTextRole.Unknown, text.Role);
    }

    [Fact]
    public void SingleDigitOneToFiveAboveNoteBecomesFingering()
    {
        var geometry = new GeometricScene([
            Shape("finger", 47, 80, 53, 90),
            Shape("note", 45, 105, 55, 115)
        ]);
        var analysis = Analysis(
            Observation("finger", "3", 0.98, geometry));
        var facts = new SemanticFacts();
        facts.Add(new NoteheadFact(
            1,
            1,
            "note",
            50,
            110,
            5,
            4,
            "filled",
            1,
            0,
            0,
            0.99,
            "test",
            ["note"]));

        new TextPass(
            analysis,
            geometry,
            Layout(),
            new LogicalOwnershipScene([]))
            .Run(Document(), facts);

        var text = Assert.Single(facts.OfType<TextFact>());
        Assert.Equal(SemanticTextRole.Fingering, text.Role);
        Assert.Equal("note", text.AnchorShapeId);
        Assert.Equal("3", text.Text);
    }

    [Fact]
    public void MultiGlyphTextAboveFirstMeasureBecomesTempoWithoutReadingWords()
    {
        var geometry = new GeometricScene([
            Shape("t1", 80, 60, 95, 75),
            Shape("t2", 98, 60, 113, 75)
        ]);
        var analysis = Analysis(
            new TextRecognitionObservation(
                "tempo",
                TextCandidateKind.HorizontalRun,
                new BoundsD(80, 60, 113, 75),
                ["t1", "t2"],
                null,
                new TextRecognition("not-a-musical-keyword", 0.96, "test")));
        var facts = new SemanticFacts();

        new TextPass(
            analysis,
            geometry,
            Layout(),
            new LogicalOwnershipScene([]))
            .Run(Document(), facts);

        var text = Assert.Single(facts.OfType<TextFact>());
        Assert.Equal(SemanticTextRole.Tempo, text.Role);
        Assert.Equal(1, text.MeasureNumber);
        Assert.Equal(1, text.Staff);
        Assert.Equal("0", text.At);
    }

    [Fact]
    public void AcceptedMusicSourceWinsOverOcrInstructionOutsideStaff()
    {
        var geometry = new GeometricScene([
            Shape("music", 80, 70, 100, 85)
        ]);
        var analysis = Analysis(
            Observation("music", "du", 0.95, geometry));
        var facts = new SemanticFacts();
        facts.Add(new DynamicDirectionFact(
            1,
            1,
            "music",
            "mp",
            "0",
            "above",
            "DYNAMICS_MP",
            0.99,
            90,
            77,
            0.99,
            "test dynamic",
            ["music"]));

        new TextPass(
            analysis,
            geometry,
            Layout(),
            new LogicalOwnershipScene([]))
            .Run(Document(), facts);

        var text = Assert.Single(facts.OfType<TextFact>());
        Assert.Equal(SemanticTextRole.Unknown, text.Role);
    }

    private static TextRecognitionAnalysisResult Analysis(
        params TextRecognitionObservation[] observations) =>
        new(observations);

    private static TextRecognitionObservation Observation(
        string shapeId,
        string text,
        double confidence,
        GeometricScene geometry)
    {
        var shape = geometry.Shapes.Single(item => item.Id == shapeId);
        return new TextRecognitionObservation(
            "ocr-" + shapeId,
            TextCandidateKind.Prototype,
            shape.Bounds,
            [shapeId],
            null,
            new TextRecognition(text, confidence, "test"));
    }

    private static LogicalOwnershipScene Ownership(string shapeId) =>
        new([
            new LogicalOwnershipAssignment(
                shapeId,
                "ShapeInstance",
                new LogicalOwnership(
                    new LogicalCoordinate("staff-1", "measure-1"),
                    new LogicalCoordinate("staff-1", "measure-1"),
                    1,
                    null,
                    0,
                    "test"))
        ]);

    private static SemanticDocument Document() =>
        new([
            new MeasureScene(
                1,
                "system-1",
                "pair-1",
                "measure-1",
                0,
                300,
                false,
                new StaffMeasureScene(
                    1,
                    "staff-1",
                    new BoundsD(0, 100, 300, 140),
                    10,
                    []),
                new StaffMeasureScene(
                    2,
                    "staff-2",
                    new BoundsD(0, 200, 300, 240),
                    10,
                    []))
        ]);

    private static ScoreLayout Layout() =>
        new(
            [
                new ScoreSystem(
                    "system-1",
                    new BoundsD(0, 100, 300, 240),
                    [
                        new StaffPairLayout(
                            "pair-1",
                            "staff-1",
                            "staff-2",
                            new BoundsD(0, 100, 300, 240),
                            [
                                new MeasureLayout(
                                    "measure-1",
                                    0,
                                    300,
                                    new MeasureBoundary(0, 100, 240, []),
                                    new MeasureBoundary(300, 100, 240, []))
                            ],
                            [])
                    ])
            ],
            [
                new StaffLayout(
                    "staff-1",
                    "system-1",
                    new BoundsD(0, 100, 300, 140),
                    [],
                    10,
                    []),
                new StaffLayout(
                    "staff-2",
                    "system-1",
                    new BoundsD(0, 200, 300, 240),
                    [],
                    10,
                    [])
            ]);

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
}
