namespace SvgMusic.Semantics;

public sealed record BeamCandidate(
    int MeasureNumber,
    StrokeElement Stroke,
    double LineSpacing,
    double Length,
    double LengthInSpacings,
    double WidthInSpacings,
    double Slope);

public sealed record BeamStemMatch(
    StemAttachmentFact Stem,
    double StemX,
    double BeamY,
    double DistanceFromFreeTipInSpacings,
    bool SupportsLeftEnd,
    bool SupportsRightEnd);

public sealed record BeamDecision(
    BeamCandidate Candidate,
    bool Accepted,
    string Decision,
    int? Level,
    IReadOnlyList<BeamStemMatch> Matches,
    bool LeftEndSupported,
    bool RightEndSupported,
    bool IsHook,
    bool IsCrossStaff,
    double Confidence,
    string Reason);

public sealed record BeamAnalysisResult(
    IReadOnlyList<BeamDecision> Decisions)
{
    public IReadOnlyList<BeamDecision> Accepted =>
        Decisions.Where(decision => decision.Accepted).ToArray();
}

public sealed class BeamAttachmentAnalyzer
{
    private const double MinimumLengthInSpacings = 0.55;
    private const double MinimumWidthInSpacings = 0.22;
    private const double MaximumWidthInSpacings = 0.80;
    private const double MaximumAbsoluteSlope = 0.75;
    private const double HorizontalEndpointToleranceInSpacings = 0.38;
    private const double StemIntersectionToleranceInSpacings = 0.20;
    private const double MaximumOutsideFreeTipInSpacings = 0.30;
    private const double MinimumLevelAgreement = 0.60;

    public BeamAnalysisResult Analyze(
        SemanticDocument document,
        SemanticFacts facts)
    {
        var stems = facts
            .OfType<StemAttachmentFact>()
            .Where(stem => stem.Direction != StemDirection.Ambiguous)
            .ToArray();

        if (stems.Length == 0)
        {
            throw new InvalidDataException(
                "BeamAttachmentPass requires StemAttachmentPass to run first.");
        }

        var stemIds = stems
            .Select(stem => stem.StemShapeId)
            .ToHashSet(StringComparer.Ordinal);
        var candidates = CollectCandidates(
            document,
            stemIds);
        var preliminary = candidates
            .Select(candidate => MatchStems(
                candidate,
                stems))
            .ToArray();
        var leveled = AssignLevels(preliminary);
        var validated = ValidateLevelRules(leveled);

        return new BeamAnalysisResult(
            validated
                .OrderBy(decision => decision.Candidate.MeasureNumber)
                .ThenBy(decision => Math.Min(
                    decision.Candidate.Stroke.Source.Start.X,
                    decision.Candidate.Stroke.Source.End.X))
                .ThenBy(decision => decision.Level ?? int.MaxValue)
                .ThenBy(decision => decision.Candidate.Stroke.ShapeId, StringComparer.Ordinal)
                .ToArray());
    }

    private static IReadOnlyList<BeamCandidate> CollectCandidates(
        SemanticDocument document,
        IReadOnlySet<string> stemIds)
    {
        var result = new List<BeamCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var measure in document.Measures)
        {
            var lineSpacing = AveragePositive(
                measure.Upper.LineSpacing,
                measure.Lower.LineSpacing);

            if (lineSpacing <= 0)
            {
                continue;
            }

            foreach (var stroke in measure.Upper.Elements
                         .OfType<StrokeElement>()
                         .Concat(measure.Lower.Elements.OfType<StrokeElement>()))
            {
                var key = $"{measure.Number}:{stroke.ShapeId}";
                if (!seen.Add(key)
                    || stemIds.Contains(stroke.ShapeId))
                {
                    continue;
                }

                var source = stroke.Source;
                var dx = source.End.X - source.Start.X;
                var dy = source.End.Y - source.Start.Y;
                var absDx = Math.Abs(dx);
                var length = Math.Sqrt(dx * dx + dy * dy);

                if (absDx <= 0.001)
                {
                    continue;
                }

                var slope = dy / dx;
                var lengthInSpacings = length / lineSpacing;
                var widthInSpacings = source.Width / lineSpacing;

                if (lengthInSpacings < MinimumLengthInSpacings
                    || widthInSpacings < MinimumWidthInSpacings
                    || widthInSpacings > MaximumWidthInSpacings
                    || Math.Abs(slope) > MaximumAbsoluteSlope)
                {
                    continue;
                }

                result.Add(new BeamCandidate(
                    measure.Number,
                    stroke,
                    lineSpacing,
                    length,
                    lengthInSpacings,
                    widthInSpacings,
                    slope));
            }
        }

