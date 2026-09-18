using SvgMusic.Canonical;
using SvgMusic.Scene;
using SvgMusic.Semantics;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class TextCanonicalProjectionTests
{
    [Fact]
    public void BuilderProjectsAcceptedTextRolesAndSkipsMeasureNumberAndUnknown()
    {
        var document = Document();
        var facts = new SemanticFacts();
        facts.Add(Text(SemanticTextRole.Title, "TITLE"));
        facts.Add(Text(SemanticTextRole.Subtitle, "SUBTITLE"));
        facts.Add(Text(SemanticTextRole.Composer, "COMPOSER"));
        facts.Add(Text(
            SemanticTextRole.Tempo,
            "Cantabile",
            measure: 1,
            staff: 1,
            at: "0",
            placement: "above"));
        facts.Add(Text(
            SemanticTextRole.Instruction,
            "dolce",
            measure: 1,
            staff: 1,
            at: "1/4",
            placement: "above"));
        facts.Add(Text(SemanticTextRole.MeasureNumber, "17", measure: 1));
        facts.Add(Text(SemanticTextRole.Unknown, "0."));

        var canonical = new CanonicalNotationBuilder().Build(
            document,
            facts,
            null,
            null);

        Assert.Equal("TITLE", canonical.Metadata.Title);
        Assert.Equal("SUBTITLE", canonical.Metadata.Subtitle);
        Assert.Equal("COMPOSER", canonical.Metadata.Composer);

        var events = canonical.Parts.Single().Measures.Single().Events;
        Assert.Equal(2, events.Count);
        Assert.Contains(events, ev =>
            ev.TextRole == nameof(SemanticTextRole.Tempo)
            && ev.Text == "Cantabile");
        Assert.Contains(events, ev =>
            ev.TextRole == nameof(SemanticTextRole.Instruction)
            && ev.Text == "dolce");
        Assert.DoesNotContain(events, ev => ev.Text is "17" or "0.");

        var xml = new MusicXmlWriter().Write(canonical);
        Assert.Equal("TITLE", xml.Descendants("work-title").Single().Value);
        Assert.Equal(
            "COMPOSER",
            xml.Descendants("creator")
                .Single(node => (string?)node.Attribute("type") == "composer")
                .Value);
        Assert.Equal(
            "SUBTITLE",
            xml.Descendants("credit")
                .Single(node => node.Element("credit-type")?.Value == "subtitle")
                .Element("credit-words")
                ?.Value);
        Assert.Equal(
            "TITLE",
            xml.Descendants("credit")
                .Single(node => node.Element("credit-type")?.Value == "title")
                .Element("credit-words")
                ?.Value);
        Assert.Equal(
            "COMPOSER",
            xml.Descendants("credit")
                .Single(node => node.Element("credit-type")?.Value == "composer")
                .Element("credit-words")
                ?.Value);

        var words = xml.Descendants("words").ToArray();
        Assert.Contains(words, item => item.Value == "Cantabile");
        Assert.Contains(words, item => item.Value == "dolce");
        Assert.Equal(
            "bold",
            words.Single(item => item.Value == "Cantabile")
                .Attribute("font-weight")
                ?.Value);
        Assert.DoesNotContain(words, item => item.Value == "17");
        Assert.DoesNotContain(words, item => item.Value == "0.");
    }

    private static TextFact Text(
        SemanticTextRole role,
        string value,
        int? measure = null,
        int? staff = null,
        string? at = null,
        string? placement = null) =>
        new(
            "ocr-" + role + "-" + value,
            role,
            value,
            new BoundsD(10, 10, 50, 20),
            10,
            0.99,
            measure,
            staff,
            at,
            null,
            placement,
            0.99,
            "test",
            ["shape-" + role + "-" + value]);

    private static SemanticDocument Document() =>
        new([
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
                    new BoundsD(0, 100, 300, 140),
                    10,
                    []),
                new StaffMeasureScene(
                    2,
                    "staff-2",
                    new BoundsD(0, 200, 300, 240),
                    10,
                    []))
        ]);
}
