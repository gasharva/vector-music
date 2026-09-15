namespace SvgMusic.Scene;

public sealed record LedgerLadderOwnershipAdjustment(
    string ShapeId,
    LogicalOwnership? PreviousOwnership,
    LogicalOwnership NewOwnership,
    int StaffStep,
    int RequiredLedgerLevels,
    string Direction);

public sealed record LedgerLadderOwnershipResult(
    NotationScene Scene,
    LogicalOwnershipScene Ownership,
    IReadOnlyList<LedgerLadderOwnershipAdjustment> Adjustments);

/// <summary>
/// Corrects the rare but important case where a very high/low ledger note
/// approaches the neighboring piano staff closely enough that generic
/// geometric ownership assigns its ellipse to that neighboring staff.
///
/// A contiguous local ledger ladder is stronger evidence than proximity.
/// The override is deliberately conservative: it is only applied when an
/// ellipse is aligned to a staff half-step grid, needs at least two ledger
/// levels, every required local ledger level crosses the ellipse x-range,
/// and exactly one staff in the system provides such a ladder.
/// </summary>
public sealed class LedgerLadderEllipseOwnershipAssigner
{
    private const double MaximumGridErrorInHalfSteps = 0.30;
    private const int MinimumLedgerLevelsForOverride = 2;
    private const int BottomStaffLineStep = 8;
    private const double LedgerHorizontalAllowanceInSpacings = 0.15;

    public LedgerLadderOwnershipResult AssignAndApply(
        NotationScene notation,
        ScoreLayout layout,
        LogicalOwnershipScene ownership)
    {
        var assignments = ownership.Assignments.ToDictionary(
            assignment => assignment.ShapeId,
            assignment => assignment.Ownership,
            StringComparer.Ordinal);
        var kinds = ownership.Assignments.ToDictionary(
            assignment => assignment.ShapeId,
            assignment => assignment.Kind,
            StringComparer.Ordinal);
        var staffsById = layout.Staffs.ToDictionary(
            staff => staff.Id,
            StringComparer.Ordinal);
        var adjustments = new List<LedgerLadderOwnershipAdjustment>();

        foreach (var ellipse in notation.Ellipses)
        {
            var matches = FindLedgerMatches(
                ellipse,
                layout,
                staffsById);

            if (matches.Count != 1)
            {
                continue;
            }

            var match = matches[0];
            var coordinate = new LogicalCoordinate(
                match.Staff.Id,
                match.Measure.Id);
            var newOwnership = new LogicalOwnership(
                coordinate,
                coordinate,
                1,
                null,
                0,
                $"LedgerLadderAnchor:{match.Direction}:levels={match.RequiredLedgerLevels}:step={match.StaffStep}");
            assignments.TryGetValue(
                ellipse.ShapeId,
                out var previousOwnership);

            if (previousOwnership is not null
                && previousOwnership.Start == coordinate
                && previousOwnership.End == coordinate)
            {
                continue;
            }

            assignments[ellipse.ShapeId] = newOwnership;
            kinds[ellipse.ShapeId] = "EllipseLike";
            adjustments.Add(new LedgerLadderOwnershipAdjustment(
                ellipse.ShapeId,
                previousOwnership,
                newOwnership,
                match.StaffStep,
                match.RequiredLedgerLevels,
                match.Direction));
        }

        if (adjustments.Count == 0)
        {
            return new LedgerLadderOwnershipResult(
                notation,
                ownership,
                []);
        }

        var updatedScene = notation with
        {
            Ellipses = notation.Ellipses
                .Select(ellipse => ellipse with
                {
                    Ownership = assignments.GetValueOrDefault(ellipse.ShapeId)
                })
                .ToArray()
        };

        var updatedOwnership = new LogicalOwnershipScene(
            assignments
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => new LogicalOwnershipAssignment(
                    item.Key,
                    kinds.GetValueOrDefault(item.Key, "Unknown"),
                    item.Value))
                .ToArray());

        return new LedgerLadderOwnershipResult(
            updatedScene,
            updatedOwnership,
            adjustments);
    }

    private static IReadOnlyList<LedgerMatch> FindLedgerMatches(
        EllipseLike ellipse,
        ScoreLayout layout,
        IReadOnlyDictionary<string, StaffLayout> staffsById)
    {
        var matches = new List<LedgerMatch>();

        foreach (var system in layout.Systems)
        {
            foreach (var pair in system.StaffPairs)
            {
                var measure = pair.Measures.FirstOrDefault(item =>
                    ellipse.Center.X >= item.XStart
                    && ellipse.Center.X <= item.XEnd);

                if (measure is null)
                {
                    continue;
                }

                foreach (var staffId in new[]
                         {
                             pair.UpperStaffId,
                             pair.LowerStaffId
                         })
                {
                    var staff = staffsById[staffId];
                    var match = TryMatch(
                        ellipse,
                        staff,
                        measure);

                    if (match is not null)
                    {
                        matches.Add(match);
                    }
                }
            }
        }

        return matches;
    }

    private static LedgerMatch? TryMatch(
        EllipseLike ellipse,
        StaffLayout staff,
        MeasureLayout measure)
    {
        var spacing = staff.AverageLineSpacing;
        var halfStep = spacing / 2.0;

        if (halfStep <= 0)
        {
            return null;
        }

        var rawStep = (ellipse.Center.Y - staff.Bounds.MinY) / halfStep;
        var staffStep = (int)Math.Round(rawStep);
        var gridError = Math.Abs(rawStep - staffStep);

        if (gridError > MaximumGridErrorInHalfSteps)
        {
            return null;
        }

        var requiredLedgerLevels = RequiredLedgerLevels(staffStep);
        if (requiredLedgerLevels < MinimumLedgerLevelsForOverride)
        {
            return null;
        }

        var direction = staffStep < 0
            ? "above"
            : "below";
        var ellipseMinX = ellipse.Center.X - ellipse.MajorRadius;
        var ellipseMaxX = ellipse.Center.X + ellipse.MajorRadius;
        var allowance = spacing * LedgerHorizontalAllowanceInSpacings;

        for (var levelNumber = 1;
             levelNumber <= requiredLedgerLevels;
             levelNumber++)
        {
            var level = staff.LedgerLevels.FirstOrDefault(item =>
                string.Equals(
                    item.Direction,
                    direction,
                    StringComparison.OrdinalIgnoreCase)
                && item.Step == levelNumber);

            if (level is null)
            {
                return null;
            }

            var crossesEllipse = level.Segments.Any(segment =>
                segment.XEnd + allowance >= ellipseMinX
                && segment.XStart - allowance <= ellipseMaxX);

            if (!crossesEllipse)
            {
                return null;
            }
        }

        return new LedgerMatch(
            staff,
            measure,
            staffStep,
            requiredLedgerLevels,
            direction,
            gridError);
    }

    private static int RequiredLedgerLevels(int staffStep)
    {
        if (staffStep >= -1
            && staffStep <= BottomStaffLineStep + 1)
        {
            return 0;
        }

        if (staffStep < -1)
        {
            return -staffStep / 2;
        }

        return (staffStep - BottomStaffLineStep) / 2;
    }

    private sealed record LedgerMatch(
        StaffLayout Staff,
        MeasureLayout Measure,
        int StaffStep,
        int RequiredLedgerLevels,
        string Direction,
        double GridError);
}
