using SvgMusic.Scene;

namespace SvgMusic.Semantics;

public sealed record NoteheadCandidate(
    int MeasureNumber,
    int StaffNumber,
    string StaffId,
    double StaffTopY,
    double LineSpacing,
    IReadOnlyList<LedgerLevelLayout> LedgerLevels,
    EllipseElement Ellipse,
    double NormalizedSize);

public sealed record EllipseSizeProfile(
    bool HasSmallDotCluster,
    double? Threshold,
    int SmallCount,
    int NoteheadBandCount,
    double? SmallMedian,
    double? NoteheadMedian,
    IReadOnlyList<NoteheadCandidate> RankedCandidates);

public sealed record StaffGridMatch(
    bool IsAligned,
    bool HasLedgerSupport,
    int NearestStep,
    double ErrorInHalfSteps,
    int RequiredLedgerLevels,
    string SupportReason)
{
    public bool IsValid => IsAligned && HasLedgerSupport;
}

public sealed record NoteheadDecision(
    NoteheadCandidate Candidate,
    bool Accepted,
    string Decision,
    StaffGridMatch Grid,
    string FillKind,
    double Confidence,
    string Reason);

public sealed record NoteheadAnalysisResult(
    EllipseSizeProfile SizeProfile,
    IReadOnlyList<NoteheadDecision> Decisions)
{
    public IReadOnlyList<NoteheadDecision> Accepted =>
        Decisions.Where(decision => decision.Accepted).ToArray();
}

public sealed class EllipseSizeProfiler
{
    private const double MinimumGapRatio = 1.35;
    private const double MaximumSmallToNoteheadMedianRatio = 0.68;
    private const double MinimumNoteheadMedian = 0.55;
    private const double MaximumNoteheadMedian = 1.60;
    private const double MaximumSmallFraction = 0.45;

    public EllipseSizeProfile Analyze(
        IReadOnlyList<NoteheadCandidate> candidates)
    {
        var ranked = candidates
            .Where(candidate => candidate.NormalizedSize > 0)
            .OrderBy(candidate => candidate.NormalizedSize)
            .ThenBy(candidate => candidate.Ellipse.ShapeId, StringComparer.Ordinal)
            .ToArray();

        if (ranked.Length < 3)
        {
            return NoSplit(ranked);
        }

        SplitCandidate? best = null;

        for (var splitIndex = 1; splitIndex < ranked.Length; splitIndex++)
        {
            var lower = ranked[..splitIndex];
            var upper = ranked[splitIndex..];

            var lowerFraction = lower.Length / (double)ranked.Length;
            if (lowerFraction > MaximumSmallFraction)
            {
                continue;
            }

            var lowerMedian = Median(lower.Select(item => item.NormalizedSize));
            var upperMedian = Median(upper.Select(item => item.NormalizedSize));
            var gapRatio = upper[0].NormalizedSize / lower[^1].NormalizedSize;

            if (gapRatio < MinimumGapRatio)
            {
                continue;
            }

            if (upperMedian < MinimumNoteheadMedian
                || upperMedian > MaximumNoteheadMedian)
            {
                continue;
            }

            if (lowerMedian > upperMedian * MaximumSmallToNoteheadMedianRatio)
            {
                continue;
            }

            var score = Math.Log(gapRatio);

            if (best is null || score > best.Score)
            {
                best = new SplitCandidate(
                    splitIndex,
                    lowerMedian,
                    upperMedian,
                    gapRatio,
                    score);
            }
        }

        if (best is null)
        {
            return NoSplit(ranked);
        }

        var lowerEdge = ranked[best.SplitIndex - 1].NormalizedSize;
        var upperEdge = ranked[best.SplitIndex].NormalizedSize;
        var threshold = Math.Sqrt(lowerEdge * upperEdge);

        return new EllipseSizeProfile(
            true,
            threshold,
            best.SplitIndex,
            ranked.Length - best.SplitIndex,
            best.SmallMedian,
            best.NoteheadMedian,
            ranked);
    }

