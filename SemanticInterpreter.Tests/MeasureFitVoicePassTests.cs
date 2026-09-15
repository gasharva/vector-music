using SvgMusic.Scene;
using SvgMusic.Semantics;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class MeasureFitVoicePassTests
{
    [Fact]
    public void OverfullThreeQuarterStaff_SplitsInterleavedOppositeStemVoice()
    {
        var (document, facts) = BuildThreeQuarterExample();

        new MeasureFitVoicePass().Run(document, facts);

        Assert.Equal(1, VoiceOf(facts, VoiceTargetKind.Chord, "primary-chord").LocalVoice);
        Assert.Equal(2, VoiceOf(facts, VoiceTargetKind.Notehead, "secondary-half").LocalVoice);
        Assert.Equal(1, VoiceOf(facts, VoiceTargetKind.Notehead, "final-quarter").LocalVoice);
        Assert.All(
            facts.OfType<VoiceFact>().Where(voice => voice.Staff == 1),
            voice => Assert.True(voice.IsPolyphonicStaff));
    }

    [Fact]
    public void UnanchoredSecondaryHalfNote_EndFitsAtQuarterBeatInThreeFour()
    {
        var (document, facts) = BuildThreeQuarterExample();

        new MeasureFitVoicePass().Run(document, facts);
        new OnsetPass().Run(document, facts);

        var provisional = OnsetOf(
            facts,
            VoiceTargetKind.Notehead,
            "secondary-half");
        Assert.Equal("0", provisional.At);
        Assert.Contains(
            "no cross-voice x anchor",
            provisional.Reason,
            StringComparison.OrdinalIgnoreCase);

        new MeasureEndOnsetRefinementPass().Run(document, facts);

        Assert.Equal(
            "0",
            OnsetOf(facts, VoiceTargetKind.Chord, "primary-chord").At);
        Assert.Equal(
            "1/4",
            OnsetOf(facts, VoiceTargetKind.Notehead, "secondary-half").At);
        Assert.Equal(
            "1/2",
            OnsetOf(facts, VoiceTargetKind.Notehead, "final-quarter").At);
    }

    private static (SemanticDocument Document, SemanticFacts Facts) BuildThreeQuarterExample()
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
                    new BoundsD(0, 80, 300, 160),
                    20,
                    []),
                new StaffMeasureScene(
                    2,
                    "staff-2",
                    new BoundsD(0, 220, 300, 300),
                    20,
                    []))
        ]);

        var facts = new SemanticFacts();
        facts.Add(new TimeSignatureFact(
            1,
            3,
            4,
            0,
            20,
            "test 3/4",
            ["time"]));

        facts.Add(Notehead("primary-a", 100, 100, "hollow"));
        facts.Add(Notehead("primary-b", 104, 112, "hollow"));
        facts.Add(Notehead("secondary-half", 150, 108, "hollow"));
        facts.Add(Notehead("final-quarter", 250, 96, "filled"));

        facts.Add(Stem(
            "primary-stem",
            StemDirection.Up,
            ["primary-a", "primary-b"],
            100));
        facts.Add(Stem(
            "secondary-stem",
            StemDirection.Down,
            ["secondary-half"],
            150));
        facts.Add(Stem(
            "final-stem",
            StemDirection.Up,
            ["final-quarter"],
            250));

        facts.Add(Duration("primary-a", "primary-stem", "1/2", "half"));
        facts.Add(Duration("primary-b", "primary-stem", "1/2", "half"));
        facts.Add(Duration("secondary-half", "secondary-stem", "1/2", "half"));
        facts.Add(Duration("final-quarter", "final-stem", "1/4", "quarter"));

        facts.Add(new ChordFact(
            1,
            "primary-chord",
            [1],
            ["primary-a", "primary-b"],
            "primary-stem",
            "hollow",
            "half",
            102,
            0.98,
            "test chord",
            ["primary-a", "primary-b", "primary-stem"]));

        facts.Add(Voice(
            VoiceTargetKind.Chord,
            "primary-chord",
            102,
            106));
        facts.Add(Voice(
            VoiceTargetKind.Notehead,
            "secondary-half",
            150,
            108));
        facts.Add(Voice(
            VoiceTargetKind.Notehead,
            "final-quarter",
            250,
            96));

        return (document, facts);
    }

    private static NoteheadFact Notehead(
        string id,
        double x,
        double y,
        string fill) =>
        new(
            1,
            1,
            id,
            x,
            y,
            6,
            4,
            fill,
            1,
            0,
            0,
            0.99,
            "test notehead",
            [id]);

    private static StemAttachmentFact Stem(
        string id,
        StemDirection direction,
        IReadOnlyList<string> noteheads,
        double x) =>
        new(
            1,
            id,
            direction,
            noteheads,
            [1],
            false,
            x,
            direction == StemDirection.Up ? 50 : 100,
            x,
            direction == StemDirection.Up ? 100 : 160,
            3,
            0.1,
            0.98,
            "test stem",
            [id, .. noteheads]);

    private static DurationFact Duration(
        string noteheadId,
        string stemId,
        string duration,
        string noteType) =>
        new(
            1,
            1,
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
            [noteheadId, stemId]);

    private static VoiceFact Voice(
        VoiceTargetKind kind,
        string id,
        double x,
        double y) =>
        new(
            1,
            1,
            kind,
            id,
            1,
            false,
            x,
            y,
            0.99,
            "provisional single voice",
            [id]);

    private static VoiceFact VoiceOf(
        SemanticFacts facts,
        VoiceTargetKind kind,
        string id) =>
        Assert.Single(
            facts.OfType<VoiceFact>(),
            voice => voice.TargetKind == kind && voice.TargetId == id);

    private static OnsetFact OnsetOf(
        SemanticFacts facts,
        VoiceTargetKind kind,
        string id) =>
        Assert.Single(
            facts.OfType<OnsetFact>(),
            onset => onset.TargetKind == kind && onset.TargetId == id);
}
