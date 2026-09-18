using SvgMusic.Scene;
using SvgMusic.Semantics;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class KeySignaturePassContinuationTests
{
    [Fact]
    public void ContinuationPageWithInheritedTimeAndKey_DoesNotUseTimeAsGeometry()
    {
        var document = new SemanticDocument(
        [
            new MeasureScene(
                1,
                "system-1",
                "pair-1",
                "measure-1",
                100,
                500,
                false,
                new StaffMeasureScene(
                    1,
                    "upper",
                    new BoundsD(100, 100, 500, 180),
                    20,
                    []),
                new StaffMeasureScene(
                    2,
                    "lower",
                    new BoundsD(100, 220, 500, 300),
                    20,
                    []))
        ]);
        var facts = new SemanticFacts();
        facts.Add(new TimeSignatureFact(
            1,
            3,
            4,
            100,
            100,
            "inherited test time",
            [],
            IsInherited: true));

        new KeySignaturePass(-2).Run(
            document,
            facts);

        var key = Assert.Single(
            facts.OfType<KeySignatureFact>());

        Assert.Equal(1, key.MeasureNumber);
        Assert.Equal(-2, key.Fifths);
        Assert.Equal("flat", key.AccidentalKind);
        Assert.True(key.IsInherited);
        Assert.Empty(key.SourceShapeIds);
        Assert.Contains(
            facts.Trace,
            line => line.Contains(
                "inherited key fifths=-2",
                StringComparison.Ordinal));
    }

    [Fact]
    public void ContinuationPageWithoutInheritedKey_FailsWithActionableMessage()
    {
        var document = new SemanticDocument(
        [
            new MeasureScene(
                1,
                "system-1",
                "pair-1",
                "measure-1",
                100,
                500,
                false,
                new StaffMeasureScene(
                    1,
                    "upper",
                    new BoundsD(100, 100, 500, 180),
                    20,
                    []),
                new StaffMeasureScene(
                    2,
                    "lower",
                    new BoundsD(100, 220, 500, 300),
                    20,
                    []))
        ]);
        var facts = new SemanticFacts();
        facts.Add(new TimeSignatureFact(
            1,
            3,
            4,
            100,
            100,
            "inherited test time",
            [],
            IsInherited: true));

        var exception = Assert.Throws<InvalidDataException>(
            () => new KeySignaturePass().Run(document, facts));

        Assert.Contains(
            "--initial-key",
            exception.Message,
            StringComparison.Ordinal);
    }
}
