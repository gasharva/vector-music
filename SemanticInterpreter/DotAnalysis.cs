namespace SvgMusic.Semantics;

public sealed record DotCandidate(
    int MeasureNumber,
    EllipseElement Ellipse,
    double LineSpacing,
    double NormalizedSize);

public sealed record DotNoteheadMatch(
    NoteheadFact Notehead,
    double HorizontalGapInSpacings,
    double VerticalOffsetInSpacings,
    double ExpectedVerticalOffsetInSpacings,
    double Score);

public sealed record DotDecision(
    DotCandidate Candidate,
    bool Accepted,
    string Decision,
    DotNoteheadMatch? Match,
    double Confidence,
    string Reason);

public sealed record DotAnalysisResult(
    double NoteheadMedianSize,
    double MinimumDotSize,
    double MaximumDotSize,
    IReadOnlyList<DotDecision> Decisions)
{
    public IReadOnlyList<DotDecision> Accepted =>
        Decisions.Where(decision => decision.Accepted).ToArray();
}

public sealed class DotAttachmentAnalyzer
{
    private const double MinimumDotToNoteheadMedianRatio = 0.20;
    private const double MaximumDotToNoteheadMedianRatio = 0.68;
    private const double ExpectedDotToNoteheadMedianRatio = 0.35;
    private const double MinimumHorizontalGapInSpacings = -0.15;
    private const double MaximumHorizontalGapInSpacings = 1.20;
    private const double MaximumSpaceNoteVerticalOffsetInSpacings = 0.34;
    private const double MinimumLineNoteVerticalOffsetInSpacings = 0.18;
    private const double MaximumLineNoteVerticalOffsetInSpacings = 0.78;
    private const double ExpectedLineNoteVerticalOffsetInSpacings = 0.50;

    public DotAnalysisResult Analyze(
        SemanticDocument document,
        SemanticFacts facts)
    {
        var noteheads = facts
            .OfType<NoteheadFact>()
            .ToArray();

        if (noteheads.Length == 0)
        {
            throw new InvalidDataException(
                "DotAttachmentPass requires NoteheadPass to run first.");
        }

        var noteheadMedianSize = Median(
            noteheads.Select(notehead => notehead.NormalizedSize));
        var minimumDotSize = noteheadMedianSize
            * MinimumDotToNoteheadMedianRatio;
        var maximumDotSize = noteheadMedianSize
            * MaximumDotToNoteheadMedianRatio;
        var noteheadIds = noteheads
            .Select(notehead => notehead.ShapeId)
            .ToHashSet(StringComparer.Ordinal);

        var candidates = CollectCandidates(
            document,
            noteheadIds,
            minimumDotSize,
            maximumDotSize);
        var decisions = candidates
            .Select(candidate => MatchNotehead(
                candidate,
                noteheads,
                noteheadMedianSize))
            .OrderBy(decision => decision.Candidate.MeasureNumber)
            .ThenBy(decision => decision.Candidate.Ellipse.CenterX)
            .ThenBy(decision => decision.Candidate.Ellipse.CenterY)
            .ThenBy(decision => decision.Candidate.Ellipse.ShapeId, StringComparer.Ordinal)
            .ToArray();

        return new DotAnalysisResult(
            noteheadMedianSize,
            minimumDotSize,
            maximumDotSize,
            decisions);
    }

    private static IReadOnlyList<DotCandidate> CollectCandidates(
        SemanticDocument document,
        IReadOnlySet<string> noteheadIds,
        double minimumDotSize,
        double maximumDotSize)
    {
        var result = new List<DotCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var measure in document.Measures)
        {
            var lineSpacing = AveragePositive(
                measure.Upper.LineSpacing,
                measure.Lower.LineSpacing);

            if (lineSpacing <= 0)
            {
                continue;
            }

            foreach (var ellipse in measure.Upper.Elements
                         .OfType<EllipseElement>()
                         .Concat(measure.Lower.Elements.OfType<EllipseElement>()))
            {
                var key = $"{measure.Number}:{ellipse.ShapeId}";
                if (!seen.Add(key)
                    || noteheadIds.Contains(ellipse.ShapeId)
                    || ellipse.Source.IsHollow)
                {
                    continue;
                }

                var equivalentDiameter = 2.0 * Math.Sqrt(
                    Math.Max(ellipse.Source.MajorRadius, 0)
                    * Math.Max(ellipse.Source.MinorRadius, 0));
                var normalizedSize = equivalentDiameter / lineSpacing;

                if (normalizedSize < minimumDotSize
                    || normalizedSize > maximumDotSize)
                {
                    continue;
                }

                result.Add(new DotCandidate(
                    measure.Number,
                    ellipse,
                    lineSpacing,
                    normalizedSize));
            }
        }

