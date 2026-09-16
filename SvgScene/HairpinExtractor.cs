namespace SvgMusic.Scene;

public interface IHairpinExtractor
{
    bool TryCreateHairpin(
        GeometricShape shape,
        out HairpinPrimitive hairpin);
}

/// <summary>
/// Recognizes an engraved crescendo/diminuendo wedge from vector geometry only.
///
/// Both MuseScore-native SVG and flattened third-party SVGs preserve a hairpin as
/// one open V-shaped contour: the first and last contour points are the open end,
/// while an interior point forms the opposite horizontal extreme (the apex).
/// This extractor intentionally does not inspect MuseScore CSS classes.
/// </summary>
public sealed class HairpinExtractor : IHairpinExtractor
{
    private const double EndpointAlignmentFraction = 0.15;
    private const double MinimumHorizontalAspect = 3.0;
    private const double MaximumOpeningFraction = 0.40;
    private const double MinimumOpeningFraction = 0.005;
    private const double MaximumApexVerticalOffsetFraction = 0.38;
    private const double MaximumBranchPathRatio = 1.10;
    private const double MaximumBranchDeviationFraction = 0.06;
    private const double MinimumBranchLengthRatio = 0.60;

    public bool TryCreateHairpin(
        GeometricShape shape,
        out HairpinPrimitive hairpin)
    {
        hairpin = default!;

        var contours = shape.EffectiveContours
            .Where(contour => contour.Points.Count >= 2)
            .ToArray();

        if (contours.Length != 1 || contours[0].IsClosed)
        {
            return false;
        }

        var points = RemoveConsecutiveDuplicates(contours[0].Points);
        if (points.Count < 3)
        {
            return false;
        }

        var bounds = BoundsD.FromPoints(points);
        var width = bounds.Width;
        var height = bounds.Height;
        var effectiveThickness = Math.Max(shape.StrokeWidth, 1e-6);

        if (width <= 1e-9
            || height <= 1e-9
            || width / Math.Max(height, effectiveThickness) < MinimumHorizontalAspect)
        {
            return false;
        }

        // In a normal SVG hairpin the two contour endpoints are the two jaws of
        // the open side. They should be almost vertically aligned.
        var first = points[0];
        var last = points[^1];
        var opening = Math.Abs(first.Y - last.Y);
        var endpointXDifference = Math.Abs(first.X - last.X);

        if (opening < Math.Max(effectiveThickness * 1.35, width * MinimumOpeningFraction)
            || opening > width * MaximumOpeningFraction
            || endpointXDifference > width * EndpointAlignmentFraction + effectiveThickness)
        {
            return false;
        }

        var openX = (first.X + last.X) / 2.0;
        var apexIndex = Enumerable.Range(1, points.Count - 2)
            .OrderByDescending(index => Math.Abs(points[index].X - openX))
            .ThenBy(index => Math.Abs(points[index].Y - (first.Y + last.Y) / 2.0))
            .First();
        var apex = points[apexIndex];
        var horizontalLength = Math.Abs(openX - apex.X);

        if (horizontalLength < width * 0.80)
        {
            return false;
        }

        var firstDx = first.X - apex.X;
        var lastDx = last.X - apex.X;

        // Both open endpoints must lie on the same horizontal side of the apex.
        if (Math.Sign(firstDx) == 0
            || Math.Sign(lastDx) == 0
            || Math.Sign(firstDx) != Math.Sign(lastDx))
        {
            return false;
        }

        var openingMidY = (first.Y + last.Y) / 2.0;
        if (Math.Abs(apex.Y - openingMidY)
            > opening * MaximumApexVerticalOffsetFraction + effectiveThickness)
        {
            return false;
        }

        var leftBranch = AnalyzeBranch(points, 0, apexIndex);
        var rightBranch = AnalyzeBranch(points, apexIndex, points.Count - 1);

        if (!leftBranch.Accepted || !rightBranch.Accepted)
        {
            return false;
        }

        var shorterBranch = Math.Min(leftBranch.ChordLength, rightBranch.ChordLength);
        var longerBranch = Math.Max(leftBranch.ChordLength, rightBranch.ChordLength);

        if (longerBranch <= 1e-9
            || shorterBranch / longerBranch < MinimumBranchLengthRatio)
        {
            return false;
        }

        var upper = first.Y <= last.Y ? first : last;
        var lower = first.Y <= last.Y ? last : first;
        var kind = openX > apex.X
            ? HairpinKind.Crescendo
            : HairpinKind.Diminuendo;

        var symmetryPenalty = 1.0 - shorterBranch / longerBranch;
        var endpointPenalty = endpointXDifference / Math.Max(width, 1e-9);
        var apexPenalty = Math.Abs(apex.Y - openingMidY) / Math.Max(opening, 1e-9);
        var confidence = Math.Clamp(
            0.99
            - symmetryPenalty * 0.18
            - endpointPenalty * 0.25
            - apexPenalty * 0.12,
            0.60,
            0.99);

        hairpin = new HairpinPrimitive(
            shape.Id,
            kind,
            apex,
            upper,
            lower,
            horizontalLength,
            opening,
            confidence,
            shape.SourceKind,
            shape.SourceIndex);

        return true;
    }

    private static IReadOnlyList<PointD> RemoveConsecutiveDuplicates(
        IReadOnlyList<PointD> source)
    {
        var result = new List<PointD>(source.Count);

        foreach (var point in source)
        {
            if (result.Count == 0
                || GeometryAlgorithms.Distance(result[^1], point) > 1e-9)
            {
                result.Add(point);
            }
        }

        return result;
    }

    private static BranchAnalysis AnalyzeBranch(
        IReadOnlyList<PointD> points,
        int startIndex,
        int endIndex)
    {
        if (startIndex == endIndex)
        {
            return new BranchAnalysis(false, 0);
        }

        var from = points[startIndex];
        var to = points[endIndex];
        var chord = GeometryAlgorithms.Distance(from, to);

        if (chord <= 1e-9)
        {
            return new BranchAnalysis(false, chord);
        }

        var path = 0.0;
        var maxDeviation = 0.0;
        var step = startIndex < endIndex ? 1 : -1;

        for (var index = startIndex; index != endIndex; index += step)
        {
            path += GeometryAlgorithms.Distance(points[index], points[index + step]);
        }

        for (var index = startIndex; ; index += step)
        {
            maxDeviation = Math.Max(
                maxDeviation,
                Math.Abs(GeometryAlgorithms.SignedDistanceToLine(
                    points[index],
                    from,
                    to)));

            if (index == endIndex)
            {
                break;
            }
        }

        return new BranchAnalysis(
            path / chord <= MaximumBranchPathRatio
            && maxDeviation / chord <= MaximumBranchDeviationFraction,
            chord);
    }

    private sealed record BranchAnalysis(
        bool Accepted,
        double ChordLength);
}
