using SvgMusic.Scene;
using SvgMusic.Semantics;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class RestPassTests
{
    [Fact]
    public void DirectClassifierLabels_MapToRestDurations()
    {
        var document = Document(
            Shape("quarter", "QUARTER_REST", x: 80, centerY: 140),
            Shape("eighth", "EIGHTH_REST", x: 120, centerY: 140),
            Shape("sixteenth", "ONE_16TH_REST", x: 160, centerY: 140));
        var facts = new SemanticFacts();

        new RestPass().Run(document, facts);

        var rests = facts.OfType<RestFact>()
            .OrderBy(rest => rest.CenterX)
            .ToArray();

        Assert.Equal(3, rests.Length);
        Assert.Equal(("quarter", "1/4"), (rests[0].NoteType, rests[0].Duration));
        Assert.Equal(("eighth", "1/8"), (rests[1].NoteType, rests[1].Duration));
        Assert.Equal(("16th", "1/16"), (rests[2].NoteType, rests[2].Duration));
    }

    [Theory]
    [InlineData(125.0, "whole", "1")]
    [InlineData(135.0, "half", "1/2")]
    public void HalfWholeRestSet_UsesStaffGridPhase(
        double centerY,
        string expectedType,
        string expectedDuration)
    {
        // Staff top=100, spacing=20.
        // y=125 => pitch -1.5 => whole-rest phase.
        // y=135 => pitch -0.5 => half-rest phase.
        var document = Document(
            Shape("hw", "HW_REST_set", x: 100, centerY: centerY));
        var facts = new SemanticFacts();

        new RestPass().Run(document, facts);

        var rest = Assert.Single(facts.OfType<RestFact>());
        Assert.Equal(expectedType, rest.NoteType);
        Assert.Equal(expectedDuration, rest.Duration);
        Assert.Contains("staff-grid phase", rest.Reason);
    }

    [Fact]
    public void HalfWholeRestSet_CrossedByAcceptedStem_IsRejected()
    {
        var document = Document(
            Shape("hw", "HW_REST_set", x: 100, centerY: 125));
        var facts = new SemanticFacts();
        facts.Add(new StemAttachmentFact(
            1,
            "stem",
            StemDirection.Up,
            ["notehead"],
            [1],
            false,
            100,
            90,
            100,
            160,
            3.5,
            0.1,
            0.99,
            "test stem",
            ["stem"]));

        var pass = new RestPass();
        pass.Run(document, facts);

        Assert.Empty(facts.OfType<RestFact>());
        var decision = Assert.Single(pass.LastAnalysis!.Decisions);
        Assert.False(decision.Accepted);
        Assert.Equal("half-whole-rest-touches-stem", decision.Decision);
    }

    [Fact]
    public void LowConfidenceRestClassification_IsIgnored()
    {
        var document = Document(
            Shape("weak", "QUARTER_REST", x: 100, centerY: 140, confidence: 0.50));
        var facts = new SemanticFacts();

        var pass = new RestPass();
        pass.Run(document, facts);

        Assert.Empty(facts.OfType<RestFact>());
        Assert.Empty(pass.LastAnalysis!.Decisions);
    }

    private static SemanticDocument Document(params ShapeElement[] shapes)
    {
        return new SemanticDocument(
        [
            new MeasureScene(
                1,
                "system-1",
                "pair-1",
                "measure-1",
                0,
                300,
                false,
                new StaffMeasureScene(
                    1,
                    "staff-1",
                    new BoundsD(0, 100, 300, 180),
                    20,
                    shapes),
                new StaffMeasureScene(
                    2,
                    "staff-2",
                    new BoundsD(0, 220, 300, 300),
                    20,
                    Array.Empty<SemanticElement>()))
        ]);
    }

    private static ShapeElement Shape(
        string id,
        string label,
        double x,
        double centerY,
        double confidence = 0.99)
    {
        const double width = 20;
        const double height = 10;
        var bounds = new BoundsD(
            x - width / 2,
            centerY - height / 2,
            x + width / 2,
            centerY + height / 2);
        var ownership = new LogicalOwnership(
            new LogicalCoordinate("staff-1", "measure-1"),
            new LogicalCoordinate("staff-1", "measure-1"),
            1,
            null,
            0,
            "test");
        var classification = new SymbolClassification(
            label,
            confidence,
            20,
            []);
        var source = new ShapeInstance(
            id,
            "prototype-rest",
            bounds.MinX,
            bounds.MinY,
            bounds.Width,
            bounds.Height,
            "path",
            null,
            Classification: classification,
            Ownership: ownership);

        return new ShapeElement
        {
            ShapeId = id,
            Bounds = bounds,
            Ownership = ownership,
            Source = source
        };
    }
}
