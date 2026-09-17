using SvgMusic.Scene;
using SvgMusic.Semantics;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class ClassifiedSymbolCrossStaffDynamicTests
{
    [Fact]
    public void InterstaffDynamic_UsesHorizontallyAlignedOnsetAcrossBothStaves()
    {
        // The dynamic is geometrically closer to the upper staff, but the only
        // rhythmically aligned onset is on the lower staff. Inter-staff directions
        // must resolve time horizontally before using vertical proximity as a tie-break.
        var symbol = Symbol(
            "shape-mp.whole",
            "DYNAMICS_MP",
            0.986,
            x: 150,
            y: 170);
        var document = Document([symbol]);
        var facts = new SemanticFacts();

        AddOnset(facts, "upper-far", x: 20, at: "0", staff: 1);
        AddOnset(facts, "lower-near", x: 151, at: "1/2", staff: 2);

        var pass = new ClassifiedSymbolPass();
        pass.Run(document, facts);

        var dynamic = Assert.Single(facts.OfType<DynamicDirectionFact>());
        Assert.Equal("mp", dynamic.Value);
        Assert.Equal(2, dynamic.Staff);
        Assert.Equal("1/2", dynamic.At);
        Assert.Equal("DYNAMICS_MP", dynamic.ClassificationLabel);
        Assert.Single(pass.LastAnalysis!.Accepted);
    }

    private static SemanticDocument Document(
        IReadOnlyList<SemanticElement> upperElements) =>
        new([
            new MeasureScene(
                1,
                "system-1",
                "pair-1",
                "m1",
                0,
                200,
                false,
                new StaffMeasureScene(
                    1,
                    "upper",
                    new BoundsD(0, 100, 200, 140),
                    10,
                    upperElements),
                new StaffMeasureScene(
                    2,
                    "lower",
                    new BoundsD(0, 220, 200, 260),
                    10,
                    []))
        ]);

    private static ShapeElement Symbol(
        string id,
        string label,
        double confidence,
        double x,
        double y)
    {
        var ownership = new LogicalOwnership(
            new LogicalCoordinate("upper", "m1"),
            new LogicalCoordinate("upper", "m1"),
            1,
            null,
            0,
            "test");
        var classification = new SymbolClassification(
            label,
            confidence,
            30,
            Array.Empty<SymbolScaleResult>());
        var source = new ShapeInstance(
            id,
            "prototype",
            x - 5,
            y - 5,
            10,
            10,
            "path",
            null,
            classification,
            null,
            ownership);

        return new ShapeElement
        {
            ShapeId = id,
            Bounds = new BoundsD(x - 5, y - 5, x + 5, y + 5),
            Ownership = ownership,
            Source = source
        };
    }

    private static void AddOnset(
        SemanticFacts facts,
        string id,
        double x,
        string at,
        int staff)
    {
        facts.Add(new OnsetFact(
            1,
            staff,
            VoiceTargetKind.Notehead,
            id,
            1,
            at,
            x,
            0.99,
            "test onset",
            [id]));
    }
}
