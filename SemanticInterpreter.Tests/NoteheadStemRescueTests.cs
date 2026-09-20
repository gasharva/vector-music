using SvgMusic.Scene;
using SvgMusic.Semantics;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class NoteheadStemRescueTests
{
    [Fact]
    public void OffGridFilledEllipse_WithTouchingStem_IsRescued()
    {
        var document = Document(
            ellipseCenterY: 127.25,
            includeTouchingStem: true);

        var analysis = new NoteheadAnalyzer().Analyze(document);

        var decision = Assert.Single(
            analysis.Decisions);

        Assert.True(decision.Accepted);
        Assert.Equal(
            "stem-supported-notehead",
            decision.Decision);
        Assert.InRange(
            decision.Grid.ErrorInHalfSteps,
            0.44,
            0.46);
    }

    [Fact]
    public void SameOffGridEllipse_WithoutTouchingStem_RemainsRejected()
    {
        var document = Document(
            ellipseCenterY: 127.25,
            includeTouchingStem: false);

        var analysis = new NoteheadAnalyzer().Analyze(document);

        var decision = Assert.Single(
            analysis.Decisions);

        Assert.False(decision.Accepted);
        Assert.Equal(
            "off-staff-grid",
            decision.Decision);
    }

    private static SemanticDocument Document(
        double ellipseCenterY,
        bool includeTouchingStem)
    {
        const double spacing = 10.0;
        const double centerX = 50.0;
        const double majorRadius = 6.0;
        const double minorRadius = 4.0;

        var ownership = new LogicalOwnership(
            new LogicalCoordinate(
                "staff-upper",
                "measure-1"),
            new LogicalCoordinate(
                "staff-upper",
                "measure-1"),
            1,
            null,
            0,
            "test");

        var ellipseSource = new EllipseLike(
            "head",
            new PointD(
                centerX,
                ellipseCenterY),
            majorRadius,
            minorRadius,
            0,
            false,
            null,
            0.05,
            "path",
            null,
            ownership);

        var elements = new List<SemanticElement>
        {
            new EllipseElement
            {
                ShapeId = "head",
                Bounds = new BoundsD(
                    centerX - majorRadius,
                    ellipseCenterY - minorRadius,
                    centerX + majorRadius,
                    ellipseCenterY + minorRadius),
                Ownership = ownership,
                Source = ellipseSource
            }
        };

        if (includeTouchingStem)
        {
            var stemX = centerX + majorRadius;
            var stem = new Stroke(
                "stem",
                new PointD(
                    stemX,
                    ellipseCenterY - 2),
                new PointD(
                    stemX,
                    ellipseCenterY - 24),
                1.0,
                "path",
                null,
                ownership);

            elements.Add(new StrokeElement
            {
                ShapeId = "stem",
                Bounds = new BoundsD(
                    stemX - 0.5,
                    ellipseCenterY - 24,
                    stemX + 0.5,
                    ellipseCenterY - 2),
                Ownership = ownership,
                Source = stem
            });
        }

        return new SemanticDocument(
        [
            new MeasureScene(
                1,
                "system-1",
                "pair-1",
                "measure-1",
                0,
                200,
                false,
                new StaffMeasureScene(
                    1,
                    "staff-upper",
                    new BoundsD(
                        0,
                        100,
                        200,
                        140),
                    spacing,
                    elements),
                new StaffMeasureScene(
                    2,
                    "staff-lower",
                    new BoundsD(
                        0,
                        180,
                        200,
                        220),
                    spacing,
                    []))
        ]);
    }
}
