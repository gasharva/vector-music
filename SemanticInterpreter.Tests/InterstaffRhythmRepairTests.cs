using SvgMusic.Scene;
using SvgMusic.Semantics;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class InterstaffRhythmRepairTests
{
    [Fact]
    public void InterstaffEighthRest_MovesToStaffThatMakesBothStaffsFitThreeFour()
    {
        var document = Document();
        var facts = new SemanticFacts();
        facts.Add(new TimeSignatureFact(
            1, 3, 4, 0, 10, "test", ["time"]));

        AddNote(
            facts,
            "upper-long",
            staff: 1,
            x: 100,
            y: 100,
            duration: "3/4");
        for (var index = 0; index < 5; index++)
        {
            AddNote(
                facts,
                $"upper-{index}",
                staff: 1,
                x: 140 + index * 25,
                y: 100,
                duration: "1/8");
        }

        AddNote(
            facts,
            "lower-long",
            staff: 2,
            x: 100,
            y: 220,
            duration: "3/4");

        facts.Add(new RestFact(
            1,
            2,
            "interstaff-rest",
            "EIGHTH_REST",
            0.99,
            "eighth",
            "1/8",
            98,
            160,
            0.99,
            "geometrically closer to lower staff",
            ["interstaff-rest"]));

        new InterstaffRestPass().Run(
            document,
            facts);

        var rest = Assert.Single(
            facts.OfType<RestFact>());

        Assert.Equal(1, rest.Staff);
        Assert.Contains(
            "interstaff repair",
            rest.Reason,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RhythmicLaneRepair_SeparatesFullMeasureChordFromRestAndEighthRun()
    {
        var document = Document();
        var facts = new SemanticFacts();
        facts.Add(new TimeSignatureFact(
            1, 3, 4, 0, 10, "test", ["time"]));

        AddNote(
            facts,
            "long",
            staff: 1,
            x: 102,
            y: 100,
            duration: "3/4");
        facts.Add(Voice(
            "long",
            x: 102,
            localVoice: 2));

        facts.Add(new RestFact(
            1,
            1,
            "rest",
            "EIGHTH_REST",
            0.99,
            "eighth",
            "1/8",
            100,
            150,
            0.99,
            "test rest",
            ["rest"]));
        facts.Add(new VoiceFact(
            1,
            1,
            VoiceTargetKind.Rest,
            "rest",
            1,
            true,
            100,
            150,
            0.99,
            "test rest voice",
            ["rest"]));

        for (var index = 0; index < 5; index++)
        {
            var id = $"melody-{index}";
            AddNote(
                facts,
                id,
                staff: 1,
                x: 140 + index * 25,
                y: 90,
                duration: "1/8");
            facts.Add(Voice(
                id,
                x: 140 + index * 25,
                localVoice: index == 0 ? 1 : 2));
        }

        new RhythmicLanePass().Run(
            document,
            facts);

        Assert.Equal(
            "0",
            Onset(facts, VoiceTargetKind.Notehead, "long").At);
        Assert.Equal(
            "0",
            Onset(facts, VoiceTargetKind.Rest, "rest").At);

        Assert.Equal(
            2,
            VoiceOf(facts, VoiceTargetKind.Notehead, "long").LocalVoice);
        Assert.Equal(
            1,
            VoiceOf(facts, VoiceTargetKind.Rest, "rest").LocalVoice);

        var expected = new[]
        {
            "1/8",
            "1/4",
            "3/8",
            "1/2",
            "5/8"
        };

        for (var index = 0; index < expected.Length; index++)
        {
            var id = $"melody-{index}";
            Assert.Equal(
                expected[index],
                Onset(
                    facts,
                    VoiceTargetKind.Notehead,
                    id).At);
            Assert.Equal(
                1,
                VoiceOf(
                    facts,
                    VoiceTargetKind.Notehead,
                    id).LocalVoice);
        }
    }

    [Fact]
    public void CrossStaffInternalAnchor_ShiftsImplicitVoiceStart()
    {
        var document = Document();
        var facts = new SemanticFacts();
        facts.Add(new TimeSignatureFact(
            1, 3, 4, 0, 10, "test", ["time"]));

        facts.Add(new RestFact(
            1,
            1,
            "upper-rest",
            "EIGHTH_REST",
            0.99,
            "eighth",
            "1/8",
            100,
            150,
            0.99,
            "test rest",
            ["upper-rest"]));
        facts.Add(new VoiceFact(
            1,
            1,
            VoiceTargetKind.Rest,
            "upper-rest",
            3,
            true,
            100,
            150,
            0.99,
            "test voice",
            ["upper-rest"]));
        facts.Add(new OnsetFact(
            1,
            1,
            VoiceTargetKind.Rest,
            "upper-rest",
            3,
            "0",
            100,
            0.95,
            "provisional voice start",
            ["upper-rest"]));

        AddNote(
            facts,
            "upper-note",
            staff: 1,
            x: 140,
            y: 100,
            duration: "1/8");
        facts.Add(new VoiceFact(
            1,
            1,
            VoiceTargetKind.Notehead,
            "upper-note",
            3,
            true,
            140,
            100,
            0.99,
            "test voice",
            ["upper-note"]));
        facts.Add(new OnsetFact(
            1,
            1,
            VoiceTargetKind.Notehead,
            "upper-note",
            3,
            "1/8",
            140,
            0.95,
            "provisional sequence",
            ["upper-note"]));

        AddNote(
            facts,
            "lower-first",
            staff: 2,
            x: 50,
            y: 220,
            duration: "1/4");
        facts.Add(new VoiceFact(
            1,
            2,
            VoiceTargetKind.Notehead,
            "lower-first",
            1,
            true,
            50,
            220,
            0.99,
            "test voice",
            ["lower-first"]));
        facts.Add(new OnsetFact(
            1,
            2,
            VoiceTargetKind.Notehead,
            "lower-first",
            1,
            "0",
            50,
            0.95,
            "lower backbone",
            ["lower-first"]));

        AddNote(
            facts,
            "lower-anchor",
            staff: 2,
            x: 102,
            y: 220,
            duration: "1/2");
        facts.Add(new VoiceFact(
            1,
            2,
            VoiceTargetKind.Notehead,
            "lower-anchor",
            1,
            true,
            102,
            220,
            0.99,
            "test voice",
            ["lower-anchor"]));
        facts.Add(new OnsetFact(
            1,
            2,
            VoiceTargetKind.Notehead,
            "lower-anchor",
            1,
            "1/4",
            102,
            0.95,
            "lower internal anchor",
            ["lower-anchor"]));

        new CrossStaffOnsetRefinementPass().Run(
            document,
            facts);

        Assert.Equal(
            "1/4",
            Onset(facts, VoiceTargetKind.Rest, "upper-rest").At);
        Assert.Equal(
            "3/8",
            Onset(facts, VoiceTargetKind.Notehead, "upper-note").At);
    }

    [Fact]
    public void SingleUnanchoredLane_EndFitsFromMeasureGeometry()
    {
        var document = Document();
        var facts = new SemanticFacts();
        facts.Add(new TimeSignatureFact(
            1, 3, 4, 0, 10, "test", ["time"]));

        AddTimedVoice(
            facts,
            "full",
            x: 50,
            duration: "3/4",
            voice: 1,
            at: "0",
            reason: "backbone");
        AddTimedVoice(
            facts,
            "half",
            x: 180,
            duration: "1/2",
            voice: 2,
            at: "1/4",
            reason: "cross-staff anchored");
        AddTimedVoice(
            facts,
            "end-fit",
            x: 220,
            duration: "3/8",
            voice: 3,
            at: "0",
            reason: "no cross-voice x anchor to local voice 1; secondary voice provisionally starts at measure origin");

        new MeasureEndOnsetRefinementPass().Run(
            document,
            facts);

        Assert.Equal(
            "3/8",
            Onset(
                facts,
                VoiceTargetKind.Notehead,
                "end-fit").At);
    }

    private static SemanticDocument Document() =>
        new(
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
                    []),
                new StaffMeasureScene(
                    2,
                    "staff-2",
                    new BoundsD(0, 200, 300, 240),
                    10,
                    []))
        ]);

    private static void AddNote(
        SemanticFacts facts,
        string id,
        int staff,
        double x,
        double y,
        string duration)
    {
        facts.Add(new NoteheadFact(
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
            "test note",
            [id]));

        facts.Add(new DurationFact(
            1,
            staff,
            id,
            null,
            duration,
            duration,
            "eighth",
            0,
            0,
            null,
            null,
            0.99,
            "test duration",
            [id]));
    }

    private static void AddTimedVoice(
        SemanticFacts facts,
        string id,
        double x,
        string duration,
        int voice,
        string at,
        string reason)
    {
        AddNote(
            facts,
            id,
            staff: 1,
            x: x,
            y: 100,
            duration: duration);
        facts.Add(new VoiceFact(
            1,
            1,
            VoiceTargetKind.Notehead,
            id,
            voice,
            true,
            x,
            100,
            0.99,
            "test voice",
            [id]));
        facts.Add(new OnsetFact(
            1,
            1,
            VoiceTargetKind.Notehead,
            id,
            voice,
            at,
            x,
            0.55,
            reason,
            [id]));
    }

    private static VoiceFact Voice(
        string id,
        double x,
        int localVoice) =>
        new(
            1,
            1,
            VoiceTargetKind.Notehead,
            id,
            localVoice,
            true,
            x,
            100,
            0.99,
            "test voice",
            [id]);

    private static VoiceFact VoiceOf(
        SemanticFacts facts,
        VoiceTargetKind kind,
        string id) =>
        Assert.Single(
            facts.OfType<VoiceFact>(),
            voice =>
                voice.TargetKind == kind
                && voice.TargetId == id);

    private static OnsetFact Onset(
        SemanticFacts facts,
        VoiceTargetKind kind,
        string id) =>
        Assert.Single(
            facts.OfType<OnsetFact>(),
            onset =>
                onset.TargetKind == kind
                && onset.TargetId == id);
}
