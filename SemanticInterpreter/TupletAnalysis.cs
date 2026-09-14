namespace SvgMusic.Semantics;

public sealed record TupletCandidate(
    int MeasureNumber,
    int StaffNumber,
    ShapeElement Shape,
    int DisplayedNumber,
    string ClassificationLabel,
    double ClassificationConfidence,
    double LineSpacing);

public sealed record TupletBeamMatch(
    BeamAttachmentFact Beam,
    double HorizontalCenterErrorInSpacings,
    double VerticalDistanceInSpacings,
    double Score);

public sealed record TupletDecision(
    TupletCandidate Candidate,
    bool Accepted,
    string Decision,
    TupletBeamMatch? Match,
    int? ActualNotes,
    int? NormalNotes,
    bool CorrectedFromStemCount,
    IReadOnlyList<string> AttachedStemIds,
    IReadOnlyList<string> AttachedNoteheadIds,
    double Confidence,
    string Reason);

public sealed record TupletAnalysisResult(
    IReadOnlyList<TupletDecision> Decisions)
{
    public IReadOnlyList<TupletDecision> Accepted =>
        Decisions.Where(decision => decision.Accepted).ToArray();
}

public sealed class TupletAnalyzer
{
    private const double MinimumClassificationConfidence = 0.60;
    private const double MaximumHorizontalOverflowInSpacings = 0.90;
    private const double MaximumVerticalDistanceInSpacings = 3.20;
    private const int MinimumPlausibleTupletCount = 3;
    private const int MaximumPlausibleTupletCount = 15;

    public TupletAnalysisResult Analyze(
        SemanticDocument document,
        SemanticFacts facts)
    {
        var beams = facts
            .OfType<BeamAttachmentFact>()
            .Where(beam => beam.Level == 1)
            .ToArray();
        var stems = facts
            .OfType<StemAttachmentFact>()
            .ToDictionary(
                stem => stem.StemShapeId,
                StringComparer.Ordinal);

        if (beams.Length == 0)
        {
            throw new InvalidDataException(
                "TupletPass requires BeamAttachmentPass to run first.");
        }

        var candidates = CollectCandidates(document);
        var decisions = candidates
            .Select(candidate => MatchBeam(
                candidate,
                beams,
                stems))
            .OrderBy(decision => decision.Candidate.MeasureNumber)
            .ThenBy(decision => decision.Candidate.Shape.Bounds.MinX)
            .ThenBy(decision => decision.Candidate.Shape.ShapeId, StringComparer.Ordinal)
            .ToArray();

        return new TupletAnalysisResult(decisions);
    }

