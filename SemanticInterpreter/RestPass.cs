using SvgMusic.Scene;

namespace SvgMusic.Semantics;

public sealed record RestCandidate(
    int MeasureNumber,
    int Staff,
    double StaffTopY,
    double LineSpacing,
    SemanticElement Element,
    string ClassificationLabel,
    double ClassificationConfidence);

public sealed record RestDecision(
    RestCandidate Candidate,
    bool Accepted,
    string Decision,
    string? NoteType,
    string? Duration,
    double Confidence,
    string Reason);

public sealed record RestAnalysisResult(
    IReadOnlyList<RestDecision> Decisions)
{
    public IReadOnlyList<RestDecision> Accepted =>
        Decisions.Where(decision => decision.Accepted).ToArray();
}

/// <summary>
/// A classified rest symbol with its musical duration.
///
/// Most rest values are direct classifier labels. Audiveris deliberately trains whole
/// and half rests as one physical HW_REST_set because the rectangles are identical;
/// their vertical phase against the staff grid is what distinguishes them.
/// </summary>
public sealed record RestFact(
    int MeasureNumber,
    int Staff,
    string ShapeId,
    string ClassificationLabel,
    double ClassificationConfidence,
    string NoteType,
    string Duration,
    double CenterX,
    double CenterY,
    double Confidence,
    string Reason,
    IReadOnlyList<string> SourceShapeIds)
    : SemanticFact(
        "RestPass",
        Reason,
        SourceShapeIds);

public sealed class RestPass : ISemanticPass
{
    private const double MinimumConfidence = 0.75;
    private const double StemGuardMarginInSpacings = 0.15;

    public string Name => nameof(RestPass);

    public RestAnalysisResult? LastAnalysis { get; private set; }

    public void Run(
        SemanticDocument document,
        SemanticFacts facts)
    {
        var collector = new Visitor();
        collector.Visit(document);

        var stems = facts
            .OfType<StemAttachmentFact>()
            .ToArray();

        var decisions = collector.Candidates
            .GroupBy(candidate =>
                (candidate.MeasureNumber, candidate.Staff, candidate.Element.ShapeId))
            .Select(group => group.First())
            .Select(candidate => Decide(candidate, stems))
            .OrderBy(decision => decision.Candidate.MeasureNumber)
            .ThenBy(decision => decision.Candidate.Staff)
            .ThenBy(decision => decision.Candidate.Element.CenterX)
            .ThenBy(decision => decision.Candidate.Element.CenterY)
            .ToArray();

        LastAnalysis = new RestAnalysisResult(decisions);

        foreach (var decision in decisions.Where(decision => decision.Accepted))
        {
            var candidate = decision.Candidate;
            var element = candidate.Element;

            facts.Add(new RestFact(
                candidate.MeasureNumber,
                candidate.Staff,
                element.ShapeId,
                candidate.ClassificationLabel,
                candidate.ClassificationConfidence,
                decision.NoteType!,
                decision.Duration!,
                element.CenterX,
                element.CenterY,
                decision.Confidence,
                decision.Reason,
                [element.ShapeId]));
        }
    }

    private static RestDecision Decide(
        RestCandidate candidate,
        IReadOnlyList<StemAttachmentFact> stems)
    {
        var label = candidate.ClassificationLabel;

        if (label == "GEOMETRIC_EIGHTH_REST")
        {
            return Accept(
                candidate,
                "eighth",
                "1/8",
                candidate.ClassificationConfidence,
                "narrow vertical curved stroke is centered inside the staff and "
                + "matches eighth-rest proportions");
        }

        if (label == "HW_REST_set")
        {
            if (TouchesStem(candidate, stems))
            {
                return Reject(
                    candidate,
                    "half-whole-rest-touches-stem",
                    "HW_REST_set rectangle is crossed by a nearby accepted stem; treating it as non-rest geometry");
            }

            return ResolveHalfWhole(candidate);
        }

        var resolved = label switch
        {
            "WHOLE_REST" => (Type: "whole", Duration: "1"),
            "HALF_REST" => (Type: "half", Duration: "1/2"),
            "QUARTER_REST" => (Type: "quarter", Duration: "1/4"),
            "EIGHTH_REST" => (Type: "eighth", Duration: "1/8"),
            "ONE_16TH_REST" => (Type: "16th", Duration: "1/16"),
            "ONE_32ND_REST" => (Type: "32nd", Duration: "1/32"),
            "ONE_64TH_REST" => (Type: "64th", Duration: "1/64"),
            "ONE_128TH_REST" => (Type: "128th", Duration: "1/128"),
            "BREVE_REST" => (Type: "breve", Duration: "2"),
            "LONG_REST" => (Type: "long", Duration: "4"),
            _ => default
        };

        if (resolved.Type is null)
        {
            return Reject(
                candidate,
                "unsupported-rest-label",
                $"classifier label {label} is not a supported rest value");
        }

        return Accept(
            candidate,
            resolved.Type,
            resolved.Duration,
            candidate.ClassificationConfidence,
            $"classifier directly identifies {label} as a {resolved.Type} rest");
    }

