using SvgMusic.Scene;
using SvgMusic.Semantics;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class ScoreRegressionTests
{
    [Fact]
    public void ChordDurationNormalization_PropagatesDetectedDotToAllMembers()
    {
        var facts = new SemanticFacts();
        facts.Add(Duration("top", "stem", "1/2", "1/2", dots: 0));
        facts.Add(Duration("bottom", "stem", "1/2", "3/4", dots: 1));
        facts.Add(new DotAttachmentFact(
            1,
            2,
            "bottom",
            ["dot"],
            1,
            0.96,
            "test dot",
            ["dot", "bottom"]));
        facts.Add(new ChordFact(
            1,
            "chord",
            [2],
            ["top", "bottom"],
            "stem",
            "hollow",
            "half",
            100,
            0.98,
            "test chord",
            ["top", "bottom", "stem"]));

        new ChordDurationNormalizationPass().Run(
            new SemanticDocument([]),
            facts);

        var durations = facts.OfType<DurationFact>()
            .OrderBy(duration => duration.NoteheadId)
            .ToArray();
        Assert.Equal(2, durations.Length);
        Assert.All(durations, duration => Assert.Equal(1, duration.Dots));
        Assert.All(durations, duration => Assert.Equal("3/4", duration.EffectiveDuration));
        Assert.Contains(
            durations,
            duration => duration.Reason.Contains(
                "chord normalization",
                StringComparison.Ordinal));
    }

    [Fact]
    public void VoiceContinuity_ReassignsLeadInAcrossRestAndRecomputesOnsets()
    {
        var facts = new SemanticFacts();
        facts.Add(Notehead("lead", 40, 90));
        facts.Add(Notehead("parallel-f", 100, 105));
        facts.Add(Notehead("parallel-g", 101, 95));
        facts.Add(Notehead("return", 150, 90));
        facts.Add(Pitch("lead", "Ab5"));
        facts.Add(Pitch("parallel-f", "F5"));
        facts.Add(Pitch("parallel-g", "G5"));
        facts.Add(Pitch("return", "Ab5"));
        facts.Add(Duration("lead", "lead-stem", "1/2", "1/2", 0));
        facts.Add(Duration("parallel-f", "parallel-stem", "1/4", "1/4", 0));
        facts.Add(Duration("parallel-g", "parallel-stem", "1/4", "1/4", 0));
        facts.Add(Duration("return", "return-stem", "1/8", "1/8", 0));
        facts.Add(new ChordFact(
            1,
            "parallel-chord",
            [1],
            ["parallel-f", "parallel-g"],
            "parallel-stem",
            "filled",
            "quarter",
            100.5,
            0.98,
            "test chord",
            ["parallel-f", "parallel-g", "parallel-stem"]));
        facts.Add(new RestFact(
            1,
            1,
            "rest",
            "EIGHTH_REST",
            0.99,
            "eighth",
            "1/8",
            100,
            80,
            0.98,
            "test rest",
            ["rest"]));

        facts.Add(Voice(VoiceTargetKind.Notehead, "lead", 2, 40, 90));
        facts.Add(Voice(VoiceTargetKind.Rest, "rest", 1, 100, 80));
        facts.Add(Voice(VoiceTargetKind.Chord, "parallel-chord", 2, 100.5, 100));
        facts.Add(Voice(VoiceTargetKind.Notehead, "return", 1, 150, 90));

        var document = Document();
        new VoiceContinuityPass().Run(document, facts);

        Assert.Equal(1, VoiceOf(facts, VoiceTargetKind.Notehead, "lead").LocalVoice);
        Assert.Equal(1, VoiceOf(facts, VoiceTargetKind.Rest, "rest").LocalVoice);
        Assert.Equal(1, VoiceOf(facts, VoiceTargetKind.Notehead, "return").LocalVoice);
        Assert.Equal(2, VoiceOf(facts, VoiceTargetKind.Chord, "parallel-chord").LocalVoice);

        Assert.Equal("0", OnsetOf(facts, VoiceTargetKind.Notehead, "lead").At);
        Assert.Equal("1/2", OnsetOf(facts, VoiceTargetKind.Rest, "rest").At);
        Assert.Equal("5/8", OnsetOf(facts, VoiceTargetKind.Notehead, "return").At);
        Assert.Equal("1/2", OnsetOf(facts, VoiceTargetKind.Chord, "parallel-chord").At);
    }

    [Fact]
    public void TieOwnershipRecovery_IgnoresCurveStaffBandForSamePitchEndpoints()
    {
        const double spacing = 20;
        var curve = Curve(
            "tie-curve",
            [
                new PointD(100, 220),
                new PointD(150, 205),
                new PointD(200, 220)
            ]);
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
                    spacing,
                    [curve]),
                new StaffMeasureScene(
                    2,
                    "staff-2",
                    new BoundsD(0, 220, 300, 300),
                    spacing,
                    []))
        ]);
        var facts = new SemanticFacts();
        facts.Add(Notehead("left", 100, 220, staff: 2));
        facts.Add(Notehead("right", 200, 220, staff: 2));
        facts.Add(Pitch("left", "F4", staff: 2));
        facts.Add(Pitch("right", "F4", staff: 2));
        facts.Add(new VoiceFact(
            1, 2, VoiceTargetKind.Notehead, "left", 1, false,
            100, 220, 0.99, "test", ["left"]));
        facts.Add(new VoiceFact(
            1, 2, VoiceTargetKind.Notehead, "right", 1, false,
            200, 220, 0.99, "test", ["right"]));

        new TieOwnershipRecoveryPass().Run(document, facts);

        var tie = Assert.Single(facts.OfType<TieFact>());
        Assert.Equal(2, tie.Staff);
        Assert.Equal("F4", tie.Pitch);
        Assert.Equal("left", tie.FromNoteheadId);
        Assert.Equal("right", tie.ToNoteheadId);
        Assert.Contains("ownership-mismatch=True", tie.Reason);
    }

    private static DurationFact Duration(
        string noteheadId,
        string stemId,
        string baseDuration,
        string effectiveDuration,
        int dots) =>
        new(
            1,
            noteheadId.StartsWith("top", StringComparison.Ordinal) || noteheadId.StartsWith("bottom", StringComparison.Ordinal) ? 2 : 1,
            noteheadId,
            stemId,
            baseDuration,
            effectiveDuration,
            baseDuration == "1/2" ? "half" : baseDuration == "1/8" ? "eighth" : "quarter",
            dots,
            baseDuration == "1/8" ? 1 : 0,
            null,
            null,
            0.98,
            "test duration",
            [noteheadId, stemId]);

    private static NoteheadFact Notehead(
        string id,
        double x,
        double y,
        int staff = 1) =>
        new(
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

    private static PitchFact Pitch(
        string id,
        string pitch,
        int staff = 1)
    {
        var step = pitch[0].ToString();
        var octave = int.Parse(pitch[^1].ToString());
        var alter = pitch.Contains('b') ? -1 : pitch.Contains('#') ? 1 : 0;
        return new PitchFact(
            1,
            staff,
            id,
            step,
            octave,
            alter,
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
            [id]);
    }

    private static VoiceFact Voice(
        VoiceTargetKind kind,
        string id,
        int localVoice,
        double x,
        double y) =>
        new(
            1,
            1,
            kind,
            id,
            localVoice,
            true,
            x,
            y,
            0.99,
            "test voice",
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

    private static CurveElement Curve(
        string id,
        IReadOnlyList<PointD> points)
    {
        var ownership = new LogicalOwnership(
            new LogicalCoordinate("staff-1", "measure-1"),
            new LogicalCoordinate("staff-1", "measure-1"),
            1,
            null,
            0,
            "test");
        var source = new CurvedStroke(
            id,
            points,
            Enumerable.Repeat(1.0, points.Count).ToArray(),
            0.2,
            1.0,
            null,
            "path",
            null,
            ownership);
        return new CurveElement
        {
            ShapeId = id,
            Bounds = BoundsD.FromPoints(points),
            Ownership = ownership,
            Source = source
        };
    }
}
