using SvgMusic.Scene;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class VerticalZigZagExtractorTests
{
    [Fact]
    public void RegularVerticalZigZag_IsExtracted()
    {
        var shape = Shape(
            "arpeggio-like",
            [
                new PointD(10, 0),
                new PointD(12, 4),
                new PointD(8, 8),
                new PointD(12, 12),
                new PointD(8, 16),
                new PointD(12, 20),
                new PointD(8, 24),
                new PointD(12, 28),
                new PointD(10, 32)
            ]);

        var extractor = new VerticalZigZagExtractor();

        Assert.True(extractor.TryCreateVerticalZigZag(shape, out var zigZag));
        Assert.Equal("arpeggio-like", zigZag.ShapeId);
        Assert.True(zigZag.TurnCount >= 5);
        Assert.True(zigZag.Confidence >= 0.60);
    }

    [Fact]
    public void StraightVerticalStroke_IsRejected()
    {
        var shape = Shape(
            "barline",
            [
                new PointD(10, 0),
                new PointD(10, 5),
                new PointD(10, 10),
                new PointD(10, 15),
                new PointD(10, 20),
                new PointD(10, 25)
            ]);

        Assert.False(new VerticalZigZagExtractor().TryCreateVerticalZigZag(
            shape,
            out _));
    }

    [Fact]
    public void ShallowSingleArc_IsRejected()
    {
        var shape = Shape(
            "arc",
            [
                new PointD(10, 0),
                new PointD(11, 4),
                new PointD(12, 8),
                new PointD(13, 12),
                new PointD(12, 16),
                new PointD(11, 20),
                new PointD(10, 24)
            ]);

        Assert.False(new VerticalZigZagExtractor().TryCreateVerticalZigZag(
            shape,
            out _));
    }

    [Fact]
    public void HorizontalZigZag_IsRejectedByAspectRatio()
    {
        var shape = Shape(
            "horizontal-zigzag",
            [
                new PointD(0, 10),
                new PointD(4, 12),
                new PointD(8, 8),
                new PointD(12, 12),
                new PointD(16, 8),
                new PointD(20, 12),
                new PointD(24, 8),
                new PointD(28, 12),
                new PointD(32, 10)
            ]);

        Assert.False(new VerticalZigZagExtractor().TryCreateVerticalZigZag(
            shape,
            out _));
    }

    [Fact]
    public void VerticalZigZagCandidate_SuppressesResidualOnlyAfterLayoutResolution()
    {
        var zigZag = Shape(
            "zigzag",
            [
                new PointD(10, 0),
                new PointD(12, 4),
                new PointD(8, 8),
                new PointD(12, 12),
                new PointD(8, 16),
                new PointD(12, 20),
                new PointD(8, 24),
                new PointD(12, 28),
                new PointD(10, 32)
            ]);

        var scene = new GeometricScene([zigZag]);
        var candidates = new CompositeCandidateDetector().Detect(scene);
        var primitiveNotation = new ShapeClusterer().Cluster(scene);

        var notation = new CompositeCandidateResolver().Resolve(
            primitiveNotation,
            candidates,
            new ScoreLayout([], []));

        var primitive = Assert.Single(notation.VerticalZigZags);
        Assert.Equal("zigzag", primitive.ShapeId);
        Assert.Empty(notation.Instances);
        Assert.Empty(notation.Prototypes);
    }

    [Fact]
    public void RepeatedAlignedGlyphTiles_AreResolvedIntoOneVerticalZigZagRun()
    {
        var shapes = Enumerable.Range(0, 9)
            .Select(index => Tile($"tile-{index}", 100, 200 + index * 18.7))
            .ToArray();
        var scene = new GeometricScene(shapes);
        var candidates = new CompositeCandidateDetector().Detect(scene);
        var primitiveNotation = new ShapeClusterer().Cluster(scene);

        var notation = new CompositeCandidateResolver().Resolve(
            primitiveNotation,
            candidates,
            new ScoreLayout([], []));

        var primitive = Assert.Single(notation.VerticalZigZags);
        Assert.Equal("tile-0", primitive.ShapeId);
        Assert.True(primitive.Bounds.Height > primitive.Bounds.Width * 5);
        Assert.Equal(8, primitive.TurnCount);
        Assert.Empty(notation.Instances);
        Assert.Empty(notation.Prototypes);
    }

    [Fact]
    public void RepeatedGlyphsWithoutVerticalAlignment_AreNotAssembled()
    {
        var shapes = Enumerable.Range(0, 6)
            .Select(index => Tile($"tile-{index}", 100 + index * 7, 200 + index * 18.7))
            .ToArray();

        var result = new VerticalZigZagRunExtractor().Extract(new GeometricScene(shapes));

        Assert.Empty(result.ZigZags);
        Assert.Empty(result.ConsumedShapeIds);
    }

    private static GeometricShape Tile(string id, double centerX, double centerY)
    {
        var points = new[]
        {
            new PointD(centerX - 5, centerY - 9),
            new PointD(centerX - 1, centerY - 10),
            new PointD(centerX + 4, centerY - 7),
            new PointD(centerX + 6, centerY - 2),
            new PointD(centerX + 3, centerY + 4),
            new PointD(centerX + 1, centerY + 9),
            new PointD(centerX - 4, centerY + 8),
            new PointD(centerX - 6, centerY + 2),
            new PointD(centerX - 5, centerY - 9)
        };

        return new GeometricShape(
            id,
            "path",
            points,
            BoundsD.FromPoints(points),
            IsClosed: true,
            HasFill: true,
            HasStroke: false);
    }

    private static GeometricShape Shape(
        string id,
        IReadOnlyList<PointD> points) =>
        new(
            id,
            "polyline",
            points,
            BoundsD.FromPoints(points),
            null,
            null,
            false,
            1.5,
            [new GeometricContour(points, false)],
            false,
            true);
}
