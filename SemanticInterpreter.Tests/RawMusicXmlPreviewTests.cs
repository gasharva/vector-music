using SvgMusic.Canonical;
using SvgMusic.Scene;
using SvgMusic.Semantics;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class RawMusicXmlPreviewTests
{
    [Fact]
    public void BuilderProjectsPitchDurationAndExplicitAccidentalIntoMusicXml()
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
                    new BoundsD(0, 100, 300, 200),
                    20,
                    []),
                new StaffMeasureScene(
                    2,
                    "staff-2",
                    new BoundsD(0, 240, 300, 340),
                    20,
                    []))
        ]);

        var facts = new SemanticFacts();
        facts.Add(new NoteheadFact(
            1,
            1,
            "note",
            100,
            140,
            5,
            4,
            "filled",
            1.1,
            0,
            0,
            0.99,
            "test notehead",
            ["note"]));
        facts.Add(new PitchFact(
            1,
            1,
            "note",
            "D",
            5,
            -1,
            "Db5",
            0,
            "G",
            2,
            "clef",
            -4,
            "flat",
            AccidentalKind.Flat,
            true,
            0.97,
            "test pitch",
            ["note", "flat"]));
        facts.Add(new StemAttachmentFact(
            1,
            "stem",
            StemDirection.Up,
            ["note"],
            [1],
            false,
            110,
            80,
            110,
            140,
            3,
            0.1,
            0.95,
            "test stem",
            ["stem", "note"]));
        facts.Add(new DurationFact(
            1,
            1,
            "note",
            "stem",
            "1/8",
            "1/8",
            "eighth",
            0,
            1,
            null,
            null,
            0.93,
            "test duration",
            ["note", "stem"]));
        facts.Add(new TextFact(
            "ocr-finger",
            SemanticTextRole.Fingering,
            "3",
            new BoundsD(98, 95, 104, 105),
            10,
            0.99,
            1,
            1,
            null,
            "note",
            "above",
            0.99,
            "test fingering",
            ["finger"]));

        var canonical = new CanonicalNotationBuilder().Build(
            document,
            facts,
            "Raw preview",
            null);

        var measure = Assert.Single(canonical.Parts.Single().Measures);
        var ev = Assert.Single(measure.Events);
        Assert.Equal("1/8", ev.Duration);
        Assert.Equal("eighth", ev.Notation?.NoteType);
        Assert.Equal("up", ev.Notation?.Stem);

        var note = Assert.Single(ev.Notes!);
        Assert.Equal("Db5", note.Pitch);
        Assert.Equal(1, note.Staff);
        Assert.Equal("flat", note.Accidental?.Type);
        var fingering = Assert.Single(note.Technical!);
        Assert.Equal("fingering", fingering.Type);
        Assert.Equal("3", fingering.Value);
        Assert.Equal("above", fingering.Placement);

        var xml = new MusicXmlWriter().Write(canonical);
        var xmlNote = Assert.Single(xml.Descendants("note"));
        Assert.Equal("D", xmlNote.Element("pitch")?.Element("step")?.Value);
        Assert.Equal("-1", xmlNote.Element("pitch")?.Element("alter")?.Value);
        Assert.Equal("5", xmlNote.Element("pitch")?.Element("octave")?.Value);
        Assert.Equal("eighth", xmlNote.Element("type")?.Value);
        Assert.Equal("flat", xmlNote.Element("accidental")?.Value);
        Assert.Equal("up", xmlNote.Element("stem")?.Value);
        Assert.Equal(
            "3",
            xmlNote.Element("notations")
                ?.Element("technical")
                ?.Element("fingering")
                ?.Value);
    }
}
