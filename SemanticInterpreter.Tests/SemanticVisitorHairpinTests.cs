using SvgMusic.Scene;
using SvgMusic.Semantics;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class SemanticVisitorHairpinTests
{
    [Fact]
    public void HairpinElement_IsDispatchedToHairpinHook()
    {
        var ownership = new LogicalOwnership(
            new LogicalCoordinate("upper", "m1"),
            new LogicalCoordinate("upper", "m1"),
            1,
            null,
            0,
            "test");
        var source = new HairpinPrimitive(
            "hp-1",
            HairpinKind.Crescendo,
            new PointD(10, 80),
            new PointD(90, 70),
            new PointD(90, 90),
            80,
            20,
            0.95,
            "polyline",
            null,
            ownership);
        var element = new HairpinElement
        {
            ShapeId = source.ShapeId,
            Bounds = BoundsD.FromPoints([source.Apex, source.OpenUpper, source.OpenLower]),
            Ownership = ownership,
            Source = source
        };
        var document = new SemanticDocument([
            new MeasureScene(
                1,
                "system-1",
                "pair-1",
                "m1",
                0,
                100,
                false,
                new StaffMeasureScene(
                    1,
                    "upper",
                    new BoundsD(0, 100, 100, 140),
                    10,
                    [element]),
                new StaffMeasureScene(
                    2,
                    "lower",
                    new BoundsD(0, 200, 100, 240),
                    10,
                    []))
        ]);
        var visitor = new CountingVisitor();

        visitor.Visit(document);

        Assert.Equal(1, visitor.Hairpins);
    }

    private sealed class CountingVisitor : SemanticVisitor
    {
        public int Hairpins { get; private set; }

        protected override void VisitHairpin(
            MeasureScene measure,
            StaffMeasureScene staff,
            HairpinElement hairpin)
        {
            Hairpins++;
        }
    }
}
