using SvgMusic.Scene;

namespace SvgMusic.Semantics;

public sealed class TimeSignaturePass : ISemanticPass
{
    private const double MinimumConfidence = 0.75;

    private readonly (int Beats, int BeatType)? _inheritedSignature;
    private readonly TimeSignatureSettings _settings;

    public TimeSignaturePass(
        (int Beats, int BeatType)? inheritedSignature = null,
        SvgMusicSettings? settings = null)
    {
        if (inheritedSignature is { } value
            && !SupportedSignatures.Contains(value))
        {
            throw new ArgumentOutOfRangeException(
                nameof(inheritedSignature),
                $"Unsupported inherited time signature {value.Beats}/{value.BeatType}.");
        }

        _inheritedSignature = inheritedSignature;
        _settings = (settings ?? SvgMusicSettings.Default).TimeSignature;
    }
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
            var strict = isFirstMeasure && _inheritedSignature is null;
            var measureCandidates = collector.Candidates
                .Where(candidate => candidate.MeasureNumber == measure.Number)
                .ToArray();

            var upper = TryReadStaffSignature(
                measure,
                measure.Upper,
                measureCandidates,
                facts,
                strict);
            var lower = TryReadStaffSignature(
                measure,
                measure.Lower,
                measureCandidates,
                facts,
                strict);

