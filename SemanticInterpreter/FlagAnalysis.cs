namespace SvgMusic.Semantics;

public sealed record FlagCandidate(
    int MeasureNumber,
    ShapeElement Shape,
    int Level,
    string ClassificationLabel,
    double ClassificationConfidence,
    double LineSpacing);

public sealed record FlagStemMatch(
    StemAttachmentFact Stem,
    double TipX,
    double TipY,
    double LeftEdgeDistanceInSpacings,
    double VerticalDistanceInSpacings,
    double Score);

public sealed record FlagDecision(
    FlagCandidate Candidate,
    bool Accepted,
    string Decision,
    FlagStemMatch? Match,
    double Confidence,
    string Reason);

public sealed record FlagAnalysisResult(
    IReadOnlyList<FlagDecision> Decisions)
{
    public IReadOnlyList<FlagDecision> Accepted =>
        Decisions.Where(decision => decision.Accepted).ToArray();
}

public sealed class FlagAttachmentAnalyzer
{
    private const double MinimumClassificationConfidence = 0.75;
    private const double MaximumLeftEdgeDistanceInSpacings = 0.60;
    private const double MaximumVerticalDistanceInSpacings = 0.50;
    private const double MaximumCenterLeftOffsetInSpacings = 0.15;
    private const double AmbiguousScoreDifference = 0.08;

    public FlagAnalysisResult Analyze(
        SemanticDocument document,
        SemanticFacts facts)
    {
        var stems = facts
            .OfType<StemAttachmentFact>()
            .ToArray();

        if (stems.Length == 0)
        {
            throw new InvalidDataException(
                "FlagAttachmentPass requires StemAttachmentPass to run first.");
        }

        var candidates = CollectCandidates(document);
        var preliminary = candidates
            .Select(candidate => MatchStem(candidate, stems))
            .ToArray();
        var resolved = ResolveStemConflicts(preliminary);

        return new FlagAnalysisResult(
            resolved
                .OrderBy(decision => decision.Candidate.MeasureNumber)
                .ThenBy(decision => decision.Candidate.Shape.Bounds.MinX)
                .ThenBy(decision => decision.Candidate.Shape.Bounds.MinY)
                .ThenBy(decision => decision.Candidate.Shape.ShapeId, StringComparer.Ordinal)
                .ToArray());
    }

    private static IReadOnlyList<FlagCandidate> CollectCandidates(
        SemanticDocument document)
    {
        var result = new List<FlagCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var measure in document.Measures)
        {
            foreach (var staff in new[] { measure.Upper, measure.Lower })
            {
                foreach (var shape in staff.Elements.OfType<ShapeElement>())
                {
                    var classification = shape.Classification;
                    if (classification is null
                        || classification.Confidence < MinimumClassificationConfidence)
                    {
                        continue;
                    }

                    var level = TryReadFlagLevel(classification.Label);
                    if (level is null)
                    {
                        continue;
                    }

                    var key = $"{measure.Number}:{shape.ShapeId}";
                    if (!seen.Add(key))
                    {
                        continue;
                    }

                    result.Add(new FlagCandidate(
                        measure.Number,
                        shape,
                        level.Value,
                        classification.Label,
                        classification.Confidence,
                        staff.LineSpacing));
                }
            }
        }

