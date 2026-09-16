using SvgMusic.Scene;
using SvgMusic.Semantics;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class PedalPassGuardTests
{
    [Fact]
    public void SolidBracketAboveStaff_IsNotPedalEvenWithPedalLabel()
    {
        var ownership = new LogicalOwnership(
            new LogicalCoordinate("upper", "m1"),
            new LogicalCoordinate("upper", "m1"),
            4,
            null,
            0,
            "OuterBandAbove");
        var labelBounds = new BoundsD(10, 70, 20, 90);
        var labelSource = new ShapeInstance(
            "pedal-label",
            "pedal-prototype",
            labelBounds.MinX,
            labelBounds.MinY,
            labelBounds.Width,
            labelBounds.Height,
            "path",
            null,
            new SymbolClassification("PEDAL_MARK", 0.94, 30, []),
            null,
            ownership);
        var bracketSource = new BracketSpannerPrimitive(
            "solid-above",
            new PointD(26, 80),
            new PointD(90, 80),
            null,
            new PointD(90, 70),
            BracketHookDirection.None,
            BracketHookDirection.Up,
            false,
            1,
            0.95,
            ["solid-line"],
            ownership);
        var elements = new SemanticElement[]
        {
            new ShapeElement
            {
                ShapeId = labelSource.ShapeId,
                Bounds = labelBounds,
                Ownership = ownership,
                Source = labelSource
            },
            new BracketSpannerElement
            {
                ShapeId = bracketSource.Id,
                Bounds = new BoundsD(25.5, 69.5, 90.5, 80.5),
                Ownership = ownership,
                Source = bracketSource
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
                new StaffMeasureScene(
                    1,
                    "upper",
                    new BoundsD(0, 100, 100, 140),
                    10,
                    elements),
                new StaffMeasureScene(
                    2,
                    "lower",
                    new BoundsD(0, 200, 100, 240),
                    10,
                    []))
        ]);
        var facts = new SemanticFacts();

        var pass = new PedalPass();
        pass.Run(document, facts);

        Assert.Empty(facts.OfType<PedalFact>());
        Assert.Contains(
            pass.LastAnalysis!.Decisions,
            decision => decision.Decision == "not-below-staff");
    }
}
