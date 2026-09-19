using SvgMusic.Scene;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class GlyphRasterizerTests
{
    [Fact]
    public void ClosedContours_UseEvenOddFillAndPreserveHole()
    {
        var outer = new GeometricContour(
            [
                new PointD(0, 0),
                new PointD(10, 0),
                new PointD(10, 10),
                new PointD(0, 10)
            ],
            true);
        var inner = new GeometricContour(
            [
                new PointD(3, 3),
                new PointD(7, 3),
                new PointD(7, 7),
                new PointD(3, 7)
            ],
            true);

        var shape = new GeometricShape(
            "shape",
            "test",
            outer.Points,
            new BoundsD(0, 0, 10, 10),
            Contours: [outer, inner],
            HasFill: true);

        var glyph = new GlyphRasterizer().Rasterize(
            shape,
            sourceInterline: 10,
            targetInterline: 10);

        Assert.True(glyph.ForegroundPixels > 0);

        // Padding=4, so these sample the center of the filled frame and hole.
        Assert.Equal(0, glyph.Pixels[6 * glyph.Width + 6]);
        Assert.Equal(255, glyph.Pixels[9 * glyph.Width + 9]);
    }
}
