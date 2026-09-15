namespace SvgMusic.Semantics;

public sealed record AugmentationDotAssignment(
    DotCandidate Dot,
    DotNoteheadMatch Match,
    double ColumnScore);

public sealed record AugmentationDotColumnMatch(
    DotColumn DotColumn,
    NoteheadColumn NoteheadColumn,
    IReadOnlyList<AugmentationDotAssignment> Assignments,
    double TotalScore,
    double TotalVerticalError,
    double HorizontalDistanceInSpacings)
{
    public int MatchedCount => Assignments.Count;
}

/// <summary>
/// Matches a vertical column of augmentation dots to a vertical notehead column
/// globally rather than deciding every dot independently. The dynamic program
/// preserves top-to-bottom order, is one-to-one inside the column and may skip
/// notes/dots when geometry does not support an attachment.
/// </summary>
public sealed class AugmentationDotMatcher
{
    private const double MinimumColumnHorizontalGapInSpacings = -0.15;
    private const double MaximumColumnHorizontalGapInSpacings = 1.20;
    private const double MaximumSpaceNoteVerticalOffsetInSpacings = 0.34;
    private const double MinimumLineNoteVerticalOffsetInSpacings = 0.18;
    private const double MaximumLineNoteVerticalOffsetInSpacings = 0.78;
    private const double ExpectedLineNoteVerticalOffsetInSpacings = 0.50;
    private const double VerticalErrorScaleInSpacings = 0.34;
    private const double HorizontalScaleInSpacings = 1.20;
    private const double ScoreEpsilon = 1e-9;

    public AugmentationDotColumnMatch Match(
        DotColumn dotColumn,
        NoteheadColumn noteheadColumn,
        Func<DotCandidate, NoteheadFact, DotNoteheadMatch?> pairEvaluator)
    {
        if (dotColumn.MeasureNumber != noteheadColumn.MeasureNumber
            || dotColumn.Staff != noteheadColumn.Staff)
        {
            return Empty(dotColumn, noteheadColumn);
        }

        var averageSpacing = dotColumn.Dots
            .Where(dot => dot.LineSpacing > 0)
            .Select(dot => dot.LineSpacing)
            .DefaultIfEmpty(1.0)
            .Average();
        var columnHorizontalGapInSpacings =
            (dotColumn.MinX - noteheadColumn.MaxX)
            / Math.Max(averageSpacing, 0.001);

        if (dotColumn.CenterX <= noteheadColumn.CenterX
            || columnHorizontalGapInSpacings < MinimumColumnHorizontalGapInSpacings
            || columnHorizontalGapInSpacings > MaximumColumnHorizontalGapInSpacings)
        {
            return Empty(dotColumn, noteheadColumn);
        }

        var dots = dotColumn.Dots
            .OrderBy(dot => dot.Ellipse.CenterY)
            .ThenBy(dot => dot.Ellipse.ShapeId, StringComparer.Ordinal)
            .ToArray();
        var notes = noteheadColumn.Noteheads
            .OrderBy(note => note.CenterY)
            .ThenBy(note => note.ShapeId, StringComparer.Ordinal)
            .ToArray();

        var states = new MatchState?[notes.Length + 1, dots.Length + 1];
        states[0, 0] = MatchState.Empty;

        for (var noteIndex = 0; noteIndex <= notes.Length; noteIndex++)
        {
            for (var dotIndex = 0; dotIndex <= dots.Length; dotIndex++)
            {
                var state = states[noteIndex, dotIndex];
                if (state is null)
                {
                    continue;
                }

                if (noteIndex < notes.Length)
                {
                    Update(
                        states,
                        noteIndex + 1,
                        dotIndex,
                        state);
                }

                if (dotIndex < dots.Length)
                {
                    Update(
                        states,
                        noteIndex,
                        dotIndex + 1,
                        state);
                }

                if (noteIndex >= notes.Length || dotIndex >= dots.Length)
                {
                    continue;
                }

                // First keep the normal local rule. If it rejects only because an
                // individual notehead in a stacked second is shifted left, the
                // already-approved column geometry lets Y perform the assignment.
                var match = pairEvaluator(
                        dots[dotIndex],
                        notes[noteIndex])
                    ?? MatchInsideCompatibleColumns(
                        dots[dotIndex],
                        notes[noteIndex]);
                if (match is null)
                {
                    continue;
                }

                var verticalError = Math.Abs(
                    match.VerticalOffsetInSpacings
                    - match.ExpectedVerticalOffsetInSpacings);
                var columnScore = ColumnScore(
                    match,
                    verticalError);
                var assignment = new AugmentationDotAssignment(
                    dots[dotIndex],
                    match,
                    columnScore);
                var matched = state.Add(
                    assignment,
                    verticalError,
                    Math.Abs(match.HorizontalGapInSpacings));

                Update(
                    states,
                    noteIndex + 1,
                    dotIndex + 1,
                    matched);
            }
        }

        var best = states[notes.Length, dots.Length]
            ?? MatchState.Empty;

        return new AugmentationDotColumnMatch(
            dotColumn,
            noteheadColumn,
            best.Assignments,
            best.Score,
            best.VerticalError,
            Math.Abs(columnHorizontalGapInSpacings));
    }

