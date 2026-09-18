namespace SvgMusic.Scene;

public sealed record ScoreLayout(
    IReadOnlyList<ScoreSystem> Systems,
    IReadOnlyList<StaffLayout> Staffs);

public sealed record ScoreSystem(
    string Id,
    BoundsD Bounds,
    IReadOnlyList<StaffPairLayout> StaffPairs);

public sealed record StaffPairLayout(
    string Id,
    string UpperStaffId,
    string LowerStaffId,
    BoundsD Bounds,
    IReadOnlyList<MeasureLayout> Measures,
    IReadOnlyList<MeasureBoundary> Boundaries);

public sealed record StaffLayout(
    string Id,
    string SystemId,
    BoundsD Bounds,
    IReadOnlyList<StaffLineLayout> Lines,
    double AverageLineSpacing,
    IReadOnlyList<LedgerLevelLayout> LedgerLevels);

public sealed record StaffLineLayout(
    int Index,
    double Y,
    double XStart,
    double XEnd,
    string StrokeId);

public sealed record LedgerLevelLayout(
    string Direction,
    int Step,
    double Y,
    IReadOnlyList<LedgerSegmentLayout> Segments);

public sealed record LedgerSegmentLayout(
    double XStart,
    double XEnd,
    string StrokeId);

public sealed record MeasureBoundary(
    double X,
    double UpperY,
    double LowerY,
    IReadOnlyList<string> StrokeIds,
    double MinStrokeWidth = 0,
    double MaxStrokeWidth = 0,
    bool IsFinal = false);

public sealed record MeasureLayout(
    string Id,
    double XStart,
    double XEnd,
    MeasureBoundary LeftBoundary,
    MeasureBoundary RightBoundary);