    private static RestDecision ResolveHalfWhole(RestCandidate candidate)
    {
        if (candidate.LineSpacing <= 0)
        {
            return Reject(
                candidate,
                "invalid-staff-spacing",
                "cannot disambiguate HW_REST_set because staff spacing is not positive");
        }

        // Audiveris uses the pitch position of the otherwise identical rectangle:
        // whole rests hang from an upper staff line, half rests sit on a lower one.
        // Our staff top is line 0 and one line spacing spans two pitch steps.
        var pitch = 2.0
            * (candidate.Shape.CenterY - candidate.StaffTopY)
            / candidate.LineSpacing
            - 4.0;
        var doubledPitch = (int)Math.Round(
            pitch * 2.0,
            MidpointRounding.AwayFromZero);
        var phase = PositiveModulo(doubledPitch, 4);

        return phase switch
        {
            1 => Accept(
                candidate,
                "whole",
                "1",
                candidate.ClassificationConfidence * 0.98,
                $"HW_REST_set resolved by staff-grid phase as whole rest; pitch={pitch:F2}, 2*pitch={doubledPitch}"),
            3 => Accept(
                candidate,
                "half",
                "1/2",
                candidate.ClassificationConfidence * 0.98,
                $"HW_REST_set resolved by staff-grid phase as half rest; pitch={pitch:F2}, 2*pitch={doubledPitch}"),
            _ => Reject(
                candidate,
                "ambiguous-half-whole-rest-position",
                $"HW_REST_set is not attached to a valid whole/half-rest staff-line side; pitch={pitch:F2}, 2*pitch={doubledPitch}")
        };
    }

    private static bool TouchesStem(
        RestCandidate candidate,
        IReadOnlyList<StemAttachmentFact> stems)
    {
        var margin = candidate.LineSpacing * StemGuardMarginInSpacings;
        var bounds = candidate.Element.Bounds;

        return stems.Any(stem =>
        {
            if (stem.MeasureNumber != candidate.MeasureNumber
                || !stem.AttachedStaffs.Contains(candidate.Staff))
            {
                return false;
            }

            var stemX = (stem.StartX + stem.EndX) / 2.0;
            var minY = Math.Min(stem.StartY, stem.EndY) - margin;
            var maxY = Math.Max(stem.StartY, stem.EndY) + margin;

            return stemX >= bounds.MinX - margin
                && stemX <= bounds.MaxX + margin
                && candidate.Shape.CenterY >= minY
                && candidate.Shape.CenterY <= maxY;
        });
    }

    private static int PositiveModulo(int value, int divisor)
    {
        var result = value % divisor;
        return result < 0 ? result + divisor : result;
    }

    private static RestDecision Accept(
        RestCandidate candidate,
        string noteType,
        string duration,
        double confidence,
        string reason)
    {
        return new RestDecision(
            candidate,
            true,
            "rest",
            noteType,
            duration,
            Math.Clamp(confidence, 0, 1),
            reason);
    }

    private static RestDecision Reject(
        RestCandidate candidate,
        string decision,
        string reason)
    {
        return new RestDecision(
            candidate,
            false,
            decision,
            null,
            null,
            0,
            reason);
    }

    private sealed class Visitor : SemanticVisitor
    {
        private readonly List<RestCandidate> _candidates = [];

        public IReadOnlyList<RestCandidate> Candidates => _candidates;

        protected override void VisitCurve(
            MeasureScene measure,
            StaffMeasureScene staff,
            CurveElement curve)
        {
            if (!IsGeometricEighthRest(
                    staff,
                    curve))
            {
                return;
            }

            _candidates.Add(new RestCandidate(
                measure.Number,
                staff.StaffNumber,
                staff.StaffBounds.MinY,
                staff.LineSpacing,
                curve,
                "GEOMETRIC_EIGHTH_REST",
                0.90));
        }

        protected override void VisitShape(
            MeasureScene measure,
            StaffMeasureScene staff,
            ShapeElement shape)
        {
            var classification = shape.Classification;

            if (classification is null
                || classification.Confidence < MinimumConfidence
                || !IsRestLabel(classification.Label))
            {
                return;
            }

            _candidates.Add(new RestCandidate(
                measure.Number,
                staff.StaffNumber,
                staff.StaffBounds.MinY,
                staff.LineSpacing,
                shape,
                classification.Label,
                classification.Confidence));
        }

        private static bool IsGeometricEighthRest(
            StaffMeasureScene staff,
            CurveElement curve)
        {
            var spacing = staff.LineSpacing;
            if (spacing <= 0)
            {
                return false;
            }

            var bounds = curve.Bounds;
            var width = bounds.Width / spacing;
            var height = bounds.Height / spacing;
            var aspect = height / Math.Max(width, 1e-9);
            var centerY = bounds.CenterY;

            return centerY >= staff.StaffBounds.MinY
                && centerY <= staff.StaffBounds.MaxY
                && width >= 0.35
                && width <= 0.90
                && height >= 1.40
                && height <= 2.30
                && aspect >= 1.80;
        }

        private static bool IsRestLabel(string label)
        {
            return label is
                "HW_REST_set"
                or "WHOLE_REST"
                or "HALF_REST"
                or "QUARTER_REST"
                or "EIGHTH_REST"
                or "ONE_16TH_REST"
                or "ONE_32ND_REST"
                or "ONE_64TH_REST"
                or "ONE_128TH_REST"
                or "BREVE_REST"
                or "LONG_REST";
        }
    }
}
