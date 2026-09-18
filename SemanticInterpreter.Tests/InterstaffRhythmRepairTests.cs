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
            Assert.Equal(
                expected[index],
                Onset(
                    facts,
                    VoiceTargetKind.Notehead,
                    $"melody-{index}").At);
        }
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