            if (upper is null && lower is null)
            {
                if (isFirstMeasure && _inheritedSignature is { } inherited)
                {
                    AddInheritedSignature(
                        facts,
                        measure,
                        inherited,
                        "no printed time signature on continuation page");
                    continue;
                }

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
                if (isFirstMeasure && _inheritedSignature is { } inherited)
                {
                    AddInheritedSignature(
                        facts,
                        measure,
                        inherited,
                        "ignored incomplete printed signature at continuation-page start");
                    continue;
                }

                if (isFirstMeasure)
                {
                    var readable = upper ?? lower;

                    if (readable is not null
                        && SupportedSignatures.Contains(
                            (readable.Beats, readable.BeatType)))
                    {
                        AddSingleStaffSignature(
                            facts,
                            measure,
                            readable,
                            upper is null ? measure.Lower.StaffNumber : measure.Upper.StaffNumber,
                            upper is null ? measure.Upper.StaffNumber : measure.Lower.StaffNumber);
                        continue;
                    }

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
                if (isFirstMeasure && _inheritedSignature is { } inherited)
                {
                    AddInheritedSignature(
                        facts,
                        measure,
                        inherited,
                        "ignored conflicting printed signatures at continuation-page start");
                    continue;
                }

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
                if (isFirstMeasure && _inheritedSignature is { } inherited)
                {
                    AddInheritedSignature(
                        facts,
                        measure,
                        inherited,
                        "ignored unsupported printed signature at continuation-page start");
                    continue;
                }

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

    private static void AddSingleStaffSignature(
        SemanticFacts facts,
        MeasureScene measure,
        StaffSignature signature,
        int readableStaff,
        int incompleteStaff)
    {
        facts.Add(new TimeSignatureFact(
            measure.Number,
            signature.Beats,
            signature.BeatType,
            signature.MinX,
            signature.MaxX,
            $"Recovered {signature.Beats}/{signature.BeatType} from staff {readableStaff}; "
                + $"peer staff {incompleteStaff} had incomplete or unsupported time-signature evidence.",
            signature.SourceShapeIds));

        facts.AddTrace(
            $"TimeSignaturePass: m{measure.Number} recovered "
            + $"{signature.Beats}/{signature.BeatType} from staff {readableStaff}; "
            + $"peer staff {incompleteStaff} incomplete");
    }

    private static void AddInheritedSignature(
        SemanticFacts facts,
        MeasureScene measure,
        (int Beats, int BeatType) inherited,
        string reason)
    {
        facts.Add(new TimeSignatureFact(
            measure.Number,
            inherited.Beats,
            inherited.BeatType,
            measure.XStart,
            measure.XStart,
            $"Inherited {inherited.Beats}/{inherited.BeatType}: {reason}.",
            [],
            IsInherited: true));

        facts.AddTrace(
            $"TimeSignaturePass: m{measure.Number} inherited "
            + $"{inherited.Beats}/{inherited.BeatType}; {reason}");
    }

    private StaffSignature? TryReadStaffSignature(
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

        var headerCandidates = allCandidates
            .Where(candidate =>
                candidate.Staff == staff.StaffNumber
                && candidate.CenterX > leftBoundary
                && candidate.CenterX <= headerLimit)
            .OrderBy(candidate => candidate.CenterX)
            .ThenBy(candidate => candidate.CenterY)
            .ToArray();

        var spacing = Math.Max(staff.LineSpacing, 0.001);
        var verticalTolerance =
            spacing * _settings.VerticalCenterToleranceInSpacings;
        var candidates = headerCandidates
            .Where(candidate =>
                candidate.CenterY >= staff.StaffBounds.MinY - verticalTolerance
                && candidate.CenterY <= staff.StaffBounds.MaxY + verticalTolerance)
            .ToArray();

        var rejectedByVerticalGate = headerCandidates
            .Where(candidate => !candidates.Contains(candidate))
            .ToArray();

        if (rejectedByVerticalGate.Length > 0)
        {
            facts.AddTrace(
                $"TimeSignaturePass: m{measure.Number} staff {staff.StaffNumber} "
                + "vertical gate rejected "
                + string.Join(
                    ", ",
                    rejectedByVerticalGate.Select(candidate =>
                        $"{candidate.ShapeId}/{candidate.Label}"
                        + $"@y={candidate.CenterY:F2}")));
        }

        if (candidates.Length == 0)
        {
            return null;
        }

        var selected = SelectBestSupportedSignature(
            candidates,
            staff,
            leftBoundary,
            spacing);

        if (selected is null)
        {
            facts.AddTrace(
                $"TimeSignaturePass: rejected time-signature candidate in "
                + $"m{measure.Number} staff {staff.StaffNumber}: "
                + "no supported compact time-signature hypothesis among: "
                + string.Join(", ", candidates.Select(candidate => candidate.Label)));
            return null;
        }

        if (selected.SourceShapeIds.Count < candidates.Length)
        {
            facts.AddTrace(
                $"TimeSignaturePass: m{measure.Number} staff {staff.StaffNumber} "
                + $"selected {selected.Beats}/{selected.BeatType} from "
                + $"{selected.SourceShapeIds.Count} of {candidates.Length} time-signature glyphs");
        }

        return selected;
    }

    private StaffSignature? SelectBestSupportedSignature(
        IReadOnlyList<TimeCandidate> candidates,
        StaffMeasureScene staff,
        double leftBoundary,
        double spacing)
    {
        var hypotheses = new List<SignatureHypothesis>();
        var staffMiddleY = staff.StaffBounds.CenterY;

        foreach (var symbolic in candidates.Where(candidate =>
                     candidate.Label is "COMMON_TIME" or "CUT_TIME"))
        {
            var value = symbolic.Label == "COMMON_TIME"
                ? (Beats: 4, BeatType: 4)
                : (Beats: 2, BeatType: 2);
            var score = ScoreHypothesis(
                [symbolic],
                leftBoundary,
                spacing,
                staffMiddleY,
                alignment: 0,
                compactness: symbolic.Bounds.Width / spacing);

            hypotheses.Add(new SignatureHypothesis(
                new StaffSignature(
                    value.Beats,
                    value.BeatType,
                    symbolic.Bounds.MinX,
                    symbolic.Bounds.MaxX,
                    [symbolic.ShapeId]),
                score));
        }

        var digits = candidates
            .Select(candidate => new TimeDigitCandidate(
                candidate,
                ParseTimeDigit(candidate.Label)))
            .Where(item => item.Digit is not null)
            .Select(item => item with { Digit = item.Digit!.Value })
            .ToArray();

        var numerator = digits
            .Where(item => item.Candidate.CenterY < staffMiddleY)
            .OrderBy(item => item.Candidate.CenterX)
            .ToArray();
        var denominator = digits
            .Where(item => item.Candidate.CenterY >= staffMiddleY)
            .OrderBy(item => item.Candidate.CenterX)
            .ToArray();

        if (numerator.Length > 0 && denominator.Length > 0)
        {
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

                        var all = top
                            .Concat(bottom)
                            .Select(item => item.Candidate)
                            .ToArray();
                        var compactness = (topSpan + bottomSpan) / spacing;
                        var score = ScoreHypothesis(
                            all,
                            leftBoundary,
                            spacing,
                            staffMiddleY,
                            alignment,
                            compactness);

                        hypotheses.Add(new SignatureHypothesis(
                            new StaffSignature(
                                signature.Beats,
                                signature.BeatType,
                                all.Min(item => item.Bounds.MinX),
                                all.Max(item => item.Bounds.MaxX),
                                all.Select(item => item.ShapeId)
                                    .Distinct(StringComparer.Ordinal)
                                    .ToArray()),
                            score));
                    }
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

    private double ScoreHypothesis(
        IReadOnlyList<TimeCandidate> candidates,
        double leftBoundary,
        double spacing,
        double staffMiddleY,
        double alignment,
        double compactness)
    {
        var minX = candidates.Min(item => item.Bounds.MinX);
        var minY = candidates.Min(item => item.Bounds.MinY);
        var maxY = candidates.Max(item => item.Bounds.MaxY);
        var verticalCenter = (minY + maxY) / 2.0;
        var verticalCenterOffset =
            Math.Abs(verticalCenter - staffMiddleY) / spacing;
        var leftDistance = Math.Max(0, minX - leftBoundary) / spacing;
        var confidencePenalty =
            1.0 - candidates.Average(item => item.Confidence);

        return alignment
            + leftDistance * 0.08
            + compactness * 0.03
            + confidencePenalty * 0.25
            + verticalCenterOffset * _settings.VerticalCenterScoreWeight;
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
