using SvgMusic.Scene;

namespace SvgMusic.Semantics;

public enum AccidentalKind
{
    Flat,
    Sharp,
    Natural,
    DoubleFlat,
    DoubleSharp
}

public sealed record AccidentalAnchor(
    double X,
    double Y,
    string Reason);

public sealed record AccidentalCandidate(
    int MeasureNumber,
    int StaffNumber,
    string StaffId,
    ShapeElement Shape,
    AccidentalKind Kind,
    AccidentalAnchor Anchor,
    double ClassificationConfidence,
    bool IsKeySignatureSymbol);

public sealed record AccidentalDecision(
    AccidentalCandidate Candidate,
    bool Accepted,
    string Decision,
    NoteheadFact? ExplicitTarget,
    IReadOnlyList<NoteheadFact> AffectedNoteheads,
    int? StaffStep,
    double VerticalErrorInHalfSteps,
    double Confidence,
    string Reason);

public sealed record AccidentalAnalysisResult(
    IReadOnlyList<AccidentalDecision> Decisions)
{
    public IReadOnlyList<AccidentalDecision> Accepted =>
        Decisions.Where(decision => decision.Accepted).ToArray();
}

public sealed class AccidentalAnchorCalculator
{
    public AccidentalAnchor Calculate(
        ShapeElement shape,
        AccidentalKind kind)
    {
        var bounds = shape.Bounds;
        var anchorX = bounds.MinX + bounds.Width * 0.86;

        return kind switch
        {
            AccidentalKind.Flat => new AccidentalAnchor(
                anchorX,
                bounds.MinY + bounds.Height * 0.68,
                "flat anchor uses the center of the lower bowl rather than the full glyph bbox center"),

            AccidentalKind.DoubleFlat => new AccidentalAnchor(
                bounds.MinX + bounds.Width * 0.90,
                bounds.MinY + bounds.Height * 0.68,
                "double-flat anchor uses the lower bowl band and is biased toward the affected note on the right"),

            AccidentalKind.Sharp => new AccidentalAnchor(
                anchorX,
                bounds.CenterY,
                "sharp anchor uses bbox vertical center and is biased toward the right edge"),

            AccidentalKind.Natural => new AccidentalAnchor(
                anchorX,
                bounds.CenterY,
                "natural anchor uses bbox vertical center and is biased toward the right edge"),

            AccidentalKind.DoubleSharp => new AccidentalAnchor(
                anchorX,
                bounds.CenterY,
                "double-sharp anchor uses bbox vertical center and is biased toward the right edge"),

            _ => throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "Unsupported accidental kind.")
        };
    }
}

public sealed class AccidentalAnalyzer
{
    private const double MinimumClassificationConfidence = 0.70;
    private const double MaximumVerticalErrorInHalfSteps = 0.45;

    private readonly AccidentalAnchorCalculator _anchorCalculator;

    public AccidentalAnalyzer(
        AccidentalAnchorCalculator? anchorCalculator = null)
    {
        _anchorCalculator = anchorCalculator ?? new AccidentalAnchorCalculator();
    }

    public AccidentalAnalysisResult Analyze(
        SemanticDocument document,
        SemanticFacts facts)
    {
        var noteheads = facts
            .OfType<NoteheadFact>()
            .ToArray();

        if (noteheads.Length == 0)
        {
            throw new InvalidDataException(
                "AccidentalPass requires NoteheadPass to run first.");
        }

        var keySignatureShapeIds = FindKeySignatureShapeIds(
            document,
            facts,
            noteheads);

        var candidates = CollectCandidates(
            document,
            keySignatureShapeIds);

        var preliminary = candidates
            .Select(candidate => MatchExplicitTarget(
                document,
                candidate,
                noteheads))
            .ToArray();

        var acceptedWithPropagation = ResolvePropagation(
            preliminary,
            noteheads);

        return new AccidentalAnalysisResult(
            acceptedWithPropagation
                .OrderBy(decision => decision.Candidate.MeasureNumber)
                .ThenBy(decision => decision.Candidate.StaffNumber)
                .ThenBy(decision => decision.Candidate.Anchor.X)
                .ThenBy(decision => decision.Candidate.Anchor.Y)
                .ToArray());
    }

