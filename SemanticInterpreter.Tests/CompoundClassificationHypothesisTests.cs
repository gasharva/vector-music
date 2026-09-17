using SvgMusic.Scene;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class CompoundClassificationHypothesisTests
{
    [Fact]
    public void SplitComponents_ProduceWholeClassificationAlternative()
    {
        var geometry = new GeometricScene([
            Rectangle("shape-20.1", 10, 20, 20, 30),
            Rectangle("shape-20.2", 22, 20, 32, 30),
            Rectangle("shape-20.3", 34, 20, 38, 30)
        ]);
        var notation = EmptyNotation();
        var layout = Layout(spacing: 10);

        var augmented = new CompoundClassificationHypothesisBuilder()
            .Augment(geometry, notation, layout);

        var whole = Assert.Single(
            augmented.Geometry.Shapes,
            shape => shape.Id == "shape-20.whole");
        Assert.Equal(new BoundsD(10, 20, 38, 30), whole.Bounds);

        var instance = Assert.Single(
            augmented.Notation.Instances,
            item => item.ShapeId == "shape-20.whole");
        Assert.Equal(
            ["shape-20.1", "shape-20.2", "shape-20.3"],
            instance.AbsorbedPrimitiveShapeIds);

        var classified = new PrototypeSymbolClassifier(new FixedClassifier())
            .Classify(augmented.Geometry, augmented.Notation, layout);
        var classifiedWhole = Assert.Single(
            classified.Instances,
            item => item.ShapeId == "shape-20.whole");
        Assert.Equal("DYNAMICS_MP", classifiedWhole.Classification?.Label);
        Assert.Equal(0.99, classifiedWhole.Classification?.Confidence);
    }

    [Fact]
    public void OversizedCompound_IsNotAddedAsClassificationAlternative()
    {
        var geometry = new GeometricScene([
            Rectangle("shape-50.1", 0, 0, 40, 10),
            Rectangle("shape-50.2", 40, 0, 80, 10)
        ]);

        var augmented = new CompoundClassificationHypothesisBuilder()
            .Augment(geometry, EmptyNotation(), Layout(spacing: 10));

        Assert.DoesNotContain(
            augmented.Geometry.Shapes,
            shape => shape.Id == "shape-50.whole");
        Assert.DoesNotContain(
            augmented.Notation.Instances,
            instance => instance.ShapeId == "shape-50.whole");
    }

    private static GeometricShape Rectangle(
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
            new PointD(minX, maxY),
            new PointD(minX, minY)
        };
        var contour = new GeometricContour(points, true);

        return new GeometricShape(
            id,
            "path",
            points,
            new BoundsD(minX, minY, maxX, maxY),
            SourceId: "dynamic",
            SourceIndex: "20",
            IsClosed: true,
            Contours: [contour],
            HasFill: true,
            SourceClass: "Dynamic");
    }

    private static NotationScene EmptyNotation() =>
        new([], [], [], [], []);

    private static ScoreLayout Layout(double spacing) =>
        new(
            [],
            [
                new StaffLayout(
                    "staff-1",
                    "system-1",
                    new BoundsD(0, 100, 200, 140),
                    [],
                    spacing,
                    [])
            ]);

    private sealed class FixedClassifier : ISymbolClassifier
    {
        public IReadOnlyList<SymbolPrediction> Classify(
            RasterGlyphData glyph,
            int top = 8) =>
            [new SymbolPrediction("DYNAMICS_MP", 0.99, glyph.Interline)];
    }
}
