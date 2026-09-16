using SvgMusic.Scene;
using SvgMusic.Semantics;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class ArpeggioPassTests
{
    [Fact]
    public void CrossStaffZigZag_AttachesSameOnsetChordsExactlyOnce()
    {
        var zigZag = ZigZag(
            minX: 20,
            maxX: 24,
            minY: 105,
            maxY: 245);
        var document = Document(zigZag);
        var facts = new SemanticFacts();

        AddChord(
            facts,
            "upper-chord",
            staff: 1,
            at: "0",
            x: 32,
            ys: [115, 125, 135]);
        AddChord(
            facts,
            "lower-chord",
            staff: 2,
            at: "0",
            x: 32,
            ys: [215, 225, 235]);

        // A simultaneous melodic singleton is deliberately not represented by a
        // ChordFact and therefore must never become part of the arpeggio relation.
        facts.Add(Notehead(
            "melody",
            staff: 1,
            x: 32,
            y: 90));

        var pass = new ArpeggioPass();
        pass.Run(document, facts);

        var arpeggio = Assert.Single(facts.OfType<ArpeggioFact>());
        Assert.Equal(1, arpeggio.MeasureNumber);
        Assert.Equal("zigzag", arpeggio.ZigZagShapeId);
        Assert.Equal("0", arpeggio.At);
        Assert.Equal(
            ["lower-chord", "upper-chord"],
            arpeggio.ChordIds.OrderBy(id => id).ToArray());
        Assert.Equal([1, 2], arpeggio.Staffs);
        Assert.Equal(6, arpeggio.NoteheadIds.Count);
        Assert.DoesNotContain("melody", arpeggio.NoteheadIds);
        Assert.Equal(["raw-zig-1", "raw-zig-2"], arpeggio.SourceShapeIds);
        Assert.Null(arpeggio.Direction);
        Assert.Single(pass.LastAnalysis!.Accepted);
    }

    [Fact]
    public void NearbyChordsWithDifferentOnsets_AreRejectedAsAmbiguous()
    {
        var zigZag = ZigZag(20, 24, 105, 245);
        var document = Document(zigZag);
        var facts = new SemanticFacts();

        AddChord(
            facts,
            "upper-chord",
            staff: 1,
            at: "0",
            x: 32,
            ys: [115, 125, 135]);
        AddChord(
            facts,
            "lower-later",
            staff: 2,
            at: "1/4",
            x: 32,
            ys: [215, 225, 235]);

        var pass = new ArpeggioPass();
        pass.Run(document, facts);

        Assert.Empty(facts.OfType<ArpeggioFact>());
        var decision = Assert.Single(pass.LastAnalysis!.Decisions);
        Assert.False(decision.Accepted);
        Assert.Equal("conflicting-onsets", decision.Decision);
    }

    [Fact]
    public void DistantZigZag_IsRejected()
    {
        var zigZag = ZigZag(0, 2, 105, 145);
        var document = Document(zigZag);
        var facts = new SemanticFacts();

        AddChord(
            facts,
            "far-chord",
            staff: 1,
            at: "0",
            x: 80,
            ys: [115, 125, 135]);

        var pass = new ArpeggioPass();
        pass.Run(document, facts);

        Assert.Empty(facts.OfType<ArpeggioFact>());
        var decision = Assert.Single(pass.LastAnalysis!.Decisions);
        Assert.False(decision.Accepted);
        Assert.Equal("no-chord-anchor", decision.Decision);
    }

    private static SemanticDocument Document(
        VerticalZigZagElement zigZag) =>
        new([
            new MeasureScene(
                1,
                "system-1",
                "pair-1",
                "measure-1",
                0,
                120,
                false,
                new StaffMeasureScene(
                    1,
                    "upper",
                    new BoundsD(0, 100, 120, 140),
                    10,
                    [zigZag]),
                new StaffMeasureScene(
                    2,
                    "lower",
                    new BoundsD(0, 210, 120, 250),
                    10,
                    [zigZag]))
        ]);

    private static VerticalZigZagElement ZigZag(
        double minX,
        double maxX,
        double minY,
        double maxY)
    {
        var ownership = new LogicalOwnership(
            new LogicalCoordinate("upper", "measure-1"),
            new LogicalCoordinate("lower", "measure-1"),
            1,
            null,
            0,
            "test");
        var primitive = new VerticalZigZagPrimitive(
            "zigzag",
            new BoundsD(minX, minY, maxX, maxY),
            12,
            3,
            10,
            0.98,
            "test",
            null,
            ownership)
        {
            SourceShapeIds = ["raw-zig-1", "raw-zig-2"]
        };

        return new VerticalZigZagElement
        {
            ShapeId = primitive.ShapeId,
            Bounds = primitive.Bounds,
            Ownership = ownership,
            Source = primitive
        };
    }

    private static void AddChord(
        SemanticFacts facts,
        string chordId,
        int staff,
        string at,
        double x,
        IReadOnlyList<double> ys)
    {
        var ids = new List<string>();
        for (var i = 0; i < ys.Count; i++)
        {
            var id = $"{chordId}-n{i + 1}";
            ids.Add(id);
            facts.Add(Notehead(id, staff, x, ys[i]));
        }

        facts.Add(new ChordFact(
            1,
            chordId,
            [staff],
            ids,
            null,
            "filled",
            "quarter",
            x,
            0.96,
            "test chord",
            ids));
        facts.Add(new OnsetFact(
            1,
            staff,
            VoiceTargetKind.Chord,
            chordId,
            1,
            at,
            x,
            0.97,
            "test onset",
            ids));
    }

    private static NoteheadFact Notehead(
        string id,
        int staff,
        double x,
        double y) =>
        new(
            1,
            staff,
            id,
            x,
            y,
            3,
            2,
            "filled",
            1,
            0,
            0,
            0.99,
            "test notehead",
            [id]);
}