    private static IReadOnlyList<TupletCandidate> CollectCandidates(
        SemanticDocument document)
    {
        var result = new List<TupletCandidate>();
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

                    var displayedNumber = TryReadTupletNumber(classification.Label);
                    if (displayedNumber is null)
                    {
                        continue;
                    }

                    var key = $"{measure.Number}:{shape.ShapeId}";
                    if (!seen.Add(key))
                    {
                        continue;
                    }

                    result.Add(new TupletCandidate(
                        measure.Number,
                        staff.StaffNumber,
                        shape,
                        displayedNumber.Value,
                        classification.Label,
                        classification.Confidence,
                        staff.LineSpacing));
                }
            }
        }

        return result;
    }

    private static TupletDecision MatchBeam(
        TupletCandidate candidate,
        IReadOnlyList<BeamAttachmentFact> beams,
        IReadOnlyDictionary<string, StemAttachmentFact> stems)
    {
        if (candidate.LineSpacing <= 0)
        {
            return Rejected(
                candidate,
                "invalid-staff-spacing",
                "staff line spacing is not positive");
        }

        var matches = beams
            .Where(beam => beam.MeasureNumber == candidate.MeasureNumber)
            .Select(beam => Match(
                candidate,
                beam))
            .Where(match => match is not null)
            .Select(match => match!)
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.VerticalDistanceInSpacings)
            .ThenBy(match => match.Beam.BeamShapeId, StringComparer.Ordinal)
            .ToArray();

        if (matches.Length == 0)
        {
            return Rejected(
                candidate,
                "no-nearby-primary-beam",
                "classified tuplet/digit candidate has no compatible level-1 beam group in the same measure");
        }

        var best = matches[0];
        var attachedStemIds = best.Beam.AttachedStemIds
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var stemCount = attachedStemIds.Length;

        if (stemCount < MinimumPlausibleTupletCount
            || stemCount > MaximumPlausibleTupletCount)
        {
            return Rejected(
                candidate,
                "implausible-beam-group-size",
                $"nearest beam contains {stemCount} stem(s), outside the supported tuplet range "
                + $"{MinimumPlausibleTupletCount}..{MaximumPlausibleTupletCount}");
        }

        var correctedFromStemCount = candidate.DisplayedNumber != stemCount;
        var actualNotes = correctedFromStemCount
            ? stemCount
            : candidate.DisplayedNumber;
        var normalNotes = LargestPowerOfTwoBelow(actualNotes);

        var attachedNoteheadIds = attachedStemIds
            .Where(stems.ContainsKey)
            .SelectMany(stemId => stems[stemId].AttachedNoteheadIds)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var agreementScore = correctedFromStemCount
            ? 0.55
            : 1.0;
        var confidence = candidate.ClassificationConfidence * 0.50
            + best.Score * 0.35
            + agreementScore * 0.15;

        var correctionReason = correctedFromStemCount
            ? $"classifier displayed {candidate.DisplayedNumber}, but the matched primary beam "
                + $"contains {stemCount} rhythmic stem(s); using stem count as actual-notes"
            : $"classifier number {candidate.DisplayedNumber} agrees with the {stemCount}-stem beam group";

        return new TupletDecision(
            candidate,
            true,
            "attached-tuplet",
            best,
            actualNotes,
            normalNotes,
            correctedFromStemCount,
            attachedStemIds,
            attachedNoteheadIds,
            confidence,
            $"{candidate.ClassificationLabel} at {candidate.ClassificationConfidence:P1}; "
            + $"matched beam {best.Beam.BeamShapeId}; "
            + $"center-error={best.HorizontalCenterErrorInSpacings:F3}sp; "
            + $"vertical-distance={best.VerticalDistanceInSpacings:F3}sp; "
            + $"{correctionReason}; ratio={actualNotes}:{normalNotes}");
    }

    private static TupletBeamMatch? Match(
        TupletCandidate candidate,
        BeamAttachmentFact beam)
    {
        var spacing = candidate.LineSpacing;
        var minX = Math.Min(beam.StartX, beam.EndX);
        var maxX = Math.Max(beam.StartX, beam.EndX);
        var allowance = spacing * MaximumHorizontalOverflowInSpacings;
        var centerX = candidate.Shape.Bounds.CenterX;

        if (centerX < minX - allowance
            || centerX > maxX + allowance)
        {
            return null;
        }

        var beamY = InterpolateY(
            beam,
            Math.Clamp(centerX, minX, maxX));
        var verticalDistance = DistanceToInterval(
            beamY,
            candidate.Shape.Bounds.MinY,
            candidate.Shape.Bounds.MaxY);
        var verticalDistanceInSpacings = verticalDistance / spacing;

        if (verticalDistanceInSpacings > MaximumVerticalDistanceInSpacings)
        {
            return null;
        }

        var beamCenterX = (minX + maxX) / 2.0;
        var halfSpan = Math.Max((maxX - minX) / 2.0, spacing * 0.5);
        var horizontalCenterError = Math.Abs(centerX - beamCenterX) / spacing;
        var centerScore = Math.Clamp(
            1.0 - Math.Abs(centerX - beamCenterX) / (halfSpan + allowance),
            0,
            1);
        var verticalScore = Math.Clamp(
            1.0 - verticalDistanceInSpacings / MaximumVerticalDistanceInSpacings,
            0,
            1);
        var score = centerScore * 0.55
            + verticalScore * 0.45;

        return new TupletBeamMatch(
            beam,
            horizontalCenterError,
            verticalDistanceInSpacings,
            score);
    }

    private static double InterpolateY(
        BeamAttachmentFact beam,
        double x)
    {
        var dx = beam.EndX - beam.StartX;
        if (Math.Abs(dx) <= 0.001)
        {
            return (beam.StartY + beam.EndY) / 2.0;
        }

        var t = (x - beam.StartX) / dx;
        return beam.StartY + (beam.EndY - beam.StartY) * t;
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

    private static int LargestPowerOfTwoBelow(int value)
    {
        var result = 1;

        while (result * 2 < value)
        {
            result *= 2;
        }

        return result;
    }

    private static int? TryReadTupletNumber(string label)
    {
        const string tupletPrefix = "TUPLET_";
        const string digitPrefix = "DIGIT_";
        string? suffix = null;

        if (label.StartsWith(tupletPrefix, StringComparison.Ordinal))
        {
            suffix = label[tupletPrefix.Length..];
        }
        else if (label.StartsWith(digitPrefix, StringComparison.Ordinal))
        {
            suffix = label[digitPrefix.Length..];
        }

        if (suffix is null)
        {
            return null;
        }

        if (int.TryParse(suffix, out var numeric)
            && numeric >= MinimumPlausibleTupletCount
            && numeric <= MaximumPlausibleTupletCount)
        {
            return numeric;
        }

        return suffix switch
        {
            "THREE" => 3,
            "FOUR" => 4,
            "FIVE" => 5,
            "SIX" => 6,
            "SEVEN" => 7,
            "EIGHT" => 8,
            "NINE" => 9,
            "TEN" => 10,
            "ELEVEN" => 11,
            "TWELVE" => 12,
            _ => null
        };
    }

    private static TupletDecision Rejected(
        TupletCandidate candidate,
        string decision,
        string reason)
    {
        return new TupletDecision(
            candidate,
            false,
            decision,
            null,
            null,
            null,
            false,
            [],
            [],
            0,
            reason);
    }
}
