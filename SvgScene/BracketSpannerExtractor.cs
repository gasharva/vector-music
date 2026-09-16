namespace SvgMusic.Scene;

public sealed record BracketSpannerExtractionResult(
    IReadOnlyList<BracketSpannerPrimitive> Brackets,
    IReadOnlySet<string> ConsumedShapeIds);

/// <summary>
/// Finds generic bracket-like horizontal spanners before ordinary stroke
/// extraction. The result is intentionally musical-meaning agnostic: pedal,
/// ottava, volta and similar constructs are all the same primitive here.
///
/// Two SVG encodings are supported:
/// 1. one open orthogonal polyline (L, mirrored L, or U-like volta);
/// 2. a long horizontal stroke plus one/two separate short vertical strokes.
///
/// MuseScore class names are retained only for diagnostics elsewhere and are not
/// consulted here. Dashed versus solid is visual SVG stroke evidence.
/// </summary>
public sealed class BracketSpannerExtractor
{
    private const double AxisToleranceFraction = 0.035;
    private const double MinimumHorizontalWidthMultiple = 20.0;
    private const double MinimumHookWidthMultiple = 2.0;
    private const double MaximumHookToHorizontalRatio = 0.35;
    private const double MaximumHorizontalSceneWidthFraction = 0.70;
    private const double EndpointToleranceWidthMultiple = 2.2;
    private const double EndpointToleranceSpanFraction = 0.012;

    public BracketSpannerExtractionResult Extract(GeometricScene scene)
    {
        var brackets = new List<BracketSpannerPrimitive>();
        var consumed = new HashSet<string>(StringComparer.Ordinal);
        var sceneWidth = SceneWidth(scene);

        foreach (var shape in scene.Shapes)
        {
            if (TryCreateSingleShapeBracket(
                    shape,
                    brackets.Count + 1,
                    out var bracket))
            {
                brackets.Add(bracket);
                consumed.Add(shape.Id);
            }
        }

        var lineCandidates = scene.Shapes
            .Where(shape => !consumed.Contains(shape.Id))
            .Select(TryCreateLineCandidate)
            .Where(candidate => candidate is not null)
            .Cast<LineCandidate>()
            .ToArray();

        var horizontals = lineCandidates
            .Where(candidate => candidate.Axis == LineAxis.Horizontal)
            .Where(candidate =>
                candidate.Length >= candidate.Width * MinimumHorizontalWidthMultiple)
            .Where(candidate =>
                sceneWidth <= 1e-9
                || candidate.Length <= sceneWidth * MaximumHorizontalSceneWidthFraction)
            .OrderByDescending(candidate => candidate.Length)
            .ToArray();

        var verticals = lineCandidates
            .Where(candidate => candidate.Axis == LineAxis.Vertical)
            .ToArray();

        var usedVerticals = new HashSet<string>(StringComparer.Ordinal);

        foreach (var horizontal in horizontals)
        {
            if (consumed.Contains(horizontal.Shape.Id))
                continue;

            var left = horizontal.Left;
            var right = horizontal.Right;
            var leftHook = FindHook(
                left,
                horizontal,
                verticals,
                usedVerticals);
            var rightHook = FindHook(
                right,
                horizontal,
                verticals,
                usedVerticals);

            if (leftHook is null && rightHook is null)
                continue;

            var sources = new List<string> { horizontal.Shape.Id };

            if (leftHook is not null)
                sources.Add(leftHook.Shape.Id);
            if (rightHook is not null
                && rightHook.Shape.Id != leftHook?.Shape.Id)
            {
                sources.Add(rightHook.Shape.Id);
            }

            var dashed = sources
                .Select(id => scene.Shapes.First(shape => shape.Id == id))
                .Any(IsDashed);
            var strokeWidth = sources
                .Select(id => scene.Shapes.First(shape => shape.Id == id).StrokeWidth)
                .DefaultIfEmpty(horizontal.Width)
                .Max();

            brackets.Add(new BracketSpannerPrimitive(
                $"bracket-{brackets.Count + 1}",
                left,
                right,
                HookEnd(leftHook, left),
                HookEnd(rightHook, right),
                HookDirection(leftHook, left),
                HookDirection(rightHook, right),
                dashed,
                strokeWidth,
                dashed ? 0.95 : 0.90,
                sources));

            consumed.Add(horizontal.Shape.Id);

            if (leftHook is not null)
            {
                consumed.Add(leftHook.Shape.Id);
                usedVerticals.Add(leftHook.Shape.Id);
            }

            if (rightHook is not null)
            {
                consumed.Add(rightHook.Shape.Id);
                usedVerticals.Add(rightHook.Shape.Id);
            }
        }

        return new BracketSpannerExtractionResult(
            brackets,
            consumed);
    }

