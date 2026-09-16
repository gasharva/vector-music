using SvgMusic.Scene;
using SvgMusic.Semantics;
using SvgMusic.Canonical;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class ClassifiedSymbolCanonicalTests
{
    [Fact]
    public void ClassifiedMarks_AppearInCanonicalAndMusicXmlNotations()
    {
        var document = Document();
        var facts = new SemanticFacts();
        AddPlayableNote(facts, "note", x: 50, at: "0");
        facts.Add(new ClassifiedNotationMarkFact(
            1, 1, "marcato", "note",
            ClassifiedNotationFamily.Articulation,
            "strong-accent", "above", "MARCATO", 0.94,
            50, 70, 0.94, "test marcato", ["marcato"]));
        facts.Add(new ClassifiedNotationMarkFact(
            1, 1, "mordent", "note",
            ClassifiedNotationFamily.Ornament,
            "inverted-mordent", "above", "MORDENT", 0.97,
            50, 65, 0.97, "test mordent", ["mordent"]));

        var canonical = new CanonicalNotationBuilder().Build(document, facts, null, null);
        var ev = Assert.Single(canonical.Parts.Single().Measures.Single().Events);

        Assert.Equal("strong-accent", Assert.Single(ev.Notation!.Articulations!).Type);
        Assert.Equal("inverted-mordent", Assert.Single(ev.Notation.Ornaments!).Type);

        var xml = new MusicXmlWriter().Write(canonical);
        Assert.Single(xml.Descendants("strong-accent"));
        Assert.Single(xml.Descendants("inverted-mordent"));
    }

    [Fact]
    public void ClassifiedDynamic_BecomesTimedMusicXmlDirection()
    {
        var document = Document();
        var facts = new SemanticFacts();
        AddPlayableNote(facts, "note", x: 50, at: "0");
        facts.Add(new DynamicDirectionFact(
            1, 1, "pp", "pp", "0", "below", "DYNAMICS_PP", 0.96,
            48, 160, 0.96, "test pp", ["pp"]));

        var canonical = new CanonicalNotationBuilder().Build(document, facts, null, null);
        var measure = canonical.Parts.Single().Measures.Single();
        var dynamic = Assert.Single(measure.Events.Where(ev => ev.Type == "dynamic"));

        Assert.Equal("pp", dynamic.Value);
        Assert.Equal("0", dynamic.At);
        Assert.Equal(1, dynamic.Staff);
        Assert.Equal("below", dynamic.Placement);

        var xml = new MusicXmlWriter().Write(canonical);
        var direction = Assert.Single(xml.Descendants("direction")
            .Where(node => node.Descendants("pp").Any()));
        Assert.Equal("below", direction.Attribute("placement")?.Value);
        Assert.Equal("1", direction.Element("staff")?.Value);
    }

    private static SemanticDocument Document() =>
        new([
            new MeasureScene(
                1,
                "system-1",
                "pair-1",
                "m1",
                0,
                100,
                false,
                new StaffMeasureScene(
                    1,
                    "upper",
                    new BoundsD(0, 100, 100, 140),
                    10,
                    []),
                new StaffMeasureScene(
                    2,
                    "lower",
                    new BoundsD(0, 220, 100, 260),
                    10,
                    []))
        ]);

    private static void AddPlayableNote(
        SemanticFacts facts,
        string id,
        double x,
        string at)
    {
        facts.Add(new NoteheadFact(
            1, 1, id, x, 120, 5, 3.5, "filled", 1, 0, 0,
            0.99, "test notehead", [id]));
        facts.Add(new PitchFact(
            1, 1, id,
            "C", 5, 0, "C5",
            0, "G", 2, "clef",
            0, null, null, false,
            0.99, "test pitch", [id]));
        facts.Add(new DurationFact(
            1, 1, id, null,
            "1/4", "1/4", "quarter",
            0, 0, null, null,
            0.99, "test duration", [id]));
        facts.Add(new OnsetFact(
            1, 1, VoiceTargetKind.Notehead, id,
            1, at, x,
            0.99, "test onset", [id]));
    }
}
