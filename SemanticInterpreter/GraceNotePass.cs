namespace SvgMusic.Semantics;

public sealed record GraceNoteFact(
    int MeasureNumber,
    int Staff,
    string NoteheadId,
    double NormalizedSize,
    double RegularNoteheadMedian,
    double Threshold,
    double Confidence,
    string Reason,
    IReadOnlyList<string> SourceShapeIds)
    : SemanticFact(
        "GraceNotePass",
        Reason,
        SourceShapeIds);

public sealed record GraceNoteSizeProfile(
    bool HasGraceCluster,
    double? Threshold,
    int GraceCount,
    int RegularCount,
    double? GraceMedian,
    double? RegularMedian,
    IReadOnlyList<NoteheadFact> RankedNoteheads);

public sealed record GraceNoteAnalysisResult(
    GraceNoteSizeProfile SizeProfile,
    IReadOnlyList<GraceNoteFact> GraceNotes);

/// <summary>
/// Tags a reduced-size subcluster inside already accepted noteheads.
/// Dots have already been removed by NoteheadPass, so this is deliberately a
/// second split: dot-sized ellipses never reach this pass, while grace heads keep
/// using the normal pitch/stem/beam pipeline.
/// </summary>
public sealed class GraceNotePass : ISemanticPass
{
    private const double MinimumGapRatio = 1.25;
    private const double MaximumGraceToRegularMedianRatio = 0.82;
    private const double MinimumGraceMedian = 0.60;
    private const double MaximumGraceMedian = 0.95;
    private const double MinimumRegularMedian = 0.90;
    private const double MaximumRegularMedian = 1.60;
    private const double MaximumGraceFraction = 0.25;
    private const double MaximumGraceSpreadRatio = 1.18;

    public string Name => nameof(GraceNotePass);

    public GraceNoteAnalysisResult? LastAnalysis { get; private set; }

    public void Run(
        SemanticDocument document,
        SemanticFacts facts)
    {
        var ranked = facts
            .OfType<NoteheadFact>()
            .Where(notehead => notehead.NormalizedSize > 0)
            .OrderBy(notehead => notehead.NormalizedSize)
            .ThenBy(notehead => notehead.ShapeId, StringComparer.Ordinal)
            .ToArray();

        var profile = Analyze(ranked);
        var graceNotes = new List<GraceNoteFact>();

        if (profile.HasGraceCluster
            && profile.Threshold is not null
            && profile.RegularMedian is not null)
        {
            foreach (var notehead in ranked
                         .Where(notehead => notehead.NormalizedSize < profile.Threshold.Value))
            {
                var gapRatio = profile.RegularMedian.Value
                    / Math.Max(notehead.NormalizedSize, 0.001);
                var confidence = 0.88 + Math.Clamp(
                    (gapRatio - MinimumGapRatio) / 0.50,
                    0,
                    1) * 0.11;
                var reason =
                    $"accepted notehead belongs to reduced-size notehead cluster; "
                    + $"size={notehead.NormalizedSize:F3}sp; "
                    + $"regular-median={profile.RegularMedian.Value:F3}sp; "
                    + $"threshold={profile.Threshold.Value:F3}sp";

                var fact = new GraceNoteFact(
                    notehead.MeasureNumber,
                    notehead.Staff,
                    notehead.ShapeId,
                    notehead.NormalizedSize,
                    profile.RegularMedian.Value,
                    profile.Threshold.Value,
                    Math.Clamp(confidence, 0, 0.99),
                    reason,
                    notehead.SourceShapeIds);
                facts.Add(fact);
                graceNotes.Add(fact);
            }
        }

        LastAnalysis = new GraceNoteAnalysisResult(profile, graceNotes);
        facts.AddTrace(
            $"GraceNotePass: noteheads={ranked.Length}; "
            + $"cluster={profile.HasGraceCluster}; grace={graceNotes.Count}; "
            + $"threshold={(profile.Threshold is null ? "-" : profile.Threshold.Value.ToString("F3", System.Globalization.CultureInfo.InvariantCulture))}; "
            + $"grace-median={(profile.GraceMedian is null ? "-" : profile.GraceMedian.Value.ToString("F3", System.Globalization.CultureInfo.InvariantCulture))}; "
            + $"regular-median={(profile.RegularMedian is null ? "-" : profile.RegularMedian.Value.ToString("F3", System.Globalization.CultureInfo.InvariantCulture))}");
    }

    private static GraceNoteSizeProfile Analyze(
        IReadOnlyList<NoteheadFact> ranked)
    {
        if (ranked.Count < 3)
        {
            return NoSplit(ranked);
        }

        SplitCandidate? best = null;

        for (var splitIndex = 1; splitIndex < ranked.Count; splitIndex++)
        {
            var lower = ranked.Take(splitIndex).ToArray();
            var upper = ranked.Skip(splitIndex).ToArray();

            if (upper.Length < 2
                || lower.Length / (double)ranked.Count > MaximumGraceFraction)
            {
                continue;
            }

            var graceMedian = Median(lower.Select(notehead => notehead.NormalizedSize));
            var regularMedian = Median(upper.Select(notehead => notehead.NormalizedSize));
            var lowerMin = lower.Min(notehead => notehead.NormalizedSize);
            var lowerMax = lower.Max(notehead => notehead.NormalizedSize);
            var gapRatio = upper[0].NormalizedSize / Math.Max(lower[^1].NormalizedSize, 0.001);
            var spreadRatio = lowerMax / Math.Max(lowerMin, 0.001);

            if (gapRatio < MinimumGapRatio
                || graceMedian < MinimumGraceMedian
                || graceMedian > MaximumGraceMedian
                || regularMedian < MinimumRegularMedian
                || regularMedian > MaximumRegularMedian
                || graceMedian > regularMedian * MaximumGraceToRegularMedianRatio
                || spreadRatio > MaximumGraceSpreadRatio)
            {
                continue;
            }

            var score = Math.Log(gapRatio)
                - Math.Log(Math.Max(spreadRatio, 1.0)) * 0.25;
            if (best is null || score > best.Score)
            {
                best = new SplitCandidate(
                    splitIndex,
                    graceMedian,
                    regularMedian,
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

        return new GraceNoteSizeProfile(
            true,
            threshold,
            best.SplitIndex,
            ranked.Count - best.SplitIndex,
            best.GraceMedian,
            best.RegularMedian,
            ranked);
    }

    private static GraceNoteSizeProfile NoSplit(
        IReadOnlyList<NoteheadFact> ranked)
    {
        var median = ranked.Count == 0
            ? (double?)null
            : Median(ranked.Select(notehead => notehead.NormalizedSize));
        return new GraceNoteSizeProfile(
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
        return ordered.Length % 2 == 1
            ? ordered[middle]
            : (ordered[middle - 1] + ordered[middle]) / 2.0;
    }

    private sealed record SplitCandidate(
        int SplitIndex,
        double GraceMedian,
        double RegularMedian,
        double Score);
}
