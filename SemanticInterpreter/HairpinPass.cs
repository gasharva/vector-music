using SvgMusic.Canonical;
using SvgMusic.Scene;

namespace SvgMusic.Semantics;

public sealed record HairpinFact(
    string ShapeId,
    string Type,
    int StartMeasureNumber,
    int EndMeasureNumber,
    int Staff,
    string Placement,
    string StartAt,
    string EndAt,
    double StartX,
    double EndX,
    double Confidence,
    string Reason,
    IReadOnlyList<string> SourceShapeIds)
    : SemanticFact(
        "HairpinPass",
        Reason,
        SourceShapeIds);

public sealed record HairpinDecision(
    string ShapeId,
    bool Accepted,
    string Decision,
    string? Type,
    int? StartMeasureNumber,
    int? EndMeasureNumber,
    int? Staff,
    string? StartAt,
    string? EndAt,
    double Confidence,
    string Reason);

public sealed record HairpinAnalysisResult(
    IReadOnlyList<HairpinDecision> Decisions)
{
    public IReadOnlyList<HairpinDecision> Accepted =>
        Decisions.Where(decision => decision.Accepted).ToArray();
}

/// <summary>
/// Projects already-recognized geometric wedges onto musical time. The parser owns
/// the geometric decision (including crescendo versus diminuendo); this pass only
/// assigns staff, placement and rhythmic anchors.
/// </summary>
public sealed class HairpinPass : ISemanticPass
{
    private const double MinimumConfidence = 0.75;
    private const double CoordinateEpsilon = 0.01;

    public string Name => nameof(HairpinPass);

    public HairpinAnalysisResult? LastAnalysis { get; private set; }

    public void Run(
        SemanticDocument document,
        SemanticFacts facts)
    {
        var contexts = BuildCoordinateContexts(document);
        var timing = new TimingResolver(document, facts);
        var decisions = new List<HairpinDecision>();

        foreach (var element in CollectHairpins(document)
                     .OrderBy(item => LeftX(item.Source))
                     .ThenBy(item => item.ShapeId, StringComparer.Ordinal))
        {
            var source = element.Source;

            if (source.Confidence < MinimumConfidence)
            {
                decisions.Add(Reject(
                    source.ShapeId,
                    "low-confidence",
                    source.Confidence,
                    $"{source.ShapeId}: geometric hairpin confidence {source.Confidence:P0} is below {MinimumConfidence:P0}"));
                continue;
            }

            if (source.Ownership is null)
            {
                decisions.Add(Reject(
                    source.ShapeId,
                    "unmapped-ownership",
                    source.Confidence,
                    $"{source.ShapeId}: hairpin has no logical ownership"));
                continue;
            }

            if (!string.Equals(
                    source.Ownership.Start.StaffId,
                    source.Ownership.End.StaffId,
                    StringComparison.Ordinal))
            {
                decisions.Add(Reject(
                    source.ShapeId,
                    "cross-staff-hairpin",
                    source.Confidence,
                    $"{source.ShapeId}: hairpin ownership crosses staffs "
                    + $"{source.Ownership.Start.StaffId}->{source.Ownership.End.StaffId}"));
                continue;
            }

            var startX = LeftX(source);
            var endX = RightX(source);
            var musicalStaff = ResolveMusicalStaffContext(
                contexts,
                source,
                startX,
                endX,
                timing);

            if (musicalStaff is null)
            {
                decisions.Add(Reject(
                    source.ShapeId,
                    "unmapped-staff",
                    source.Confidence,
                    $"{source.ShapeId}: cannot resolve a musical staff inside the owned piano pair"));
                continue;
            }

            var staffId = musicalStaff.Coordinate.StaffId;
            var startFallback = new LogicalCoordinate(
                staffId,
                source.Ownership.Start.MeasureId);
            var endFallback = new LogicalCoordinate(
                staffId,
                source.Ownership.End.MeasureId);
            var startContext = ResolveContextAtX(
                contexts,
                staffId,
                startX,
                startFallback,
                preferLaterAtBoundary: true);
            var endContext = ResolveContextAtX(
                contexts,
                staffId,
                endX,
                endFallback,
                preferLaterAtBoundary: false);

            if (startContext is null || endContext is null)
            {
                decisions.Add(Reject(
                    source.ShapeId,
                    "unmapped-span",
                    source.Confidence,
                    $"{source.ShapeId}: X endpoints cannot be mapped to semantic measures"));
                continue;
            }

            if (startContext.StaffNumber != endContext.StaffNumber)
            {
                decisions.Add(Reject(
                    source.ShapeId,
                    "cross-staff-hairpin",
                    source.Confidence,
                    $"{source.ShapeId}: mapped span crosses staff "
                    + $"{startContext.StaffNumber}->{endContext.StaffNumber}"));
                continue;
            }

            var startAt = timing.ResolveStartAt(
                startContext.MeasureNumber,
                startContext.StaffNumber,
                startX);
            var endAt = timing.ResolveEndAt(
                endContext.MeasureNumber,
                endContext.StaffNumber,
                endX);

            if (!IsForwardSpan(
                    startContext.MeasureNumber,
                    startAt,
                    endContext.MeasureNumber,
                    endAt))
            {
                decisions.Add(Reject(
                    source.ShapeId,
                    "non-positive-span",
                    source.Confidence,
                    $"{source.ShapeId}: resolved span is not forward in musical time: "
                    + $"m{startContext.MeasureNumber}:{startAt} -> m{endContext.MeasureNumber}:{endAt}"));
                continue;
            }

            var type = source.Kind == HairpinKind.Crescendo
                ? "crescendo"
                : "diminuendo";
            var placement = ResolvePlacement(source, startContext);
            var reason =
                $"{source.ShapeId}: geometric {type} ({source.Confidence:P0}); "
                + $"genericOwnership={source.Ownership.Start.StaffId}; "
                + $"semanticStaff={staffId}/{startContext.StaffNumber}; placement={placement}; "
                + $"x={startX:F2}->{endX:F2}; "
                + $"m{startContext.MeasureNumber}:{startAt} -> m{endContext.MeasureNumber}:{endAt}";

            facts.Add(new HairpinFact(
                source.ShapeId,
                type,
                startContext.MeasureNumber,
                endContext.MeasureNumber,
                startContext.StaffNumber,
                placement,
                startAt,
                endAt,
                startX,
                endX,
                source.Confidence,
                reason,
                [source.ShapeId]));

            decisions.Add(new HairpinDecision(
                source.ShapeId,
                true,
                "hairpin",
                type,
                startContext.MeasureNumber,
                endContext.MeasureNumber,
                startContext.StaffNumber,
                startAt,
                endAt,
                source.Confidence,
                reason));
        }

        LastAnalysis = new HairpinAnalysisResult(decisions);
        facts.AddTrace(
            $"HairpinPass: candidates={decisions.Count}; "
            + $"accepted={decisions.Count(decision => decision.Accepted)}; "
            + $"rejected={decisions.Count(decision => !decision.Accepted)}");
    }

