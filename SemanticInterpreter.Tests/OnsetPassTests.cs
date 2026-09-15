using SvgMusic.Scene;
using SvgMusic.Semantics;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class OnsetPassTests
{
    [Fact]
    public void DottedRest_AdvancesFollowingVoiceEventsByThreeEighths()
    {
        var facts = new SemanticFacts();
        facts.Add(Rest("rest", x: 100, duration: "1/4", noteType: "quarter"));
        facts.Add(new RestDotAttachmentFact(
            1,
            1,
            "rest",
            ["dot"],
            1,
            0.98,
            "test dotted rest",
            ["rest", "dot"]));
        facts.Add(Voice(VoiceTargetKind.Rest, "rest", localVoice: 1, x: 100));

        AddNote(facts, "n1", x: 200, duration: "1/8", localVoice: 1);
        AddNote(facts, "n2", x: 300, duration: "1/8", localVoice: 1);
        AddNote(facts, "n3", x: 400, duration: "1/8", localVoice: 1);

        var pass = new OnsetPass();
        pass.Run(Document(), facts);

        Assert.Equal("0", At(facts, VoiceTargetKind.Rest, "rest"));
        Assert.Equal("3/8", At(facts, VoiceTargetKind.Notehead, "n1"));
        Assert.Equal("1/2", At(facts, VoiceTargetKind.Notehead, "n2"));
        Assert.Equal("5/8", At(facts, VoiceTargetKind.Notehead, "n3"));
    }

    [Fact]
    public void SecondaryVoice_UsesAlignedBackboneEventAsLeadingSilenceAnchor()
    {
        var facts = new SemanticFacts();
        AddNote(facts, "v1a", x: 100, duration: "1/4", localVoice: 1);
        AddNote(facts, "v1b", x: 200, duration: "1/4", localVoice: 1);
        AddNote(facts, "v1c", x: 300, duration: "1/4", localVoice: 1);
        AddNote(facts, "v1d", x: 400, duration: "1/4", localVoice: 1);
        AddNote(facts, "v2", x: 200, duration: "1/2", localVoice: 2);

        var pass = new OnsetPass();
        pass.Run(Document(), facts);

        Assert.Equal("0", At(facts, VoiceTargetKind.Notehead, "v1a"));
        Assert.Equal("1/4", At(facts, VoiceTargetKind.Notehead, "v1b"));
        Assert.Equal("1/2", At(facts, VoiceTargetKind.Notehead, "v1c"));
        Assert.Equal("3/4", At(facts, VoiceTargetKind.Notehead, "v1d"));
        Assert.Equal("1/4", At(facts, VoiceTargetKind.Notehead, "v2"));

        var secondary = facts.OfType<OnsetFact>()
            .Single(onset => onset.TargetId == "v2");
        Assert.Contains("aligned", secondary.Reason);
    }

    private static void AddNote(
        SemanticFacts facts,
        string id,
        double x,
        string duration,
        int localVoice)
    {
        facts.Add(new NoteheadFact(
            1,
            1,
            id,
            x,
            140,
            5,
            4,
            "filled",
            0.5,
            0,
            0,
            0.99,
            "test note",
            [id]));
        facts.Add(new DurationFact(
            1,
            1,
            id,
            $"stem-{id}",
            duration,
            duration,
            duration == "1/8" ? "eighth" : duration == "1/2" ? "half" : "quarter",
            0,
            0,
            null,
            null,
            0.99,
            "test duration",
            [id]));
        facts.Add(Voice(
            VoiceTargetKind.Notehead,
            id,
            localVoice,
            x));
    }

    private static VoiceFact Voice(
        VoiceTargetKind kind,
        string id,
        int localVoice,
        double x)
    {
        return new VoiceFact(
            1,
            1,
            kind,
            id,
            localVoice,
            localVoice == 2,
            x,
            140,
            0.99,
            "test voice",
            [id]);
    }

    private static RestFact Rest(
        string id,
        double x,
        string duration,
        string noteType)
    {
        return new RestFact(
            1,
            1,
            id,
            "QUARTER_REST",
            0.99,
            noteType,
            duration,
            x,
            140,
            0.99,
            "test rest",
            [id]);
    }

    private static string At(
        SemanticFacts facts,
        VoiceTargetKind kind,
        string id)
    {
        return facts.OfType<OnsetFact>()
            .Single(onset =>
                onset.TargetKind == kind
                && onset.TargetId == id)
            .At;
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
                500,
                false,
                new StaffMeasureScene(
                    1,
                    "staff-1",
                    new BoundsD(0, 100, 500, 180),
                    20,
                    []),
                new StaffMeasureScene(
                    2,
                    "staff-2",
                    new BoundsD(0, 220, 500, 300),
                    20,
                    []))
        ]);
    }
}
