using SvgMusic.Scene;
using SvgMusic.Semantics;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class VerticalZigZagVisitorTests
{
    [Fact]
    public void Visit_DispatchesVerticalZigZagWithoutBreakingOtherPasses()
    {
        var ownership = new LogicalOwnership(
            new LogicalCoordinate("upper", "measure-1"),
            new LogicalCoordinate("upper", "measure-1"),
            1,
            null,
            0,
            "test");
        var primitive = new VerticalZigZagPrimitive(
            "zigzag",
            new BoundsD(20, 105, 24, 135),
            8,
            3,
            8,
            0.98,
            "test",
            null,
            ownership);
        var element = new VerticalZigZagElement
        {
            ShapeId = primitive.ShapeId,
            Bounds = primitive.Bounds,
            Ownership = ownership,
            Source = primitive
        };
        var document = new SemanticDocument([
            new MeasureScene(
                1,
                "system-1",
                "pair-1",
                "measure-1",
                0,
                120,
                false,
                new StaffMeasureScene(
                    1,
                    "upper",
                    new BoundsD(0, 100, 120, 140),
                    10,
                    [element]),
                new StaffMeasureScene(
                    2,
                    "lower",
                    new BoundsD(0, 210, 120, 250),
                    10,
                    []))
        ]);

        var visitor = new CountingVisitor();
        visitor.Visit(document);

        Assert.Equal(1, visitor.VerticalZigZags);
    }

    private sealed class CountingVisitor : SemanticVisitor
    {
        public int VerticalZigZags { get; private set; }

        protected override void VisitVerticalZigZag(
            MeasureScene measure,
            StaffMeasureScene staff,
            VerticalZigZagElement zigZag)
        {
            VerticalZigZags++;
        }
    }
}
