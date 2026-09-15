namespace SvgMusic.Semantics;

public sealed class TimeSignaturePass : ISemanticPass
{
    private const double MinimumConfidence = 0.75;
    private const double HeaderWidthFraction = 0.45;

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
            var measureCandidates = collector.Candidates
                .Where(candidate => candidate.MeasureNumber == measure.Number)
                .ToArray();

            var upper = TryReadStaffSignature(
                measure,
                measure.Upper,
                measureCandidates,
                facts);
            var lower = TryReadStaffSignature(
                measure,
                measure.Lower,
                measureCandidates,
                facts);

            if (upper is null && lower is null)
            {
                if (measure.Number == firstMeasureNumber)
                {
                    throw new InvalidDataException(
                        $"No time signature found in first measure {measure.Number}.");
                }

                // No printed signature means the active signature is inherited.
                continue;
            }

            if (upper is null || lower is null)
            {
                throw new InvalidDataException(
                    $"Incomplete time signature in measure {measure.Number}: "
                    + $"upper={(upper is null ? "missing" : $"{upper.Beats}/{upper.BeatType}")}, "
                    + $"lower={(lower is null ? "missing" : $"{lower.Beats}/{lower.BeatType}")}.");
            }

            if (upper.Beats != lower.Beats
                || upper.BeatType != lower.BeatType)
            {
                throw new InvalidDataException(
                    $"Time signature disagreement between staves in measure "
                    + $"{measure.Number}: upper={upper.Beats}/{upper.BeatType}, "
                    + $"lower={lower.Beats}/{lower.BeatType}.");
            }

            if (!SupportedSignatures.Contains((upper.Beats, upper.BeatType)))
            {
                throw new InvalidDataException(
                    $"Unsupported or suspicious time signature "
                    + $"{upper.Beats}/{upper.BeatType} in measure {measure.Number}.");
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
                    + $"only common signatures are accepted.",
                sources));
        }
    }

    private static StaffSignature? TryReadStaffSignature(
        MeasureScene measure,
        StaffMeasureScene staff,
        IReadOnlyList<TimeCandidate> allCandidates,
        SemanticFacts facts)
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
            // Time candidates are already classifier-filtered to TIME_*/COMMON_TIME/CUT_TIME,
            // so a missing clef fact need not prevent reading the signature itself. Keep the
            // same narrow header window and let the two staves independently validate each other.
            facts.AddTrace(
                $"TimeSignaturePass: m{measure.Number} staff {staff.StaffNumber} "
                + "has no clef fact; using measure start as the time-signature left boundary");
        }

        var headerLimit = measure.XStart
            + (measure.XEnd - measure.XStart) * HeaderWidthFraction;

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
            if (common.Length + cut.Length != 1 || candidates.Length != 1)
            {
                throw new InvalidDataException(
                    $"Ambiguous symbolic time signature in measure {measure.Number}, "
                    + $"staff {staff.StaffNumber}: "
                    + string.Join(", ", candidates.Select(candidate => candidate.Label)));
            }

            var symbolic = candidates[0];
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
            .Select(candidate => new
            {
                Candidate = candidate,
                Digit = ParseTimeDigit(candidate.Label)
            })
            .ToArray();

        if (digits.Any(item => item.Digit is null))
        {
            throw new InvalidDataException(
                $"Unexpected time-signature classifier labels in measure "
                + $"{measure.Number}, staff {staff.StaffNumber}: "
                + string.Join(", ", candidates.Select(candidate => candidate.Label)));
        }

        var staffMiddleY = staff.StaffBounds.CenterY;
        var numerator = digits
            .Where(item => item.Candidate.CenterY < staffMiddleY)
            .OrderBy(item => item.Candidate.CenterX)
            .ToArray();
        var denominator = digits
            .Where(item => item.Candidate.CenterY >= staffMiddleY)
            .OrderBy(item => item.Candidate.CenterX)
            .ToArray();

        if (numerator.Length == 0 || denominator.Length == 0)
        {
            throw new InvalidDataException(
                $"Incomplete time signature in measure {measure.Number}, "
                + $"staff {staff.StaffNumber}: numerator glyphs={numerator.Length}, "
                + $"denominator glyphs={denominator.Length}.");
        }

        var beats = ComposeNumber(numerator.Select(item => item.Digit!.Value));
        var beatType = ComposeNumber(denominator.Select(item => item.Digit!.Value));
        var sourceShapeIds = candidates
            .Select(candidate => candidate.ShapeId)
            .ToArray();

        return new StaffSignature(
            beats,
            beatType,
            candidates.Min(candidate => candidate.Bounds.MinX),
            candidates.Max(candidate => candidate.Bounds.MaxX),
            sourceShapeIds);
    }

    private static int ComposeNumber(IEnumerable<int> digits)
    {
        var result = 0;

        foreach (var digit in digits)
        {
            result = checked(result * 10 + digit);
        }

        return result;
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

    private sealed record StaffSignature(
        int Beats,
        int BeatType,
        double MinX,
        double MaxX,
        IReadOnlyList<string> SourceShapeIds);
}