        return result;
    }

    private static DotDecision MatchNotehead(
        DotCandidate candidate,
        IReadOnlyList<NoteheadFact> noteheads,
        double noteheadMedianSize)
    {
        var matches = noteheads
            .Where(notehead => notehead.MeasureNumber == candidate.MeasureNumber)
            .Select(notehead => Match(
                candidate,
                notehead))
            .Where(match => match is not null)
            .Select(match => match!)
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.HorizontalGapInSpacings)
            .ThenBy(match => match.Notehead.ShapeId, StringComparer.Ordinal)
            .ToArray();

        if (matches.Length == 0)
        {
            return Rejected(
                candidate,
                "no-compatible-notehead-to-left",
                "small filled ellipse has no notehead immediately to its left at an augmentation-dot vertical position");
        }

        var best = matches[0];
        var expectedDotSize = noteheadMedianSize
            * ExpectedDotToNoteheadMedianRatio;
        var sizeError = Math.Abs(
            candidate.NormalizedSize - expectedDotSize)
            / Math.Max(expectedDotSize, 0.001);
        var sizeScore = Math.Clamp(
            1.0 - sizeError,
            0,
            1);
        var confidence = best.Score * 0.78
            + sizeScore * 0.22;

        return new DotDecision(
            candidate,
            true,
            "augmentation-dot",
            best,
            confidence,
            $"small filled ellipse size={candidate.NormalizedSize:F3}sp; "
            + $"target={best.Notehead.ShapeId}; "
            + $"horizontal-gap={best.HorizontalGapInSpacings:F3}sp; "
            + $"vertical-offset={best.VerticalOffsetInSpacings:F3}sp "
            + $"(expected {best.ExpectedVerticalOffsetInSpacings:F2}sp for staff-step {best.Notehead.StaffStep})");
    }

    private static DotNoteheadMatch? Match(
        DotCandidate candidate,
        NoteheadFact notehead)
    {
        var spacing = candidate.LineSpacing;
        var dot = candidate.Ellipse;
        var horizontalRadius = Math.Max(
            notehead.MajorRadius,
            notehead.MinorRadius);
        var dotRadius = Math.Max(
            dot.Source.MajorRadius,
            dot.Source.MinorRadius);
        var horizontalGap = dot.CenterX - dotRadius
            - (notehead.CenterX + horizontalRadius);
        var horizontalGapInSpacings = horizontalGap / spacing;

        if (horizontalGapInSpacings < MinimumHorizontalGapInSpacings
            || horizontalGapInSpacings > MaximumHorizontalGapInSpacings)
        {
            return null;
        }

        if (dot.CenterX <= notehead.CenterX)
        {
            return null;
        }

        var verticalOffsetInSpacings = Math.Abs(
            dot.CenterY - notehead.CenterY) / spacing;
        var noteIsOnLine = Math.Abs(notehead.StaffStep) % 2 == 0;
        double expectedVerticalOffset;
        double verticalScore;

        if (noteIsOnLine)
        {
            if (verticalOffsetInSpacings < MinimumLineNoteVerticalOffsetInSpacings
                || verticalOffsetInSpacings > MaximumLineNoteVerticalOffsetInSpacings)
            {
                return null;
            }

            expectedVerticalOffset = ExpectedLineNoteVerticalOffsetInSpacings;
            var error = Math.Abs(
                verticalOffsetInSpacings - expectedVerticalOffset);
            var tolerance = Math.Max(
                expectedVerticalOffset - MinimumLineNoteVerticalOffsetInSpacings,
                MaximumLineNoteVerticalOffsetInSpacings - expectedVerticalOffset);
            verticalScore = Math.Clamp(
                1.0 - error / tolerance,
                0,
                1);
        }
        else
        {
            if (verticalOffsetInSpacings > MaximumSpaceNoteVerticalOffsetInSpacings)
            {
                return null;
            }

            expectedVerticalOffset = 0;
            verticalScore = Math.Clamp(
                1.0 - verticalOffsetInSpacings / MaximumSpaceNoteVerticalOffsetInSpacings,
                0,
                1);
        }

        var horizontalScore = Math.Clamp(
            1.0 - Math.Max(0, horizontalGapInSpacings)
                / MaximumHorizontalGapInSpacings,
            0,
            1);
        var score = horizontalScore * 0.48
            + verticalScore * 0.52;

        return new DotNoteheadMatch(
            notehead,
            horizontalGapInSpacings,
            verticalOffsetInSpacings,
            expectedVerticalOffset,
            score);
    }

    private static DotDecision Rejected(
        DotCandidate candidate,
        string decision,
        string reason)
    {
        return new DotDecision(
            candidate,
            false,
            decision,
            null,
            0,
            reason);
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values
            .OrderBy(value => value)
            .ToArray();

        if (ordered.Length == 0)
        {
            return 0;
        }

        var middle = ordered.Length / 2;
        if (ordered.Length % 2 == 1)
        {
            return ordered[middle];
        }

        return (ordered[middle - 1] + ordered[middle]) / 2.0;
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
