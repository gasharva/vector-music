namespace SvgMusic.Semantics;

public sealed class TimeSignaturePass : ISemanticPass
{
    private const double MinimumConfidence = 0.75;
    private const double FirstMeasureHeaderWidthFraction = 0.45;
    private const double LaterMeasureHeaderWidthFraction = 0.30;
    private const double MaximumDigitColumnOffsetInSpacings = 1.0;
    private const double MaximumMultiDigitSpanInSpacings = 2.5;

    private static readonly HashSet<(int Beats, int BeatType)> SupportedSignatures =
    [
        (2, 2),
        (2, 4),
        (3, 4),
        (4, 4),
        (5, 4),
        (3, 8),
        (6, 8),
        (9, 8),
        (12, 8)
    ];

    public string Name => nameof(TimeSignaturePass);

    public void Run(
        SemanticDocument document,
        SemanticFacts facts)
    {
        var measures = document.Measures
            .OrderBy(measure => measure.Number)
            .ToArray();

        if (measures.Length == 0)
        {
            throw new InvalidOperationException(
                "TimeSignaturePass requires at least one measure.");
        }

        var collector = new Visitor();
        collector.Visit(document);

        var firstMeasureNumber = measures[0].Number;

        foreach (var measure in measures)
        {
            var isFirstMeasure = measure.Number == firstMeasureNumber;
            var measureCandidates = collector.Candidates
                .Where(candidate => candidate.MeasureNumber == measure.Number)
                .ToArray();

            var upper = TryReadStaffSignature(
                measure,
                measure.Upper,
                measureCandidates,
                facts,
                strict: isFirstMeasure);
            var lower = TryReadStaffSignature(
                measure,
                measure.Lower,
                measureCandidates,
                facts,
                strict: isFirstMeasure);

            if (upper is null && lower is null)
            {
                if (isFirstMeasure)
                {
                    throw new InvalidDataException(
                        $"No time signature found in first measure {measure.Number}.");
                }

                // No printed signature means the active signature is inherited.
                continue;
            }

            if (upper is null || lower is null)
            {
                if (isFirstMeasure)
                {
                    throw new InvalidDataException(
                        $"Incomplete time signature in measure {measure.Number}: "
                        + $"upper={(upper is null ? "missing" : $"{upper.Beats}/{upper.BeatType}")}, "
                        + $"lower={(lower is null ? "missing" : $"{lower.Beats}/{lower.BeatType}")}.");
                }

                facts.AddTrace(
                    $"TimeSignaturePass: ignored incomplete later signature in m{measure.Number}; "
                    + $"upper={(upper is null ? "missing" : $"{upper.Beats}/{upper.BeatType}")}; "
                    + $"lower={(lower is null ? "missing" : $"{lower.Beats}/{lower.BeatType}")}");
                continue;
            }

            if (upper.Beats != lower.Beats
                || upper.BeatType != lower.BeatType)
            {
                if (isFirstMeasure)
                {
                    throw new InvalidDataException(
                        $"Time signature disagreement between staves in measure "
                        + $"{measure.Number}: upper={upper.Beats}/{upper.BeatType}, "
                        + $"lower={lower.Beats}/{lower.BeatType}.");
                }

                facts.AddTrace(
                    $"TimeSignaturePass: ignored conflicting later signature in m{measure.Number}; "
                    + $"upper={upper.Beats}/{upper.BeatType}; "
                    + $"lower={lower.Beats}/{lower.BeatType}");
                continue;
            }

            if (!SupportedSignatures.Contains((upper.Beats, upper.BeatType)))
            {
                if (isFirstMeasure)
                {
                    throw new InvalidDataException(
                        $"Unsupported or suspicious time signature "
                        + $"{upper.Beats}/{upper.BeatType} in measure {measure.Number}.");
                }

                facts.AddTrace(
                    $"TimeSignaturePass: ignored unsupported later signature "
                    + $"{upper.Beats}/{upper.BeatType} in m{measure.Number}");
                continue;
            }

            var sources = upper.SourceShapeIds
                .Concat(lower.SourceShapeIds)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var minX = Math.Min(upper.MinX, lower.MinX);
            var maxX = Math.Max(upper.MaxX, lower.MaxX);

            facts.Add(new TimeSignatureFact(
                measure.Number,
                upper.Beats,
                upper.BeatType,
                minX,
                maxX,
                $"Both staves independently read as {upper.Beats}/{upper.BeatType}; "
                    + $"only supported geometric digit hypotheses are accepted.",
                sources));
        }
    }

