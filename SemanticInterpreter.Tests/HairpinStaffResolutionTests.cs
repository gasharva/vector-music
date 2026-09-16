using SvgMusic.Scene;
using SvgMusic.Semantics;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class HairpinStaffResolutionTests
{
    [Fact]
    public void InterStaffHairpin_UsesNearestStaffInsteadOfGenericOwnership()
    {
        var wrongOwnership = new LogicalOwnership(
            new LogicalCoordinate("lower", "m1"),
            new LogicalCoordinate("lower", "m1"),
            1,
            null,
            0,
            "InterStaff");
        var source = new HairpinPrimitive(
            "interstaff-hairpin",
            HairpinKind.Crescendo,
            new PointD(20, 155),
            new PointD(170, 145),
            new PointD(170, 165),
            150,
            20,
            0.96,
            "polyline",
            null,
            wrongOwnership);
        var element = new HairpinElement
        {
            ShapeId = source.ShapeId,
            Bounds = BoundsD.FromPoints([source.Apex, source.OpenUpper, source.OpenLower]),
            Ownership = wrongOwnership,
            Source = source
        };
        var document = new SemanticDocument([
            Measure(1, "m1", 0, 100, [], [element]),
            Measure(2, "m2", 100, 200, [], [])
        ]);
        var facts = new SemanticFacts();

        // Upper staff: the intended hairpin ends at 1/2.
        AddNote(facts, 1, 1, "u-start", 20, "0", "1/4");
        AddNote(facts, 2, 1, "u-end", 150, "1/4", "1/4");

        // Lower staff deliberately resolves to a different end time. If the pass
        // blindly trusts generic ownership this test would produce 3/4 on staff 2.
        AddNote(facts, 1, 2, "l-start", 20, "0", "1/4");
        AddNote(facts, 2, 2, "l-end", 150, "1/2", "1/4");

        new HairpinPass().Run(document, facts);

        var hairpin = Assert.Single(facts.OfType<HairpinFact>());
        Assert.Equal(1, hairpin.Staff);
        Assert.Equal("below", hairpin.Placement);
        Assert.Equal("0", hairpin.StartAt);
        Assert.Equal("1/2", hairpin.EndAt);
    }

    private static MeasureScene Measure(
        int number,
        string id,
        double xStart,
        double xEnd,
        IReadOnlyList<SemanticElement> upperElements,
        IReadOnlyList<SemanticElement> lowerElements) =>
        new(
            number,
            "system-1",
            "pair-1",
            id,
            xStart,
            xEnd,
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
                new BoundsD(0, 200, 200, 240),
                10,
                lowerElements));

    private static void AddNote(
        SemanticFacts facts,
        int measure,
        int staff,
        string id,
        double x,
        string at,
        string duration)
    {
        facts.Add(new DurationFact(
            measure,
            staff,
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
            measure,
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
