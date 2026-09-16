using SvgMusic.Scene;
using SvgMusic.Semantics;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class ClassifiedDynamicStaffResolutionTests
{
    [Fact]
    public void InterStaffDynamic_UsesNearestStaffInsteadOfGenericOwnership()
    {
        var symbol = DynamicSymbol(
            "pp-between",
            x: 50,
            y: 160,
            ownerStaffId: "lower");
        var document = Document(lowerElements: [symbol]);
        var facts = new SemanticFacts();
        AddOnset(facts, staff: 1, "upper", x: 50, at: "0");
        AddOnset(facts, staff: 2, "lower", x: 50, at: "1/2");

        new ClassifiedSymbolPass().Run(document, facts);

        var dynamic = Assert.Single(facts.OfType<DynamicDirectionFact>());
        Assert.Equal(1, dynamic.Staff);
        Assert.Equal("0", dynamic.At);
        Assert.Equal("below", dynamic.Placement);
    }

    [Fact]
    public void DynamicBelowLowerStaff_RemainsOnLowerStaff()
    {
        var symbol = DynamicSymbol(
            "pp-lower",
            x: 50,
            y: 270,
            ownerStaffId: "lower");
        var document = Document(lowerElements: [symbol]);
        var facts = new SemanticFacts();
        AddOnset(facts, staff: 1, "upper", x: 50, at: "0");
        AddOnset(facts, staff: 2, "lower", x: 50, at: "1/2");

        new ClassifiedSymbolPass().Run(document, facts);

        var dynamic = Assert.Single(facts.OfType<DynamicDirectionFact>());
        Assert.Equal(2, dynamic.Staff);
        Assert.Equal("1/2", dynamic.At);
        Assert.Equal("below", dynamic.Placement);
    }

    private static SemanticDocument Document(
        IReadOnlyList<SemanticElement>? upperElements = null,
        IReadOnlyList<SemanticElement>? lowerElements = null) =>
        new([
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
                    upperElements ?? []),
                new StaffMeasureScene(
                    2,
                    "lower",
                    new BoundsD(0, 200, 100, 240),
                    10,
                    lowerElements ?? []))
        ]);

    private static ShapeElement DynamicSymbol(
        string id,
        double x,
        double y,
        string ownerStaffId)
    {
        var ownership = new LogicalOwnership(
            new LogicalCoordinate(ownerStaffId, "m1"),
            new LogicalCoordinate(ownerStaffId, "m1"),
            1,
            null,
            0,
            "test ownership");
        var classification = new SymbolClassification(
            "DYNAMICS_PP",
            0.96,
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
        int staff,
        string id,
        double x,
        string at)
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
