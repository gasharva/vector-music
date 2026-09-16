using SvgMusic.Scene;
using SvgMusic.Semantics;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class OttavaSpanResolutionTests
{
    [Fact]
    public void BracketGeometry_CanRecoverLaterEndMeasure_WhenOwnershipIsCollapsed()
    {
        var ownership = new LogicalOwnership(
            new LogicalCoordinate("upper", "m1"),
            new LogicalCoordinate("upper", "m1"),
            4,
            null,
            0,
            "OuterBandAbove");
        var elements = new SemanticElement[]
        {
            Label(ownership),
            Bracket(ownership, endX: 198)
        };
        var document = TwoMeasures(elements);
        var facts = new SemanticFacts();
        AddNote(facts, 1, "m1-note", x: 5, at: "0", duration: "3/4");
        AddNote(facts, 2, "m2-note", x: 105, at: "0", duration: "3/4");

        new OttavaPass().Run(document, facts);

        var ottava = Assert.Single(facts.OfType<OttavaFact>());
        Assert.Equal(1, ottava.StartMeasureNumber);
        Assert.Equal(2, ottava.EndMeasureNumber);
        Assert.Equal("3/4", ottava.EndAt);
    }

    [Fact]
    public void BracketEnd_UsesEndOfLastAffectedEvent_NotNearestOnset()
    {
        var ownership = new LogicalOwnership(
            new LogicalCoordinate("upper", "m1"),
            new LogicalCoordinate("upper", "m1"),
            4,
            null,
            0,
            "OuterBandAbove");
        var elements = new SemanticElement[]
        {
            Label(ownership),
            Bracket(ownership, endX: 80)
        };
        var document = OneMeasure(elements);
        var facts = new SemanticFacts();
        AddNote(facts, 1, "note-1", x: 5, at: "0", duration: "1/4");
        AddNote(facts, 1, "note-2", x: 70, at: "1/4", duration: "1/2");

        new OttavaPass().Run(document, facts);

        var ottava = Assert.Single(facts.OfType<OttavaFact>());
        Assert.Equal("3/4", ottava.EndAt);
    }

    private static SemanticDocument OneMeasure(IReadOnlyList<SemanticElement> elements) =>
        new([Measure(1, "m1", 0, 100, elements)]);

    private static SemanticDocument TwoMeasures(IReadOnlyList<SemanticElement> elements) =>
        new([
            Measure(1, "m1", 0, 100, elements),
            Measure(2, "m2", 100, 200, [])
        ]);

    private static MeasureScene Measure(
        int number,
        string id,
        double xStart,
        double xEnd,
        IReadOnlyList<SemanticElement> upperElements) =>
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
                []));

    private static ShapeElement Label(LogicalOwnership ownership)
    {
        var bounds = new BoundsD(10, 70, 20, 90);
        var source = new ShapeInstance(
            "label-OTTAVA",
            "prototype-ottava",
            bounds.MinX,
            bounds.MinY,
            bounds.Width,
            bounds.Height,
            "path",
            null,
            new SymbolClassification("OTTAVA", 0.95, 30, []),
            null,
            ownership);

        return new ShapeElement
        {
            ShapeId = source.ShapeId,
            Bounds = bounds,
            Ownership = ownership,
            Source = source
        };
    }

    private static BracketSpannerElement Bracket(
        LogicalOwnership ownership,
        double endX)
    {
        const double y = 80;
        var source = new BracketSpannerPrimitive(
            "bracket-ottava",
            new PointD(26, y),
            new PointD(endX, y),
            null,
            new PointD(endX, y - 10),
            BracketHookDirection.None,
            BracketHookDirection.Up,
            true,
            1,
            0.95,
            ["line-source", "hook-source"],
            ownership);

        return new BracketSpannerElement
        {
            ShapeId = source.Id,
            Bounds = new BoundsD(25.5, y - 10.5, endX + 0.5, y + 0.5),
            Ownership = ownership,
            Source = source
        };
    }

    private static void AddNote(
        SemanticFacts facts,
        int measure,
        string noteheadId,
        double x,
        string at,
        string duration)
    {
        facts.Add(new DurationFact(
            measure,
            1,
            noteheadId,
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
            [noteheadId]));
        facts.Add(new OnsetFact(
            measure,
            1,
            VoiceTargetKind.Notehead,
            noteheadId,
            1,
            at,
            x,
            0.99,
            "test onset",
            [noteheadId]));
    }
}
