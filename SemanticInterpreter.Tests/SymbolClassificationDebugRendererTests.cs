using System.Xml.Linq;
using SvgMusic.Scene;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class SymbolClassificationDebugRendererTests
{
    [Fact]
    public void Render_ShowsEveryClassification_WithTrafficLightConfidenceLabels()
    {
        var input = Path.Combine(
            Path.GetTempPath(),
            $"classified-input-{Guid.NewGuid():N}.svg");
        var output = Path.Combine(
            Path.GetTempPath(),
            $"classified-output-{Guid.NewGuid():N}.svg");

        try
        {
            File.WriteAllText(
                input,
                "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 100 100\"></svg>");

            var shapes = new[]
            {
                Shape("green", 5),
                Shape("amber", 35),
                Shape("red", 65)
            };

            var instances = new[]
            {
                Instance("green", 5, "TUPLET_5", 0.92),
                Instance("amber", 35, "fermata", 0.70),
                Instance("red", 65, "flag-1-up", 0.42)
            };

            var notation = new NotationScene(
                Array.Empty<ShapePrototype>(),
                instances,
                Array.Empty<Stroke>(),
                Array.Empty<CurvedStroke>(),
                Array.Empty<EllipseLike>());

            new SymbolClassificationDebugRenderer().Render(
                input,
                new GeometricScene(shapes),
                notation,
                output);

            var document = XDocument.Load(output);
            var labels = document
                .Descendants()
                .Where(element => element.Name.LocalName == "text")
                .ToDictionary(
                    element => element.Value,
                    element => (string?)element.Attribute("fill"));

            Assert.Equal("#1a7f37", labels["TUPLET_5 92%"]);
            Assert.Equal("#bf8700", labels["FERMATA 70%"]);
            Assert.Equal("#cf222e", labels["FLAG_1_UP 42%"]);
            Assert.Equal(3, labels.Count);
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    private static GeometricShape Shape(
        string id,
        double x)
    {
        IReadOnlyList<PointD> points =
        [
            new PointD(x, 10),
            new PointD(x + 8, 10),
            new PointD(x + 8, 18),
            new PointD(x, 18)
        ];

        return new GeometricShape(
            id,
            "path",
            points,
            BoundsD.FromPoints(points),
            null,
            null,
            true,
            0,
            [new GeometricContour(points, true)],
            true,
            false);
    }

    private static ShapeInstance Instance(
        string shapeId,
        double x,
        string label,
        double confidence) =>
        new(
            shapeId,
            "prototype",
            x,
            10,
            8,
            8,
            "path",
            null,
            new SymbolClassification(
                label,
                confidence,
                30,
                Array.Empty<SymbolScaleResult>()));
}