    private static DotNoteheadMatch? MatchInsideCompatibleColumns(
        DotCandidate candidate,
        NoteheadFact notehead)
    {
        if (candidate.MeasureNumber != notehead.MeasureNumber
            || candidate.StaffNumber != notehead.Staff
            || candidate.LineSpacing <= 0
            || candidate.Ellipse.CenterX <= notehead.CenterX)
        {
            return null;
        }

        var spacing = candidate.LineSpacing;
        var dot = candidate.Ellipse;
        var noteHorizontalRadius = Math.Max(
            notehead.MajorRadius,
            notehead.MinorRadius);
        var dotRadius = Math.Max(
            dot.Source.MajorRadius,
            dot.Source.MinorRadius);
        var horizontalGapInSpacings = (
            dot.CenterX - dotRadius
            - (notehead.CenterX + noteHorizontalRadius)) / spacing;
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
                1.0 - verticalOffsetInSpacings
                    / MaximumSpaceNoteVerticalOffsetInSpacings,
                0,
                1);
        }

        // The enclosing column already passed the horizontal gate. Individual X
        // displacement therefore becomes only a weak preference, not a veto.
        var horizontalScore = Math.Clamp(
            1.0 - Math.Max(0, horizontalGapInSpacings)
                / HorizontalScaleInSpacings,
            0,
            1);
        var score = verticalScore * 0.95
            + horizontalScore * 0.05;

        return new DotNoteheadMatch(
            notehead,
            horizontalGapInSpacings,
            verticalOffsetInSpacings,
            expectedVerticalOffset,
            score);
    }

    private static AugmentationDotColumnMatch Empty(
        DotColumn dots,
        NoteheadColumn notes)
    {
        return new AugmentationDotColumnMatch(
            dots,
            notes,
            Array.Empty<AugmentationDotAssignment>(),
            0,
            0,
            double.PositiveInfinity);
    }

    private static double ColumnScore(
        DotNoteheadMatch match,
        double verticalError)
    {
        // Y is the semantic signal for a stacked chord. X merely decides between
        // otherwise equivalent columns that happen to be nearby.
        var verticalScore = Math.Clamp(
            1.0 - verticalError / VerticalErrorScaleInSpacings,
            0,
            1);
        var horizontalScore = Math.Clamp(
            1.0 - Math.Max(0, match.HorizontalGapInSpacings)
                / HorizontalScaleInSpacings,
            0,
            1);

        return verticalScore * 0.95
            + horizontalScore * 0.05;
    }

    private static void Update(
        MatchState?[,] states,
        int noteIndex,
        int dotIndex,
        MatchState candidate)
    {
        var current = states[noteIndex, dotIndex];
        if (current is null || IsBetter(candidate, current))
        {
            states[noteIndex, dotIndex] = candidate;
        }
    }

    private static bool IsBetter(
        MatchState candidate,
        MatchState current)
    {
        if (candidate.Assignments.Count != current.Assignments.Count)
        {
            return candidate.Assignments.Count > current.Assignments.Count;
        }

        if (Math.Abs(candidate.Score - current.Score) > ScoreEpsilon)
        {
            return candidate.Score > current.Score;
        }

        if (Math.Abs(candidate.VerticalError - current.VerticalError) > ScoreEpsilon)
        {
            return candidate.VerticalError < current.VerticalError;
        }

        if (Math.Abs(candidate.HorizontalGap - current.HorizontalGap) > ScoreEpsilon)
        {
            return candidate.HorizontalGap < current.HorizontalGap;
        }

        return false;
    }

    private sealed record MatchState(
        IReadOnlyList<AugmentationDotAssignment> Assignments,
        double Score,
        double VerticalError,
        double HorizontalGap)
    {
        public static MatchState Empty { get; } = new(
            Array.Empty<AugmentationDotAssignment>(),
            0,
            0,
            0);

        public MatchState Add(
            AugmentationDotAssignment assignment,
            double verticalError,
            double horizontalGap)
        {
            return new MatchState(
                Assignments
                    .Concat(new[] { assignment })
                    .ToArray(),
                Score + assignment.ColumnScore,
                VerticalError + verticalError,
                HorizontalGap + horizontalGap);
        }
    }
}
