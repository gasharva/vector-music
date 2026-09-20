using SvgMusic.Scene;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class SvgNormalizerTransformTests
{
    [Fact]
    public void UniformScale_TransformsStrokeWidthTogetherWithCoordinates()
    {
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100">
              <path
                d="M 0 0 L 100 0"
                fill="none"
                stroke="black"
                stroke-width="10"
                transform="matrix(0.06, 0, 0, 0.06, 5, 7)" />
            </svg>
            """;

        var path = WriteTempSvg(svg);

        try
        {
            var scene = new SvgNormalizer().Normalize(path);
            var shape = Assert.Single(scene.Shapes);

            Assert.Equal(5.0, shape.Points[0].X, 6);
            Assert.Equal(7.0, shape.Points[0].Y, 6);
            Assert.Equal(11.0, shape.Points[^1].X, 6);
            Assert.Equal(7.0, shape.Points[^1].Y, 6);
            Assert.Equal(0.6, shape.StrokeWidth, 6);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void NestedUniformScale_AlsoTransformsInheritedStrokeWidth()
    {
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100">
              <g
                stroke="black"
                stroke-width="8"
                fill="none"
                transform="matrix(0.125, 0, 0, 0.125, 3, 4)">
                <path d="M 0 0 L 40 0" />
              </g>
            </svg>
            """;

        var path = WriteTempSvg(svg);

        try
        {
            var scene = new SvgNormalizer().Normalize(path);
            var shape = Assert.Single(scene.Shapes);

            Assert.Equal(3.0, shape.Points[0].X, 6);
            Assert.Equal(4.0, shape.Points[0].Y, 6);
            Assert.Equal(8.0, shape.Points[^1].X, 6);
            Assert.Equal(4.0, shape.Points[^1].Y, 6);
            Assert.Equal(1.0, shape.StrokeWidth, 6);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WriteTempSvg(string svg)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"svg-normalizer-{Guid.NewGuid():N}.svg");
        File.WriteAllText(path, svg);
        return path;
    }
}