    private static IReadOnlyList<HairpinElement> CollectHairpins(
        SemanticDocument document)
    {
        return document.Measures
            .SelectMany(measure => new[] { measure.Upper, measure.Lower })
            .SelectMany(staff => staff.Elements.OfType<HairpinElement>())
            .GroupBy(hairpin => hairpin.ShapeId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
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
                    measure.SystemId,
                    measure.PairId,
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

    private static CoordinateContext? ResolveMusicalStaffContext(
        IReadOnlyList<CoordinateContext> contexts,
        HairpinPrimitive source,
        double startX,
        double endX,
        TimingResolver timing)
    {
        var ownership = source.Ownership!;
        var ownershipContext = contexts.FirstOrDefault(context =>
            context.Coordinate == ownership.Start);

        if (ownershipContext is null)
        {
            return null;
        }

        var y = CenterY(source);
        var candidates = contexts
            .Where(context =>
                string.Equals(
                    context.SystemId,
                    ownershipContext.SystemId,
                    StringComparison.Ordinal)
                && string.Equals(
                    context.PairId,
                    ownershipContext.PairId,
                    StringComparison.Ordinal)
                && startX >= context.XStart - CoordinateEpsilon
                && startX <= context.XEnd + CoordinateEpsilon)
            .GroupBy(
                context => context.Coordinate.StaffId,
                StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(context => context.XStart)
                .ThenBy(context => context.MeasureNumber)
                .First())
            .ToArray();

        if (candidates.Length == 0)
        {
            return ownershipContext;
        }

        return candidates
            .Select(context =>
            {
                var endContext = ResolveContextAtX(
                    contexts,
                    context.Coordinate.StaffId,
                    endX,
                    new LogicalCoordinate(
                        context.Coordinate.StaffId,
                        source.Ownership!.End.MeasureId),
                    preferLaterAtBoundary: false)
                    ?? context;
                var spacing = Math.Max(
                    (context.LineSpacing + endContext.LineSpacing) / 2.0,
                    0.001);
                var rhythmicFit =
                    timing.NearestAnchorDistanceInSpacings(
                        context.MeasureNumber,
                        context.StaffNumber,
                        startX,
                        spacing)
                    + timing.NearestAnchorDistanceInSpacings(
                        endContext.MeasureNumber,
                        endContext.StaffNumber,
                        endX,
                        spacing);
                var verticalDistance =
                    DistanceToStaff(y, context.StaffBounds) / spacing;

                return new
                {
                    Context = context,
                    Score = rhythmicFit + 0.35 * verticalDistance,
                    RhythmicFit = rhythmicFit,
                    VerticalDistance = verticalDistance
                };
            })
            .OrderBy(candidate => candidate.Score)
            .ThenBy(candidate => candidate.RhythmicFit)
            .ThenBy(candidate => candidate.VerticalDistance)
            .ThenByDescending(candidate => string.Equals(
                candidate.Context.Coordinate.StaffId,
                ownership.Start.StaffId,
                StringComparison.Ordinal))
            .ThenBy(candidate => candidate.Context.StaffNumber)
            .Select(candidate => candidate.Context)
            .First();
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

    private static double LeftX(HairpinPrimitive source) =>
        Math.Min(source.Apex.X, OpenMiddle(source).X);

    private static double RightX(HairpinPrimitive source) =>
        Math.Max(source.Apex.X, OpenMiddle(source).X);

    private static PointD OpenMiddle(HairpinPrimitive source) =>
        new(
            (source.OpenUpper.X + source.OpenLower.X) / 2.0,
            (source.OpenUpper.Y + source.OpenLower.Y) / 2.0);

    private static double CenterY(HairpinPrimitive source)
    {
        var openMiddle = OpenMiddle(source);
        return (source.Apex.Y + openMiddle.Y) / 2.0;
    }

    private static double DistanceToStaff(
        double y,
        BoundsD bounds)
    {
        if (y < bounds.MinY)
        {
            return bounds.MinY - y;
        }

        if (y > bounds.MaxY)
        {
            return y - bounds.MaxY;
        }

        return 0;
    }

    private static string ResolvePlacement(
        HairpinPrimitive source,
        CoordinateContext context)
    {
        var y = CenterY(source);

        if (y <= context.StaffBounds.MinY)
        {
            return "above";
        }

        if (y >= context.StaffBounds.MaxY)
        {
            return "below";
        }

        return Math.Abs(y - context.StaffBounds.MinY)
            < Math.Abs(y - context.StaffBounds.MaxY)
                ? "above"
                : "below";
    }

    private static bool IsForwardSpan(
        int startMeasure,
        string startAt,
        int endMeasure,
        string endAt)
    {
        if (endMeasure != startMeasure)
        {
            return endMeasure > startMeasure;
        }

        return FractionValue(Fraction.Parse(endAt))
            > FractionValue(Fraction.Parse(startAt));
    }

    private static HairpinDecision Reject(
        string shapeId,
        string decision,
        double confidence,
        string reason) =>
        new(
            shapeId,
            false,
            decision,
            null,
            null,
            null,
            null,
            null,
            null,
            confidence,
            reason);

    private static double FractionValue(Fraction value) =>
        value.Numerator / (double)value.Denominator;

    private sealed record CoordinateContext(
        string SystemId,
        string PairId,
        LogicalCoordinate Coordinate,
        int MeasureNumber,
        int StaffNumber,
        BoundsD StaffBounds,
        double LineSpacing,
        double XStart,
        double XEnd);

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
                .ThenBy(anchor => HairpinPass.FractionValue(anchor.At))
                .First()
                .At
                .ToString();
        }

        public string ResolveEndAt(
            int measureNumber,
            int staff,
            double x)
        {
            // A wedge stop is engraved at the rhythmic position where the change
            // stops. Snap to the nearest onset or measure boundary; do not add the
            // previous note duration (that rule belongs to pedal-like spans).
            return ResolveStartAt(measureNumber, staff, x);
        }

        public double NearestAnchorDistanceInSpacings(
            int measureNumber,
            int staff,
            double x,
            double spacing)
        {
            var measure = _document.Measures.Single(item => item.Number == measureNumber);
            var distances = new List<double>
            {
                Math.Abs(measure.XStart - x),
                Math.Abs(measure.XEnd - x)
            };

            distances.AddRange(
                _onsets
                    .Where(item => item.MeasureNumber == measureNumber && item.Staff == staff)
                    .Select(item => Math.Abs(item.AnchorX - x)));

            return distances.Min() / Math.Max(spacing, 0.001);
        }

        private Fraction MeasureDuration(int measureNumber)
        {
            var best = Fraction.Zero;

            foreach (var onset in _onsets.Where(item => item.MeasureNumber == measureNumber))
            {
                var end = Fraction.Parse(onset.At) + Duration(onset);
                if (HairpinPass.FractionValue(end) > HairpinPass.FractionValue(best))
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

        private sealed record TimeAnchorCandidate(
            double X,
            Fraction At);
    }
}
