using SvgMusic.Scene;

namespace SvgMusic.Semantics;

public enum StemDirection
{
    Up,
    Down,
    Ambiguous
}

public sealed record StemCandidate(
    int MeasureNumber,
    StrokeElement Stroke,
    double LineSpacing,
    double Length,
    double VerticalRatio,
    double NormalizedWidth,
    bool OwnershipSpansStaffs);

public sealed record StemNoteheadMatch(
    NoteheadFact Notehead,
    double EdgeDistance,
    double EdgeDistanceInSpacings,
    double VerticalOverlapMargin,
    double Score);

public sealed record StemDecision(
    StemCandidate Candidate,
    bool Accepted,
    string Decision,
    StemDirection Direction,
    IReadOnlyList<StemNoteheadMatch> Matches,
    bool IsCrossStaff,
    double Confidence,
    string Reason);

public sealed record StemAnalysisResult(
    IReadOnlyList<StemDecision> Decisions,
    int TotalNoteheads,
    int AttachedNoteheads)
{
    public IReadOnlyList<StemDecision> Accepted =>
        Decisions.Where(decision => decision.Accepted).ToArray();
}

public sealed class StemAttachmentAnalyzer
{
    private const double MaximumVerticalRatio = 0.18;
    private const double MinimumLengthInSpacings = 1.25;
    private const double MaximumWidthInSpacings = 0.38;
    private const double MaximumHeadEdgeDistanceInSpacings = 0.34;
    private const double VerticalTouchToleranceInSpacings = 0.18;
    private const double MinimumDirectionDifferenceInSpacings = 0.35;

    public StemAnalysisResult Analyze(
        SemanticDocument document,
        SemanticFacts facts)
    {
        var noteheads = facts
            .OfType<NoteheadFact>()
            .ToArray();

        if (noteheads.Length == 0)
        {
            throw new InvalidDataException(
                "StemAttachmentPass requires NoteheadPass to run first.");
        }

        var candidates = CollectCandidates(document);
        var decisions = new List<StemDecision>();

        foreach (var candidate in candidates)
        {
            decisions.Add(EvaluateCandidate(
                candidate,
                noteheads));
        }

        var attached = decisions
            .Where(decision => decision.Accepted)
            .SelectMany(decision => decision.Matches)
            .Select(match => match.Notehead.ShapeId)
            .Distinct(StringComparer.Ordinal)
            .Count();

        return new StemAnalysisResult(
            decisions
                .OrderBy(decision => decision.Candidate.MeasureNumber)
                .ThenBy(decision => decision.Candidate.Stroke.Source.Start.X)
                .ThenBy(decision => decision.Candidate.Stroke.Source.Start.Y)
                .ThenBy(decision => decision.Candidate.Stroke.ShapeId, StringComparer.Ordinal)
                .ToArray(),
            noteheads.Length,
            attached);
    }

    private static IReadOnlyList<StemCandidate> CollectCandidates(
        SemanticDocument document)
    {
        var result = new List<StemCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var measure in document.Measures)
        {
            var lineSpacing = AveragePositive(
                measure.Upper.LineSpacing,
                measure.Lower.LineSpacing);

            foreach (var stroke in measure.Upper.Elements
                         .OfType<StrokeElement>()
                         .Concat(measure.Lower.Elements.OfType<StrokeElement>()))
            {
                var key = $"{measure.Number}:{stroke.ShapeId}";
                if (!seen.Add(key))
                {
                    continue;
                }

                var source = stroke.Source;
                var dx = Math.Abs(source.End.X - source.Start.X);
                var dy = Math.Abs(source.End.Y - source.Start.Y);
                var length = Math.Sqrt(dx * dx + dy * dy);
                var verticalRatio = dy <= 0.001
                    ? double.PositiveInfinity
                    : dx / dy;
                var normalizedWidth = lineSpacing <= 0
                    ? double.PositiveInfinity
                    : source.Width / lineSpacing;
                var ownershipSpansStaffs = source.Ownership is not null
                    && source.Ownership.Start.StaffId != source.Ownership.End.StaffId;

                if (lineSpacing <= 0
                    || length < lineSpacing * MinimumLengthInSpacings
                    || verticalRatio > MaximumVerticalRatio
                    || normalizedWidth > MaximumWidthInSpacings)
                {
                    continue;
                }

                result.Add(new StemCandidate(
                    measure.Number,
                    stroke,
                    lineSpacing,
                    length,
                    verticalRatio,
                    normalizedWidth,
                    ownershipSpansStaffs));
            }
        }

