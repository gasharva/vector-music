using System.Reflection;
using SvgMusic.Canonical;
using SvgMusic.Scene;
using SvgMusic.Semantics;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class ArpeggioCanonicalTests
{
    [Fact]
    public void CrossStaffArpeggio_BecomesOneRelationAndSixMusicXmlMarks()
    {
        var document = Document();
        var facts = new SemanticFacts();

        var upper = new[] { "u1", "u2", "u3" };
        var lower = new[] { "l1", "l2", "l3" };

        AddNote(facts, upper[0], 1, 32, 112, "F5");
        AddNote(facts, upper[1], 1, 32, 122, "Db5");
        AddNote(facts, upper[2], 1, 32, 132, "Bb4");
        AddNote(facts, lower[0], 2, 32, 212, "F4");
        AddNote(facts, lower[1], 2, 32, 222, "Db4");
        AddNote(facts, lower[2], 2, 32, 232, "G3");

        AddChord(facts, "upper-chord", 1, upper);
        AddChord(facts, "lower-chord", 2, lower);

        facts.Add(new ArpeggioFact(
            1,
            "zigzag",
            ["upper-chord", "lower-chord"],
            upper.Concat(lower).ToArray(),
            [1, 2],
            "0",
            32,
            109,
            235,
            null,
            0.97,
            "test cross-staff arpeggio",
            ["zigzag"]));

        var canonical = new CanonicalNotationBuilder().Build(
            document,
            facts,
            null,
            null);

        var relation = Assert.Single(canonical.Relations.Arpeggios);
        Assert.Equal(
            ["lower-chord", "upper-chord"],
            relation.Events.OrderBy(id => id).ToArray());
        Assert.Null(relation.Direction);

        var xml = new MusicXmlWriter().Write(canonical);
        var arpeggiates = xml.Descendants("arpeggiate").ToArray();
        Assert.Equal(6, arpeggiates.Length);
        Assert.All(arpeggiates, element =>
            Assert.Equal("1", element.Attribute("number")?.Value));
        Assert.All(arpeggiates, element =>
            Assert.Null(element.Attribute("direction")));
    }

    [Fact]
    public void AutoInsertedArpeggioPass_RunsBeforeResidualClassifier()
    {
        var pipeline = new SemanticPipeline([
            new NoteheadPass(),
            new ChordPass()
        ]);
        var field = typeof(SemanticPipeline).GetField(
            "_passes",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var passes = Assert.IsAssignableFrom<IReadOnlyList<ISemanticPass>>(
            field!.GetValue(pipeline));

        var arpeggioIndex = passes
            .Select((pass, index) => (pass, index))
            .Single(item => item.pass is ArpeggioPass)
            .index;
        var classifiedIndex = passes
            .Select((pass, index) => (pass, index))
            .Single(item => item.pass is ClassifiedSymbolPass)
            .index;

        Assert.True(arpeggioIndex < classifiedIndex);
        Assert.Equal(passes.Count - 1, classifiedIndex);
    }

    private static SemanticDocument Document() =>
        new([
            new MeasureScene(
                1,
                "system-1",
                "pair-1",
                "measure-1",
                0,
                120,
                false,
                new StaffMeasureScene(
                    1,
                    "upper",
                    new BoundsD(0, 100, 120, 140),
                    10,
                    []),
                new StaffMeasureScene(
                    2,
                    "lower",
                    new BoundsD(0, 210, 120, 250),
                    10,
                    []))
        ]);

    private static void AddChord(
        SemanticFacts facts,
        string chordId,
        int staff,
        IReadOnlyList<string> noteheadIds)
    {
        facts.Add(new ChordFact(
            1,
            chordId,
            [staff],
            noteheadIds,
            null,
            "filled",
            "quarter",
            32,
            0.98,
            "test chord",
            noteheadIds));
        facts.Add(new OnsetFact(
            1,
            staff,
            VoiceTargetKind.Chord,
            chordId,
            1,
            "0",
            32,
            0.99,
            "test onset",
            noteheadIds));
    }

    private static void AddNote(
        SemanticFacts facts,
        string id,
        int staff,
        double x,
        double y,
        string pitch)
    {
        facts.Add(new NoteheadFact(
            1,
            staff,
            id,
            x,
            y,
            3,
            2,
            "filled",
            1,
            0,
            0,
            0.99,
            "test notehead",
            [id]));
        facts.Add(new PitchFact(
            1,
            staff,
            id,
            pitch[..1],
            int.Parse(pitch[^1..]),
            pitch.Contains('b') ? -1 : 0,
            pitch,
            0,
            staff == 1 ? "G" : "F",
            staff == 1 ? 2 : 4,
            "clef",
            0,
            null,
            null,
            false,
            0.99,
            "test pitch",
            [id]));
        facts.Add(new DurationFact(
            1,
            staff,
            id,
            null,
            "1/4",
            "1/4",
            "quarter",
            0,
            0,
            null,
            null,
            0.99,
            "test duration",
            [id]));
    }
}
