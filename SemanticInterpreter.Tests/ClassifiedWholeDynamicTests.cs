using SvgMusic.Scene;
using SvgMusic.Semantics;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class ClassifiedWholeDynamicTests
{
    [Fact]
    public void WholeMpAndSplitFragments_ProduceOneMpAndPreferWholeConfidence()
    {
        var mFragment = Symbol(
            "shape-20.1",
            "TREMOLO_3",
            0.12,
            x: 40,
            y: 160,
            width: 12);
        var whole = Symbol(
            "shape-20.whole",
            "DYNAMICS_MP",
            0.99,
            x: 47,
            y: 160,
            width: 26);
        var pFragment = Symbol(
            "shape-20.2",
            "DYNAMICS_P",
            0.99,
            x: 54,
            y: 160,
            width: 10);

        var document = Document([mFragment, whole, pFragment]);
        var facts = new SemanticFacts();
        AddOnset(facts, "note", x: 52, at: "1/4");

        new ClassifiedSymbolPass().Run(document, facts);

        var dynamic = Assert.Single(facts.OfType<DynamicDirectionFact>());
        Assert.Equal("mp", dynamic.Value);
        Assert.Equal("shape-20.whole", dynamic.ShapeId);
        Assert.Equal(0.99, dynamic.Confidence, 3);
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
        double y,
        double width)
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
            x - width / 2.0,
            y - 5,
            width,
            10,
            "path",
            null,
            classification,
            null,
            ownership);

        return new ShapeElement
        {
            ShapeId = id,
            Bounds = new BoundsD(x - width / 2.0, y - 5, x + width / 2.0, y + 5),
            Ownership = ownership,
            Source = source
        };
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
}
