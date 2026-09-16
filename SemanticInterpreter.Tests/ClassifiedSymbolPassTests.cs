using SvgMusic.Scene;
using SvgMusic.Semantics;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class ClassifiedSymbolPassTests
{
    [Fact]
    public void HighConfidenceMarcato_BecomesStrongAccentOnNearestNote()
    {
        var symbol = Symbol("marcato", "MARCATO", 0.94, x: 52, y: 72);
        var document = Document([symbol]);
        var facts = new SemanticFacts();
        AddNotehead(facts, "note", x: 50, y: 110);

        var pass = new ClassifiedSymbolPass();
        pass.Run(document, facts);

        var mark = Assert.Single(facts.OfType<ClassifiedNotationMarkFact>());
        Assert.Equal("note", mark.TargetNoteheadId);
        Assert.Equal(ClassifiedNotationFamily.Articulation, mark.Family);
        Assert.Equal("strong-accent", mark.Type);
        Assert.Equal("above", mark.Placement);
        Assert.Equal("MARCATO", mark.ClassificationLabel);
        Assert.Single(pass.LastAnalysis!.Accepted);
    }

    [Fact]
    public void HighConfidenceMordent_UsesMusicXmlHistoricName()
    {
        var symbol = Symbol("mordent", "MORDENT", 0.97, x: 48, y: 70);
        var document = Document([symbol]);
        var facts = new SemanticFacts();
        AddNotehead(facts, "note", x: 50, y: 110);

        new ClassifiedSymbolPass().Run(document, facts);

        var mark = Assert.Single(facts.OfType<ClassifiedNotationMarkFact>());
        Assert.Equal(ClassifiedNotationFamily.Ornament, mark.Family);
        Assert.Equal("inverted-mordent", mark.Type);
        Assert.Equal("note", mark.TargetNoteheadId);
    }

    [Fact]
    public void CompositeDynamic_UsesNearestRhythmicOnset()
    {
        var symbol = Symbol("pp", "DYNAMICS_PP", 0.96, x: 52, y: 160);
        var document = Document([symbol]);
        var facts = new SemanticFacts();
        AddOnset(facts, "early", x: 20, at: "0");
        AddOnset(facts, "target", x: 50, at: "1/4");

        new ClassifiedSymbolPass().Run(document, facts);

        var dynamic = Assert.Single(facts.OfType<DynamicDirectionFact>());
        Assert.Equal("pp", dynamic.Value);
        Assert.Equal("1/4", dynamic.At);
        Assert.Equal("below", dynamic.Placement);
        Assert.Equal(1, dynamic.Staff);
    }

    [Fact]
    public void ShapeConsumedByEarlierPass_IsNotReinterpreted()
    {
        var symbol = Symbol("used", "MARCATO", 0.99, x: 50, y: 72);
        var document = Document([symbol]);
        var facts = new SemanticFacts();
        AddNotehead(facts, "note", x: 50, y: 110);
        facts.Add(new TestConsumedFact("used"));

        var pass = new ClassifiedSymbolPass();
        pass.Run(document, facts);

        Assert.Empty(facts.OfType<ClassifiedNotationMarkFact>());
        var decision = Assert.Single(pass.LastAnalysis!.Decisions);
        Assert.Equal("already-consumed", decision.Decision);
    }

    [Fact]
    public void TenutoBelowConservativeThreshold_IsRejected()
    {
        var symbol = Symbol("tenuto", "TENUTO", 0.84, x: 50, y: 72);
        var document = Document([symbol]);
        var facts = new SemanticFacts();
        AddNotehead(facts, "note", x: 50, y: 110);

        var pass = new ClassifiedSymbolPass();
        pass.Run(document, facts);

        Assert.Empty(facts.OfType<ClassifiedNotationMarkFact>());
        var decision = Assert.Single(pass.LastAnalysis!.Decisions);
        Assert.Equal("low-confidence", decision.Decision);
    }

    [Fact]
    public void SingleLetterDynamic_IsLeftForFutureCompositionRule()
    {
        var symbol = Symbol("f", "DYNAMICS_F", 0.99, x: 50, y: 160);
        var document = Document([symbol]);
        var facts = new SemanticFacts();
        AddOnset(facts, "note", x: 50, at: "0");

        var pass = new ClassifiedSymbolPass();
        pass.Run(document, facts);

        Assert.Empty(facts.OfType<DynamicDirectionFact>());
        Assert.Empty(pass.LastAnalysis!.Decisions);
    }

    private static SemanticDocument Document(
        IReadOnlyList<SemanticElement> elements) =>
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
                    elements),
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

    private static void AddNotehead(
        SemanticFacts facts,
        string id,
        double x,
        double y)
    {
        facts.Add(new NoteheadFact(
            1,
            1,
            id,
            x,
            y,
            5,
            3.5,
            "filled",
            1,
            0,
            0,
            0.99,
            "test notehead",
            [id]));
    }

    private static void AddOnset(
        SemanticFacts facts,
        string id,
        double x,
        string at)
    {
        facts.Add(new OnsetFact(
            1,
            1,
            VoiceTargetKind.Notehead,
            id,
            1,
            at,
            x,
            0.99,
            "test onset",
            [id]));
    }

    private sealed record TestConsumedFact(string ShapeId)
        : SemanticFact(
            "TestPass",
            "test consumed shape",
            [ShapeId]);
}
