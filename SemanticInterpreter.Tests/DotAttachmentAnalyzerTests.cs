using Xunit;
using SvgMusic.Scene;
using SvgMusic.Semantics;

namespace SemanticInterpreter.Tests;

public sealed class DotAttachmentAnalyzerTests
{
    [Fact]
    public void StackedDots_AreMatchedMonotonicallyAcrossWholeNoteheadColumn()
    {
        const int measure = 12;
        const int staff = 1;
        const double spacing = 10.0;

        var facts = new SemanticFacts();
        facts.Add(Notehead("n-top", measure, staff, 100, 100, 0, "hollow", spacing));
        facts.Add(Notehead("n-middle", measure, staff, 100, 110, 2, "hollow", spacing));
        facts.Add(Notehead("n-bottom", measure, staff, 100, 120, 4, "hollow", spacing));

        var document = Document(
            measure,
            spacing,
            staff,
            Dot("d-top", 118, 105, measure, staff, spacing),
            Dot("d-middle", 118, 115, measure, staff, spacing),
            Dot("d-bottom", 118, 125, measure, staff, spacing));

        var result = new DotAttachmentAnalyzer().Analyze(document, facts);

        Assert.Equal("n-top", Target(result, "d-top"));
        Assert.Equal("n-middle", Target(result, "d-middle"));
        Assert.Equal("n-bottom", Target(result, "d-bottom"));
        Assert.All(result.Accepted, decision =>
            Assert.Contains("global monotonic dot-column match", decision.Reason));
    }

    [Fact]
    public void HorizontalDoubleDot_RemainsFallbackAndBothDotsMayTargetSameNote()
    {
        const int measure = 1;
        const int staff = 1;
        const double spacing = 20.0;

        var facts = new SemanticFacts();
        facts.Add(Notehead("n1", measure, staff, 100, 100, 1, "filled", spacing, radius: 10));

        var document = Document(
            measure,
            spacing,
            staff,
            Dot("d1", 120, 100, measure, staff, spacing, radius: 4),
            Dot("d2", 130, 100, measure, staff, spacing, radius: 4));

        var result = new DotAttachmentAnalyzer().Analyze(document, facts);

        Assert.Equal(2, result.Accepted.Count);
        Assert.Equal("n1", Target(result, "d1"));
        Assert.Equal("n1", Target(result, "d2"));
        Assert.All(result.Accepted, decision =>
            Assert.DoesNotContain("global monotonic dot-column match", decision.Reason));
    }

    [Fact]
    public void NoteheadColumns_UseTransitiveXOverlapButKeepFillKindsSeparate()
    {
        const double spacing = 10.0;
        var helper = new NoteheadColumnHelper();
        var noteheads = new[]
        {
            Notehead("f1", 1, 1, 100, 100, 1, "filled", spacing, radius: 5),
            Notehead("f2", 1, 1, 104, 110, 3, "filled", spacing, radius: 5),
            Notehead("f3", 1, 1, 108, 120, 5, "filled", spacing, radius: 5),
            Notehead("h1", 1, 1, 104, 130, 7, "hollow", spacing, radius: 5)
        };

        var columns = helper.Build(noteheads);

        Assert.Equal(2, columns.Count);
        var filled = Assert.Single(columns, column => column.FillKind == "filled");
        Assert.Equal(new[] { "f1", "f2", "f3" }, filled.Noteheads.Select(note => note.ShapeId));
        var hollow = Assert.Single(columns, column => column.FillKind == "hollow");
        Assert.Equal("h1", Assert.Single(hollow.Noteheads).ShapeId);
    }

    [Fact]
    public void DotColumns_DoNotMergeHorizontalDoubleDots()
    {
        const double spacing = 20.0;
        var helper = new DotColumnHelper();
        var dots = new[]
        {
            Candidate("d1", 120, 100, 1, 1, spacing, radius: 4),
            Candidate("d2", 130, 100, 1, 1, spacing, radius: 4)
        };

        var columns = helper.Build(dots);

        Assert.Equal(2, columns.Count);
        Assert.All(columns, column => Assert.Single(column.Dots));
    }

    private static string Target(
        DotAnalysisResult result,
        string dotId)
    {
        return Assert.Single(result.Accepted, decision =>
                decision.Candidate.Ellipse.ShapeId == dotId)
            .Match!
            .Notehead
            .ShapeId;
    }

    private static NoteheadFact Notehead(
        string id,
        int measure,
        int staff,
        double x,
        double y,
        int staffStep,
        string fillKind,
        double spacing,
        double radius = 5)
    {
        return new NoteheadFact(
            measure,
            staff,
            id,
            x,
            y,
            radius,
            radius * 0.75,
            fillKind,
            radius * 2 / spacing,
            staffStep,
            0,
            1,
            "test",
            [id]);
    }

    private static SemanticDocument Document(
        int measure,
        double spacing,
        int staff,
        params EllipseElement[] dots)
    {
        var upper = new StaffMeasureScene(
            staff,
            "staff-1",
            new BoundsD(0, 80, 300, 160),
            spacing,
            dots);
        var lower = new StaffMeasureScene(
            2,
            "staff-2",
            new BoundsD(0, 200, 300, 280),
            spacing,
            Array.Empty<SemanticElement>());

        return new SemanticDocument(
        [
            new MeasureScene(
                measure,
                "system-1",
                "pair-1",
                $"measure-{measure}",
                0,
                300,
                false,
                upper,
                lower)
        ]);
    }

    private static DotCandidate Candidate(
        string id,
        double x,
        double y,
        int measure,
        int staff,
        double spacing,
        double radius = 2)
    {
        var ellipse = Dot(id, x, y, measure, staff, spacing, radius);
        return new DotCandidate(
            measure,
            staff,
            ellipse,
            spacing,
            2 * radius / spacing);
    }

    private static EllipseElement Dot(
        string id,
        double x,
        double y,
        int measure,
        int staff,
        double spacing,
        double radius = 2)
    {
        var ownership = new LogicalOwnership(
            new LogicalCoordinate($"staff-{staff}", $"measure-{measure}"),
            new LogicalCoordinate($"staff-{staff}", $"measure-{measure}"),
            1,
            null,
            0,
            "test");
        var source = new EllipseLike(
            id,
            new PointD(x, y),
            radius,
            radius,
            0,
            false,
            null,
            0,
            "test",
            null,
            ownership);

        return new EllipseElement
        {
            ShapeId = id,
            Bounds = new BoundsD(
                x - radius,
                y - radius,
                x + radius,
                y + radius),
            Ownership = ownership,
            Source = source
        };
    }
}