    private static StaffSignature? TryReadStaffSignature(
        MeasureScene measure,
        StaffMeasureScene staff,
        IReadOnlyList<TimeCandidate> allCandidates,
        SemanticFacts facts,
        bool strict)
    {
        var clef = facts
            .OfType<ClefFact>()
            .Where(fact =>
                fact.MeasureNumber == measure.Number
                && fact.Staff == staff.StaffNumber)
            .OrderBy(fact => fact.X)
            .FirstOrDefault();

        var leftBoundary = clef?.X ?? measure.XStart;
        if (clef is null && allCandidates.Count > 0)
        {
            facts.AddTrace(
                $"TimeSignaturePass: m{measure.Number} staff {staff.StaffNumber} "
                + "has no clef fact; using measure start as the time-signature left boundary");
        }

        var headerWidthFraction = strict
            ? FirstMeasureHeaderWidthFraction
            : LaterMeasureHeaderWidthFraction;
        var headerLimit = measure.XStart
            + (measure.XEnd - measure.XStart) * headerWidthFraction;

        var candidates = allCandidates
            .Where(candidate =>
                candidate.Staff == staff.StaffNumber
                && candidate.CenterX > leftBoundary
                && candidate.CenterX <= headerLimit)
            .OrderBy(candidate => candidate.CenterX)
            .ThenBy(candidate => candidate.CenterY)
            .ToArray();

        if (candidates.Length == 0)
        {
            return null;
        }

        var common = candidates
            .Where(candidate => candidate.Label == "COMMON_TIME")
            .ToArray();
        var cut = candidates
            .Where(candidate => candidate.Label == "CUT_TIME")
            .ToArray();

        if (common.Length > 0 || cut.Length > 0)
        {
            var symbolicCandidates = common
                .Concat(cut)
                .OrderBy(candidate => candidate.CenterX)
                .ThenByDescending(candidate => candidate.Confidence)
                .ToArray();

            var symbolic = symbolicCandidates[0];
            var value = symbolic.Label == "COMMON_TIME"
                ? (Beats: 4, BeatType: 4)
                : (Beats: 2, BeatType: 2);

            return new StaffSignature(
                value.Beats,
                value.BeatType,
                symbolic.Bounds.MinX,
                symbolic.Bounds.MaxX,
                [symbolic.ShapeId]);
        }

        var digits = candidates
            .Select(candidate => new TimeDigitCandidate(
                candidate,
                ParseTimeDigit(candidate.Label)))
            .ToArray();

        if (digits.Any(item => item.Digit is null))
        {
            return RejectOrThrow(
                strict,
                facts,
                measure.Number,
                staff.StaffNumber,
                "unexpected classifier labels: "
                + string.Join(", ", candidates.Select(candidate => candidate.Label)));
        }

        var staffMiddleY = staff.StaffBounds.CenterY;
        var numerator = digits
            .Where(item => item.Candidate.CenterY < staffMiddleY)
            .Select(item => item with { Digit = item.Digit!.Value })
            .OrderBy(item => item.Candidate.CenterX)
            .ToArray();
        var denominator = digits
            .Where(item => item.Candidate.CenterY >= staffMiddleY)
            .Select(item => item with { Digit = item.Digit!.Value })
            .OrderBy(item => item.Candidate.CenterX)
            .ToArray();

        if (numerator.Length == 0 || denominator.Length == 0)
        {
            return RejectOrThrow(
                strict,
                facts,
                measure.Number,
                staff.StaffNumber,
                $"incomplete candidate: numerator glyphs={numerator.Length}, "
                + $"denominator glyphs={denominator.Length}");
        }

        var selected = SelectBestSupportedDigitSignature(
            numerator,
            denominator,
            leftBoundary,
            Math.Max(staff.LineSpacing, 0.001));

        if (selected is null)
        {
            return RejectOrThrow(
                strict,
                facts,
                measure.Number,
                staff.StaffNumber,
                "no supported compact digit hypothesis among: "
                + string.Join(", ", candidates.Select(candidate => candidate.Label)));
        }

        if (selected.SourceShapeIds.Count < candidates.Length)
        {
            facts.AddTrace(
                $"TimeSignaturePass: m{measure.Number} staff {staff.StaffNumber} "
                + $"selected {selected.Beats}/{selected.BeatType} from "
                + $"{selected.SourceShapeIds.Count} of {candidates.Length} TIME_* glyphs");
        }

        return selected;
    }

