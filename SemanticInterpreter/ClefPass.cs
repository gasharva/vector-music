namespace SvgMusic.Semantics;

public sealed class ClefPass : ISemanticPass
{
    private const double MinimumConfidence = 0.75;

    public string Name => nameof(ClefPass);

    public void Run(
        SemanticDocument document,
        SemanticFacts facts)
    {
        new Visitor(facts).Visit(document);
    }

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

            if (classification is null
                || classification.Confidence < MinimumConfidence
                || !_seen.Add(shape.ShapeId))
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