    private static EllipseSizeProfile NoSplit(
        IReadOnlyList<NoteheadCandidate> ranked)
    {
        double? median = ranked.Count == 0
            ? null
            : Median(ranked.Select(item => item.NormalizedSize));

        return new EllipseSizeProfile(
            false,
            null,
            0,
            ranked.Count,
            null,
            median,
            ranked);
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.OrderBy(value => value).ToArray();
        var middle = ordered.Length / 2;

        if (ordered.Length % 2 == 1)
        {
            return ordered[middle];
        }

        return (ordered[middle - 1] + ordered[middle]) / 2.0;
    }

    private sealed record SplitCandidate(
        int SplitIndex,
        double SmallMedian,
        double NoteheadMedian,
        double GapRatio,
        double Score);
}

public sealed class StaffGridRule
{
    private const double MaximumErrorInHalfSteps = 0.30;
    private const int BottomStaffLineStep = 8;

    public StaffGridMatch Evaluate(
        NoteheadCandidate candidate,
        double maximumErrorInHalfSteps = MaximumErrorInHalfSteps)
    {
        var halfStep = candidate.LineSpacing / 2.0;

        if (halfStep <= 0)
        {
            return new StaffGridMatch(
                false,
                false,
                0,
                double.PositiveInfinity,
                0,
                "staff spacing is not positive");
        }

        var rawStep = (candidate.Ellipse.CenterY - candidate.StaffTopY) / halfStep;
        var nearestStep = (int)Math.Round(rawStep);
        var error = Math.Abs(rawStep - nearestStep);
        var isAligned = error <= maximumErrorInHalfSteps;

        if (!isAligned)
        {
            return new StaffGridMatch(
                false,
                false,
                nearestStep,
                error,
                0,
                "ellipse center is not on the staff half-step grid");
        }

        var requiredLedgerLevels = RequiredLedgerLevels(nearestStep);
        if (requiredLedgerLevels == 0)
        {
            return new StaffGridMatch(
                true,
                true,
                nearestStep,
                error,
                0,
                "position is inside the staff or in the immediately adjacent staff space");
        }

        var direction = nearestStep < 0
            ? "above"
            : "below";
        var missingLevel = FindMissingLocalLedgerLevel(
            candidate,
            direction,
            requiredLedgerLevels);

        if (missingLevel is not null)
        {
            return new StaffGridMatch(
                true,
                false,
                nearestStep,
                error,
                requiredLedgerLevels,
                $"requires {requiredLedgerLevels} local ledger level(s) {direction}, "
                + $"but level {missingLevel.Value} does not cross this ellipse x-range");
        }

        return new StaffGridMatch(
            true,
            true,
            nearestStep,
            error,
            requiredLedgerLevels,
            $"supported by {requiredLedgerLevels} local ledger level(s) {direction}");
    }

    private static int RequiredLedgerLevels(int nearestStep)
    {
        if (nearestStep >= -1 && nearestStep <= BottomStaffLineStep + 1)
        {
            return 0;
        }

        if (nearestStep < -1)
        {
            return -nearestStep / 2;
        }

        return (nearestStep - BottomStaffLineStep) / 2;
    }

    private static int? FindMissingLocalLedgerLevel(
        NoteheadCandidate candidate,
        string direction,
        int requiredLedgerLevels)
    {
        var allowance = candidate.LineSpacing * 0.15;
        var ellipseBounds = candidate.Ellipse.Bounds;

        for (var step = 1; step <= requiredLedgerLevels; step++)
        {
            var level = candidate.LedgerLevels.FirstOrDefault(item =>
                string.Equals(
                    item.Direction,
                    direction,
                    StringComparison.OrdinalIgnoreCase)
                && item.Step == step);

            if (level is null)
            {
                return step;
            }

            var crossesEllipse = level.Segments.Any(segment =>
                segment.XEnd + allowance >= ellipseBounds.MinX
                && segment.XStart - allowance <= ellipseBounds.MaxX);

            if (!crossesEllipse)
            {
                return step;
            }
        }

        return null;
    }
}

public sealed class NoteheadAnalyzer
{
    private const double MaximumGridError = 0.30;
    private const double MaximumStemSupportedGridError = 0.52;

    private readonly EllipseSizeProfiler _sizeProfiler;
    private readonly StaffGridRule _staffGridRule;

