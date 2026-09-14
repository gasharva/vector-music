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
        var firstMeasure = document.Measures
            .OrderBy(measure => measure.Number)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                "TimeSignaturePass requires at least one measure.");

        var collector = new Visitor(firstMeasure.Number);
        collector.Visit(document);

        var upper = ReadStaffSignature(
            firstMeasure,
            firstMeasure.Upper,
            collector.Candidates,
            facts);
        var lower = ReadStaffSignature(
            firstMeasure,
            firstMeasure.Lower,
            collector.Candidates,
            facts);

        if (upper.Beats != lower.Beats
            || upper.BeatType != lower.BeatType)
        {
            throw new InvalidDataException(
                $"Time signature disagreement between staves in measure "
                + $"{firstMeasure.Number}: upper={upper.Beats}/{upper.BeatType}, "
                + $"lower={lower.Beats}/{lower.BeatType}.");
        }

        if (!SupportedSignatures.Contains((upper.Beats, upper.BeatType)))
        {
            throw new InvalidDataException(
                $"Unsupported or suspicious time signature "
                + $"{upper.Beats}/{upper.BeatType} in measure {firstMeasure.Number}.");
        }

        var sources = upper.SourceShapeIds
            .Concat(lower.SourceShapeIds)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var minX = Math.Min(upper.MinX, lower.MinX);
        var maxX = Math.Max(upper.MaxX, lower.MaxX);

        facts.Add(new TimeSignatureFact(
            firstMeasure.Number,
            upper.Beats,
            upper.BeatType,
            minX,
            maxX,
            $"Both staves independently read as {upper.Beats}/{upper.BeatType}; "
                + $"only common signatures are accepted; no inference fallback is used.",
            sources));
    }

    private static StaffSignature ReadStaffSignature(
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
            .FirstOrDefault()
            ?? throw new InvalidDataException(
                $"Cannot read time signature in measure {measure.Number}, "
                + $"staff {staff.StaffNumber}: no clef fact exists.");

        var headerLimit = measure.XStart
            + (measure.XEnd - measure.XStart) * HeaderWidthFraction;

        var candidates = allCandidates
            .Where(candidate =>
                candidate.Staff == staff.StaffNumber
                && candidate.CenterX > clef.X
                && candidate.CenterX <= headerLimit)
            .OrderBy(candidate => candidate.CenterX)
            .ThenBy(candidate => candidate.CenterY)
            .ToArray();

        if (candidates.Length == 0)
        {
            throw new InvalidDataException(
                $"No time-signature glyphs found at the beginning of measure "
                + $"{measure.Number}, staff {staff.StaffNumber}.");
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
        private readonly int _measureNumber;
        private readonly List<TimeCandidate> _candidates = [];
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

        public Visitor(int measureNumber)
        {
            _measureNumber = measureNumber;
        }

        public IReadOnlyList<TimeCandidate> Candidates => _candidates;

        protected override void VisitShape(
            MeasureScene measure,
            StaffMeasureScene staff,
            ShapeElement shape)
        {
            if (measure.Number != _measureNumber
                || !_seen.Add(shape.ShapeId))
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
                staff.StaffNumber,
                shape.ShapeId,
                classification.Label,
                classification.Confidence,
                shape.Bounds));
        }
    }

    private sealed record TimeCandidate(
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
