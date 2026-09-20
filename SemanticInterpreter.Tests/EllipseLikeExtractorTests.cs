using SvgMusic.Scene;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class EllipseLikeExtractorTests
{
    [Fact]
    public void StylizedOuterContour_WithCleanInnerHole_IsRecoveredAsHollowEllipse()
    {
        var outer = StylizedEllipse(
            centerX: 20,
            centerY: 30,
            radiusX: 3.0,
            radiusY: 2.0,
            radialWarp: 0.30);
        var inner = Ellipse(
            centerX: 20,
            centerY: 30,
            radiusX: 2.2,
            radiusY: 0.85);

        var shape = Shape(
            "hollow-notehead",
            outer,
            inner);
        var extractor = new EllipseLikeExtractor();

        var recognized = extractor.TryCreateEllipse(
            shape,
            out var ellipse);

        Assert.True(recognized);
        Assert.True(ellipse.IsHollow);
        Assert.InRange(ellipse.FitError, 0.18, 0.26);
        Assert.NotNull(ellipse.HoleRatio);
    }

    [Fact]
    public void SameStylizedOuterContour_WithoutInnerHole_RemainsRejected()
    {
        var outer = StylizedEllipse(
            centerX: 20,
            centerY: 30,
            radiusX: 3.0,
            radiusY: 2.0,
            radialWarp: 0.30);

        var shape = Shape(
            "arbitrary-stylized-contour",
            outer);
        var extractor = new EllipseLikeExtractor();

        var recognized = extractor.TryCreateEllipse(
            shape,
            out _);

        Assert.False(recognized);
    }

    private static GeometricShape Shape(
        string id,
        params IReadOnlyList<PointD>[] contours)
    {
        var geometricContours = contours
            .Select(points => new GeometricContour(
                points,
                IsClosed: true))
            .ToArray();
        var allPoints = contours
            .SelectMany(points => points)
            .ToArray();

        return new GeometricShape(
            id,
            "path",
            allPoints,
            BoundsD.FromPoints(allPoints),
            IsClosed: true,
            Contours: geometricContours,
            HasFill: true);
    }

    private static IReadOnlyList<PointD> StylizedEllipse(
        double centerX,
        double centerY,
        double radiusX,
        double radiusY,
        double radialWarp)
    {
        const int samples = 80;
        var points = new List<PointD>(samples + 1);

        for (var index = 0; index < samples; index++)
        {
            var angle = Math.PI * 2.0 * index / samples;
            var radiusScale =
                1.0 + radialWarp * Math.Cos(3.0 * angle);

            points.Add(new PointD(
                centerX
                    + radiusX
                    * radiusScale
                    * Math.Cos(angle),
                centerY
                    + radiusY
                    * radiusScale
                    * Math.Sin(angle)));
        }

        points.Add(points[0]);
        return points;
    }

    private static IReadOnlyList<PointD> Ellipse(
        double centerX,
        double centerY,
        double radiusX,
        double radiusY)
    {
        const int samples = 80;
        var points = new List<PointD>(samples + 1);

        for (var index = 0; index < samples; index++)
        {
            var angle = Math.PI * 2.0 * index / samples;

            points.Add(new PointD(
                centerX + radiusX * Math.Cos(angle),
                centerY + radiusY * Math.Sin(angle)));
        }

        points.Add(points[0]);
        return points;
    }
}
