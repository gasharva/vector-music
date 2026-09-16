using SvgMusic.Canonical;
using SvgMusic.Scene;

namespace SvgMusic.Semantics;

public sealed record PedalFact(
    string BracketId,
    string LabelShapeId,
    int StartMeasureNumber,
    int EndMeasureNumber,
    int Staff,
    string Placement,
    string StartAt,
    string EndAt,
    double StartX,
    double EndX,
    bool Line,
    bool StartMark,
    double Confidence,
    string Reason,
    IReadOnlyList<string> SourceShapeIds)
    : SemanticFact(
        "PedalPass",
        Reason,
        SourceShapeIds);

public sealed record PedalDecision(
    string BracketId,
    bool Accepted,
    string Decision,
    string? LabelShapeId,
    int? StartMeasureNumber,
    int? EndMeasureNumber,
    int? Staff,
    string? StartAt,
    string? EndAt,
    double Confidence,
    string Reason);

public sealed record PedalAnalysisResult(
    IReadOnlyList<PedalDecision> Decisions)
{
    public IReadOnlyList<PedalDecision> Accepted =>
        Decisions.Where(decision => decision.Accepted).ToArray();
}

/// <summary>
/// Interprets a classified PEDAL_MARK glyph together with a generic solid bracket.
/// The scene parser remains purely geometric: pedal meaning is introduced only here.
/// Ownership supplies the staff/system hint, while the bracket's real X endpoints
/// determine the semantic measure span.
/// </summary>
public sealed class PedalPass : ISemanticPass
{
    private const double MinimumLabelConfidence = 0.75;
    private const double MinimumGapInSpacings = -0.50;
    private const double MaximumGapInSpacings = 2.50;
    private const double MaximumVerticalDistanceInSpacings = 2.25;
    private const double OutsideStaffToleranceInSpacings = 0.10;
    private const double CoordinateEpsilon = 0.01;

    public string Name => nameof(PedalPass);

    public PedalAnalysisResult? LastAnalysis { get; private set; }

    public void Run(
        SemanticDocument document,
        SemanticFacts facts)
    {
        var contexts = BuildCoordinateContexts(document);
        var labels = CollectLabels(document);
        var brackets = CollectBrackets(document);
        var timing = new TimingResolver(document, facts);
        var decisions = new List<PedalDecision>();
        var usedLabels = new HashSet<string>(StringComparer.Ordinal);

        foreach (var bracket in brackets
                     .OrderBy(item => item.Source.Start.X)
                     .ThenBy(item => item.ShapeId, StringComparer.Ordinal))
        {
            var source = bracket.Source;

            if (source.IsDashed)
            {
                decisions.Add(Reject(
                    source.Id,
                    "dashed-bracket",
                    $"{source.Id}: pedal line is expected to be solid"));
                continue;
            }

            if (source.Ownership is null)
            {
                decisions.Add(Reject(
                    source.Id,
                    "unmapped-ownership",
                    $"{source.Id}: bracket has no logical ownership"));
                continue;
            }

            var staffId = source.Ownership.Start.StaffId;
            var startContext = ResolveContextAtX(
                contexts,
                staffId,
                source.Start.X,
                source.Ownership.Start,
                preferLaterAtBoundary: true);
            var endContext = ResolveContextAtX(
                contexts,
                staffId,
                source.End.X,
                source.Ownership.End,
                preferLaterAtBoundary: false);

            if (startContext is null || endContext is null)
            {
                decisions.Add(Reject(
                    source.Id,
                    "unmapped-ownership",
                    $"{source.Id}: bracket X endpoints cannot be mapped to semantic measures"));
                continue;
            }

            if (startContext.StaffNumber != endContext.StaffNumber)
            {
                decisions.Add(Reject(
                    source.Id,
                    "cross-staff-bracket",
                    $"{source.Id}: pedal candidate crosses staff {startContext.StaffNumber}->{endContext.StaffNumber}"));
                continue;
            }

            if (!IsBelowStaff(source, startContext))
            {
                decisions.Add(Reject(
                    source.Id,
                    "not-below-staff",
                    $"{source.Id}: solid bracket baseline is not below its owning staff"));
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
                    "no-pedal-label",
                    $"{source.Id}: no high-confidence PEDAL_MARK glyph is positioned at the bracket start"));
                continue;
            }

            usedLabels.Add(match.Label.ShapeId);

