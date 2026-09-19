using SvgMusic.Scene;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class ScoreLayoutAnalyzerTests
{
    [Fact]
    public void MissingTopLineOfLowerStaff_IsRecoveredFromBarlineEndpoints()
    {
        var strokes = new List<Stroke>();

        for (var line = 0; line < 5; line++)
        {
            var y = line * 10.0;
            strokes.Add(new Stroke(
                $"upper-line-{line}",
                new PointD(0, y),
                new PointD(200, y),
                1,
                "test",
                null));
        }

        // Cairo can absorb the top line of the lower staff into connected
        // barline geometry. Only the remaining four long horizontals survive.
        for (var line = 1; line < 5; line++)
        {
            var y = 100 + line * 10.0;
            strokes.Add(new Stroke(
                $"lower-line-{line}",
                new PointD(0, y),
                new PointD(200, y),
                1,
                "test",
                null));
        }

        AddMuseScoreBarline(strokes, "left", 0);
        AddMuseScoreBarline(strokes, "middle", 100);
        AddMuseScoreBarline(strokes, "right", 200);

        var notation = new NotationScene(
            [],
            [],
            strokes,
            [],
            []);
        var layout = new ScoreLayoutAnalyzer().Analyze(notation);

        Assert.Equal(2, layout.Staffs.Count);
        var lower = layout.Staffs[1];
        Assert.Equal(100, lower.Bounds.MinY, 6);
        Assert.Equal(140, lower.Bounds.MaxY, 6);

        var pair = Assert.Single(Assert.Single(layout.Systems).StaffPairs);
        Assert.Equal(3, pair.Boundaries.Count);
        Assert.Equal(2, pair.Measures.Count);
    }

    [Fact]
    public void SegmentedStaffLines_AreMergedIntoLogicalStaffs()
    {
        var strokes = new List<Stroke>();

        foreach (var (prefix, top) in new[]
                 {
                     ("upper", 0.0),
                     ("lower", 100.0)
                 })
        {
            for (var line = 0; line < 5; line++)
            {
                var y = top + line * 10;
                var boundaries = new[] { 0.0, 55.0, 105.0, 155.0, 200.0 };

                for (var segment = 0; segment < boundaries.Length - 1; segment++)
                {
                    strokes.Add(new Stroke(
                        $"{prefix}-line-{line}-segment-{segment}",
                        new PointD(boundaries[segment], y),
                        new PointD(boundaries[segment + 1], y),
                        1,
                        "test",
                        null));
                }
            }
        }

        AddMuseScoreBarline(strokes, "left", 0);
        AddMuseScoreBarline(strokes, "middle", 100);
        AddMuseScoreBarline(strokes, "right", 200);

        var notation = new NotationScene(
            [],
            [],
            strokes,
            [],
            []);
        var layout = new ScoreLayoutAnalyzer().Analyze(notation);

        Assert.Equal(2, layout.Staffs.Count);
        var pair = Assert.Single(Assert.Single(layout.Systems).StaffPairs);
        Assert.Equal(3, pair.Boundaries.Count);
        Assert.Equal(2, pair.Measures.Count);
        Assert.All(layout.Staffs, staff =>
            Assert.Equal(200, staff.Bounds.Width, 6));
    }

    [Fact]
    public void AlignedStemLikeVerticals_DoNotBecomeMeasureBoundaries()
    {
        var strokes = StaffLines();

        AddMuseScoreBarline(strokes, "left", 0);
        AddMuseScoreBarline(strokes, "middle", 100);
        AddMuseScoreBarline(strokes, "right", 200);

        // Both strokes cover most of their respective staves, so the old
        // SpansStaff rule accepted them as a false piano barline pair.
        strokes.Add(Vertical("upper-stem", 50, 4, 50));
        strokes.Add(Vertical("lower-stem", 50, 88, 140));

        var pair = Pair(strokes);

        Assert.Equal(3, pair.Boundaries.Count);
        Assert.Equal(2, pair.Measures.Count);
        Assert.DoesNotContain(
            pair.Boundaries,
            boundary => boundary.StrokeIds.Contains("upper-stem"));
    }

    [Fact]
    public void MuseScoreUpperConnectorAndLowerSegment_AreRecognized()
    {
        var strokes = StaffLines();

        AddMuseScoreBarline(strokes, "left", 0);
        AddMuseScoreBarline(strokes, "middle", 100);
        AddMuseScoreBarline(strokes, "right", 200);

        var pair = Pair(strokes);

        Assert.Equal(3, pair.Boundaries.Count);
        Assert.Equal(2, pair.Measures.Count);
        Assert.All(pair.Boundaries, boundary => Assert.Equal(2, boundary.StrokeIds.Count));
    }

    [Fact]
    public void NearbyDoubleBarlineStrokes_CollapseToOneLogicalBoundary()
    {
        var strokes = StaffLines();

        AddMuseScoreBarline(strokes, "left", 0);
        AddMuseScoreBarline(strokes, "middle", 100);
        AddMuseScoreBarline(strokes, "final-thin", 194);
        AddMuseScoreBarline(strokes, "final-thick", 200);

        var pair = Pair(strokes);

        Assert.Equal(3, pair.Boundaries.Count);
        Assert.Equal(2, pair.Measures.Count);

        var final = pair.Boundaries.OrderBy(boundary => boundary.X).Last();
        Assert.Contains("final-thin-upper", final.StrokeIds);
        Assert.Contains("final-thick-upper", final.StrokeIds);
    }

    [Fact]
    public void HeavyLightFinalBarline_CollapsesAcrossWiderGapAndMarksFinal()
    {
        var strokes = StaffLines();

        AddMuseScoreBarline(strokes, "left", 0);
        AddMuseScoreBarline(strokes, "middle", 100);
        AddMuseScoreBarline(strokes, "final-thin", 188, width: 1);
        AddMuseScoreBarline(strokes, "final-thick", 200, width: 3);

        var pair = Pair(strokes);

        Assert.Equal(3, pair.Boundaries.Count);
        Assert.Equal(2, pair.Measures.Count);

        var final = pair.Boundaries.OrderBy(boundary => boundary.X).Last();
        Assert.True(final.IsFinal);
        Assert.Equal(200, final.X);
        Assert.Equal(1, final.MinStrokeWidth);
        Assert.Equal(3, final.MaxStrokeWidth);
    }

    private static StaffPairLayout Pair(List<Stroke> strokes)
    {
        var notation = new NotationScene(
            [],
            [],
            strokes,
            [],
            []);
        var layout = new ScoreLayoutAnalyzer().Analyze(notation);

        return Assert.Single(Assert.Single(layout.Systems).StaffPairs);
    }

    private static List<Stroke> StaffLines()
    {
        var result = new List<Stroke>();

        foreach (var (prefix, top) in new[]
                 {
                     ("upper", 0.0),
                     ("lower", 100.0)
                 })
        {
            for (var line = 0; line < 5; line++)
            {
                var y = top + line * 10;
                result.Add(new Stroke(
                    $"{prefix}-line-{line}",
                    new PointD(0, y),
                    new PointD(200, y),
                    1,
                    "test",
                    null));
            }
        }

        return result;
    }

    private static void AddMuseScoreBarline(
        ICollection<Stroke> strokes,
        string id,
        double x,
        double width = 1)
    {
        // Matches the physical split used by the sample SVG: the upper piece
        // continues through the inter-staff gap to the top line of the lower
        // staff; the lower piece spans the lower staff itself.
        strokes.Add(Vertical($"{id}-upper", x, 0, 100, width));
        strokes.Add(Vertical($"{id}-lower", x, 100, 140, width));
    }

    private static Stroke Vertical(
        string id,
        double x,
        double y1,
        double y2,
        double width = 1)
    {
        return new Stroke(
            id,
            new PointD(x, y1),
            new PointD(x, y2),
            width,
            "test",
            null);
    }
}