        return result;
    }

    private static StemDecision EvaluateCandidate(
        StemCandidate candidate,
        IReadOnlyList<NoteheadFact> noteheads)
    {
        var source = candidate.Stroke.Source;
        var stemX = (source.Start.X + source.End.X) / 2.0;
        var stemMinY = Math.Min(source.Start.Y, source.End.Y);
        var stemMaxY = Math.Max(source.Start.Y, source.End.Y);
        var touchTolerance = candidate.LineSpacing * VerticalTouchToleranceInSpacings;
        var maximumEdgeDistance = candidate.LineSpacing * MaximumHeadEdgeDistanceInSpacings;

        var matches = noteheads
            .Where(notehead => notehead.MeasureNumber == candidate.MeasureNumber)
            .Select(notehead => Match(
                notehead,
                stemX,
                stemMinY,
                stemMaxY,
                touchTolerance,
                maximumEdgeDistance,
                candidate.LineSpacing))
            .Where(match => match is not null)
            .Select(match => match!)
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Notehead.CenterY)
            .ThenBy(match => match.Notehead.ShapeId, StringComparer.Ordinal)
            .ToArray();

        if (matches.Length == 0)
        {
            return new StemDecision(
                candidate,
                false,
                "no-touching-notehead",
                StemDirection.Ambiguous,
                [],
                false,
                0,
                "near-vertical thin stroke has no notehead whose side edge and vertical band touch the stroke");
        }

        var direction = InferDirection(
            candidate,
            matches);
        var attachedStaffs = matches
            .Select(match => match.Notehead.Staff)
            .Distinct()
            .ToArray();
        var isCrossStaff = candidate.OwnershipSpansStaffs
            || attachedStaffs.Length > 1;
        var bestMatch = matches[0];
        var verticalityScore = Math.Clamp(
            1.0 - candidate.VerticalRatio / MaximumVerticalRatio,
            0,
            1);
        var edgeScore = Math.Clamp(
            1.0 - bestMatch.EdgeDistanceInSpacings / MaximumHeadEdgeDistanceInSpacings,
            0,
            1);
        var confidence = 0.45 * bestMatch.Score
            + 0.30 * verticalityScore
            + 0.25 * edgeScore;

        return new StemDecision(
            candidate,
            true,
            "attached-stem",
            direction,
            matches,
            isCrossStaff,
            confidence,
            $"vertical stroke touches {matches.Length} notehead(s); "
            + $"best edge distance={bestMatch.EdgeDistanceInSpacings:F3}sp; "
            + $"vertical-ratio={candidate.VerticalRatio:F3}; "
            + $"width={candidate.NormalizedWidth:F3}sp; "
            + $"direction={direction}; cross-staff={isCrossStaff}");
    }

    private static StemNoteheadMatch? Match(
        NoteheadFact notehead,
        double stemX,
        double stemMinY,
        double stemMaxY,
        double touchTolerance,
        double maximumEdgeDistance,
        double lineSpacing)
    {
        var horizontalRadius = Math.Max(
            notehead.MajorRadius,
            notehead.MinorRadius);
        var verticalRadius = Math.Max(
            Math.Min(notehead.MajorRadius, notehead.MinorRadius),
            lineSpacing * 0.22);

        var leftEdge = notehead.CenterX - horizontalRadius;
        var rightEdge = notehead.CenterX + horizontalRadius;
        var edgeDistance = Math.Min(
            Math.Abs(stemX - leftEdge),
            Math.Abs(stemX - rightEdge));

        if (edgeDistance > maximumEdgeDistance)
        {
            return null;
        }

        var noteMinY = notehead.CenterY - verticalRadius;
        var noteMaxY = notehead.CenterY + verticalRadius;
        var expandedMinY = noteMinY - touchTolerance;
        var expandedMaxY = noteMaxY + touchTolerance;
        var verticalTouches = stemMaxY >= expandedMinY
            && stemMinY <= expandedMaxY;

        if (!verticalTouches)
        {
            return null;
        }

        var overlap = Math.Min(stemMaxY, expandedMaxY)
            - Math.Max(stemMinY, expandedMinY);
        var edgeDistanceInSpacings = edgeDistance / lineSpacing;
        var edgeScore = Math.Clamp(
            1.0 - edgeDistance / maximumEdgeDistance,
            0,
            1);
        var overlapScore = Math.Clamp(
            overlap / Math.Max(verticalRadius * 2.0, 0.001),
            0,
            1);
        var score = 0.72 * edgeScore
            + 0.28 * overlapScore;

        return new StemNoteheadMatch(
            notehead,
            edgeDistance,
            edgeDistanceInSpacings,
            overlap,
            score);
    }

    private static StemDirection InferDirection(
        StemCandidate candidate,
        IReadOnlyList<StemNoteheadMatch> matches)
    {
        var stemMinY = Math.Min(
            candidate.Stroke.Source.Start.Y,
            candidate.Stroke.Source.End.Y);
        var stemMaxY = Math.Max(
            candidate.Stroke.Source.Start.Y,
            candidate.Stroke.Source.End.Y);
        var topHeadY = matches.Min(match => match.Notehead.CenterY);
        var bottomHeadY = matches.Max(match => match.Notehead.CenterY);
        var extensionAbove = Math.Max(0, topHeadY - stemMinY);
        var extensionBelow = Math.Max(0, stemMaxY - bottomHeadY);
        var minimumDifference = candidate.LineSpacing * MinimumDirectionDifferenceInSpacings;

        if (extensionAbove >= extensionBelow + minimumDifference)
        {
            return StemDirection.Up;
        }

        if (extensionBelow >= extensionAbove + minimumDifference)
        {
            return StemDirection.Down;
        }

        return StemDirection.Ambiguous;
    }

    private static double AveragePositive(
        double first,
        double second)
    {
        var values = new[] { first, second }
            .Where(value => value > 0)
            .ToArray();

        return values.Length == 0
            ? 0
            : values.Average();
    }
}
