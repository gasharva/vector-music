using SvgMusic.Canonical;
using SvgMusic.Scene;

namespace SvgMusic.Semantics;

public sealed record OttavaFact(
    string BracketId,
    string LabelShapeId,
    int StartMeasureNumber,
    int EndMeasureNumber,
    int Staff,
    string Direction,
    int Size,
    string Placement,
    string StartAt,
    string EndAt,
    double StartX,
    double EndX,
    double Confidence,
    string Reason,
    IReadOnlyList<string> SourceShapeIds)
    : SemanticFact(
        "OttavaPass",
        Reason,
        SourceShapeIds);

public sealed record OttavaDecision(
    string BracketId,
    bool Accepted,
    string Decision,
    string? LabelShapeId,
    int? StartMeasureNumber,
    int? EndMeasureNumber,
    int? Staff,
    string? Direction,
    string? Placement,
    string? StartAt,
    string? EndAt,
    double Confidence,
    string Reason);

public sealed record OttavaAnalysisResult(
    IReadOnlyList<OttavaDecision> Decisions)
{
    public IReadOnlyList<OttavaDecision> Accepted =>
        Decisions.Where(decision => decision.Accepted).ToArray();
}

/// <summary>
/// Interprets a classified OTTAVA glyph together with a generic dashed bracket
/// spanner. The scene parser remains music-agnostic: it only exposes the glyph,
/// bracket geometry and logical ownership. This pass decides that the combination
/// means an octave shift and projects its horizontal endpoints onto rhythmic time.
/// </summary>
public sealed class OttavaPass : ISemanticPass
{
    private const double MinimumLabelConfidence = 0.75;
    private const double MinimumGapInSpacings = -0.50;
    private const double MaximumGapInSpacings = 2.50;
    private const double MaximumVerticalDistanceInSpacings = 2.25;
    private const double OutsideStaffToleranceInSpacings = 0.10;

    public string Name => nameof(OttavaPass);

    public OttavaAnalysisResult? LastAnalysis { get; private set; }

    public void Run(
        SemanticDocument document,
        SemanticFacts facts)
    {
        var contexts = BuildCoordinateContexts(document);
        var labels = CollectLabels(document);
        var brackets = CollectBrackets(document);
        var timing = new TimingResolver(document, facts);
        var decisions = new List<OttavaDecision>();
        var usedLabels = new HashSet<string>(StringComparer.Ordinal);

        foreach (var bracket in brackets
                     .OrderBy(item => item.Source.Start.X)
                     .ThenBy(item => item.ShapeId, StringComparer.Ordinal))
        {
            var source = bracket.Source;

            if (!source.IsDashed)
            {
                decisions.Add(Reject(
                    source.Id,
                    "solid-bracket",
                    $"{source.Id}: generic bracket is solid; ottava line is expected to be dashed"));
                continue;
            }

            if (!contexts.TryGetValue(source.Ownership!.Start, out var startContext)
                || !contexts.TryGetValue(source.Ownership.End, out var endContext))
            {
                decisions.Add(Reject(
                    source.Id,
                    "unmapped-ownership",
                    $"{source.Id}: logical start/end cannot be mapped back to semantic measures"));
                continue;
            }

            if (startContext.StaffNumber != endContext.StaffNumber)
            {
                decisions.Add(Reject(
                    source.Id,
                    "cross-staff-bracket",
                    $"{source.Id}: ottava candidate crosses staff {startContext.StaffNumber}->{endContext.StaffNumber}"));
                continue;
            }

            var placement = ResolvePlacement(source, startContext, out var direction);
            if (placement is null || direction is null)
            {
                decisions.Add(Reject(
                    source.Id,
                    "inside-staff-bracket",
                    $"{source.Id}: dashed bracket baseline lies inside the staff rather than above/below it"));
                continue;
            }

            var match = FindLabel(
                source,
                startContext,
                labels,
                usedLabels);

            if (match is null)
            {
                decisions.Add(Reject(
                    source.Id,
                    "no-ottava-label",
                    $"{source.Id}: no high-confidence OTTAVA glyph is positioned at the bracket start"));
                continue;
            }

            usedLabels.Add(match.Label.ShapeId);

            var startAt = timing.ResolveAt(
                startContext.MeasureNumber,
                startContext.StaffNumber,
                source.Start.X);
            var endAt = timing.ResolveAt(
                endContext.MeasureNumber,
                endContext.StaffNumber,
                source.End.X);
            var confidence = Math.Clamp(
                0.55
                + 0.30 * match.Label.Classification!.Confidence
                + 0.10 * source.Confidence
                + 0.05 * (1.0 - Math.Min(match.Score / 3.0, 1.0)),
                0.60,
                0.99);
            var reason =
                $"{source.Id}: OTTAVA {match.Label.ShapeId} ({match.Label.Classification.Confidence:P0}) "
                + $"pairs with dashed bracket; gap={match.GapInSpacings:F2}sp, "
                + $"vertical={match.VerticalDistanceInSpacings:F2}sp; "
                + $"{placement}/{direction}, m{startContext.MeasureNumber}:{startAt} -> "
                + $"m{endContext.MeasureNumber}:{endAt}";
            var sourceShapeIds = new[] { match.Label.ShapeId }
                .Concat(source.SourceShapeIds)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            facts.Add(new OttavaFact(
                source.Id,
                match.Label.ShapeId,
                startContext.MeasureNumber,
                endContext.MeasureNumber,
                startContext.StaffNumber,
                direction,
                8,
                placement,
                startAt,
                endAt,
                source.Start.X,
                source.End.X,
                confidence,
                reason,
                sourceShapeIds));

            decisions.Add(new OttavaDecision(
                source.Id,
                true,
                "octave-shift",
                match.Label.ShapeId,
                startContext.MeasureNumber,
                endContext.MeasureNumber,
                startContext.StaffNumber,
                direction,
                placement,
                startAt,
                endAt,
                confidence,
                reason));
        }

        LastAnalysis = new OttavaAnalysisResult(decisions);

        facts.AddTrace(
            $"OttavaPass: brackets={brackets.Count}; labels={labels.Count}; "
            + $"accepted={decisions.Count(decision => decision.Accepted)}; "
            + $"unmatched={decisions.Count(decision => decision.Decision == "no-ottava-label")}");
    }

