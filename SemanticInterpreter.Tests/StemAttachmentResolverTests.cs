using SvgMusic.Scene;
using SvgMusic.Semantics;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class StemAttachmentResolverTests
{
    [Fact]
    public void CompetingOppositeStems_AssignEachNoteheadToBestLocalMatch()
    {
        var upper = Notehead(
            "upper",
            centerX: 100,
            centerY: 50);
        var lower = Notehead(
            "lower",
            centerX: 100,
            centerY: 56);

        var up = Decision(
            stemId: "up-stem",
            startY: 32,
            endY: 49,
            direction: StemDirection.Up,
            confidence: 0.90,
            matches:
            [
                Match(upper, score: 0.81, edge: 0.016)
            ]);

        var down = Decision(
            stemId: "down-stem",
            startY: 53,
            endY: 65,
            direction: StemDirection.Down,
            confidence: 0.86,
            matches:
            [
                Match(upper, score: 0.70, edge: 0.016),
                Match(lower, score: 0.53, edge: 0.169)
            ]);

        var resolver = new StemAttachmentResolver();
        var resolved = resolver.Resolve(
            new StemAnalysisResult(
                [up, down],
                TotalNoteheads: 2,
                AttachedNoteheads: 2));

        var resolvedUp = resolved.Accepted.Single(decision =>
            decision.Candidate.Stroke.ShapeId == "up-stem");
        var resolvedDown = resolved.Accepted.Single(decision =>
            decision.Candidate.Stroke.ShapeId == "down-stem");

        Assert.Equal(
            ["upper"],
            resolvedUp.Matches
                .Select(match => match.Notehead.ShapeId)
                .ToArray());
        Assert.Equal(
            ["lower"],
            resolvedDown.Matches
                .Select(match => match.Notehead.ShapeId)
                .ToArray());

        Assert.Equal(StemDirection.Up, resolvedUp.Direction);
        Assert.Equal(StemDirection.Down, resolvedDown.Direction);

        var conflict = Assert.Single(resolver.LastConflicts);
        Assert.Equal("upper", conflict.NoteheadId);
        Assert.Equal("up-stem", conflict.WinnerStemShapeId);
        Assert.Equal(2, conflict.Alternatives.Count);
    }

    [Fact]
    public void NoteheadMatchScore_WinsEvenWhenCompetingStemHasHigherOverallConfidence()
    {
        var notehead = Notehead(
            "note",
            centerX: 100,
            centerY: 50);

        var ownStem = Decision(
            stemId: "own-stem",
            startY: 32,
            endY: 49,
            direction: StemDirection.Up,
            confidence: 0.91,
            matches:
            [
                Match(notehead, score: 0.819, edge: 0.011)
            ]);

        // Mirrors the m19 Cairo failure: the foreign down-stem has excellent
        // overall confidence because it genuinely serves other heads, but its
        // local evidence for this particular notehead is weaker.
        var foreignStem = Decision(
            stemId: "foreign-stem",
            startY: 53,
            endY: 75,
            direction: StemDirection.Down,
            confidence: 0.973,
            matches:
            [
                Match(notehead, score: 0.714, edge: 0.011)
            ]);

        var resolved = new StemAttachmentResolver().Resolve(
            new StemAnalysisResult(
                [ownStem, foreignStem],
                TotalNoteheads: 1,
                AttachedNoteheads: 1));

        var accepted = Assert.Single(resolved.Accepted);

        Assert.Equal("own-stem", accepted.Candidate.Stroke.ShapeId);
        Assert.Equal("note", Assert.Single(accepted.Matches).Notehead.ShapeId);

        var rejected = Assert.Single(
            resolved.Decisions.Where(decision => !decision.Accepted));
        Assert.Equal("foreign-stem", rejected.Candidate.Stroke.ShapeId);
        Assert.Equal("lost-notehead-competition", rejected.Decision);
    }

    [Fact]
    public void NormalChord_KeepsMultipleNoteheadsOnOneStem()
    {
        var low = Notehead("low", 100, 56);
        var middle = Notehead("middle", 100, 50);
        var high = Notehead("high", 100, 44);

        var chordStem = Decision(
            stemId: "chord-stem",
            startY: 26,
            endY: 57,
            direction: StemDirection.Up,
            confidence: 0.95,
            matches:
            [
                Match(low, score: 0.76, edge: 0.03),
                Match(middle, score: 0.84, edge: 0.02),
                Match(high, score: 0.80, edge: 0.02)
            ]);

        var resolver = new StemAttachmentResolver();
        var resolved = resolver.Resolve(
            new StemAnalysisResult(
                [chordStem],
                TotalNoteheads: 3,
                AttachedNoteheads: 3));

        var accepted = Assert.Single(resolved.Accepted);

        Assert.Equal(
            ["low", "middle", "high"],
            accepted.Matches
                .Select(match => match.Notehead.ShapeId)
                .ToArray());
        Assert.Empty(resolver.LastConflicts);
        Assert.Equal(3, resolved.AttachedNoteheads);
    }

    private static StemDecision Decision(
        string stemId,
        double startY,
        double endY,
        StemDirection direction,
        double confidence,
        IReadOnlyList<StemNoteheadMatch> matches)
    {
        var source = new Stroke(
            stemId,
            new PointD(102.5, startY),
            new PointD(102.5, endY),
            1,
            "test",
            null);

        var ownership = new LogicalOwnership(
            new LogicalCoordinate("staff-1", "measure-1"),
            new LogicalCoordinate("staff-1", "measure-1"),
            1,
            null,
            0,
            "test");

        var element = new StrokeElement
        {
            ShapeId = stemId,
            Bounds = new BoundsD(
                102,
                Math.Min(startY, endY),
                103,
                Math.Max(startY, endY)),
            Ownership = ownership,
            Source = source
        };

        var candidate = new StemCandidate(
            1,
            element,
            LineSpacing: 5,
            Length: Math.Abs(endY - startY),
            VerticalRatio: 0,
            NormalizedWidth: 0.2,
            OwnershipSpansStaffs: false);

        return new StemDecision(
            candidate,
            Accepted: true,
            Decision: "attached-stem",
            direction,
            matches,
            IsCrossStaff: false,
            confidence,
            "test");
    }

    private static StemNoteheadMatch Match(
        NoteheadFact notehead,
        double score,
        double edge) =>
        new(
            notehead,
            EdgeDistance: edge * 5,
            EdgeDistanceInSpacings: edge,
            VerticalOverlapMargin: 1,
            Score: score);

    private static NoteheadFact Notehead(
        string id,
        double centerX,
        double centerY) =>
        new(
            MeasureNumber: 1,
            Staff: 1,
            ShapeId: id,
            CenterX: centerX,
            CenterY: centerY,
            MajorRadius: 2.8,
            MinorRadius: 2.3,
            FillKind: "hollow",
            NormalizedSize: 1.1,
            StaffStep: 0,
            StaffStepError: 0,
            Confidence: 0.95,
            Reason: "test",
            SourceShapeIds: [id]);
}
