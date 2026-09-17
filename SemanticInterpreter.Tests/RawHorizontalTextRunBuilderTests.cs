using SvgMusic.Scene;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class RawHorizontalTextRunBuilderTests
{
    [Fact]
    public void Build_UsesRawPrimitiveLikeShapeAsUncertainBridge()
    {
        var geometry = new GeometricScene([
            Shape("L", 0, 0, 5, 10),
            Shape("O-ellipse", 7, 0, 13, 10),
            Shape("W", 15, 0, 24, 10)
        ]);
        var recognitions = new Dictionary<string, TextRecognition?>(StringComparer.Ordinal)
        {
            ["L"] = new TextRecognition("L", 0.98, "test"),
            ["W"] = new TextRecognition("W", 0.97, "test")
            // O is intentionally absent, exactly like a shape consumed earlier as
            // an ellipse/stroke primitive and therefore never singleton-OCR'd.
        };

        var runs = new RawHorizontalTextRunBuilder().Build(
            geometry,
            Layout(10),
            recognitions);

        var run = Assert.Single(runs);
        Assert.Equal("raw-run:1", run.Id);
        Assert.Equal(
            ["L", "O-ellipse", "W"],
            run.SourceShapeIds);
    }

    [Fact]
    public void Build_SkipsRunWhenEverySingletonGlyphIsClear()
    {
        var geometry = new GeometricScene([
            Shape("A", 0, 0, 5, 10),
            Shape("B", 7, 0, 13, 10),
            Shape("C", 15, 0, 21, 10)
        ]);
        var recognitions = new Dictionary<string, TextRecognition?>(StringComparer.Ordinal)
        {
            ["A"] = new TextRecognition("A", 0.99, "test"),
            ["B"] = new TextRecognition("B", 0.96, "test"),
            ["C"] = new TextRecognition("C", 0.95, "test")
        };

        var runs = new RawHorizontalTextRunBuilder().Build(
            geometry,
            Layout(10),
            recognitions);

        Assert.Empty(runs);
    }

    [Fact]
    public void Build_RejectsGiantStaffLineBeforeGrouping()
    {
        var geometry = new GeometricScene([
            Shape("left", 0, 0, 5, 10),
            Shape("staff-line", 6, 4, 200, 5),
            Shape("right", 202, 0, 207, 10)
        ]);

        var runs = new RawHorizontalTextRunBuilder().Build(
            geometry,
            Layout(10),
            new Dictionary<string, TextRecognition?>(StringComparer.Ordinal));

        Assert.Empty(runs);
    }

    [Fact]
    public void Build_PrefersReconstructedWholeGlyphOverItsSplitParts()
    {
        var geometry = new GeometricScene([
            Shape("glyph.1", 0, 0, 2, 10),
            Shape("glyph.2", 3, 0, 5, 10),
            Shape("glyph.whole", 0, 0, 5, 10),
            Shape("next", 7, 0, 13, 10)
        ]);
        var recognitions = new Dictionary<string, TextRecognition?>(StringComparer.Ordinal)
        {
            ["next"] = new TextRecognition("X", 0.98, "test")
        };

        var runs = new RawHorizontalTextRunBuilder().Build(
            geometry,
            Layout(10),
            recognitions);

        var run = Assert.Single(runs);
        Assert.Equal(["glyph.whole", "next"], run.SourceShapeIds);
        Assert.DoesNotContain("glyph.1", run.SourceShapeIds);
        Assert.DoesNotContain("glyph.2", run.SourceShapeIds);
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
}
