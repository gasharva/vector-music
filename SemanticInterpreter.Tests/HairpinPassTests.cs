using SvgMusic.Scene;
using SvgMusic.Semantics;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class HairpinPassTests
{
    [Fact]
    public void CrossMeasureCrescendo_UsesGeometryForTimedSpan()
    {
        var ownership = Ownership("lower", "m1");
        var hairpin = Hairpin(
            "crescendo-1",
            HairpinKind.Crescendo,
            apex: new PointD(25, 260),
            openUpper: new PointD(170, 250),
            openLower: new PointD(170, 270),
            confidence: 0.96,
            ownership);
        var document = TwoMeasures(
            lowerElements: [Element(hairpin)],
            upperElements: []);
        var facts = new SemanticFacts();
        AddNote(facts, 1, 2, "start", 25, "1/8", "1/8");
        AddNote(facts, 2, 2, "end", 150, "1/2", "1/8");

        var pass = new HairpinPass();
        pass.Run(document, facts);

        var fact = Assert.Single(facts.OfType<HairpinFact>());
        Assert.Equal("crescendo", fact.Type);
        Assert.Equal(1, fact.StartMeasureNumber);
        Assert.Equal(2, fact.EndMeasureNumber);
        Assert.Equal(2, fact.Staff);
        Assert.Equal("below", fact.Placement);
        Assert.Equal("1/8", fact.StartAt);
        Assert.Equal("5/8", fact.EndAt);
        Assert.Single(pass.LastAnalysis!.Accepted);
    }

    [Fact]
    public void Diminuendo_PreservesGeometricKindAndPlacement()
    {
        var ownership = Ownership("upper", "m1");
        var hairpin = Hairpin(
            "diminuendo-1",
            HairpinKind.Diminuendo,
            apex: new PointD(90, 80),
            openUpper: new PointD(10, 70),
            openLower: new PointD(10, 90),
            confidence: 0.93,
            ownership);
        var document = OneMeasure(
            upperElements: [Element(hairpin)],
            lowerElements: []);
        var facts = new SemanticFacts();
        AddNote(facts, 1, 1, "start", 10, "0", "1/4");
        AddNote(facts, 1, 1, "end", 70, "1/4", "1/2");

        new HairpinPass().Run(document, facts);

        var fact = Assert.Single(facts.OfType<HairpinFact>());
        Assert.Equal("diminuendo", fact.Type);
        Assert.Equal("above", fact.Placement);
        Assert.Equal("0", fact.StartAt);
        Assert.Equal("3/4", fact.EndAt);
    }

    [Fact]
    public void LowConfidenceGeometricCandidate_IsRejected()
    {
        var ownership = Ownership("lower", "m1");
        var hairpin = Hairpin(
            "weak-hairpin",
            HairpinKind.Crescendo,
            apex: new PointD(20, 260),
            openUpper: new PointD(80, 250),
            openLower: new PointD(80, 270),
            confidence: 0.70,
            ownership);
        var document = OneMeasure(
            upperElements: [],
            lowerElements: [Element(hairpin)]);
        var facts = new SemanticFacts();
        var pass = new HairpinPass();

        pass.Run(document, facts);

        Assert.Empty(facts.OfType<HairpinFact>());
        var decision = Assert.Single(pass.LastAnalysis!.Decisions);
        Assert.False(decision.Accepted);
        Assert.Equal("low-confidence", decision.Decision);
    }

    [Fact]
    public void CanonicalBuilder_ProjectsHairpinFactIntoSpanRelation()
    {
        var document = TwoMeasures([], []);
        var facts = new SemanticFacts();
        facts.Add(new HairpinFact(
            "hp-1",
            "crescendo",
            1,
            2,
            2,
            "below",
            "1/8",
            "5/8",
            25,
            170,
            0.96,
            "test hairpin",
            ["hp-1"]));

        var canonical = new CanonicalNotationBuilder().Build(
            document,
            facts,
            null,
            null);

        var relation = Assert.Single(canonical.Relations.Hairpins);
        Assert.Equal("hairpin", relation.Kind);
        Assert.Equal("crescendo", relation.Type);
        Assert.Equal(1, relation.From.Measure);
        Assert.Equal("1/8", relation.From.At);
        Assert.Equal(2, relation.To.Measure);
        Assert.Equal("5/8", relation.To.At);
        Assert.Equal(2, relation.From.Staff);
        Assert.Equal("below", relation.Placement);
    }

    private static LogicalOwnership Ownership(string staffId, string measureId) =>
        new(
            new LogicalCoordinate(staffId, measureId),
            new LogicalCoordinate(staffId, measureId),
            1,
            null,
            0,
            "test");

    private static HairpinPrimitive Hairpin(
        string id,
        HairpinKind kind,
        PointD apex,
        PointD openUpper,
        PointD openLower,
        double confidence,
        LogicalOwnership ownership) =>
        new(
            id,
            kind,
            apex,
            openUpper,
            openLower,
            Math.Abs(((openUpper.X + openLower.X) / 2.0) - apex.X),
            Math.Abs(openLower.Y - openUpper.Y),
            confidence,
            "polyline",
            null,
            ownership);

    private static HairpinElement Element(HairpinPrimitive source) =>
        new()
        {
            ShapeId = source.ShapeId,
            Bounds = BoundsD.FromPoints([source.Apex, source.OpenUpper, source.OpenLower]),
            Ownership = source.Ownership!,
            Source = source
        };

    private static SemanticDocument OneMeasure(
        IReadOnlyList<SemanticElement> upperElements,
        IReadOnlyList<SemanticElement> lowerElements) =>
        new([
            Measure(1, "m1", 0, 100, upperElements, lowerElements)
        ]);

    private static SemanticDocument TwoMeasures(
        IReadOnlyList<SemanticElement> lowerElements,
        IReadOnlyList<SemanticElement> upperElements) =>
        new([
            Measure(1, "m1", 0, 100, upperElements, lowerElements),
            Measure(2, "m2", 100, 200, [], [])
        ]);

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
