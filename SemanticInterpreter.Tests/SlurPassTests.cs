using SvgMusic.Scene;
using SvgMusic.Semantics;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class SlurPassTests
{
    [Fact]
    public void Slur_AttachesThroughStemTips_WhenNoteheadsAreFarFromCurveEndpoints()
    {
        const double spacing = 20;
        var document = Document(
            Curve(
                "curve",
                [
                    new PointD(100, 100),
                    new PointD(150, 70),
                    new PointD(200, 100)
                ],
                spacing));
        var facts = new SemanticFacts();
        facts.Add(Notehead("left", x: 100, y: 180, pitchStep: 4, spacing));
        facts.Add(Notehead("right", x: 200, y: 180, pitchStep: 0, spacing));
        facts.Add(Pitch("left", "C4"));
        facts.Add(Pitch("right", "E4"));
        facts.Add(Stem("s-left", "left", 100, 100, 100, 180));
        facts.Add(Stem("s-right", "right", 200, 100, 200, 180));

        var pass = new SlurPass();
        pass.Run(document, facts);

        var slur = Assert.Single(facts.OfType<SlurFact>());
        Assert.Equal("left", slur.FromNoteheadId);
        Assert.Equal("right", slur.ToNoteheadId);
        Assert.Equal("above", slur.Placement);
        Assert.True(slur.StartDistanceInSpacings < 0.05);
        Assert.True(slur.EndDistanceInSpacings < 0.05);
    }

    [Fact]
    public void SamePitchArc_IsReservedForTiePass()
    {
        const double spacing = 20;
        var document = Document(
            Curve(
                "tie",
                [
                    new PointD(100, 160),
                    new PointD(150, 145),
                    new PointD(200, 160)
                ],
                spacing));
        var facts = new SemanticFacts();
        facts.Add(Notehead("left", 100, 160, 2, spacing));
        facts.Add(Notehead("right", 200, 160, 2, spacing));
        facts.Add(Pitch("left", "D4"));
        facts.Add(Pitch("right", "D4"));

        var pass = new SlurPass();
        pass.Run(document, facts);

        Assert.Empty(facts.OfType<SlurFact>());
        var decision = Assert.Single(pass.LastAnalysis!.Decisions);
        Assert.Equal("tie-like", decision.Decision);
        Assert.False(decision.Accepted);
    }

    [Fact]
    public void SamePitchArc_WithInterveningRhythmicEvent_IsSlurNotTie()
    {
        const double spacing = 20;
        var document = Document(
            Curve(
                "phrase-arc",
                [
                    new PointD(100, 160),
                    new PointD(170, 125),
                    new PointD(240, 160)
                ],
                spacing));
        var facts = new SemanticFacts();
        facts.Add(Notehead("left", 100, 160, 2, spacing));
        facts.Add(Notehead("middle", 170, 150, 3, spacing));
        facts.Add(Notehead("right", 240, 160, 2, spacing));
        facts.Add(Pitch("left", "D4"));
        facts.Add(Pitch("middle", "E4"));
        facts.Add(Pitch("right", "D4"));
        facts.Add(Duration("left", "1/4"));
        facts.Add(Duration("middle", "1/4"));
        facts.Add(Duration("right", "1/4"));
        facts.Add(Onset("left", "0", 100));
        facts.Add(Onset("middle", "1/4", 170));
        facts.Add(Onset("right", "1/2", 240));

        var pass = new SlurPass();
        pass.Run(document, facts);

        var slur = Assert.Single(facts.OfType<SlurFact>());
        Assert.Equal("left", slur.FromNoteheadId);
        Assert.Equal("right", slur.ToNoteheadId);
        Assert.Equal("slur", Assert.Single(pass.LastAnalysis!.Decisions).Decision);
    }

    [Fact]
    public void VerticalParenthesisLikeCurve_IsRejectedBeforeEndpointMatching()
    {
        const double spacing = 20;
        var document = Document(
            Curve(
                "paren",
                [
                    new PointD(100, 100),
                    new PointD(108, 150),
                    new PointD(102, 200)
                ],
                spacing));
        var facts = new SemanticFacts();
        facts.Add(Notehead("n1", 100, 100, 0, spacing));
        facts.Add(Notehead("n2", 102, 200, 4, spacing));
        facts.Add(Pitch("n1", "C4"));
        facts.Add(Pitch("n2", "G4"));

        var pass = new SlurPass();
        pass.Run(document, facts);

        Assert.Empty(facts.OfType<SlurFact>());
        Assert.Equal(
            "non-horizontal-arch",
            Assert.Single(pass.LastAnalysis!.Decisions).Decision);
    }

    [Fact]
    public void DuplicateCurveObservation_IsEvaluatedOnce()
    {
        const double spacing = 20;
        var curve = Curve(
            "curve",
            [
                new PointD(100, 160),
                new PointD(150, 140),
                new PointD(200, 160)
            ],
            spacing);
        var document = new SemanticDocument(
        [
            Measure(1, spacing, curve),
            Measure(2, spacing, curve)
        ]);
        var facts = new SemanticFacts();
        facts.Add(Notehead("left", 100, 160, 0, spacing, measure: 1));
        facts.Add(Notehead("right", 200, 160, 2, spacing, measure: 2));
        facts.Add(Pitch("left", "C4", measure: 1));
        facts.Add(Pitch("right", "E4", measure: 2));

        var pass = new SlurPass();
        pass.Run(document, facts);

        Assert.Single(pass.LastAnalysis!.Decisions);
        Assert.Single(facts.OfType<SlurFact>());
    }

    private static SemanticDocument Document(CurveElement curve) =>
        new([Measure(1, 20, curve)]);

    private static MeasureScene Measure(
        int number,
        double spacing,
        params SemanticElement[] elements)
    {
        return new MeasureScene(
            number,
            "system-1",
            "pair-1",
            $"measure-{number}",
            0,
            300,
            false,
            new StaffMeasureScene(
                1,
                "staff-1",
                new BoundsD(0, 100, 300, 180),
                spacing,
                elements),
            new StaffMeasureScene(
                2,
                "staff-2",
                new BoundsD(0, 220, 300, 300),
                spacing,
                Array.Empty<SemanticElement>()));
    }

    private static CurveElement Curve(
        string id,
        IReadOnlyList<PointD> points,
        double spacing)
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
            Enumerable.Repeat(spacing * 0.08, points.Count).ToArray(),
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

    private static NoteheadFact Notehead(
        string id,
        double x,
        double y,
        int pitchStep,
        double spacing,
        int measure = 1)
    {
        return new NoteheadFact(
            measure,
            1,
            id,
            x,
            y,
            spacing * 0.45,
            spacing * 0.32,
            "filled",
            0.9,
            pitchStep,
            0,
            0.99,
            "test",
            [id]);
    }

    private static PitchFact Pitch(
        string noteheadId,
        string pitch,
        int measure = 1)
    {
        var step = pitch[0].ToString();
        var octave = int.Parse(pitch[^1].ToString());
        return new PitchFact(
            measure,
            1,
            noteheadId,
            step,
            octave,
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
            "test",
            [noteheadId]);
    }

    private static DurationFact Duration(
        string noteheadId,
        string duration)
    {
        return new DurationFact(
            1,
            1,
            noteheadId,
            null,
            duration,
            duration,
            "quarter",
            0,
            0,
            null,
            null,
            0.99,
            "test duration",
            [noteheadId]);
    }

    private static OnsetFact Onset(
        string noteheadId,
        string at,
        double x)
    {
        return new OnsetFact(
            1,
            1,
            VoiceTargetKind.Notehead,
            noteheadId,
            1,
            at,
            x,
            0.99,
            "test onset",
            [noteheadId]);
    }

    private static StemAttachmentFact Stem(
        string id,
        string noteheadId,
        double x1,
        double y1,
        double x2,
        double y2)
    {
        return new StemAttachmentFact(
            1,
            id,
            StemDirection.Up,
            [noteheadId],
            [1],
            false,
            x1,
            y1,
            x2,
            y2,
            4,
            0.1,
            0.99,
            "test",
            [id, noteheadId]);
    }
}
