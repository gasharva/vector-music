using SvgMusic.Scene;
using SvgMusic.Semantics;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class PedalPassConfidenceTests
{
    [Fact]
    public void LowConfidencePedalMark_DoesNotCreatePedalSpan()
    {
        var ownership = new LogicalOwnership(
            new LogicalCoordinate("lower", "m1"),
            new LogicalCoordinate("lower", "m1"),
            4,
            null,
            0,
            "OuterBandBelow");
        var bounds = new BoundsD(10, 250, 20, 270);
        var label = new ShapeInstance(
            "weak-pedal-mark",
            "pedal-prototype",
            bounds.MinX,
            bounds.MinY,
            bounds.Width,
            bounds.Height,
            "path",
            null,
            new SymbolClassification("PEDAL_MARK", 0.50, 30, []),
            null,
            ownership);
        var bracket = new BracketSpannerPrimitive(
            "pedal-line",
            new PointD(26, 264),
            new PointD(95, 264),
            null,
            new PointD(95, 254),
            BracketHookDirection.None,
            BracketHookDirection.Up,
            false,
            1,
            0.95,
            ["line-source"],
            ownership);
        var elements = new SemanticElement[]
        {
            new ShapeElement
            {
                ShapeId = label.ShapeId,
                Bounds = bounds,
                Ownership = ownership,
                Source = label
            },
            new BracketSpannerElement
            {
                ShapeId = bracket.Id,
                Bounds = new BoundsD(25.5, 253.5, 95.5, 264.5),
                Ownership = ownership,
                Source = bracket
            }
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
                new StaffMeasureScene(1, "upper", new BoundsD(0, 100, 100, 140), 10, []),
                new StaffMeasureScene(2, "lower", new BoundsD(0, 200, 100, 240), 10, elements))
        ]);
        var facts = new SemanticFacts();

        var pass = new PedalPass();
        pass.Run(document, facts);

        Assert.Empty(facts.OfType<PedalFact>());
        Assert.Empty(pass.LastAnalysis!.Accepted);
    }
}