        return result;
    }

    private static BeamDecision MatchStems(
        BeamCandidate candidate,
        IReadOnlyList<StemAttachmentFact> stems)
    {
        var source = candidate.Stroke.Source;
        var left = source.Start.X <= source.End.X
            ? source.Start
            : source.End;
        var right = source.Start.X <= source.End.X
            ? source.End
            : source.Start;
        var endpointTolerance = candidate.LineSpacing
            * HorizontalEndpointToleranceInSpacings;
        var intersectionTolerance = candidate.LineSpacing
            * StemIntersectionToleranceInSpacings
            + source.Width / 2.0;

        var matches = stems
            .Where(stem => stem.MeasureNumber == candidate.MeasureNumber)
            .Select(stem => MatchStem(
                candidate,
                stem,
                left.X,
                right.X,
                endpointTolerance,
                intersectionTolerance))
            .Where(match => match is not null)
            .Select(match => match!)
            .OrderBy(match => match.StemX)
            .ThenBy(match => match.Stem.StemShapeId, StringComparer.Ordinal)
            .ToArray();

        if (matches.Length == 0)
        {
            return Rejected(
                candidate,
                "no-intersecting-stem",
                "beam-like horizontal stroke does not intersect any accepted stem in the same measure");
        }

        var leftSupported = matches.Any(match => match.SupportsLeftEnd);
        var rightSupported = matches.Any(match => match.SupportsRightEnd);

        if (!leftSupported && !rightSupported)
        {
            return Rejected(
                candidate,
                "no-supported-beam-end",
                "beam-like stroke crosses stem shafts but neither beam end terminates at a stem");
        }

        var attachedStaffs = matches
            .SelectMany(match => match.Stem.AttachedStaffs)
            .Distinct()
            .OrderBy(staff => staff)
            .ToArray();
        var isCrossStaff = attachedStaffs.Length > 1
            || matches.Any(match => match.Stem.IsCrossStaff);

        var endSupportScore = leftSupported && rightSupported
            ? 1.0
            : 0.72;
        var widthScore = BeamWidthScore(candidate.WidthInSpacings);
        var slopeScore = Math.Clamp(
            1.0 - Math.Abs(candidate.Slope) / MaximumAbsoluteSlope,
            0,
            1);
        var confidence = 0.48 * endSupportScore
            + 0.30 * widthScore
            + 0.22 * slopeScore;

        return new BeamDecision(
            candidate,
            true,
            "beam-geometry",
            null,
            matches,
            leftSupported,
            rightSupported,
            leftSupported != rightSupported,
            isCrossStaff,
            confidence,
            $"beam-like stroke intersects {matches.Length} stem(s); "
            + $"left-supported={leftSupported}; right-supported={rightSupported}; "
            + $"width={candidate.WidthInSpacings:F3}sp; slope={candidate.Slope:F3}");
    }

    private static BeamStemMatch? MatchStem(
        BeamCandidate candidate,
        StemAttachmentFact stem,
        double leftX,
        double rightX,
        double endpointTolerance,
        double intersectionTolerance)
    {
        var stemX = (stem.StartX + stem.EndX) / 2.0;

        if (stemX < leftX - endpointTolerance
            || stemX > rightX + endpointTolerance)
        {
            return null;
        }

        var beamY = InterpolateBeamY(
            candidate.Stroke.Source,
            stemX);
        var stemMinY = Math.Min(stem.StartY, stem.EndY);
        var stemMaxY = Math.Max(stem.StartY, stem.EndY);

        if (beamY < stemMinY - intersectionTolerance
            || beamY > stemMaxY + intersectionTolerance)
        {
            return null;
        }

        var (_, freeTipY) = FreeTip(stem);
        var distanceTowardHead = stem.Direction switch
        {
            StemDirection.Up => beamY - freeTipY,
            StemDirection.Down => freeTipY - beamY,
            _ => double.NegativeInfinity
        };
        var distanceInSpacings = distanceTowardHead / candidate.LineSpacing;

        if (distanceInSpacings < -MaximumOutsideFreeTipInSpacings)
        {
            return null;
        }

        var supportsLeft = Math.Abs(stemX - leftX) <= endpointTolerance;
        var supportsRight = Math.Abs(stemX - rightX) <= endpointTolerance;

        return new BeamStemMatch(
            stem,
            stemX,
            beamY,
            Math.Max(0, distanceInSpacings),
            supportsLeft,
            supportsRight);
    }

    private static IReadOnlyList<BeamDecision> AssignLevels(
        IReadOnlyList<BeamDecision> decisions)
    {
        var accepted = decisions
            .Where(decision => decision.Accepted)
            .ToArray();
        var rankByBeamAndStem = new Dictionary<(string BeamId, string StemId), int>();

        foreach (var group in accepted
                     .SelectMany(decision => decision.Matches.Select(match => new
                     {
                         Decision = decision,
                         Match = match
                     }))
                     .GroupBy(item => new
                     {
                         item.Decision.Candidate.MeasureNumber,
                         StemId = item.Match.Stem.StemShapeId
                     }))
        {
            var ordered = group
                .OrderBy(item => item.Match.DistanceFromFreeTipInSpacings)
                .ThenBy(item => item.Decision.Candidate.Stroke.ShapeId, StringComparer.Ordinal)
                .ToArray();

            for (var index = 0; index < ordered.Length; index++)
            {
                rankByBeamAndStem[
                    (
                        ordered[index].Decision.Candidate.Stroke.ShapeId,
                        ordered[index].Match.Stem.StemShapeId
                    )] = index + 1;
            }
        }

        return decisions
            .Select(decision => AssignLevel(
                decision,
                rankByBeamAndStem))
            .ToArray();
    }

    private static BeamDecision AssignLevel(
        BeamDecision decision,
        IReadOnlyDictionary<(string BeamId, string StemId), int> rankByBeamAndStem)
    {
        if (!decision.Accepted)
        {
            return decision;
        }

        var beamId = decision.Candidate.Stroke.ShapeId;
        var ranks = decision.Matches
            .Select(match => rankByBeamAndStem[(beamId, match.Stem.StemShapeId)])
            .ToArray();
        var grouped = ranks
            .GroupBy(rank => rank)
            .Select(group => new
            {
                Level = group.Key,
                Count = group.Count()
            })
            .OrderByDescending(group => group.Count)
            .ThenBy(group => group.Level)
            .ToArray();
        var best = grouped[0];
        var agreement = (double)best.Count / ranks.Length;

        if (agreement < MinimumLevelAgreement)
        {
            return decision with
            {
                Accepted = false,
                Decision = "ambiguous-beam-level",
                Level = null,
                Confidence = 0,
                Reason = decision.Reason
                    + $"; per-stem beam ranks disagree: [{string.Join(',', ranks)}]"
            };
        }

        return decision with
        {
            Level = best.Level,
            Confidence = decision.Confidence * (0.80 + 0.20 * agreement),
            Reason = decision.Reason
                + $"; level={best.Level} from per-stem rank(s) [{string.Join(',', ranks)}]"
        };
    }

    private static IReadOnlyList<BeamDecision> ValidateLevelRules(
        IReadOnlyList<BeamDecision> decisions)
    {
        return decisions
            .Select(decision => ValidateLevelRule(decision))
            .ToArray();
    }

    private static BeamDecision ValidateLevelRule(BeamDecision decision)
    {
        if (!decision.Accepted || decision.Level is null)
        {
            return decision;
        }

        if (decision.Level == 1)
        {
            if (decision.Matches.Count < 2)
            {
                return decision with
                {
                    Accepted = false,
                    Decision = "primary-beam-needs-two-stems",
                    Confidence = 0,
                    Reason = decision.Reason
                        + "; a first-level beam must connect at least two stems"
                };
            }

            if (!decision.LeftEndSupported || !decision.RightEndSupported)
            {
                return decision with
                {
                    Accepted = false,
                    Decision = "primary-beam-needs-both-ends-supported",
                    Confidence = 0,
                    Reason = decision.Reason
                        + "; a first-level beam must terminate at stems on both ends"
                };
            }

            return decision with
            {
                Decision = "attached-primary-beam",
                IsHook = false,
                Reason = decision.Reason
                    + "; first-level beam has both ends supported by stems"
            };
        }

        if (!decision.LeftEndSupported && !decision.RightEndSupported)
        {
            return decision with
            {
                Accepted = false,
                Decision = "secondary-beam-needs-supported-end",
                Confidence = 0,
                Reason = decision.Reason
                    + "; level 2+ beam must terminate at at least one stem"
            };
        }

        return decision with
        {
            Decision = decision.IsHook
                ? "attached-beam-hook"
                : "attached-secondary-beam",
            Reason = decision.Reason
                + (decision.IsHook
                    ? "; one beam end is intentionally free (beam hook)"
                    : "; both beam ends terminate at stems")
        };
    }

    private static double InterpolateBeamY(
        SvgMusic.Scene.Stroke beam,
        double x)
    {
        var dx = beam.End.X - beam.Start.X;
        if (Math.Abs(dx) <= 0.001)
        {
            return (beam.Start.Y + beam.End.Y) / 2.0;
        }

        var t = (x - beam.Start.X) / dx;
        return beam.Start.Y + (beam.End.Y - beam.Start.Y) * t;
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
                $"Stem {stem.StemShapeId} has ambiguous direction.")
        };
    }

    private static double BeamWidthScore(double widthInSpacings)
    {
        const double ideal = 0.48;
        const double halfRange = 0.32;

        return Math.Clamp(
            1.0 - Math.Abs(widthInSpacings - ideal) / halfRange,
            0,
            1);
    }

    private static BeamDecision Rejected(
        BeamCandidate candidate,
        string decision,
        string reason)
    {
        return new BeamDecision(
            candidate,
            false,
            decision,
            null,
            [],
            false,
            false,
            false,
            false,
            0,
            reason);
    }

    private static double AveragePositive(
        double first,
        double second)
    {
        var values = new[] { first, second }
            .Where(value => value > 0)
            .ToArray();

        return values.Length == 0
            ? 0
            : values.Average();
    }
}
