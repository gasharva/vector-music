using SvgMusic.Scene;

namespace SvgMusic.Semantics;

public sealed record NoteheadCandidate(
    int MeasureNumber,
    int StaffNumber,
    string StaffId,
    double StaffTopY,
    double LineSpacing,
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
    int NearestStep,
    double ErrorInHalfSteps);

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

    public StaffGridMatch Evaluate(NoteheadCandidate candidate)
    {
        var halfStep = candidate.LineSpacing / 2.0;

        if (halfStep <= 0)
        {
            return new StaffGridMatch(
                false,
                0,
                double.PositiveInfinity);
        }

        var rawStep = (candidate.Ellipse.CenterY - candidate.StaffTopY) / halfStep;
        var nearestStep = (int)Math.Round(rawStep);
        var error = Math.Abs(rawStep - nearestStep);

        return new StaffGridMatch(
            error <= MaximumErrorInHalfSteps,
            nearestStep,
            error);
    }
}

public sealed class NoteheadAnalyzer
{
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
            .Select(candidate => Decide(candidate, profile))
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
        EllipseSizeProfile profile)
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
            return new NoteheadDecision(
                candidate,
                false,
                "off-staff-grid",
                grid,
                fillKind,
                0,
                $"center is {grid.ErrorInHalfSteps:F3} half-step(s) from nearest staff/ledger position");
        }

        var alignmentScore = Math.Clamp(
            1.0 - grid.ErrorInHalfSteps / 0.30,
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
            $"{sizeReason}; staff-step={grid.NearestStep}; grid-error={grid.ErrorInHalfSteps:F3}; fill={fillKind}");
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
                ellipse,
                normalizedSize));
        }
    }
}
