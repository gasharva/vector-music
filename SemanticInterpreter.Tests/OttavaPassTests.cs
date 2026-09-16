using SvgMusic.Canonical;
using SvgMusic.Scene;
using SvgMusic.Semantics;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class OttavaPassTests
{
    [Fact]
    public void ClassifiedOttavaAboveStaff_WithDashedBracket_BecomesDownOctaveShift()
    {
        var document = Document(
            LabelAndBracket(
                staffId: "upper",
                measureId: "m1",
                staffTop: 100,
                staffBottom: 140,
                labelBounds: new BoundsD(10, 70, 20, 90),
                bracketY: 80,
                endX: 98,
                endMeasureId: "m1"));
        var facts = TimingFacts(1, 1, "note-1", 5, "3/4");

        var pass = new OttavaPass();
        pass.Run(document, facts);

        var ottava = Assert.Single(facts.OfType<OttavaFact>());
        Assert.Equal(1, ottava.StartMeasureNumber);
        Assert.Equal(1, ottava.EndMeasureNumber);
        Assert.Equal(1, ottava.Staff);
        Assert.Equal("down", ottava.Direction);
        Assert.Equal("above", ottava.Placement);
        Assert.Equal(8, ottava.Size);
        Assert.Equal("0", ottava.StartAt);
        Assert.Equal("3/4", ottava.EndAt);
        Assert.Contains("label-OTTAVA", ottava.SourceShapeIds);
        Assert.Contains("line-source", ottava.SourceShapeIds);
    }

    [Fact]
    public void ClassifiedOttavaBelowStaff_WithDashedBracket_BecomesUpOctaveShift()
    {
        var ownership = new LogicalOwnership(
            new LogicalCoordinate("lower", "m1"),
            new LogicalCoordinate("lower", "m1"),
            4,
            null,
            0,
            "OuterBandBelow");
        var label = Label(
            ownership,
            new BoundsD(10, 250, 20, 270));
        var bracket = Bracket(
            ownership,
            y: 260,
            endX: 98);
        var document = Document([label, bracket]);
        var facts = TimingFacts(1, 2, "note-2", 5, "3/4");

        new OttavaPass().Run(document, facts);

        var ottava = Assert.Single(facts.OfType<OttavaFact>());
        Assert.Equal(2, ottava.Staff);
        Assert.Equal("up", ottava.Direction);
        Assert.Equal("below", ottava.Placement);
    }

    [Fact]
    public void BracketOwnershipSpan_PreservesStartAndEndMeasures()
    {
        var ownership = new LogicalOwnership(
            new LogicalCoordinate("upper", "m1"),
            new LogicalCoordinate("upper", "m2"),
            4,
            null,
            0,
            "OuterBandSpan");
        var elements = new SemanticElement[]
        {
            Label(ownership, new BoundsD(10, 70, 20, 90)),
            Bracket(ownership, 80, 198)
        };
        var document = TwoMeasureDocument(elements);
        var facts = new SemanticFacts();
        AddTiming(facts, 1, 1, "note-1", 5, "3/4");
        AddTiming(facts, 2, 1, "note-2", 105, "3/4");

        new OttavaPass().Run(document, facts);

        var ottava = Assert.Single(facts.OfType<OttavaFact>());
        Assert.Equal(1, ottava.StartMeasureNumber);
        Assert.Equal(2, ottava.EndMeasureNumber);
        Assert.Equal("0", ottava.StartAt);
        Assert.Equal("3/4", ottava.EndAt);
    }

    [Fact]
    public void OttavaFact_ProjectsToCanonicalRelation_AndMusicXmlDirections()
    {
        var document = TwoMeasureDocument([]);
        var facts = new SemanticFacts();
        facts.Add(new OttavaFact(
            "bracket-1",
            "label-1",
            1,
            2,
            1,
            "down",
            8,
            "above",
            "0",
            "3/4",
            25,
            195,
            0.95,
            "test ottava",
            ["label-1", "line-1"]));

        var canonical = new CanonicalNotationBuilder().Build(
            document,
            facts,
            "Test",
            null);

        var span = Assert.Single(canonical.Relations.OctaveShifts);
        Assert.Equal("octaveShift", span.Kind);
        Assert.Equal(new TimeAnchor(1, "0", 1), span.From);
        Assert.Equal(new TimeAnchor(2, "3/4", 1), span.To);
        Assert.Equal("down", span.Direction);
        Assert.Equal(8, span.Size);
        Assert.Equal("above", span.Placement);

        var xml = new MusicXmlWriter().Write(canonical);
        var shifts = xml.Descendants("octave-shift").ToArray();

        Assert.Equal(2, shifts.Length);
        Assert.Equal("down", (string?)shifts[0].Attribute("type"));
        Assert.Equal("8", (string?)shifts[0].Attribute("size"));
        Assert.Equal("stop", (string?)shifts[1].Attribute("type"));
        Assert.Equal("8", (string?)shifts[1].Attribute("size"));
    }

    private static SemanticFacts TimingFacts(
        int measure,
        int staff,
        string noteheadId,
        double x,
        string duration)
    {
        var facts = new SemanticFacts();
        AddTiming(facts, measure, staff, noteheadId, x, duration);
        return facts;
    }

    private static void AddTiming(
        SemanticFacts facts,
        int measure,
        int staff,
        string noteheadId,
        double x,
        string duration)
    {
        facts.Add(new DurationFact(
            measure,
            staff,
            noteheadId,
            null,
            duration,
            duration,
            "half",
            0,
            0,
            null,
            null,
            0.99,
            "test duration",
            [noteheadId]));
        facts.Add(new OnsetFact(
            measure,
            staff,
            VoiceTargetKind.Notehead,
            noteheadId,
            1,
            "0",
            x,
            0.99,
            "test onset",
            [noteheadId]));
    }

    private static IReadOnlyList<SemanticElement> LabelAndBracket(
        string staffId,
        string measureId,
        double staffTop,
        double staffBottom,
        BoundsD labelBounds,
        double bracketY,
        double endX,
        string endMeasureId)
    {
        var ownership = new LogicalOwnership(
            new LogicalCoordinate(staffId, measureId),
            new LogicalCoordinate(staffId, endMeasureId),
            4,
            null,
            0,
            "OuterBand");
        return
        [
            Label(ownership, labelBounds),
            Bracket(ownership, bracketY, endX)
        ];
    }

    private static ShapeElement Label(
        LogicalOwnership ownership,
        BoundsD bounds)
    {
        var classification = new SymbolClassification(
            "OTTAVA",
            0.95,
            30,
            []);
        var source = new ShapeInstance(
            "label-OTTAVA",
            "prototype-ottava",
            bounds.MinX,
            bounds.MinY,
            bounds.Width,
            bounds.Height,
            "path",
            null,
            classification,
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
        double y,
        double endX)
    {
        var source = new BracketSpannerPrimitive(
            "bracket-1",
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

    private static SemanticDocument Document(
        IReadOnlyList<SemanticElement> upperElements)
    {
        return new SemanticDocument(
        [
            Measure(
                1,
                "m1",
                0,
                100,
                upperElements,
                [])
        ]);
    }

    private static SemanticDocument TwoMeasureDocument(
        IReadOnlyList<SemanticElement> upperElements)
    {
        return new SemanticDocument(
        [
            Measure(1, "m1", 0, 100, upperElements, []),
            Measure(2, "m2", 100, 200, upperElements, [])
        ]);
    }

    private static MeasureScene Measure(
        int number,
        string id,
        double xStart,
        double xEnd,
        IReadOnlyList<SemanticElement> upperElements,
        IReadOnlyList<SemanticElement> lowerElements)
    {
        return new MeasureScene(
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
    }
}