    private static IReadOnlyDictionary<LogicalCoordinate, CoordinateContext> BuildCoordinateContexts(
        SemanticDocument document)
    {
        var result = new Dictionary<LogicalCoordinate, CoordinateContext>();

        foreach (var measure in document.Measures)
        {
            foreach (var staff in new[] { measure.Upper, measure.Lower })
            {
                result[new LogicalCoordinate(staff.StaffId, measure.LayoutMeasureId)] =
                    new CoordinateContext(
                        measure.Number,
                        staff.StaffNumber,
                        staff.StaffBounds,
                        staff.LineSpacing,
                        measure.XStart,
                        measure.XEnd);
            }
        }

        return result;
    }

    private static IReadOnlyList<ShapeElement> CollectLabels(SemanticDocument document)
    {
        return document.Measures
            .SelectMany(measure => new[] { measure.Upper, measure.Lower })
            .SelectMany(staff => staff.Elements.OfType<ShapeElement>())
            .Where(shape =>
                shape.Classification is not null
                && shape.Classification.Confidence >= MinimumLabelConfidence
                && string.Equals(
                    shape.Classification.Label,
                    "OTTAVA",
                    StringComparison.OrdinalIgnoreCase))
            .GroupBy(shape => shape.ShapeId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
    }

    private static IReadOnlyList<BracketSpannerElement> CollectBrackets(SemanticDocument document)
    {
        return document.Measures
            .SelectMany(measure => new[] { measure.Upper, measure.Lower })
            .SelectMany(staff => staff.Elements.OfType<BracketSpannerElement>())
            .GroupBy(bracket => bracket.ShapeId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
    }

    private static LabelMatch? FindLabel(
        BracketSpannerPrimitive bracket,
        CoordinateContext context,
        IReadOnlyList<ShapeElement> labels,
        IReadOnlySet<string> usedLabels)
    {
        var spacing = Math.Max(context.LineSpacing, 0.001);
        var baselineY = (bracket.Start.Y + bracket.End.Y) / 2.0;

        return labels
            .Where(label => !usedLabels.Contains(label.ShapeId))
            .Where(label =>
                label.Ownership.Start == bracket.Ownership!.Start
                || label.Ownership.End == bracket.Ownership.Start)
            .Select(label =>
            {
                var gap = (bracket.Start.X - label.Bounds.MaxX) / spacing;
                var vertical = Math.Abs(label.CenterY - baselineY) / spacing;
                var score = Math.Abs(gap - 0.60) + 0.35 * vertical;
                return new LabelMatch(label, gap, vertical, score);
            })
            .Where(match =>
                match.GapInSpacings >= MinimumGapInSpacings
                && match.GapInSpacings <= MaximumGapInSpacings
                && match.VerticalDistanceInSpacings <= MaximumVerticalDistanceInSpacings)
            .OrderBy(match => match.Score)
            .ThenByDescending(match => match.Label.Classification!.Confidence)
            .FirstOrDefault();
    }

    private static string? ResolvePlacement(
        BracketSpannerPrimitive bracket,
        CoordinateContext context,
        out string? direction)
    {
        var baselineY = (bracket.Start.Y + bracket.End.Y) / 2.0;
        var tolerance = Math.Max(context.LineSpacing, 0.001)
            * OutsideStaffToleranceInSpacings;

        if (baselineY <= context.StaffBounds.MinY - tolerance)
        {
            direction = "down";
            return "above";
        }

        if (baselineY >= context.StaffBounds.MaxY + tolerance)
        {
            direction = "up";
            return "below";
        }

        direction = null;
        return null;
    }

    private static OttavaDecision Reject(
        string bracketId,
        string decision,
        string reason)
    {
        return new OttavaDecision(
            bracketId,
            false,
            decision,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            0,
            reason);
    }

    private sealed record CoordinateContext(
        int MeasureNumber,
        int StaffNumber,
        BoundsD StaffBounds,
        double LineSpacing,
        double XStart,
        double XEnd);

    private sealed record LabelMatch(
        ShapeElement Label,
        double GapInSpacings,
        double VerticalDistanceInSpacings,
        double Score);

    private sealed class TimingResolver
    {
        private readonly SemanticDocument _document;
        private readonly IReadOnlyList<OnsetFact> _onsets;
        private readonly IReadOnlyDictionary<string, DurationFact> _durations;
        private readonly IReadOnlyDictionary<string, ChordFact> _chords;
        private readonly IReadOnlyDictionary<string, RestFact> _rests;
        private readonly IReadOnlyDictionary<string, RestDotAttachmentFact> _restDots;

        public TimingResolver(
            SemanticDocument document,
            SemanticFacts facts)
        {
            _document = document;
            _onsets = facts.OfType<OnsetFact>().ToArray();
            _durations = facts
                .OfType<DurationFact>()
                .GroupBy(fact => fact.NoteheadId, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderByDescending(fact => fact.Confidence).First(),
                    StringComparer.Ordinal);
            _chords = facts
                .OfType<ChordFact>()
                .GroupBy(fact => fact.ChordId, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderByDescending(fact => fact.Confidence).First(),
                    StringComparer.Ordinal);
            _rests = facts
                .OfType<RestFact>()
                .GroupBy(fact => fact.ShapeId, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderByDescending(fact => fact.Confidence).First(),
                    StringComparer.Ordinal);
            _restDots = facts
                .OfType<RestDotAttachmentFact>()
                .GroupBy(fact => fact.TargetRestShapeId, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderByDescending(fact => fact.Confidence).First(),
                    StringComparer.Ordinal);
        }

        public string ResolveAt(
            int measureNumber,
            int staff,
            double x)
        {
            var measure = _document.Measures.Single(item => item.Number == measureNumber);
            var anchors = new List<TimeAnchorCandidate>
            {
                new(measure.XStart, Fraction.Zero)
            };

            foreach (var onset in _onsets
                         .Where(item => item.MeasureNumber == measureNumber && item.Staff == staff))
            {
                anchors.Add(new TimeAnchorCandidate(
                    onset.AnchorX,
                    Fraction.Parse(onset.At)));
            }

            anchors.Add(new TimeAnchorCandidate(
                measure.XEnd,
                MeasureDuration(measureNumber)));

            return anchors
                .OrderBy(anchor => Math.Abs(anchor.X - x))
                .ThenBy(anchor => FractionValue(anchor.At))
                .First()
                .At
                .ToString();
        }

        private Fraction MeasureDuration(int measureNumber)
        {
            var best = Fraction.Zero;

            foreach (var onset in _onsets.Where(item => item.MeasureNumber == measureNumber))
            {
                var at = Fraction.Parse(onset.At);
                var duration = Duration(onset);
                var end = at + duration;

                if (FractionValue(end) > FractionValue(best))
                {
                    best = end;
                }
            }

            return best;
        }

        private Fraction Duration(OnsetFact onset)
        {
            return onset.TargetKind switch
            {
                VoiceTargetKind.Notehead =>
                    _durations.TryGetValue(onset.TargetId, out var duration)
                        ? Fraction.Parse(duration.EffectiveDuration)
                        : Fraction.Zero,

                VoiceTargetKind.Chord => ChordDuration(onset.TargetId),

                VoiceTargetKind.Rest => RestDuration(onset.TargetId),

                _ => Fraction.Zero
            };
        }

        private Fraction ChordDuration(string chordId)
        {
            if (!_chords.TryGetValue(chordId, out var chord))
            {
                return Fraction.Zero;
            }

            foreach (var noteheadId in chord.NoteheadIds)
            {
                if (_durations.TryGetValue(noteheadId, out var duration))
                {
                    return Fraction.Parse(duration.EffectiveDuration);
                }
            }

            return Fraction.Zero;
        }

        private Fraction RestDuration(string restId)
        {
            if (!_rests.TryGetValue(restId, out var rest))
            {
                return Fraction.Zero;
            }

            var dots = _restDots.TryGetValue(restId, out var dot)
                ? dot.Count
                : 0;
            return Fraction.Parse(DurationMath.ApplyDots(rest.Duration, dots));
        }

        private static double FractionValue(Fraction value) =>
            value.Numerator / (double)value.Denominator;

        private sealed record TimeAnchorCandidate(
            double X,
            Fraction At);
    }
}