    private static StaffSignature? SelectBestSupportedDigitSignature(
        IReadOnlyList<TimeDigitCandidate> numerator,
        IReadOnlyList<TimeDigitCandidate> denominator,
        double leftBoundary,
        double spacing)
    {
        var hypotheses = new List<SignatureHypothesis>();

        foreach (var signature in SupportedSignatures)
        {
            foreach (var top in MatchNumber(numerator, signature.Beats))
            {
                foreach (var bottom in MatchNumber(denominator, signature.BeatType))
                {
                    var topCenter = top.Average(item => item.Candidate.CenterX);
                    var bottomCenter = bottom.Average(item => item.Candidate.CenterX);
                    var alignment = Math.Abs(topCenter - bottomCenter) / spacing;
                    if (alignment > MaximumDigitColumnOffsetInSpacings)
                    {
                        continue;
                    }

                    var topSpan = top.Max(item => item.Candidate.Bounds.MaxX)
                        - top.Min(item => item.Candidate.Bounds.MinX);
                    var bottomSpan = bottom.Max(item => item.Candidate.Bounds.MaxX)
                        - bottom.Min(item => item.Candidate.Bounds.MinX);
                    if (topSpan / spacing > MaximumMultiDigitSpanInSpacings
                        || bottomSpan / spacing > MaximumMultiDigitSpanInSpacings)
                    {
                        continue;
                    }

                    var all = top.Concat(bottom).ToArray();
                    var minX = all.Min(item => item.Candidate.Bounds.MinX);
                    var maxX = all.Max(item => item.Candidate.Bounds.MaxX);
                    var leftDistance = Math.Max(0, minX - leftBoundary) / spacing;
                    var compactness = (topSpan + bottomSpan) / spacing;
                    var confidencePenalty = 1.0 - all.Average(item => item.Candidate.Confidence);
                    var score = alignment
                        + leftDistance * 0.08
                        + compactness * 0.03
                        + confidencePenalty * 0.25;

                    hypotheses.Add(new SignatureHypothesis(
                        new StaffSignature(
                            signature.Beats,
                            signature.BeatType,
                            minX,
                            maxX,
                            all.Select(item => item.Candidate.ShapeId)
                                .Distinct(StringComparer.Ordinal)
                                .ToArray()),
                        score));
                }
            }
        }

        return hypotheses
            .OrderBy(hypothesis => hypothesis.Score)
            .ThenBy(hypothesis => hypothesis.Signature.MinX)
            .ThenByDescending(hypothesis => hypothesis.Signature.SourceShapeIds.Count)
            .Select(hypothesis => hypothesis.Signature)
            .FirstOrDefault();
    }

    private static IEnumerable<IReadOnlyList<TimeDigitCandidate>> MatchNumber(
        IReadOnlyList<TimeDigitCandidate> candidates,
        int number)
    {
        var required = number
            .ToString(System.Globalization.CultureInfo.InvariantCulture)
            .Select(character => character - '0')
            .ToArray();

        if (required.Length == 1)
        {
            foreach (var candidate in candidates.Where(candidate =>
                         candidate.Digit == required[0]))
            {
                yield return [candidate];
            }

            yield break;
        }

        if (required.Length == 2)
        {
            for (var first = 0; first < candidates.Count; first++)
            {
                if (candidates[first].Digit != required[0])
                {
                    continue;
                }

                for (var second = first + 1; second < candidates.Count; second++)
                {
                    if (candidates[second].Digit == required[1])
                    {
                        yield return [candidates[first], candidates[second]];
                    }
                }
            }
        }
    }

    private static StaffSignature? RejectOrThrow(
        bool strict,
        SemanticFacts facts,
        int measureNumber,
        int staffNumber,
        string reason)
    {
        if (strict)
        {
            throw new InvalidDataException(
                $"Invalid time signature in measure {measureNumber}, "
                + $"staff {staffNumber}: {reason}.");
        }

        facts.AddTrace(
            $"TimeSignaturePass: ignored later time-signature candidate in "
            + $"m{measureNumber} staff {staffNumber}: {reason}");
        return null;
    }

    private static int? ParseTimeDigit(string label)
    {
        return label switch
        {
            "TIME_ZERO" => 0,
            "TIME_ONE" => 1,
            "TIME_TWO" => 2,
            "TIME_THREE" => 3,
            "TIME_FOUR" => 4,
            "TIME_FIVE" => 5,
            "TIME_SIX" => 6,
            "TIME_SEVEN" => 7,
            "TIME_EIGHT" => 8,
            "TIME_NINE" => 9,
            _ => null
        };
    }

    private sealed class Visitor : SemanticVisitor
    {
        private readonly List<TimeCandidate> _candidates = [];
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

        public IReadOnlyList<TimeCandidate> Candidates => _candidates;

        protected override void VisitShape(
            MeasureScene measure,
            StaffMeasureScene staff,
            ShapeElement shape)
        {
            if (!_seen.Add($"{measure.Number}:{staff.StaffNumber}:{shape.ShapeId}"))
            {
                return;
            }

            var classification = shape.Classification;

            if (classification is null
                || classification.Confidence < MinimumConfidence)
            {
                return;
            }

            if (!classification.Label.StartsWith(
                    "TIME_",
                    StringComparison.Ordinal)
                && classification.Label != "COMMON_TIME"
                && classification.Label != "CUT_TIME")
            {
                return;
            }

            _candidates.Add(new TimeCandidate(
                measure.Number,
                staff.StaffNumber,
                shape.ShapeId,
                classification.Label,
                classification.Confidence,
                shape.Bounds));
        }
    }

    private sealed record TimeCandidate(
        int MeasureNumber,
        int Staff,
        string ShapeId,
        string Label,
        double Confidence,
        SvgMusic.Scene.BoundsD Bounds)
    {
        public double CenterX => Bounds.CenterX;
        public double CenterY => Bounds.CenterY;
    }

    private sealed record TimeDigitCandidate(
        TimeCandidate Candidate,
        int? Digit);

    private sealed record SignatureHypothesis(
        StaffSignature Signature,
        double Score);

    private sealed record StaffSignature(
        int Beats,
        int BeatType,
        double MinX,
        double MaxX,
        IReadOnlyList<string> SourceShapeIds);
}
