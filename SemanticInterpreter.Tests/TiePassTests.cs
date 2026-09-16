using SvgMusic.Canonical;
using SvgMusic.Scene;
using SvgMusic.Semantics;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class TiePassTests
{
    [Fact]
    public void SamePitchArc_BecomesTieFact()
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
        facts.Add(Notehead("left", 100, 160, spacing));
        facts.Add(Notehead("right", 200, 160, spacing));
        facts.Add(Pitch("left", "D4"));
        facts.Add(Pitch("right", "D4"));

        var pass = new TiePass();
        pass.Run(document, facts);

        var tie = Assert.Single(facts.OfType<TieFact>());
        Assert.Equal("tie", tie.CurveShapeId);
        Assert.Equal("left", tie.FromNoteheadId);
        Assert.Equal("right", tie.ToNoteheadId);
        Assert.Equal("D4", tie.Pitch);
        Assert.Equal("above", tie.Placement);
        Assert.True(tie.Confidence >= 0.60);
    }

    [Fact]
    public void DifferentPitchArc_IsNotTie()
    {
        const double spacing = 20;
        var document = Document(
            Curve(
                "slur",
                [
                    new PointD(100, 160),
                    new PointD(150, 145),
                    new PointD(200, 160)
                ],
                spacing));
        var facts = new SemanticFacts();
        facts.Add(Notehead("left", 100, 160, spacing));
        facts.Add(Notehead("right", 200, 160, spacing));
        facts.Add(Pitch("left", "D4"));
        facts.Add(Pitch("right", "F4"));

        var pass = new TiePass();
        pass.Run(document, facts);

        Assert.Empty(facts.OfType<TieFact>());
        Assert.Empty(pass.LastAnalysis!.Accepted);
    }

    [Fact]
    public void CurveAlreadyClaimedBySlur_IsNotAlsoTie()
    {
        const double spacing = 20;
        var document = Document(
            Curve(
                "curve",
                [
                    new PointD(100, 160),
                    new PointD(150, 145),
                    new PointD(200, 160)
                ],
                spacing));
        var facts = new SemanticFacts();
        facts.Add(Notehead("left", 100, 160, spacing));
        facts.Add(Notehead("right", 200, 160, spacing));
        facts.Add(Pitch("left", "D4"));
        facts.Add(Pitch("right", "D4"));
        facts.Add(new SlurFact(
            "curve",
            1,
            1,
            1,
            1,
            "left",
            "right",
            "above",
            0.1,
            0.1,
            0.9,
            "claimed for test",
            ["curve", "left", "right"]));

        var pass = new TiePass();
        pass.Run(document, facts);

        Assert.Empty(facts.OfType<TieFact>());
        Assert.Equal(
            "claimed-by-slur",
            Assert.Single(pass.LastAnalysis!.Decisions).Decision);
    }

    [Fact]
    public void TieFact_ProjectsToCanonicalAndMusicXmlTieMarkers()
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
        facts.Add(Notehead("left", 100, 160, spacing));
        facts.Add(Notehead("right", 200, 160, spacing));
        facts.Add(Pitch("left", "D4"));
        facts.Add(Pitch("right", "D4"));
        facts.Add(Duration("left"));
        facts.Add(Duration("right"));

        new TiePass().Run(document, facts);

        var canonical = new CanonicalNotationBuilder().Build(
            document,
            facts,
            "Tie test",
            null);

        var relation = Assert.Single(canonical.Relations.Ties);
        Assert.Equal("D4", relation.From.Note);
        Assert.Equal("D4", relation.To.Note);
        Assert.NotEqual(relation.From.Event, relation.To.Event);

        var xml = new MusicXmlWriter().Write(canonical);
        var notes = xml.Descendants("note").ToArray();
        Assert.Equal(2, notes.Length);
        Assert.Contains(notes[0].Elements("tie"), element => (string?)element.Attribute("type") == "start");
        Assert.Contains(notes[1].Elements("tie"), element => (string?)element.Attribute("type") == "stop");
        Assert.Contains(notes[0].Descendants("tied"), element => (string?)element.Attribute("type") == "start");
        Assert.Contains(notes[1].Descendants("tied"), element => (string?)element.Attribute("type") == "stop");
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
        double spacing)
    {
        return new NoteheadFact(
            1,
            1,
            id,
            x,
            y,
            spacing * 0.45,
            spacing * 0.32,
            "filled",
            0.9,
            0,
            0,
            0.99,
            "test",
            [id]);
    }

    private static PitchFact Pitch(
        string noteheadId,
        string pitch)
    {
        return new PitchFact(
            1,
            1,
            noteheadId,
            pitch[0].ToString(),
            int.Parse(pitch[^1].ToString()),
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

    private static DurationFact Duration(string noteheadId)
    {
        return new DurationFact(
            1,
            1,
            noteheadId,
            null,
            "1/4",
            "1/4",
            "quarter",
            0,
            0,
            null,
            null,
            0.99,
            "test",
            [noteheadId]);
    }
}
