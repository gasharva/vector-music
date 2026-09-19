using SvgMusic.Scene;
using SvgMusic.Semantics;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class CrossSystemCurvePassTests
{
    [Fact]
    public void YellowLeavesStyleSplitTie_IsReconstructedBeforeTieClassification()
    {
        var document = Document(
            breakBeforeSecond: true);
        var facts = Facts(
            secondPitch: "F4");

        new CrossSystemCurvePass().Run(
            document,
            facts);

        var fragments = facts
            .OfType<CrossSystemCurveFragmentFact>()
            .OrderBy(fragment => fragment.MeasureNumber)
            .ToArray();
        Assert.Equal(2, fragments.Length);
        Assert.Equal(
            CrossSystemCurveFragmentKind.ToSystemEnd,
            fragments[0].Kind);
        Assert.Equal(
            CrossSystemCurveFragmentKind.FromSystemStart,
            fragments[1].Kind);

        var reconstructed = Assert.Single(
            facts.OfType<CrossSystemCurveFact>());
        Assert.Equal(
            "curve-out+curve-in",
            reconstructed.CurveId);
        Assert.Equal(
            "note-1",
            reconstructed.FromNoteheadId);
        Assert.Equal(
            "note-2",
            reconstructed.ToNoteheadId);

        // The new system fragment begins ~4.5 staff spacings after the physical
        // left barline, just like the real Yellow Leaves SVG. That must still count
        // as the system-start notation band rather than an ordinary complete arc.
        var incoming = Assert.Single(
            fragments,
            fragment =>
                fragment.Kind
                    == CrossSystemCurveFragmentKind.FromSystemStart);
        Assert.InRange(
            incoming.EdgeDistanceInSpacings,
            4.0,
            5.0);

        new SlurPass().Run(
            document,
            facts);
        new TiePass().Run(
            document,
            facts);

        Assert.Empty(
            facts.OfType<SlurFact>());

        var tie = Assert.Single(
            facts.OfType<TieFact>());

        Assert.Equal(1, tie.StartMeasureNumber);
        Assert.Equal(2, tie.EndMeasureNumber);
        Assert.Equal("F4", tie.Pitch);
        Assert.Equal("note-1", tie.FromNoteheadId);
        Assert.Equal("note-2", tie.ToNoteheadId);
        Assert.Contains(
            "curve-out",
            tie.SourceShapeIds);
        Assert.Contains(
            "curve-in",
            tie.SourceShapeIds);
    }

    [Fact]
    public void SplitCurveWithDifferentPitches_IsReconstructedAsSlur()
    {
        var document = Document(
            breakBeforeSecond: true);
        var facts = Facts(
            secondPitch: "G4");

        new CrossSystemCurvePass().Run(
            document,
            facts);
        new SlurPass().Run(
            document,
            facts);
        new TiePass().Run(
            document,
            facts);

        var slur = Assert.Single(
            facts.OfType<SlurFact>());

        Assert.Equal(1, slur.StartMeasureNumber);
        Assert.Equal(2, slur.EndMeasureNumber);
        Assert.Equal("note-1", slur.FromNoteheadId);
        Assert.Equal("note-2", slur.ToNoteheadId);
        Assert.Contains(
            "curve-out",
            slur.SourceShapeIds);
        Assert.Contains(
            "curve-in",
            slur.SourceShapeIds);

        Assert.Empty(
            facts.OfType<TieFact>());
    }

    [Fact]
    public void SplitLookingFragmentsWithoutSystemBreak_AreNotReconstructed()
    {
        var document = Document(
            breakBeforeSecond: false);
        var facts = Facts(
            secondPitch: "F4");

        new CrossSystemCurvePass().Run(
            document,
            facts);

        Assert.Empty(
            facts.OfType<CrossSystemCurveFact>());
        Assert.Empty(
            facts.OfType<CrossSystemCurveFragmentFact>());
    }

    private static SemanticDocument Document(
        bool breakBeforeSecond)
    {
        const double spacing = 27.72;

        var firstOwnership = Ownership(
            "staff-5",
            "measure-12");
        var secondOwnership = Ownership(
            "staff-7",
            "measure-13");

        return new SemanticDocument(
        [
            new MeasureScene(
                1,
                "system-3",
                "pair-3",
                "measure-12",
                2238.06,
                2915.77,
                false,
                new StaffMeasureScene(
                    1,
                    "staff-5",
                    new BoundsD(
                        2238.06,
                        2221.67,
                        2915.77,
                        2521.12),
                    spacing,
                    [
                        Curve(
                            "curve-out",
                            firstOwnership,
                            [
                                new PointD(2296.38, 2340.56),
                                new PointD(2601.36, 2370.00),
                                new PointD(2906.35, 2340.56)
                            ])
                    ]),
                new StaffMeasureScene(
                    2,
                    "staff-6",
                    new BoundsD(
                        2238.06,
                        2519.59,
                        2915.77,
                        2633.52),
                    spacing,
                    [])),
            new MeasureScene(
                2,
                "system-4",
                "pair-4",
                "measure-13",
                144.227,
                1556.47,
                breakBeforeSecond,
                new StaffMeasureScene(
                    1,
                    "staff-7",
                    new BoundsD(
                        144.227,
                        3164.54,
                        1556.47,
                        3513.35),
                    spacing,
                    [
                        Curve(
                            "curve-in",
                            secondOwnership,
                            [
                                new PointD(270.232, 3283.43),
                                new PointD(298.146, 3296.00),
                                new PointD(326.060, 3283.43)
                            ])
                    ]),
                new StaffMeasureScene(
                    2,
                    "staff-8",
                    new BoundsD(
                        144.227,
                        3511.82,
                        1556.47,
                        3625.75),
                    spacing,
                    []))
        ]);
    }

    private static SemanticFacts Facts(
        string secondPitch)
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

        AddNote(
            facts,
            measure: 1,
            id: "note-1",
            x: 2294.52,
            y: 2320.16,
            pitch: "F4");
        AddNote(
            facts,
            measure: 2,
            id: "note-2",
            x: 329.75,
            y: 3263.03,
            pitch: secondPitch);

        return facts;
    }

    private static void AddNote(
        SemanticFacts facts,
        int measure,
        string id,
        double x,
        double y,
        string pitch)
    {
        facts.Add(new NoteheadFact(
            measure,
            1,
            id,
            x,
            y,
            15,
            11,
            "hollow",
            1,
            0,
            0,
            0.99,
            "test note",
            [id]));

        facts.Add(new PitchFact(
            measure,
            1,
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
            1,
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
            1,
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
            1,
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
