using SvgMusic.Scene;
using SvgMusic.Semantics;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class VoicePassTests
{
    [Fact]
    public void MonophonicStemDirectionChanges_DoNotCreateSecondVoice()
    {
        var facts = new SemanticFacts();
        facts.Add(Notehead("up", 100, 95, staff: 1));
        facts.Add(Notehead("down", 150, 105, staff: 1));
        facts.Add(Stem("stem-up", "up", StemDirection.Up, 100, staff: 1));
        facts.Add(Stem("stem-down", "down", StemDirection.Down, 150, staff: 1));

        var pass = new VoicePass();
        pass.Run(Document(), facts);

        var voices = facts.OfType<VoiceFact>()
            .OrderBy(voice => voice.TargetId)
            .ToArray();

        Assert.Equal(2, voices.Length);
        Assert.All(voices, voice => Assert.Equal(1, voice.LocalVoice));
        Assert.All(voices, voice => Assert.False(voice.IsPolyphonicStaff));
        Assert.Empty(pass.LastAnalysis!.PolyphonicStaffs);
    }

    [Fact]
    public void AlignedOppositeStems_CreateTwoVoices()
    {
        var facts = new SemanticFacts();
        facts.Add(Notehead("upper", 100, 92, staff: 1));
        facts.Add(Notehead("lower", 104, 108, staff: 1));
        facts.Add(Stem("stem-up", "upper", StemDirection.Up, 100, staff: 1));
        facts.Add(Stem("stem-down", "lower", StemDirection.Down, 104, staff: 1));

        var pass = new VoicePass();
        pass.Run(Document(), facts);

        Assert.Equal(1, Voice(facts, VoiceTargetKind.Notehead, "upper").LocalVoice);
        Assert.Equal(2, Voice(facts, VoiceTargetKind.Notehead, "lower").LocalVoice);
        Assert.Contains((1, 1), pass.LastAnalysis!.PolyphonicStaffs);
    }

    [Fact]
    public void LowerStaff_UsesSameLocalConvention_UpOneDownTwo()
    {
        var facts = new SemanticFacts();
        facts.Add(Notehead("upper", 100, 212, staff: 2));
        facts.Add(Notehead("lower", 104, 228, staff: 2));
        facts.Add(Stem("stem-up", "upper", StemDirection.Up, 100, staff: 2));
        facts.Add(Stem("stem-down", "lower", StemDirection.Down, 104, staff: 2));

        new VoicePass().Run(Document(), facts);

        Assert.Equal(1, Voice(facts, VoiceTargetKind.Notehead, "upper").LocalVoice);
        Assert.Equal(2, Voice(facts, VoiceTargetKind.Notehead, "lower").LocalVoice);
    }

    [Fact]
    public void RestBelowAlignedVoiceOneNote_BelongsToOtherVoice()
    {
        var facts = new SemanticFacts();
        facts.Add(Notehead("note", 100, 90, staff: 1));
        facts.Add(Stem("stem", "note", StemDirection.Up, 100, staff: 1));
        facts.Add(Rest("rest", 103, 112, staff: 1));

        new VoicePass().Run(Document(), facts);

        Assert.Equal(1, Voice(facts, VoiceTargetKind.Notehead, "note").LocalVoice);
        var rest = Voice(facts, VoiceTargetKind.Rest, "rest");
        Assert.Equal(2, rest.LocalVoice);
        Assert.Contains("other local voice", rest.Reason);
    }

    [Fact]
    public void VerticallyPairedRests_AreDifferentVoices()
    {
        var facts = new SemanticFacts();
        facts.Add(Rest("upper-rest", 100, 92, staff: 1));
        facts.Add(Rest("lower-rest", 102, 108, staff: 1));

        new VoicePass().Run(Document(), facts);

        Assert.Equal(1, Voice(facts, VoiceTargetKind.Rest, "upper-rest").LocalVoice);
        Assert.Equal(2, Voice(facts, VoiceTargetKind.Rest, "lower-rest").LocalVoice);
    }

    [Fact]
    public void Chord_IsOneVoiceTargetRatherThanOneTargetPerHead()
    {
        var facts = new SemanticFacts();
        facts.Add(Notehead("c1", 100, 90, staff: 1));
        facts.Add(Notehead("c2", 101, 100, staff: 1));
        facts.Add(Notehead("other", 104, 112, staff: 1));
        facts.Add(Stem("chord-stem", "c1", StemDirection.Up, 100, staff: 1, additionalNotehead: "c2"));
        facts.Add(Stem("other-stem", "other", StemDirection.Down, 104, staff: 1));
        facts.Add(new ChordFact(
            1,
            "chord-1",
            [1],
            ["c1", "c2"],
            "chord-stem",
            "filled",
            "quarter",
            100.5,
            0.98,
            "test chord",
            ["c1", "c2", "chord-stem"]));

        new VoicePass().Run(Document(), facts);

        var chord = Voice(facts, VoiceTargetKind.Chord, "chord-1");
        Assert.Equal(1, chord.LocalVoice);
        Assert.DoesNotContain(
            facts.OfType<VoiceFact>(),
            voice => voice.TargetKind == VoiceTargetKind.Notehead
                && (voice.TargetId == "c1" || voice.TargetId == "c2"));
        Assert.Equal(2, Voice(facts, VoiceTargetKind.Notehead, "other").LocalVoice);
    }

    private static VoiceFact Voice(
        SemanticFacts facts,
        VoiceTargetKind kind,
        string targetId)
    {
        return Assert.Single(facts.OfType<VoiceFact>(), voice =>
            voice.TargetKind == kind && voice.TargetId == targetId);
    }

    private static NoteheadFact Notehead(
        string id,
        double x,
        double y,
        int staff)
    {
        return new NoteheadFact(
            1,
            staff,
            id,
            x,
            y,
            5,
            4,
            "filled",
            1,
            0,
            0,
            0.99,
            "test notehead",
            [id]);
    }

    private static StemAttachmentFact Stem(
        string id,
        string noteheadId,
        StemDirection direction,
        double x,
        int staff,
        string? additionalNotehead = null)
    {
        var noteheads = additionalNotehead is null
            ? new[] { noteheadId }
            : new[] { noteheadId, additionalNotehead };

        return new StemAttachmentFact(
            1,
            id,
            direction,
            noteheads,
            [staff],
            false,
            x,
            direction == StemDirection.Up ? 60 : 90,
            x,
            direction == StemDirection.Up ? 100 : 130,
            4,
            0.1,
            0.98,
            "test stem",
            [id, .. noteheads]);
    }

    private static RestFact Rest(
        string id,
        double x,
        double y,
        int staff)
    {
        return new RestFact(
            1,
            staff,
            id,
            "QUARTER_REST",
            0.99,
            "quarter",
            "1/4",
            x,
            y,
            0.98,
            "test rest",
            [id]);
    }

    private static SemanticDocument Document()
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
                    new BoundsD(0, 80, 300, 120),
                    10,
                    Array.Empty<SemanticElement>()),
                new StaffMeasureScene(
                    2,
                    "staff-2",
                    new BoundsD(0, 200, 300, 240),
                    10,
                    Array.Empty<SemanticElement>()))
        ]);
    }
}
