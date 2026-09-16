using SvgMusic.Canonical;
using SvgMusic.Scene;
using SvgMusic.Semantics;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class RestMusicXmlPreviewTests
{
    [Fact]
    public void RestOnlyMeasure_IsProjectedAsTypedMusicXmlRest()
    {
        var document = new SemanticDocument(
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
                    Array.Empty<SemanticElement>()),
                new StaffMeasureScene(
                    2,
                    "staff-2",
                    new BoundsD(0, 220, 300, 300),
                    20,
                    Array.Empty<SemanticElement>()))
        ]);
        var facts = new SemanticFacts();
        facts.Add(new RestFact(
            1,
            1,
            "rest-shape",
            "HW_REST_set",
            0.99,
            "half",
            "1/2",
            100,
            135,
            0.97,
            "test rest",
            ["rest-shape"]));

        var score = new CanonicalNotationBuilder().Build(
            document,
            facts,
            "Rest preview",
            null);
        var xml = new MusicXmlWriter().Write(score);

        var note = Assert.Single(xml.Descendants("note"));
        Assert.NotNull(note.Element("rest"));
        Assert.Equal("half", note.Element("type")?.Value);
        Assert.Equal("1", note.Element("staff")?.Value);
        Assert.NotNull(note.Element("duration"));

        var canonicalRest = Assert.Single(
            score.Parts.Single().Measures.Single().Events,
            ev => ev.Type == "rest");
        Assert.Equal("1/2", canonicalRest.Duration);
        Assert.Equal(1, canonicalRest.Staff);
    }
}