            // The PEDAL_MARK itself is the musical down-point. The horizontal
            // continuation line starts to its right and can already be closer to the
            // next note, so using the line start would shift the pedal onset late.
            var startAt = timing.ResolveStartAt(
                startContext.MeasureNumber,
                startContext.StaffNumber,
                match.Label.CenterX);
            var endAt = timing.ResolveEndAt(
                endContext.MeasureNumber,
                endContext.StaffNumber,
                source.End.X);
            var hookEvidence = source.RightHookDirection == BracketHookDirection.Up
                ? 1.0
                : 0.5;
            var confidence = Math.Clamp(
                0.50
                + 0.30 * match.Label.Classification!.Confidence
                + 0.10 * source.Confidence
                + 0.05 * (1.0 - Math.Min(match.Score / 3.0, 1.0))
                + 0.05 * hookEvidence,
                0.60,
                0.99);
            var reason =
                $"{source.Id}: PEDAL_MARK {match.Label.ShapeId} ({match.Label.Classification.Confidence:P0}) "
                + $"pairs with solid bracket below staff; gap={match.GapInSpacings:F2}sp, "
                + $"vertical={match.VerticalDistanceInSpacings:F2}sp, "
                + $"right-hook={source.RightHookDirection}; "
                + $"m{startContext.MeasureNumber}:{startAt} -> m{endContext.MeasureNumber}:{endAt}";
            var sourceShapeIds = new[] { match.Label.ShapeId }
                .Concat(source.SourceShapeIds)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            facts.Add(new PedalFact(
                source.Id,
                match.Label.ShapeId,
                startContext.MeasureNumber,
                endContext.MeasureNumber,
                startContext.StaffNumber,
                "below",
                startAt,
                endAt,
                source.Start.X,
                source.End.X,
                true,
                true,
                confidence,
                reason,
                sourceShapeIds));

            decisions.Add(new PedalDecision(
                source.Id,
                true,
                "pedal",
                match.Label.ShapeId,
                startContext.MeasureNumber,
                endContext.MeasureNumber,
                startContext.StaffNumber,
                startAt,
                endAt,
                confidence,
                reason));
        }

        LastAnalysis = new PedalAnalysisResult(decisions);

        facts.AddTrace(
            $"PedalPass: brackets={brackets.Count}; labels={labels.Count}; "
            + $"accepted={decisions.Count(decision => decision.Accepted)}; "
            + $"unmatched={decisions.Count(decision => decision.Decision == "no-pedal-label")}");
    }

    private static IReadOnlyList<CoordinateContext> BuildCoordinateContexts(
        SemanticDocument document)
    {
        var result = new List<CoordinateContext>();

        foreach (var measure in document.Measures)
        {
            foreach (var staff in new[] { measure.Upper, measure.Lower })
            {
                result.Add(new CoordinateContext(
                    new LogicalCoordinate(staff.StaffId, measure.LayoutMeasureId),
                    measure.Number,
                    staff.StaffNumber,
                    staff.StaffBounds,
                    staff.LineSpacing,
                    measure.XStart,
                    measure.XEnd));
            }
        }

        return result;
    }

    private static CoordinateContext? ResolveContextAtX(
        IReadOnlyList<CoordinateContext> contexts,
        string staffId,
        double x,
        LogicalCoordinate fallback,
        bool preferLaterAtBoundary)
    {
        var matchingStaff = contexts
            .Where(context => string.Equals(
                context.Coordinate.StaffId,
                staffId,
                StringComparison.Ordinal))
            .ToArray();

        var containing = matchingStaff
            .Where(context =>
                x >= context.XStart - CoordinateEpsilon
                && x <= context.XEnd + CoordinateEpsilon)
            .ToArray();

        if (containing.Length > 0)
        {
            return preferLaterAtBoundary
                ? containing
                    .OrderByDescending(context => context.XStart)
                    .ThenBy(context => context.MeasureNumber)
                    .First()
                : containing
                    .OrderBy(context => context.XEnd)
                    .ThenByDescending(context => context.MeasureNumber)
                    .First();
        }

        return contexts.FirstOrDefault(context => context.Coordinate == fallback);
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
                    "PEDAL_MARK",
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
                label.Ownership.Start == context.Coordinate
                || label.Ownership.End == context.Coordinate)
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

    private static bool IsBelowStaff(
        BracketSpannerPrimitive bracket,
        CoordinateContext context)
    {
        var baselineY = (bracket.Start.Y + bracket.End.Y) / 2.0;
        var tolerance = Math.Max(context.LineSpacing, 0.001)
            * OutsideStaffToleranceInSpacings;
        return baselineY >= context.StaffBounds.MaxY + tolerance;
    }

    private static PedalDecision Reject(
        string bracketId,
        string decision,
        string reason)
    {
        return new PedalDecision(
            bracketId,
            false,
            decision,
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
        LogicalCoordinate Coordinate,
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

        public string ResolveStartAt(
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

        public string ResolveEndAt(
            int measureNumber,
            int staff,
            double x)
        {
            var measureDuration = MeasureDuration(measureNumber);
            var candidate = _onsets
                .Where(onset =>
                    onset.MeasureNumber == measureNumber
                    && onset.Staff == staff
                    && onset.AnchorX <= x + CoordinateEpsilon)
                .OrderByDescending(onset => onset.AnchorX)
                .ThenByDescending(onset => FractionValue(Fraction.Parse(onset.At)))
                .FirstOrDefault();

            if (candidate is null)
            {
                return ResolveStartAt(measureNumber, staff, x);
            }

            var end = Fraction.Parse(candidate.At) + Duration(candidate);
            if (FractionValue(measureDuration) > 0
                && FractionValue(end) > FractionValue(measureDuration))
            {
                end = measureDuration;
            }

            return end.ToString();
        }

        private Fraction MeasureDuration(int measureNumber)
        {
            var best = Fraction.Zero;

            foreach (var onset in _onsets.Where(item => item.MeasureNumber == measureNumber))
            {
                var end = Fraction.Parse(onset.At) + Duration(onset);
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