    private IReadOnlyList<AccidentalCandidate> CollectCandidates(
        SemanticDocument document,
        IReadOnlySet<string> keySignatureShapeIds)
    {
        var collector = new CandidateCollector(
            _anchorCalculator,
            keySignatureShapeIds);
        collector.Visit(document);

        return collector.Candidates
            .GroupBy(candidate => candidate.Shape.ShapeId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
    }

    private static AccidentalDecision MatchExplicitTarget(
        SemanticDocument document,
        AccidentalCandidate candidate,
        IReadOnlyList<NoteheadFact> noteheads)
    {
        if (candidate.IsKeySignatureSymbol)
        {
            return Rejected(
                candidate,
                "key-signature-symbol",
                "classified accidental belongs to the printed key signature header and is not a local accidental");
        }

        var measure = document.Measures
            .Single(item => item.Number == candidate.MeasureNumber);
        var staff = candidate.StaffNumber == 1
            ? measure.Upper
            : measure.Lower;
        var halfStep = staff.LineSpacing / 2.0;

        if (halfStep <= 0)
        {
            return Rejected(
                candidate,
                "invalid-staff-spacing",
                "staff line spacing is not positive");
        }

        var matches = noteheads
            .Where(notehead =>
                notehead.MeasureNumber == candidate.MeasureNumber
                && notehead.Staff == candidate.StaffNumber
                && notehead.CenterX > candidate.Shape.Bounds.MaxX)
            .Select(notehead => new
            {
                Notehead = notehead,
                Error = Math.Abs(
                    notehead.CenterY - candidate.Anchor.Y) / halfStep
            })
            .Where(match => match.Error <= MaximumVerticalErrorInHalfSteps)
            .OrderBy(match => match.Notehead.CenterX)
            .ThenBy(match => match.Error)
            .ThenBy(match => match.Notehead.ShapeId, StringComparer.Ordinal)
            .ToArray();

        if (matches.Length == 0)
        {
            return Rejected(
                candidate,
                "no-right-notehead-on-anchor-line",
                $"no notehead to the right matches accidental anchor y={candidate.Anchor.Y:F2} "
                + $"within {MaximumVerticalErrorInHalfSteps:F2} half-step(s)");
        }

        var explicitTarget = matches[0].Notehead;
        var verticalError = matches[0].Error;
        var verticalScore = Math.Clamp(
            1.0 - verticalError / MaximumVerticalErrorInHalfSteps,
            0,
            1);
        var confidence = candidate.ClassificationConfidence * 0.70
            + verticalScore * 0.30;

        return new AccidentalDecision(
            candidate,
            true,
            "local-accidental",
            explicitTarget,
            [explicitTarget],
            explicitTarget.StaffStep,
            verticalError,
            confidence,
            $"{candidate.Anchor.Reason}; first matching notehead to the right is "
            + $"{explicitTarget.ShapeId} on staff-step={explicitTarget.StaffStep}; "
            + $"vertical-error={verticalError:F3} half-step(s)");
    }

    private static IReadOnlyList<AccidentalDecision> ResolvePropagation(
        IReadOnlyList<AccidentalDecision> decisions,
        IReadOnlyList<NoteheadFact> noteheads)
    {
        var result = decisions.ToDictionary(
            decision => decision.Candidate.Shape.ShapeId,
            StringComparer.Ordinal);

        var accepted = decisions
            .Where(decision =>
                decision.Accepted
                && decision.ExplicitTarget is not null
                && decision.StaffStep is not null)
            .ToArray();

        foreach (var group in accepted.GroupBy(decision => new
                 {
                     decision.Candidate.MeasureNumber,
                     decision.Candidate.StaffNumber,
                     StaffStep = decision.StaffStep!.Value
                 }))
        {
            var orderedAccidentals = group
                .OrderBy(decision => decision.Candidate.Anchor.X)
                .ToArray();

            for (var index = 0; index < orderedAccidentals.Length; index++)
            {
                var current = orderedAccidentals[index];
                var nextX = index + 1 < orderedAccidentals.Length
                    ? orderedAccidentals[index + 1].Candidate.Anchor.X
                    : double.PositiveInfinity;
                var explicitTarget = current.ExplicitTarget!;

                var affected = noteheads
                    .Where(notehead =>
                        notehead.MeasureNumber == current.Candidate.MeasureNumber
                        && notehead.Staff == current.Candidate.StaffNumber
                        && notehead.StaffStep == current.StaffStep
                        && notehead.CenterX >= explicitTarget.CenterX - 0.001
                        && notehead.CenterX < nextX)
                    .OrderBy(notehead => notehead.CenterX)
                    .ThenBy(notehead => notehead.ShapeId, StringComparer.Ordinal)
                    .ToArray();

                result[current.Candidate.Shape.ShapeId] = current with
                {
                    AffectedNoteheads = affected,
                    Reason = current.Reason
                        + $"; affects {affected.Length} notehead(s) on this staff-step "
                        + (double.IsPositiveInfinity(nextX)
                            ? "through the end of the measure"
                            : $"until the next accidental at x={nextX:F2}")
                };
            }
        }

        return decisions
            .Select(decision => result[decision.Candidate.Shape.ShapeId])
            .ToArray();
    }

    private static HashSet<string> FindKeySignatureShapeIds(
        SemanticDocument document,
        SemanticFacts facts,
        IReadOnlyList<NoteheadFact> noteheads)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var key = facts
            .OfType<KeySignatureFact>()
            .OrderBy(fact => fact.MeasureNumber)
            .FirstOrDefault();

        if (key is null)
        {
            return result;
        }

        foreach (var shapeId in key.SourceShapeIds)
        {
            result.Add(shapeId);
        }

        if (key.AccidentalCount == 0)
        {
            return result;
        }

        var expectedLabel = key.AccidentalKind switch
        {
            "flat" => "FLAT",
            "sharp" => "SHARP",
            _ => null
        };

        if (expectedLabel is null)
        {
            return result;
        }

        foreach (var measure in document.Measures.Where(measure =>
                     measure.Number == 1 || measure.BreakBefore))
        {
            foreach (var staff in new[] { measure.Upper, measure.Lower })
            {
                var clef = facts
                    .OfType<ClefFact>()
                    .Where(fact =>
                        fact.MeasureNumber == measure.Number
                        && fact.Staff == staff.StaffNumber)
                    .OrderBy(fact => fact.X)
                    .FirstOrDefault();

                if (clef is null)
                {
                    continue;
                }

                var clefElement = staff.Elements
                    .FirstOrDefault(element => element.ShapeId == clef.ShapeId);

                if (clefElement is null)
                {
                    continue;
                }

                var firstNoteX = noteheads
                    .Where(notehead =>
                        notehead.MeasureNumber == measure.Number
                        && notehead.Staff == staff.StaffNumber)
                    .Select(notehead => notehead.CenterX)
                    .DefaultIfEmpty(double.PositiveInfinity)
                    .Min();

                var repeatedKeySymbols = staff.Elements
                    .OfType<ShapeElement>()
                    .Where(shape =>
                        shape.Classification is not null
                        && shape.Classification.Confidence >= MinimumClassificationConfidence
                        && shape.Classification.Label == expectedLabel
                        && shape.Bounds.MinX > clefElement.Bounds.MaxX
                        && shape.CenterX < firstNoteX)
                    .OrderBy(shape => shape.CenterX)
                    .Take(key.AccidentalCount)
                    .ToArray();

                if (repeatedKeySymbols.Length != key.AccidentalCount)
                {
                    continue;
                }

                foreach (var symbol in repeatedKeySymbols)
                {
                    result.Add(symbol.ShapeId);
                }
            }
        }

        return result;
    }

