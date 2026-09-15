namespace SvgMusic.Semantics;

public sealed class ClefPass : ISemanticPass
{
    private const double MinimumConfidence = 0.75;
    private const double RecoveredConfidence = 0.72;

    public string Name => nameof(ClefPass);

    public void Run(
        SemanticDocument document,
        SemanticFacts facts)
    {
        new Visitor(facts).Visit(document);

        // A freshly exported MuseScore SVG can occasionally split an F clef into the
        // main contour plus its two dots in a way that makes the prototype classifier
        // miss the composite. Do not guess a clef from staff position alone: recover it
        // only when the characteristic contour + aligned two-dot geometry is present.
        foreach (var measure in document.Measures
                     .Where(measure => measure.Number == 1 || measure.BreakBefore))
        {
            RecoverMissingFClef(measure, measure.Upper, facts);
            RecoverMissingFClef(measure, measure.Lower, facts);
        }
    }

    private static void RecoverMissingFClef(
        MeasureScene measure,
        StaffMeasureScene staff,
        SemanticFacts facts)
    {
        if (facts.OfType<ClefFact>().Any(clef =>
                clef.MeasureNumber == measure.Number
                && clef.Staff == staff.StaffNumber))
        {
            return;
        }

        var spacing = staff.LineSpacing;
        if (spacing <= 0)
        {
            return;
        }

        var headerLimit = measure.XStart
            + (measure.XEnd - measure.XStart) * 0.35;
        var dots = staff.Elements
            .OfType<EllipseElement>()
            .Where(ellipse => !ellipse.Source.IsHollow)
            .Where(ellipse => ellipse.CenterX <= headerLimit)
            .Where(ellipse =>
            {
                var diameter = 2.0 * Math.Max(
                    ellipse.Source.MajorRadius,
                    ellipse.Source.MinorRadius);
                return diameter >= spacing * 0.12
                    && diameter <= spacing * 0.75;
            })
            .ToArray();

        var candidates = new List<RecoveredFClefCandidate>();

        foreach (var shape in staff.Elements
                     .OfType<ShapeElement>()
                     .Where(shape => shape.CenterX <= headerLimit))
        {
            var widthInSpaces = shape.Bounds.Width / spacing;
            var heightInSpaces = shape.Bounds.Height / spacing;

            if (widthInSpaces < 0.45
                || widthInSpaces > 3.2
                || heightInSpaces < 1.8
                || heightInSpaces > 5.2)
            {
                continue;
            }

            for (var first = 0; first < dots.Length; first++)
            {
                for (var second = first + 1; second < dots.Length; second++)
                {
                    var upper = dots[first].CenterY <= dots[second].CenterY
                        ? dots[first]
                        : dots[second];
                    var lower = ReferenceEquals(upper, dots[first])
                        ? dots[second]
                        : dots[first];
                    var vertical = (lower.CenterY - upper.CenterY) / spacing;
                    var horizontalAlignment = Math.Abs(
                        lower.CenterX - upper.CenterX) / spacing;
                    var dotX = (upper.CenterX + lower.CenterX) / 2.0;
                    var horizontalGap = (dotX - shape.Bounds.MaxX) / spacing;
                    var pairCenterY = (upper.CenterY + lower.CenterY) / 2.0;
                    var centerOffset = Math.Abs(
                        pairCenterY - shape.Bounds.CenterY) / spacing;

                    if (vertical < 0.55
                        || vertical > 1.45
                        || horizontalAlignment > 0.45
                        || horizontalGap < -0.10
                        || horizontalGap > 1.35
                        || centerOffset > 0.85)
                    {
                        continue;
                    }

                    var classifierBonus = shape.Classification?.Label == "F_CLEF"
                        ? -0.75 * Math.Clamp(shape.Classification.Confidence, 0, 1)
                        : 0;
                    var score = Math.Abs(vertical - 1.0)
                        + horizontalAlignment
                        + Math.Abs(horizontalGap - 0.30) * 0.35
                        + centerOffset * 0.30
                        + classifierBonus;

                    candidates.Add(new RecoveredFClefCandidate(
                        shape,
                        upper,
                        lower,
                        score));
                }
            }
        }

        var best = candidates
            .OrderBy(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Main.CenterX)
            .ThenBy(candidate => candidate.Main.ShapeId, StringComparer.Ordinal)
            .FirstOrDefault();

        if (best is null)
        {
            facts.AddTrace(
                $"ClefPass: m{measure.Number} staff {staff.StaffNumber} "
                + "has no classified clef and no geometric F-clef dot pair");
            return;
        }

        facts.Add(new ClefFact(
            measure.Number,
            staff.StaffNumber,
            "F",
            4,
            best.Main.CenterX,
            RecoveredConfidence,
            best.Main.ShapeId,
            $"Recovered F4 from unambiguous main-contour + two-dot geometry; "
                + $"dots={best.Upper.ShapeId},{best.Lower.ShapeId}; "
                + $"score={best.Score:F3}; logical owner={staff.StaffId}+{measure.LayoutMeasureId}",
            [
                best.Main.ShapeId,
                best.Upper.ShapeId,
                best.Lower.ShapeId
            ]));

        facts.AddTrace(
            $"ClefPass: recovered F4 m{measure.Number} staff={staff.StaffNumber} "
            + $"main={best.Main.ShapeId} dots={best.Upper.ShapeId},{best.Lower.ShapeId}");
    }

    private sealed record RecoveredFClefCandidate(
        ShapeElement Main,
        EllipseElement Upper,
        EllipseElement Lower,
        double Score);

    private sealed class Visitor : SemanticVisitor
    {
        private readonly SemanticFacts _facts;
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

        public Visitor(SemanticFacts facts)
        {
            _facts = facts;
        }

        protected override void VisitShape(
            MeasureScene measure,
            StaffMeasureScene staff,
            ShapeElement shape)
        {
            var classification = shape.Classification;
            var logicalKey = $"{measure.Number}:{staff.StaffNumber}:{shape.ShapeId}";

            if (classification is null
                || classification.Confidence < MinimumConfidence
                || !_seen.Add(logicalKey))
            {
                return;
            }

            var clef = classification.Label switch
            {
                "G_CLEF" => (Sign: "G", Line: 2),
                "F_CLEF" => (Sign: "F", Line: 4),
                _ => ((string Sign, int Line)?)null
            };

            if (clef is null)
            {
                return;
            }

            _facts.Add(new ClefFact(
                measure.Number,
                staff.StaffNumber,
                clef.Value.Sign,
                clef.Value.Line,
                shape.CenterX,
                classification.Confidence,
                shape.ShapeId,
                $"Audiveris {classification.Label} "
                    + $"at {classification.Confidence:P1}; "
                    + $"logical owner={staff.StaffId}+{measure.LayoutMeasureId}",
                [shape.ShapeId]));
        }
    }
}
