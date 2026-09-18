using SvgMusic.Scene;
using SvgMusic.Semantics;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class CrossSystemTieRecoveryPassTests
{
    [Fact]
    public void SplitTieFragmentsAcrossSystemBreak_AreStitchedIntoOneTie()
    {
        var document = Document(
            breakBeforeSecond: true);
        var facts = Facts();

        new CrossSystemTieRecoveryPass().Run(
            document,
            facts);

        var tie = Assert.Single(
            facts.OfType<TieFact>());

        Assert.Equal(1, tie.StartMeasureNumber);
        Assert.Equal(2, tie.EndMeasureNumber);
        Assert.Equal(1, tie.Staff);
        Assert.Equal("F4", tie.Pitch);
        Assert.Equal("note-1", tie.FromNoteheadId);
        Assert.Equal("note-2", tie.ToNoteheadId);
        Assert.Contains(
            "curve-out",
            tie.SourceShapeIds);
        Assert.Contains(
            "curve-in",
            tie.SourceShapeIds);
        Assert.Contains(
            "cross-system tie",
            tie.Reason,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SameFragmentsWithoutSystemBreak_AreNotStitched()
    {
        var document = Document(
            breakBeforeSecond: false);
        var facts = Facts();

        new CrossSystemTieRecoveryPass().Run(
            document,
            facts);

        Assert.Empty(
            facts.OfType<TieFact>());
    }

    private static SemanticDocument Document(
        bool breakBeforeSecond)
    {
        var firstOwnership = Ownership(
            "staff-1",
            "measure-1");
        var secondOwnership = Ownership(
            "staff-3",
            "measure-2");

        return new SemanticDocument(
        [
            new MeasureScene(
                1,
                "system-1",
                "pair-1",
                "measure-1",
                0,
                100,
                false,
                new StaffMeasureScene(
                    1,
                    "staff-1",
                    new BoundsD(0, 80, 100, 120),
                    10,
                    [
                        Curve(
                            "curve-out",
                            firstOwnership,
                            [
                                new PointD(70, 95),
                                new PointD(85, 90),
                                new PointD(100, 95)
                            ])
                    ]),
                new StaffMeasureScene(
                    2,
                    "staff-2",
                    new BoundsD(0, 180, 100, 220),
                    10,
                    [])),
            new MeasureScene(
                2,
                "system-2",
                "pair-2",
                "measure-2",
                0,
                100,
                breakBeforeSecond,
                new StaffMeasureScene(
                    1,
                    "staff-3",
                    new BoundsD(0, 280, 100, 320),
                    10,
                    [
                        Curve(
                            "curve-in",
                            secondOwnership,
                            [
                                new PointD(0, 295),
                                new PointD(15, 290),
                                new PointD(30, 295)
                            ])
                    ]),
                new StaffMeasureScene(
                    2,
                    "staff-4",
                    new BoundsD(0, 380, 100, 420),
                    10,
                    []))
        ]);
    }

    private static SemanticFacts Facts()
    {
        var facts = new SemanticFacts();

        facts.Add(new TimeSignatureFact(
            1,
            3,
            4,
            0,
            10,
            "test 3/4",
            ["time"]));

        AddTiedNote(
            facts,
            measure: 1,
            staff: 1,
            id: "note-1",
            x: 70,
            y: 100,
            pitch: "F4");
        AddTiedNote(
            facts,
            measure: 2,
            staff: 1,
            id: "note-2",
            x: 30,
            y: 300,
            pitch: "F4");

        return facts;
    }

    private static void AddTiedNote(
        SemanticFacts facts,
        int measure,
        int staff,
        string id,
        double x,
        double y,
        string pitch)
    {
        facts.Add(new NoteheadFact(
            measure,
            staff,
            id,
            x,
            y,
            5,
            4,
            "hollow",
            1,
            0,
            0,
            0.99,
            "test note",
            [id]));

        facts.Add(new PitchFact(
            measure,
            staff,
            id,
            pitch[..1],
            4,
            0,
            pitch,
            0,
            "G",
            2,
            "clef",
            0,
            null,
            null,
            false,
            0.99,
            "test pitch",
            [id]));

        facts.Add(new DurationFact(
            measure,
            staff,
            id,
            null,
            "1/2",
            "3/4",
            "half",
            1,
            0,
            null,
            null,
            0.99,
            "test dotted half",
            [id]));

        facts.Add(new VoiceFact(
            measure,
            staff,
            VoiceTargetKind.Notehead,
            id,
            1,
            true,
            x,
            y,
            0.99,
            "test voice",
            [id]));

        facts.Add(new OnsetFact(
            measure,
            staff,
            VoiceTargetKind.Notehead,
            id,
            1,
            "0",
            x,
            0.99,
            "test onset",
            [id]));
    }

    private static CurveElement Curve(
        string id,
        LogicalOwnership ownership,
        IReadOnlyList<PointD> centerline)
    {
        var source = new CurvedStroke(
            id,
            centerline,
            centerline.Select(_ => 1.0).ToArray(),
            0.1,
            1.0,
            null,
            "test",
            null,
            ownership);

        return new CurveElement
        {
            ShapeId = id,
            Bounds = new BoundsD(
                centerline.Min(point => point.X),
                centerline.Min(point => point.Y),
                centerline.Max(point => point.X),
                centerline.Max(point => point.Y)),
            Ownership = ownership,
            Source = source
        };
    }

    private static LogicalOwnership Ownership(
        string staffId,
        string measureId) =>
        new(
            new LogicalCoordinate(
                staffId,
                measureId),
            new LogicalCoordinate(
                staffId,
                measureId),
            1,
            null,
            0,
            "test");
}