    private static AccidentalDecision Rejected(
        AccidentalCandidate candidate,
        string decision,
        string reason)
    {
        return new AccidentalDecision(
            candidate,
            false,
            decision,
            null,
            [],
            null,
            double.PositiveInfinity,
            0,
            reason);
    }

    private static AccidentalKind? ReadKind(string label)
    {
        return label switch
        {
            "FLAT" => AccidentalKind.Flat,
            "SHARP" => AccidentalKind.Sharp,
            "NATURAL" => AccidentalKind.Natural,
            "DOUBLE_FLAT" => AccidentalKind.DoubleFlat,
            "DOUBLE_SHARP" => AccidentalKind.DoubleSharp,
            _ => null
        };
    }

    private sealed class CandidateCollector : SemanticVisitor
    {
        private readonly AccidentalAnchorCalculator _anchorCalculator;
        private readonly IReadOnlySet<string> _keySignatureShapeIds;
        private readonly List<AccidentalCandidate> _candidates = [];

        public CandidateCollector(
            AccidentalAnchorCalculator anchorCalculator,
            IReadOnlySet<string> keySignatureShapeIds)
        {
            _anchorCalculator = anchorCalculator;
            _keySignatureShapeIds = keySignatureShapeIds;
        }

        public IReadOnlyList<AccidentalCandidate> Candidates => _candidates;

        protected override void VisitShape(
            MeasureScene measure,
            StaffMeasureScene staff,
            ShapeElement shape)
        {
            var classification = shape.Classification;

            if (classification is null
                || classification.Confidence < MinimumClassificationConfidence)
            {
                return;
            }

            var kind = ReadKind(classification.Label);
            if (kind is null)
            {
                return;
            }

            _candidates.Add(new AccidentalCandidate(
                measure.Number,
                staff.StaffNumber,
                staff.StaffId,
                shape,
                kind.Value,
                _anchorCalculator.Calculate(shape, kind.Value),
                classification.Confidence,
                _keySignatureShapeIds.Contains(shape.ShapeId)));
        }
    }
}