    public NoteheadAnalyzer(
        EllipseSizeProfiler? sizeProfiler = null,
        StaffGridRule? staffGridRule = null)
    {
        _sizeProfiler = sizeProfiler ?? new EllipseSizeProfiler();
        _staffGridRule = staffGridRule ?? new StaffGridRule();
    }

    public NoteheadAnalysisResult Analyze(SemanticDocument document)
    {
        var collector = new EllipseCollector();
        collector.Visit(document);

        var candidates = collector.Candidates
            .GroupBy(candidate => candidate.Ellipse.ShapeId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();

        var profile = _sizeProfiler.Analyze(candidates);
        var decisions = candidates
            .Select(candidate => Decide(candidate, profile, document))
            .OrderBy(decision => decision.Candidate.MeasureNumber)
            .ThenBy(decision => decision.Candidate.StaffNumber)
            .ThenBy(decision => decision.Candidate.Ellipse.CenterX)
            .ToArray();

        return new NoteheadAnalysisResult(
            profile,
            decisions);
    }

    private NoteheadDecision Decide(
        NoteheadCandidate candidate,
        EllipseSizeProfile profile,
        SemanticDocument document)
    {
        var grid = _staffGridRule.Evaluate(candidate);
        var fillKind = candidate.Ellipse.Source.IsHollow
            ? "hollow"
            : "filled";

        if (profile.HasSmallDotCluster
            && profile.Threshold is not null
            && candidate.NormalizedSize < profile.Threshold.Value)
        {
            return new NoteheadDecision(
                candidate,
                false,
                "small-dot-size-cluster",
                grid,
                fillKind,
                0,
                $"normalized-size={candidate.NormalizedSize:F3} below threshold={profile.Threshold.Value:F3}");
        }

        if (!grid.IsAligned)
        {
            var hasStemSupport = HasRawStemSupport(
                candidate,
                document);

            if (hasStemSupport
                && grid.ErrorInHalfSteps <= MaximumStemSupportedGridError)
            {
                var rescuedGrid = _staffGridRule.Evaluate(
                    candidate,
                    MaximumStemSupportedGridError);

                if (rescuedGrid.IsValid)
                {
                    var rescueConfidence = Math.Clamp(
                        0.76
                        + 0.16
                        * (1.0
                            - rescuedGrid.ErrorInHalfSteps
                            / MaximumStemSupportedGridError),
                        0,
                        1);

                    return new NoteheadDecision(
                        candidate,
                        true,
                        "stem-supported-notehead",
                        rescuedGrid,
                        fillKind,
                        rescueConfidence,
                        $"center is {rescuedGrid.ErrorInHalfSteps:F3} half-step(s) "
                        + "from nearest staff position, outside the ordinary grid gate; "
                        + "rescued by a touching thin vertical stem; "
                        + $"{rescuedGrid.SupportReason}; fill={fillKind}");
                }

                grid = rescuedGrid;
            }

            return new NoteheadDecision(
                candidate,
                false,
                "off-staff-grid",
                grid,
                fillKind,
                0,
                $"center is {grid.ErrorInHalfSteps:F3} half-step(s) from nearest staff position; "
                + $"raw-stem-support={hasStemSupport}");
        }

        if (!grid.HasLedgerSupport)
        {
            return new NoteheadDecision(
                candidate,
                false,
                "unsupported-ledger-position",
                grid,
                fillKind,
                0,
                grid.SupportReason);
        }

        var alignmentScore = Math.Clamp(
            1.0 - grid.ErrorInHalfSteps / MaximumGridError,
            0,
            1);
        var confidence = 0.80 + alignmentScore * 0.20;
        var sizeReason = profile.HasSmallDotCluster
            ? $"size={candidate.NormalizedSize:F3}sp is above dot threshold={profile.Threshold:F3}sp"
            : $"size={candidate.NormalizedSize:F3}sp; no separate small-dot cluster was detected";

        return new NoteheadDecision(
            candidate,
            true,
            "notehead",
            grid,
            fillKind,
            confidence,
            $"{sizeReason}; staff-step={grid.NearestStep}; "
            + $"grid-error={grid.ErrorInHalfSteps:F3}; {grid.SupportReason}; fill={fillKind}");
    }

    private static bool HasRawStemSupport(
        NoteheadCandidate candidate,
        SemanticDocument document)
    {
        const double maximumVerticalRatio = 0.18;
        const double minimumLengthInSpacings = 1.25;
        const double maximumWidthInSpacings = 0.38;
        const double maximumHeadEdgeDistanceInSpacings = 0.34;
        const double verticalTouchToleranceInSpacings = 0.18;

        var spacing = candidate.LineSpacing;
        if (spacing <= 0)
        {
            return false;
        }

        var measure = document.Measures
            .FirstOrDefault(item =>
                item.Number == candidate.MeasureNumber);

        if (measure is null)
        {
            return false;
        }

        var ellipse = candidate.Ellipse.Source;
        var horizontalRadius = Math.Max(
            ellipse.MajorRadius,
            ellipse.MinorRadius);
        var verticalRadius = Math.Max(
            Math.Min(
                ellipse.MajorRadius,
                ellipse.MinorRadius),
            spacing * 0.22);
        var maximumEdgeDistance =
            spacing * maximumHeadEdgeDistanceInSpacings;
        var touchTolerance =
            spacing * verticalTouchToleranceInSpacings;

        return measure.Upper.Elements
            .OfType<StrokeElement>()
            .Concat(
                measure.Lower.Elements
                    .OfType<StrokeElement>())
            .GroupBy(
                stroke => stroke.ShapeId,
                StringComparer.Ordinal)
            .Select(group => group.First())
            .Any(stroke =>
            {
                var source = stroke.Source;
                var dx = Math.Abs(
                    source.End.X - source.Start.X);
                var dy = Math.Abs(
                    source.End.Y - source.Start.Y);
                var length = Math.Sqrt(
                    dx * dx + dy * dy);
                var verticalRatio = dy <= 0.001
                    ? double.PositiveInfinity
                    : dx / dy;
                var normalizedWidth =
                    source.Width / spacing;

                if (length < spacing * minimumLengthInSpacings
                    || verticalRatio > maximumVerticalRatio
                    || normalizedWidth > maximumWidthInSpacings)
                {
                    return false;
                }

                var stemX =
                    (source.Start.X + source.End.X) / 2.0;
                var leftEdge =
                    candidate.Ellipse.CenterX - horizontalRadius;
                var rightEdge =
                    candidate.Ellipse.CenterX + horizontalRadius;
                var edgeDistance = Math.Min(
                    Math.Abs(stemX - leftEdge),
                    Math.Abs(stemX - rightEdge));

                if (edgeDistance > maximumEdgeDistance)
                {
                    return false;
                }

                var stemMinY = Math.Min(
                    source.Start.Y,
                    source.End.Y);
                var stemMaxY = Math.Max(
                    source.Start.Y,
                    source.End.Y);
                var noteMinY =
                    candidate.Ellipse.CenterY - verticalRadius
                    - touchTolerance;
                var noteMaxY =
                    candidate.Ellipse.CenterY + verticalRadius
                    + touchTolerance;

                return stemMaxY >= noteMinY
                    && stemMinY <= noteMaxY;
            });
    }

    private sealed class EllipseCollector : SemanticVisitor
    {
        private readonly List<NoteheadCandidate> _candidates = [];

        public IReadOnlyList<NoteheadCandidate> Candidates => _candidates;

        protected override void VisitEllipse(
            MeasureScene measure,
            StaffMeasureScene staff,
            EllipseElement ellipse)
        {
            if (staff.LineSpacing <= 0)
            {
                return;
            }

            var equivalentDiameter = 2.0 * Math.Sqrt(
                Math.Max(ellipse.Source.MajorRadius, 0)
                * Math.Max(ellipse.Source.MinorRadius, 0));
            var normalizedSize = equivalentDiameter / staff.LineSpacing;

            _candidates.Add(new NoteheadCandidate(
                measure.Number,
                staff.StaffNumber,
                staff.StaffId,
                staff.StaffBounds.MinY,
                staff.LineSpacing,
                staff.LedgerLevels ?? [],
                ellipse,
                normalizedSize));
        }
    }
}