    private static bool TryCreateSingleShapeBracket(
        GeometricShape shape,
        int number,
        out BracketSpannerPrimitive bracket)
    {
        bracket = default!;

        if (!shape.HasStroke || shape.HasFill)
            return false;

        var contours = shape.EffectiveContours
            .Where(contour => contour.Points.Count >= 2)
            .ToArray();

        if (contours.Length != 1 || contours[0].IsClosed)
            return false;

        var points = RemoveConsecutiveDuplicates(contours[0].Points);

        if (points.Count is < 3 or > 4)
            return false;

        var segments = Enumerable.Range(0, points.Count - 1)
            .Select(index => Segment(points[index], points[index + 1], shape.StrokeWidth))
            .ToArray();

        if (segments.Any(segment => segment.Axis == LineAxis.Other))
            return false;

        var horizontals = segments
            .Where(segment => segment.Axis == LineAxis.Horizontal)
            .ToArray();
        var verticals = segments
            .Where(segment => segment.Axis == LineAxis.Vertical)
            .ToArray();

        if (horizontals.Length != 1
            || verticals.Length is < 1 or > 2)
        {
            return false;
        }

        var horizontal = horizontals[0];
        var width = Math.Max(shape.StrokeWidth, 1e-6);

        if (horizontal.Length < width * MinimumHorizontalWidthMultiple)
            return false;

        var left = horizontal.Start.X <= horizontal.End.X
            ? horizontal.Start
            : horizontal.End;
        var right = horizontal.Start.X <= horizontal.End.X
            ? horizontal.End
            : horizontal.Start;
        PointD? leftHookEnd = null;
        PointD? rightHookEnd = null;

        foreach (var vertical in verticals)
        {
            if (vertical.Length < width * MinimumHookWidthMultiple
                || vertical.Length > horizontal.Length * MaximumHookToHorizontalRatio)
            {
                return false;
            }

            var tolerance = EndpointTolerance(
                horizontal.Length,
                Math.Max(width, shape.StrokeWidth));

            if (TryOtherEndpoint(vertical, left, tolerance, out var leftEnd))
            {
                if (leftHookEnd is not null)
                    return false;

                leftHookEnd = leftEnd;
                continue;
            }

            if (TryOtherEndpoint(vertical, right, tolerance, out var rightEnd))
            {
                if (rightHookEnd is not null)
                    return false;

                rightHookEnd = rightEnd;
                continue;
            }

            return false;
        }

        if (leftHookEnd is null && rightHookEnd is null)
            return false;

        bracket = new BracketSpannerPrimitive(
            $"bracket-{number}",
            left,
            right,
            leftHookEnd,
            rightHookEnd,
            Direction(left, leftHookEnd),
            Direction(right, rightHookEnd),
            IsDashed(shape),
            width,
            0.97,
            [shape.Id]);

        return true;
    }

    private static LineCandidate? TryCreateLineCandidate(GeometricShape shape)
    {
        if (!shape.HasStroke || shape.HasFill)
            return null;

        var contours = shape.EffectiveContours
            .Where(contour => contour.Points.Count >= 2)
            .ToArray();

        if (contours.Length != 1 || contours[0].IsClosed)
            return null;

        var points = RemoveConsecutiveDuplicates(contours[0].Points);

        if (points.Count != 2)
            return null;

        var segment = Segment(
            points[0],
            points[1],
            shape.StrokeWidth);

        if (segment.Axis == LineAxis.Other)
            return null;

        var left = segment.Start.X <= segment.End.X
            ? segment.Start
            : segment.End;
        var right = segment.Start.X <= segment.End.X
            ? segment.End
            : segment.Start;

        return new LineCandidate(
            shape,
            segment.Axis,
            segment.Length,
            Math.Max(shape.StrokeWidth, 1e-6),
            left,
            right);
    }

