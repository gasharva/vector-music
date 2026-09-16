using SvgMusic.Scene;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class BracketSpannerExtractorTests
{
    [Fact]
    public void MuseScorePedalPolyline_IsSolidRightUpBracket()
    {
        var scene = Scene(Shape(
            "pedal",
            [
                new PointD(974.9, 1892.3),
                new PointD(1931.1, 1892.3),
                new PointD(1931.1, 1866.81)
            ],
            strokeWidth: 2.34));

        var result = new BracketSpannerExtractor().Extract(scene);

        var bracket = Assert.Single(result.Brackets);
        Assert.False(bracket.IsDashed);
        Assert.Equal(BracketHookDirection.None, bracket.LeftHookDirection);
        Assert.Equal(BracketHookDirection.Up, bracket.RightHookDirection);
        Assert.Equal(["pedal"], bracket.SourceShapeIds);
    }

    [Fact]
    public void RightDownHook_IsRecognizedWithoutMusicalMeaning()
    {
        var scene = Scene(Shape(
            "right-down",
            [
                new PointD(100, 200),
                new PointD(500, 200),
                new PointD(500, 230)
            ],
            strokeWidth: 2.0));

        var bracket = Assert.Single(
            new BracketSpannerExtractor().Extract(scene).Brackets);

        Assert.Equal(BracketHookDirection.Down, bracket.RightHookDirection);
        Assert.Equal(BracketHookDirection.None, bracket.LeftHookDirection);
    }

    [Fact]
    public void MuseScoreVoltaPolyline_IsBothDownBracket()
    {
        var scene = Scene(Shape(
            "volta",
            [
                new PointD(1277.07, 3367.03),
                new PointD(1277.07, 3320.3),
                new PointD(2260.4, 3320.3),
                new PointD(2260.4, 3367.03)
            ],
            strokeWidth: 2.34));

        var bracket = Assert.Single(
            new BracketSpannerExtractor().Extract(scene).Brackets);

        Assert.False(bracket.IsDashed);
        Assert.Equal(BracketHookDirection.Down, bracket.LeftHookDirection);
        Assert.Equal(BracketHookDirection.Down, bracket.RightHookDirection);
    }

    [Fact]
    public void SeparateDashedOttavaStrokes_AreAssembledIntoOneBracket()
    {
        var horizontal = Shape(
            "ottava-horizontal",
            [
                new PointD(1886.18, 2506.44),
                new PointD(2084.9, 2506.44)
            ],
            strokeWidth: 2.34,
            dash: "14.0184,12.3676");
        var hook = Shape(
            "ottava-hook",
            [
                new PointD(2083.73, 2485.2),
                new PointD(2083.73, 2507.61)
            ],
            strokeWidth: 2.34,
            dash: "14.0184,-5.6286");
        var context = new GeometricShape(
            "context",
            "rect",
            [
                new PointD(1000, 2400),
                new PointD(2400, 2400),
                new PointD(2400, 2410),
                new PointD(1000, 2410),
                new PointD(1000, 2400)
            ],
            new BoundsD(1000, 2400, 2400, 2410),
            IsClosed: true,
            HasFill: true,
            HasStroke: false);

        var result = new BracketSpannerExtractor().Extract(
            Scene(horizontal, hook, context));

        var bracket = Assert.Single(result.Brackets);
        Assert.True(bracket.IsDashed);
        Assert.Equal(BracketHookDirection.Up, bracket.RightHookDirection);
        Assert.Equal(2, bracket.SourceShapeIds.Count);
        Assert.Contains("ottava-horizontal", bracket.SourceShapeIds);
        Assert.Contains("ottava-hook", bracket.SourceShapeIds);
    }

    [Fact]
    public void FullWidthStaffLineTouchingBarline_IsNotBracket()
    {
        var staff = Shape(
            "staff-line",
            [new PointD(100, 200), new PointD(1100, 200)],
            strokeWidth: 2.0);
        var barline = Shape(
            "barline",
            [new PointD(1100, 160), new PointD(1100, 240)],
            strokeWidth: 2.0);

        var result = new BracketSpannerExtractor().Extract(
            Scene(staff, barline));

        Assert.Empty(result.Brackets);
    }

    private static GeometricScene Scene(params GeometricShape[] shapes) =>
        new(shapes);

    private static GeometricShape Shape(
        string id,
        IReadOnlyList<PointD> points,
        double strokeWidth,
        string? dash = null) =>
        new(
            id,
            "polyline",
            points,
            BoundsD.FromPoints(points),
            null,
            null,
            false,
            strokeWidth,
            [new GeometricContour(points, false)],
            false,
            true,
            null,
            dash);
}
