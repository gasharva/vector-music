namespace SvgMusic.Semantics;

public sealed record DotCandidate(
    int MeasureNumber,
    int StaffNumber,
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

    private readonly NoteheadColumnHelper _noteheadColumnHelper;
    private readonly DotColumnHelper _dotColumnHelper;
    private readonly AugmentationDotMatcher _columnMatcher;

    public DotAttachmentAnalyzer(
        NoteheadColumnHelper? noteheadColumnHelper = null,
        DotColumnHelper? dotColumnHelper = null,
        AugmentationDotMatcher? columnMatcher = null)
    {
        _noteheadColumnHelper = noteheadColumnHelper ?? new NoteheadColumnHelper();
        _dotColumnHelper = dotColumnHelper ?? new DotColumnHelper();
        _columnMatcher = columnMatcher ?? new AugmentationDotMatcher();
    }

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
        var decisions = MatchCandidates(
                candidates,
                noteheads,
                noteheadMedianSize)
            .OrderBy(decision => decision.Candidate.MeasureNumber)
            .ThenBy(decision => decision.Candidate.StaffNumber)
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

    private IReadOnlyList<DotDecision> MatchCandidates(
        IReadOnlyList<DotCandidate> candidates,
        IReadOnlyList<NoteheadFact> noteheads,
        double noteheadMedianSize)
    {
        var decisions = new Dictionary<CandidateKey, DotDecision>();
        var excludedTargets = new Dictionary<CandidateKey, HashSet<string>>();
        var noteColumns = _noteheadColumnHelper.Build(noteheads);
        var dotColumns = _dotColumnHelper.Build(candidates);

        foreach (var originalDotColumn in dotColumns.Where(column => column.Dots.Count >= 2))
        {
            var remainingDots = originalDotColumn.Dots.ToList();
            var usedTargets = new HashSet<string>(StringComparer.Ordinal);

            // One physical x-column may contain several rhythmic layers (for example,
            // filled and hollow heads sharing the same x). Consume the strongest
            // multi-note solution, remove only what it owns, then let another
            // notehead column compete for the remaining dots.
            while (remainingDots.Count >= 2)
            {
                var dotColumn = new DotColumn(
                    originalDotColumn.MeasureNumber,
                    originalDotColumn.Staff,
                    originalDotColumn.MinX,
                    originalDotColumn.MaxX,
                    remainingDots.ToArray());

                var possible = noteColumns
                    .Where(column =>
                        column.MeasureNumber == dotColumn.MeasureNumber
                        && column.Staff == dotColumn.Staff)
                    .Select(column => FilterUsedTargets(column, usedTargets))
                    .Where(column => column.Noteheads.Count >= 2)
                    .Select(column => _columnMatcher.Match(
                        dotColumn,
                        column,
                        Match))
                    .Where(match => match.MatchedCount >= 2)
                    .OrderByDescending(match => match.MatchedCount)
                    .ThenByDescending(match => match.TotalScore)
                    .ThenBy(match => match.TotalVerticalError)
                    .ThenBy(match => match.HorizontalDistanceInSpacings)
                    .ThenBy(match => match.NoteheadColumn.CenterX)
                    .ToArray();

                if (possible.Length == 0)
                {
                    break;
                }

                var best = possible[0];
                var matchedKeys = best.Assignments
                    .Select(assignment => Key(assignment.Dot))
                    .ToHashSet();

                foreach (var assignment in best.Assignments)
                {
                    var key = Key(assignment.Dot);
                    decisions[key] = Accepted(
                        assignment.Dot,
                        assignment.Match,
                        noteheadMedianSize,
                        $"global monotonic dot-column match {best.MatchedCount}/{dotColumn.Dots.Count}; "
                        + $"note-column fill={best.NoteheadColumn.FillKind}; ");
                    usedTargets.Add(assignment.Match.Notehead.ShapeId);
                }

                remainingDots.RemoveAll(dot => matchedKeys.Contains(Key(dot)));
            }

            // Leftovers may legitimately belong to another rhythmic layer in the
            // same x-column. Keep the old local matcher for them, but forbid it
            // from stealing a notehead already consumed by the global solution.
            foreach (var dot in remainingDots)
            {
                var key = Key(dot);
                if (usedTargets.Count == 0)
                {
                    continue;
                }

                excludedTargets[key] = new HashSet<string>(
                    usedTargets,
                    StringComparer.Ordinal);
            }
        }

        // Preserve the proven single-dot and horizontal double-dot behavior as a fallback.
        foreach (var candidate in candidates)
        {
            var key = Key(candidate);
            if (decisions.ContainsKey(key))
            {
                continue;
            }

            excludedTargets.TryGetValue(key, out var excluded);
            decisions[key] = MatchNotehead(
                candidate,
                noteheads,
                noteheadMedianSize,
                excluded);
        }

        return candidates
            .Select(candidate => decisions[Key(candidate)])
            .ToArray();
    }

    private static NoteheadColumn FilterUsedTargets(
        NoteheadColumn column,
        IReadOnlySet<string> usedTargets)
    {
        if (usedTargets.Count == 0)
        {
            return column;
        }

        return column with
        {
            Noteheads = column.Noteheads
                .Where(notehead => !usedTargets.Contains(notehead.ShapeId))
                .ToArray()
        };
    }

    private static CandidateKey Key(DotCandidate candidate) =>
        new(
            candidate.MeasureNumber,
            candidate.StaffNumber,
            candidate.Ellipse.ShapeId);

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

            CollectFromStaff(
                measure,
                measure.Upper,
                lineSpacing,
                noteheadIds,
                minimumDotSize,
                maximumDotSize,
                seen,
                result);
            CollectFromStaff(
                measure,
                measure.Lower,
                lineSpacing,
                noteheadIds,
                minimumDotSize,
                maximumDotSize,
                seen,
                result);
        }

        return result;
    }

    private static void CollectFromStaff(
        MeasureScene measure,
        StaffMeasureScene staff,
        double lineSpacing,
        IReadOnlySet<string> noteheadIds,
        double minimumDotSize,
        double maximumDotSize,
        ISet<string> seen,
        ICollection<DotCandidate> result)
    {
        foreach (var ellipse in staff.Elements.OfType<EllipseElement>())
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
                staff.StaffNumber,
                ellipse,
                lineSpacing,
                normalizedSize));
        }
    }

    private static DotDecision MatchNotehead(
        DotCandidate candidate,
        IReadOnlyList<NoteheadFact> noteheads,
        double noteheadMedianSize,
        IReadOnlySet<string>? excludedTargets = null)
    {
        var matches = noteheads
            .Where(notehead =>
                notehead.MeasureNumber == candidate.MeasureNumber
                && notehead.Staff == candidate.StaffNumber
                && (excludedTargets is null
                    || !excludedTargets.Contains(notehead.ShapeId)))
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

        return Accepted(
            candidate,
            matches[0],
            noteheadMedianSize,
            string.Empty);
    }

    private static DotDecision Accepted(
        DotCandidate candidate,
        DotNoteheadMatch match,
        double noteheadMedianSize,
        string reasonPrefix)
    {
        var expectedDotSize = noteheadMedianSize
            * ExpectedDotToNoteheadMedianRatio;
        var sizeError = Math.Abs(
            candidate.NormalizedSize - expectedDotSize)
            / Math.Max(expectedDotSize, 0.001);
        var sizeScore = Math.Clamp(
            1.0 - sizeError,
            0,
            1);
        var confidence = match.Score * 0.78
            + sizeScore * 0.22;

        return new DotDecision(
            candidate,
            true,
            "augmentation-dot",
            match,
            confidence,
            reasonPrefix
            + $"small filled ellipse size={candidate.NormalizedSize:F3}sp; "
            + $"target={match.Notehead.ShapeId}; "
            + $"horizontal-gap={match.HorizontalGapInSpacings:F3}sp; "
            + $"vertical-offset={match.VerticalOffsetInSpacings:F3}sp "
            + $"(expected {match.ExpectedVerticalOffsetInSpacings:F2}sp for staff-step {match.Notehead.StaffStep})");
    }

    private static DotNoteheadMatch? Match(
        DotCandidate candidate,
        NoteheadFact notehead)
    {
        if (notehead.MeasureNumber != candidate.MeasureNumber
            || notehead.Staff != candidate.StaffNumber)
        {
            return null;
        }

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

    private readonly record struct CandidateKey(
        int MeasureNumber,
        int StaffNumber,
        string ShapeId);

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
