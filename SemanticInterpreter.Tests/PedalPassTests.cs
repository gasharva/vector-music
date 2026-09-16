using SvgMusic.Canonical;
using SvgMusic.Scene;
using SvgMusic.Semantics;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class PedalPassTests
{
    [Fact]
    public void ClassifiedPedalMarkBelowStaff_WithSolidBracket_BecomesPedalSpan()
    {
        var ownership = Ownership("m1", "m1");
        var document = Document([
            Label(ownership),
            Bracket(ownership, endX: 98, dashed: false)
        ]);
        var facts = TimingFacts(1, "note-1", 5, "3/4");

        var pass = new PedalPass();
        pass.Run(document, facts);

        var pedal = Assert.Single(facts.OfType<PedalFact>());
        Assert.Equal(1, pedal.StartMeasureNumber);
        Assert.Equal(1, pedal.EndMeasureNumber);
        Assert.Equal(2, pedal.Staff);
        Assert.Equal("below", pedal.Placement);
        Assert.Equal("0", pedal.StartAt);
        Assert.Equal("3/4", pedal.EndAt);
        Assert.True(pedal.Line);
        Assert.True(pedal.StartMark);
        Assert.Contains("label-PEDAL_MARK", pedal.SourceShapeIds);
        Assert.Contains("pedal-line", pedal.SourceShapeIds);
    }

    [Fact]
    public void DashedBracket_DoesNotBecomePedal()
    {
        var ownership = Ownership("m1", "m1");
        var document = Document([
            Label(ownership),
            Bracket(ownership, endX: 98, dashed: true)
        ]);
        var facts = TimingFacts(1, "note-1", 5, "3/4");

        var pass = new PedalPass();
        pass.Run(document, facts);

        Assert.Empty(facts.OfType<PedalFact>());
        Assert.Contains(
            pass.LastAnalysis!.Decisions,
            decision => decision.Decision == "dashed-bracket");
    }

    [Fact]
    public void PedalBracketAcrossMeasures_UsesGeometryForFullSpan()
    {
        var ownership = Ownership("m1", "m1");
        var elements = new SemanticElement[]
        {
            Label(ownership),
            Bracket(ownership, endX: 198, dashed: false)
        };
        var document = TwoMeasureDocument(elements);
        var facts = new SemanticFacts();
        AddTiming(facts, 1, "note-1", 5, "0", "3/4");
        AddTiming(facts, 2, "note-2", 105, "0", "3/4");

        new PedalPass().Run(document, facts);

        var pedal = Assert.Single(facts.OfType<PedalFact>());
        Assert.Equal(1, pedal.StartMeasureNumber);
        Assert.Equal(2, pedal.EndMeasureNumber);
        Assert.Equal("0", pedal.StartAt);
        Assert.Equal("3/4", pedal.EndAt);
    }

    [Fact]
    public void PedalFact_ProjectsToCanonicalRelation_AndMusicXmlDirections()
    {
        var document = TwoMeasureDocument([]);
        var facts = new SemanticFacts();
        facts.Add(new PedalFact(
            "bracket-pedal",
            "label-pedal",
            1,
            2,
            2,
            "below",
            "0",
            "3/4",
            25,
            195,
            true,
            true,
            0.96,
            "test pedal",
            ["label-pedal", "pedal-line"]));

        var canonical = new CanonicalNotationBuilder().Build(
            document,
            facts,
            "Test",
            null);

        var span = Assert.Single(canonical.Relations.Pedals);
        Assert.Equal("pedal", span.Kind);
        Assert.Equal(new TimeAnchor(1, "0", 2), span.From);
        Assert.Equal(new TimeAnchor(2, "3/4", 2), span.To);
        Assert.True(span.Line);
        Assert.True(span.StartMark);
        Assert.Equal("below", span.Placement);

        var xml = new MusicXmlWriter().Write(canonical);
        var pedals = xml.Descendants("pedal").ToArray();

        Assert.Equal(2, pedals.Length);
        Assert.Equal("resume", (string?)pedals[0].Attribute("type"));
        Assert.Equal("yes", (string?)pedals[0].Attribute("line"));
        Assert.Equal("yes", (string?)pedals[0].Attribute("sign"));
        Assert.Equal("stop", (string?)pedals[1].Attribute("type"));
        Assert.Equal("yes", (string?)pedals[1].Attribute("line"));
        Assert.Equal("no", (string?)pedals[1].Attribute("sign"));
        Assert.All(
            xml.Descendants("direction")
                .Where(direction => direction.Descendants("pedal").Any()),
            direction => Assert.Equal("2", direction.Element("staff")?.Value));
    }

    private static LogicalOwnership Ownership(string startMeasure, string endMeasure) =>
        new(
            new LogicalCoordinate("lower", startMeasure),
            new LogicalCoordinate("lower", endMeasure),
            4,
            null,
            0,
            "OuterBandBelow");

    private static ShapeElement Label(LogicalOwnership ownership)
    {
        var bounds = new BoundsD(10, 250, 20, 270);
        var source = new ShapeInstance(
            "label-PEDAL_MARK",
            "prototype-pedal",
            bounds.MinX,
            bounds.MinY,
            bounds.Width,
            bounds.Height,
            "path",
            null,
            new SymbolClassification("PEDAL_MARK", 0.94, 30, []),
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
        double endX,
        bool dashed)
    {
        const double y = 264;
        var source = new BracketSpannerPrimitive(
            "bracket-pedal",
            new PointD(26, y),
            new PointD(endX, y),
            null,
            new PointD(endX, y - 10),
            BracketHookDirection.None,
            BracketHookDirection.Up,
            dashed,
            1,
            0.95,
            ["pedal-line", "pedal-hook"],
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
        IReadOnlyList<SemanticElement> lowerElements) =>
        new([
            Measure(1, "m1", 0, 100, lowerElements)
        ]);

    private static SemanticDocument TwoMeasureDocument(
        IReadOnlyList<SemanticElement> lowerElements) =>
        new([
            Measure(1, "m1", 0, 100, lowerElements),
            Measure(2, "m2", 100, 200, lowerElements)
        ]);

    private static MeasureScene Measure(
        int number,
        string id,
        double xStart,
        double xEnd,
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
                []),
            new StaffMeasureScene(
                2,
                "lower",
                new BoundsD(0, 200, 200, 240),
                10,
                lowerElements));

    private static SemanticFacts TimingFacts(
        int measure,
        string noteheadId,
        double x,
        string duration)
    {
        var facts = new SemanticFacts();
        AddTiming(facts, measure, noteheadId, x, "0", duration);
        return facts;
    }

    private static void AddTiming(
        SemanticFacts facts,
        int measure,
        string noteheadId,
        double x,
        string at,
        string duration)
    {
        facts.Add(new DurationFact(
            measure,
            2,
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
            2,
            VoiceTargetKind.Notehead,
            noteheadId,
            5,
            at,
            x,
            0.99,
            "test onset",
            [noteheadId]));
    }
}
