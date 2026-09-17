using SvgMusic.Semantics;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class SemanticFactsDynamicDedupTests
{
    [Fact]
    public void Add_KeepsOnlyBestEquivalentDynamicDirection()
    {
        var facts = new SemanticFacts();

        facts.Add(Dynamic("split", 0.92));
        facts.Add(Dynamic("whole", 0.986));

        var dynamic = Assert.Single(facts.OfType<DynamicDirectionFact>());
        Assert.Equal("whole", dynamic.ShapeId);
        Assert.Equal(0.986, dynamic.Confidence, 3);
    }

    private static DynamicDirectionFact Dynamic(
        string shapeId,
        double confidence) =>
        new(
            4,
            1,
            shapeId,
            "mp",
            "0",
            "below",
            "DYNAMICS_MP",
            confidence,
            100,
            200,
            confidence,
            "test",
            [shapeId]);
}
