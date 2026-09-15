using SvgMusic.Semantics;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class ChordPassTests
{
    [Fact]
    public void SharedStem_GroupsSameFillNoteheadsIntoChord()
    {
        var facts = new SemanticFacts();
        facts.Add(Notehead("n1", x: 100, y: 90, fill: "filled"));
        facts.Add(Notehead("n2", x: 102, y: 100, fill: "filled"));
        facts.Add(Notehead("n3", x: 100, y: 120, fill: "filled"));
        facts.Add(Stem("stem", ["n1", "n2", "n3"]));
        facts.Add(Duration("n1", "stem", "quarter", "1/4"));
        facts.Add(Duration("n2", "stem", "quarter", "1/4"));
        facts.Add(Duration("n3", "stem", "quarter", "1/4"));

        var pass = new ChordPass();
        pass.Run(new SemanticDocument([]), facts);

        var chord = Assert.Single(facts.OfType<ChordFact>());
        Assert.Equal("stem", chord.StemShapeId);
        Assert.Equal("quarter", chord.NoteType);
        Assert.Equal(new[] { "n1", "n2", "n3" }, chord.NoteheadIds);
    }

    [Fact]
    public void StemlessWholeNotes_UseTransitiveXOverlapForDisplacedSeconds()
    {
        var facts = new SemanticFacts();

        // Radius=6 gives physical x intervals:
        // n1 94..106, n2 102..114, n3 106..118, n4 102..114.
        // The outer heads do not all share the same center, but the intervals form
        // one connected component exactly like an engraved whole-note chord with seconds.
        facts.Add(Notehead("n1", x: 100, y: 90, fill: "hollow", radius: 6));
        facts.Add(Notehead("n2", x: 108, y: 100, fill: "hollow", radius: 6));
        facts.Add(Notehead("n3", x: 112, y: 110, fill: "hollow", radius: 6));
        facts.Add(Notehead("n4", x: 108, y: 120, fill: "hollow", radius: 6));

        facts.Add(Duration("n1", null, "whole", "1"));
        facts.Add(Duration("n2", null, "whole", "1"));
        facts.Add(Duration("n3", null, "whole", "1"));
        facts.Add(Duration("n4", null, "whole", "1"));

        var pass = new ChordPass();
        pass.Run(new SemanticDocument([]), facts);

        var chord = Assert.Single(facts.OfType<ChordFact>());
        Assert.Null(chord.StemShapeId);
        Assert.Equal("whole", chord.NoteType);
        Assert.Equal(new[] { "n1", "n2", "n3", "n4" }, chord.NoteheadIds);
        Assert.Contains("transitive X-overlap", chord.Reason);
    }

    [Fact]
    public void SeparateStemlessWholeColumns_RemainSeparateNotes()
    {
        var facts = new SemanticFacts();
        facts.Add(Notehead("left", x: 100, y: 100, fill: "hollow"));
        facts.Add(Notehead("right", x: 150, y: 100, fill: "hollow"));
        facts.Add(Duration("left", null, "whole", "1"));
        facts.Add(Duration("right", null, "whole", "1"));

        new ChordPass().Run(new SemanticDocument([]), facts);

        Assert.Empty(facts.OfType<ChordFact>());
    }

    [Fact]
    public void SharedCrossStaffStem_ProducesOneCrossStaffChordFact()
    {
        var facts = new SemanticFacts();
        facts.Add(Notehead("upper", x: 100, y: 100, fill: "filled", staff: 1));
        facts.Add(Notehead("lower", x: 101, y: 200, fill: "filled", staff: 2));
        facts.Add(new StemAttachmentFact(
            1,
            "cross-stem",
            StemDirection.Down,
            ["upper", "lower"],
            [1, 2],
            true,
            100,
            80,
            100,
            220,
            6,
            0.1,
            0.95,
            "test cross staff stem",
            ["cross-stem", "upper", "lower"]));
        facts.Add(Duration("upper", "cross-stem", "quarter", "1/4", staff: 1));
        facts.Add(Duration("lower", "cross-stem", "quarter", "1/4", staff: 2));

        new ChordPass().Run(new SemanticDocument([]), facts);

        var chord = Assert.Single(facts.OfType<ChordFact>());
        Assert.Equal(new[] { 1, 2 }, chord.Staffs);
        Assert.Equal(new[] { "upper", "lower" }, chord.NoteheadIds);
    }

    private static NoteheadFact Notehead(
        string id,
        double x,
        double y,
        string fill,
        int staff = 1,
        double radius = 5)
    {
        return new NoteheadFact(
            1,
            staff,
            id,
            x,
            y,
            radius,
            radius * 0.75,
            fill,
            1.0,
            0,
            0,
            0.99,
            "test notehead",
            [id]);
    }

    private static StemAttachmentFact Stem(
        string id,
        IReadOnlyList<string> noteheads)
    {
        return new StemAttachmentFact(
            1,
            id,
            StemDirection.Up,
            noteheads,
            [1],
            false,
            106,
            50,
            106,
            125,
            3,
            0.1,
            0.96,
            "test stem",
            [id, .. noteheads]);
    }

    private static DurationFact Duration(
        string noteheadId,
        string? stemId,
        string noteType,
        string duration,
        int staff = 1)
    {
        return new DurationFact(
            1,
            staff,
            noteheadId,
            stemId,
            duration,
            duration,
            noteType,
            0,
            0,
            null,
            null,
            0.98,
            "test duration",
            stemId is null
                ? [noteheadId]
                : [noteheadId, stemId]);
    }
}