        return result;
    }

    private static FlagDecision MatchStem(
        FlagCandidate candidate,
        IReadOnlyList<StemAttachmentFact> stems)
    {
        if (candidate.LineSpacing <= 0)
        {
            return Rejected(
                candidate,
                "invalid-staff-spacing",
                "staff line spacing is not positive");
        }

        var compatible = stems
            .Where(stem =>
                stem.MeasureNumber == candidate.MeasureNumber
                && stem.Direction != StemDirection.Ambiguous)
            .Select(stem => Match(
                candidate,
                stem))
            .Where(match => match is not null)
            .Select(match => match!)
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.LeftEdgeDistanceInSpacings)
            .ThenBy(match => match.VerticalDistanceInSpacings)
            .ThenBy(match => match.Stem.StemShapeId, StringComparer.Ordinal)
            .ToArray();

        if (compatible.Length == 0)
        {
            return Rejected(
                candidate,
                "no-compatible-stem-tip",
                "classified flag has no stem free tip close to its left edge in the same measure");
        }

        if (compatible.Length > 1
            && compatible[0].Score - compatible[1].Score < AmbiguousScoreDifference)
        {
            return Rejected(
                candidate,
                "ambiguous-stem-tip",
                $"two stem tips match almost equally: "
                + $"{compatible[0].Stem.StemShapeId}={compatible[0].Score:F3}, "
                + $"{compatible[1].Stem.StemShapeId}={compatible[1].Score:F3}");
        }

        var best = compatible[0];
        var geometryScore = best.Score;
        var confidence = candidate.ClassificationConfidence * 0.60
            + geometryScore * 0.40;

        return new FlagDecision(
            candidate,
            true,
            "attached-flag",
            best,
            confidence,
            $"{candidate.ClassificationLabel} at {candidate.ClassificationConfidence:P1}; "
            + $"free tip of stem {best.Stem.StemShapeId} is "
            + $"{best.LeftEdgeDistanceInSpacings:F3}sp from flag left edge and "
            + $"{best.VerticalDistanceInSpacings:F3}sp from its vertical bbox; "
            + $"level={candidate.Level}");
    }

    private static FlagStemMatch? Match(
        FlagCandidate candidate,
        StemAttachmentFact stem)
    {
        var (tipX, tipY) = FreeTip(stem);
        var bounds = candidate.Shape.Bounds;
        var spacing = candidate.LineSpacing;

        var leftEdgeDistance = Math.Abs(tipX - bounds.MinX);
        var leftEdgeDistanceInSpacings = leftEdgeDistance / spacing;

        if (leftEdgeDistanceInSpacings > MaximumLeftEdgeDistanceInSpacings)
        {
            return null;
        }

        var verticalDistance = DistanceToInterval(
            tipY,
            bounds.MinY,
            bounds.MaxY);
        var verticalDistanceInSpacings = verticalDistance / spacing;

        if (verticalDistanceInSpacings > MaximumVerticalDistanceInSpacings)
        {
            return null;
        }

        var centerLeftOffset = Math.Max(0, tipX - bounds.CenterX) / spacing;
        if (centerLeftOffset > MaximumCenterLeftOffsetInSpacings)
        {
            return null;
        }

        var horizontalScore = Math.Clamp(
            1.0 - leftEdgeDistanceInSpacings / MaximumLeftEdgeDistanceInSpacings,
            0,
            1);
        var verticalScore = Math.Clamp(
            1.0 - verticalDistanceInSpacings / MaximumVerticalDistanceInSpacings,
            0,
            1);
        var score = horizontalScore * 0.62
            + verticalScore * 0.38;

        return new FlagStemMatch(
            stem,
            tipX,
            tipY,
            leftEdgeDistanceInSpacings,
            verticalDistanceInSpacings,
            score);
    }

    private static IReadOnlyList<FlagDecision> ResolveStemConflicts(
        IReadOnlyList<FlagDecision> decisions)
    {
        var result = decisions.ToDictionary(
            decision => decision.Candidate.Shape.ShapeId,
            StringComparer.Ordinal);

        foreach (var group in decisions
                     .Where(decision => decision.Accepted && decision.Match is not null)
                     .GroupBy(decision => decision.Match!.Stem.StemShapeId, StringComparer.Ordinal))
        {
            var ordered = group
                .OrderByDescending(decision => decision.Confidence)
                .ThenByDescending(decision => decision.Match!.Score)
                .ThenBy(decision => decision.Candidate.Shape.ShapeId, StringComparer.Ordinal)
                .ToArray();

            if (ordered.Length <= 1)
            {
                continue;
            }

            var winner = ordered[0];
            foreach (var loser in ordered.Skip(1))
            {
                result[loser.Candidate.Shape.ShapeId] = loser with
                {
                    Accepted = false,
                    Decision = "stem-already-has-better-flag",
                    Confidence = 0,
                    Reason = $"stem {loser.Match!.Stem.StemShapeId} also matched "
                        + $"{winner.Candidate.Shape.ShapeId}; the higher-confidence flag was kept"
                };
            }
        }

        return decisions
            .Select(decision => result[decision.Candidate.Shape.ShapeId])
            .ToArray();
    }

    private static (double X, double Y) FreeTip(StemAttachmentFact stem)
    {
        return stem.Direction switch
        {
            StemDirection.Up => stem.StartY <= stem.EndY
                ? (stem.StartX, stem.StartY)
                : (stem.EndX, stem.EndY),

            StemDirection.Down => stem.StartY >= stem.EndY
                ? (stem.StartX, stem.StartY)
                : (stem.EndX, stem.EndY),

            _ => throw new InvalidOperationException(
                $"Stem {stem.StemShapeId} has no unambiguous free tip.")
        };
    }

    private static double DistanceToInterval(
        double value,
        double min,
        double max)
    {
        if (value < min)
        {
            return min - value;
        }

        if (value > max)
        {
            return value - max;
        }

        return 0;
    }

    private static int? TryReadFlagLevel(string label)
    {
        const string prefix = "FLAG_";
        if (!label.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var index = prefix.Length;
        var value = 0;
        var hasDigit = false;

        while (index < label.Length && char.IsDigit(label[index]))
        {
            hasDigit = true;
            value = value * 10 + (label[index] - '0');
            index++;
        }

        return hasDigit && value > 0
            ? value
            : null;
    }

    private static FlagDecision Rejected(
        FlagCandidate candidate,
        string decision,
        string reason)
    {
        return new FlagDecision(
            candidate,
            false,
            decision,
            null,
            0,
            reason);
    }
}
