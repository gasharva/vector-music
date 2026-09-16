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
    public void ShapeClusterer_RemovesVerticalZigZagFromResidualClassifierInput()
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

        var notation = new ShapeClusterer().Cluster(new GeometricScene([zigZag]));

        var primitive = Assert.Single(notation.VerticalZigZags);
        Assert.Equal("zigzag", primitive.ShapeId);
        Assert.Empty(notation.Instances);
        Assert.Empty(notation.Prototypes);
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
