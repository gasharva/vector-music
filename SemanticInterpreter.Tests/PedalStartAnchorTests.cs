using SvgMusic.Scene;
using SvgMusic.Semantics;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class PedalStartAnchorTests
{
    [Fact]
    public void PedalStart_IsAnchoredByPedalMark_NotContinuationLineStart()
    {
        var ownership = new LogicalOwnership(
            new LogicalCoordinate("lower", "m1"),
            new LogicalCoordinate("lower", "m1"),
            4,
            null,
            0,
            "OuterBandBelow");
        var labelBounds = new BoundsD(10, 250, 20, 270);
        var labelSource = new ShapeInstance(
            "pedal-mark",
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
            "pedal-line",
            new PointD(55, 264),
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
                ShapeId = labelSource.ShapeId,
                Bounds = labelBounds,
                Ownership = ownership,
                Source = labelSource
            },
            new BracketSpannerElement
            {
                ShapeId = bracketSource.Id,
                Bounds = new BoundsD(54.5, 253.5, 95.5, 264.5),
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
                    []),
                new StaffMeasureScene(
                    2,
                    "lower",
                    new BoundsD(0, 200, 100, 240),
                    10,
                    elements))
        ]);
        var facts = new SemanticFacts();
        AddNote(facts, "first", x: 15, at: "0", duration: "1/8");
        AddNote(facts, "second", x: 50, at: "1/8", duration: "5/8");

        new PedalPass().Run(document, facts);

        var pedal = Assert.Single(facts.OfType<PedalFact>());
        Assert.Equal("0", pedal.StartAt);
        Assert.Equal("3/4", pedal.EndAt);
    }

    private static void AddNote(
        SemanticFacts facts,
        string id,
        double x,
        string at,
        string duration)
    {
        facts.Add(new DurationFact(
            1,
            2,
            id,
            null,
            duration,
            duration,
            "quarter",
            0,
            0,
            null,
            null,
            0.99,
            "test duration",
            [id]));
        facts.Add(new OnsetFact(
            1,
            2,
            VoiceTargetKind.Notehead,
            id,
            5,
            at,
            x,
            0.99,
            "test onset",
            [id]));
    }
}
