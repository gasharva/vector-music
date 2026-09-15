using SvgMusic.Scene;
using SvgMusic.Semantics;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class DurationPassTests
{
    [Fact]
    public void FilledStem_WithPrimaryBeamAndSecondLevelHook_IsSixteenth()
    {
        var facts = new SemanticFacts();
        facts.Add(Notehead("note", "filled"));
        facts.Add(Stem("stem", "note"));
        facts.Add(Beam("primary", "stem", level: 1, isHook: false));
        facts.Add(Beam("hook", "stem", level: 2, isHook: true));

        new DurationPass().Run(new SemanticDocument([]), facts);

        var duration = Assert.Single(facts.OfType<DurationFact>());
        Assert.Equal("1/16", duration.BaseDuration);
        Assert.Equal("1/16", duration.EffectiveDuration);
        Assert.Equal("16th", duration.NoteType);
        Assert.Equal(2, duration.SubdivisionLevel);
        Assert.Equal("stem", duration.StemShapeId);
    }

    [Fact]
    public void DottedTripletEighth_AppliesDotThenTupletScaling()
    {
        var facts = new SemanticFacts();
        facts.Add(Notehead("note", "filled"));
        facts.Add(Stem("stem", "note"));
        facts.Add(Beam("primary", "stem", level: 1, isHook: false));
        facts.Add(new DotAttachmentFact(
            1,
            1,
            "note",
            ["dot"],
            1,
            0.95,
            "test dot",
            ["dot", "note"]));
        facts.Add(new TupletFact(
            1,
            "tuplet",
            "TIME_THREE",
            3,
            3,
            2,
            false,
            "primary",
            ["stem"],
            ["note"],
            0.99,
            0.94,
            "test tuplet",
            ["tuplet", "primary", "stem"]));

        new DurationPass().Run(new SemanticDocument([]), facts);

        var duration = Assert.Single(facts.OfType<DurationFact>());
        Assert.Equal("1/8", duration.BaseDuration);
        Assert.Equal("1/8", duration.EffectiveDuration);
        Assert.Equal("eighth", duration.NoteType);
        Assert.Equal(1, duration.Dots);
        Assert.Equal(3, duration.TupletActual);
        Assert.Equal(2, duration.TupletNormal);
    }

    [Fact]
    public void HollowNotehead_UsesWholeWithoutStem_AndHalfWithStem()
    {
        var wholeFacts = new SemanticFacts();
        wholeFacts.Add(Notehead("whole", "hollow"));
        new DurationPass().Run(new SemanticDocument([]), wholeFacts);

        var whole = Assert.Single(wholeFacts.OfType<DurationFact>());
        Assert.Equal("1", whole.BaseDuration);
        Assert.Equal("whole", whole.NoteType);

        var halfFacts = new SemanticFacts();
        halfFacts.Add(Notehead("half", "hollow"));
        halfFacts.Add(Stem("stem-half", "half"));
        new DurationPass().Run(new SemanticDocument([]), halfFacts);

        var half = Assert.Single(halfFacts.OfType<DurationFact>());
        Assert.Equal("1/2", half.BaseDuration);
        Assert.Equal("half", half.NoteType);
    }

    private static NoteheadFact Notehead(
        string id,
        string fillKind)
    {
        return new NoteheadFact(
            1,
            1,
            id,
            100,
            100,
            5,
            4,
            fillKind,
            1.1,
            0,
            0,
            0.99,
            "test notehead",
            [id]);
    }

    private static StemAttachmentFact Stem(
        string stemId,
        string noteheadId)
    {
        return new StemAttachmentFact(
            1,
            stemId,
            StemDirection.Up,
            [noteheadId],
            [1],
            false,
            110,
            50,
            110,
            100,
            2.5,
            0.1,
            0.95,
            "test stem",
            [stemId, noteheadId]);
    }

    private static BeamAttachmentFact Beam(
        string beamId,
        string stemId,
        int level,
        bool isHook)
    {
        return new BeamAttachmentFact(
            1,
            beamId,
            level,
            [stemId],
            [1],
            !isHook,
            true,
            isHook,
            false,
            80,
            50,
            110,
            50,
            1.2,
            0.5,
            0,
            0.93,
            "test beam",
            [beamId, stemId]);
    }
}