    private static LineCandidate? FindHook(
        PointD endpoint,
        LineCandidate horizontal,
        IReadOnlyList<LineCandidate> verticals,
        IReadOnlySet<string> usedVerticals)
    {
        var tolerance = EndpointTolerance(
            horizontal.Length,
            horizontal.Width);

        return verticals
            .Where(vertical => !usedVerticals.Contains(vertical.Shape.Id))
            .Where(vertical =>
                vertical.Length >= Math.Max(
                    vertical.Width,
                    horizontal.Width) * MinimumHookWidthMultiple)
            .Where(vertical =>
                vertical.Length <= horizontal.Length * MaximumHookToHorizontalRatio)
            .Select(vertical => new
            {
                Candidate = vertical,
                Distance = Math.Min(
                    Distance(endpoint, vertical.Left),
                    Distance(endpoint, vertical.Right))
            })
            .Where(item => item.Distance <= tolerance)
            .OrderBy(item => item.Distance)
            .ThenBy(item => item.Candidate.Length)
            .Select(item => item.Candidate)
            .FirstOrDefault();
    }

    private static PointD? HookEnd(
        LineCandidate? hook,
        PointD endpoint)
    {
        if (hook is null)
            return null;

        return Distance(endpoint, hook.Left) <= Distance(endpoint, hook.Right)
            ? hook.Right
            : hook.Left;
    }

    private static BracketHookDirection HookDirection(
        LineCandidate? hook,
        PointD endpoint) =>
        Direction(endpoint, HookEnd(hook, endpoint));

    private static BracketHookDirection Direction(
        PointD baseline,
        PointD? hookEnd)
    {
        if (hookEnd is null)
            return BracketHookDirection.None;

        return hookEnd.Value.Y < baseline.Y
            ? BracketHookDirection.Up
            : BracketHookDirection.Down;
    }

    private static bool TryOtherEndpoint(
        SegmentInfo segment,
        PointD junction,
        double tolerance,
        out PointD other)
    {
        if (Distance(segment.Start, junction) <= tolerance)
        {
            other = segment.End;
            return true;
        }

        if (Distance(segment.End, junction) <= tolerance)
        {
            other = segment.Start;
            return true;
        }

        other = default;
        return false;
    }

    private static SegmentInfo Segment(
        PointD start,
        PointD end,
        double strokeWidth)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        var tolerance = Math.Max(
            Math.Max(strokeWidth, 1e-6) * 1.5,
            length * AxisToleranceFraction);
        var axis = Math.Abs(dy) <= tolerance
            ? LineAxis.Horizontal
            : Math.Abs(dx) <= tolerance
                ? LineAxis.Vertical
                : LineAxis.Other;

        return new SegmentInfo(
            start,
            end,
            length,
            axis);
    }

    private static IReadOnlyList<PointD> RemoveConsecutiveDuplicates(
        IReadOnlyList<PointD> source)
    {
        var result = new List<PointD>(source.Count);

        foreach (var point in source)
        {
            if (result.Count == 0
                || Distance(result[^1], point) > 1e-9)
            {
                result.Add(point);
            }
        }

        return result;
    }

    private static bool IsDashed(GeometricShape shape) =>
        !string.IsNullOrWhiteSpace(shape.StrokeDashArray);

    private static double EndpointTolerance(
        double span,
        double width) =>
        Math.Max(
            width * EndpointToleranceWidthMultiple,
            span * EndpointToleranceSpanFraction);

    private static double SceneWidth(GeometricScene scene)
    {
        var points = scene.Shapes
            .SelectMany(shape => shape.Points)
            .ToArray();

        return points.Length == 0
            ? 0
            : BoundsD.FromPoints(points).Width;
    }

    private static double Distance(PointD left, PointD right)
    {
        var dx = left.X - right.X;
        var dy = left.Y - right.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private enum LineAxis
    {
        Other,
        Horizontal,
        Vertical
    }

    private sealed record SegmentInfo(
        PointD Start,
        PointD End,
        double Length,
        LineAxis Axis);

    private sealed record LineCandidate(
        GeometricShape Shape,
        LineAxis Axis,
        double Length,
        double Width,
        PointD Left,
        PointD Right);
}
