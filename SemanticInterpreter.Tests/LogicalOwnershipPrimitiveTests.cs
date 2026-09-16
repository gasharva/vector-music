using SvgMusic.Scene;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class LogicalOwnershipPrimitiveTests
{
    [Fact]
    public void VerticalZigZag_InsideStaff_IsOwnedByGenerationOne()
    {
        var zigZag = new VerticalZigZagPrimitive(
            "tile-0",
            new BoundsD(48, 102, 52, 138),
            8,
            2,
            4,
            0.95,
            "path",
            null)
        {
            SourceShapeIds = ["tile-0", "tile-1", "tile-2"]
        };
        var notation = EmptyNotation() with
        {
            VerticalZigZags = [zigZag]
        };

        var result = new LogicalOwnershipAnalyzer().AnalyzeAndApply(
            new GeometricScene([]),
            notation,
            Layout());

        var ownership = Assert.Single(result.Scene.VerticalZigZags).Ownership;
        Assert.NotNull(ownership);
        Assert.Equal(1, ownership.Generation);
        Assert.Equal(new LogicalCoordinate("staff-upper", "m1"), ownership.Start);
        Assert.Equal(ownership.Start, ownership.End);
        Assert.Contains(
            result.Ownership.Assignments,
            assignment =>
                assignment.ShapeId == "tile-2"
                && assignment.Kind == "VerticalZigZag"
                && assignment.Ownership == ownership);
    }

    [Fact]
    public void Hairpin_InsideStaff_IsOwnedByGenerationOne()
    {
        var hairpin = new HairpinPrimitive(
            "hairpin-source",
            HairpinKind.Crescendo,
            new PointD(20, 120),
            new PointD(80, 115),
            new PointD(80, 125),
            60,
            10,
            0.95,
            "path",
            null);
        var notation = EmptyNotation() with
        {
            Hairpins = [hairpin]
        };

        var result = new LogicalOwnershipAnalyzer().AnalyzeAndApply(
            new GeometricScene([]),
            notation,
            Layout());

        var ownership = Assert.Single(result.Scene.Hairpins).Ownership;
        Assert.NotNull(ownership);
        Assert.Equal(1, ownership.Generation);
        Assert.Equal(new LogicalCoordinate("staff-upper", "m1"), ownership.Start);
        Assert.Equal(ownership.Start, ownership.End);
    }

    [Fact]
    public void BracketSpanner_AcrossMeasureBoundary_PreservesLogicalSpan()
    {
        var bracket = new BracketSpannerPrimitive(
            "bracket-1",
            new PointD(80, 130),
            new PointD(130, 130),
            null,
            new PointD(130, 140),
            BracketHookDirection.None,
            BracketHookDirection.Down,
            false,
            1,
            0.95,
            ["span-source", "hook-source"]);
        var notation = EmptyNotation() with
        {
            BracketSpanners = [bracket]
        };

        var result = new LogicalOwnershipAnalyzer().AnalyzeAndApply(
            new GeometricScene([]),
            notation,
            Layout());

        var ownership = Assert.Single(result.Scene.BracketSpanners).Ownership;
        Assert.NotNull(ownership);
        Assert.Equal(1, ownership.Generation);
        Assert.Equal(new LogicalCoordinate("staff-upper", "m1"), ownership.Start);
        Assert.Equal(new LogicalCoordinate("staff-upper", "m2"), ownership.End);
        Assert.True(ownership.IsSpan);

        Assert.Contains(
            result.Ownership.Assignments,
            assignment =>
                assignment.ShapeId == "span-source"
                && assignment.Kind == "BracketSpanner"
                && assignment.Ownership == ownership);
        Assert.Contains(
            result.Ownership.Assignments,
            assignment =>
                assignment.ShapeId == "hook-source"
                && assignment.Kind == "BracketSpanner"
                && assignment.Ownership == ownership);
    }

    [Fact]
    public void BracketSpanner_InOuterBand_IsOwnedByGenerationFour()
    {
        var anchorBounds = new BoundsD(20, 85, 30, 105);
        var anchorPoints = new[]
        {
            new PointD(20, 85),
            new PointD(30, 105)
        };
        var geometry = new GeometricScene(
        [
            new GeometricShape(
                "anchor",
                "path",
                anchorPoints,
                anchorBounds)
        ]);
        var coordinate = new LogicalCoordinate("staff-upper", "m1");
        var existingOwnership = new LogicalOwnershipScene(
        [
            new LogicalOwnershipAssignment(
                "anchor",
                "ShapeInstance",
                new LogicalOwnership(
                    coordinate,
                    coordinate,
                    1,
                    null,
                    0,
                    "DirectIntersection"))
        ]);
        var anchor = new ShapeInstance(
            "anchor",
            "prototype-anchor",
            anchorBounds.CenterX,
            anchorBounds.CenterY,
            anchorBounds.Width,
            anchorBounds.Height,
            "path",
            null);
        var bracket = new BracketSpannerPrimitive(
            "bracket-outer",
            new PointD(40, 90),
            new PointD(80, 90),
            null,
            new PointD(80, 96),
            BracketHookDirection.None,
            BracketHookDirection.Down,
            false,
            1,
            0.95,
            ["outer-span-source"]);
        var notation = EmptyNotation() with
        {
            Instances = [anchor],
            BracketSpanners = [bracket]
        };

        var result = new FourthGenerationOuterBandAssigner().AssignAndApply(
            geometry,
            notation,
            Layout(),
            existingOwnership);

        var ownership = Assert.Single(result.Scene.BracketSpanners).Ownership;
        Assert.NotNull(ownership);
        Assert.Equal(4, ownership.Generation);
        Assert.Equal(coordinate, ownership.Start);
        Assert.Equal(coordinate, ownership.End);
        Assert.Contains(
            result.Ownership.Assignments,
            assignment =>
                assignment.ShapeId == "outer-span-source"
                && assignment.Kind == "BracketSpanner"
                && assignment.Ownership == ownership);
    }

    private static NotationScene EmptyNotation() =>
        new([], [], [], [], []);

    private static ScoreLayout Layout()
    {
        var left = new MeasureBoundary(0, 100, 240, []);
        var middle = new MeasureBoundary(100, 100, 240, []);
        var right = new MeasureBoundary(200, 100, 240, []);
        var measures = new[]
        {
            new MeasureLayout("m1", 0, 100, left, middle),
            new MeasureLayout("m2", 100, 200, middle, right)
        };
        var pair = new StaffPairLayout(
            "pair-1",
            "staff-upper",
            "staff-lower",
            new BoundsD(0, 100, 200, 240),
            measures,
            [left, middle, right]);
        var system = new ScoreSystem(
            "system-1",
            new BoundsD(0, 90, 200, 250),
            [pair]);
        var upper = new StaffLayout(
            "staff-upper",
            "system-1",
            new BoundsD(0, 100, 200, 140),
            StaffLines("u", 100),
            10,
            []);
        var lower = new StaffLayout(
            "staff-lower",
            "system-1",
            new BoundsD(0, 200, 200, 240),
            StaffLines("l", 200),
            10,
            []);

        return new ScoreLayout([system], [upper, lower]);
    }

    private static IReadOnlyList<StaffLineLayout> StaffLines(
        string prefix,
        double top) =>
        Enumerable.Range(0, 5)
            .Select(index => new StaffLineLayout(
                index,
                top + index * 10,
                0,
                200,
                $"{prefix}-{index}"))
            .ToArray();
}
