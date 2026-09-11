namespace SvgMusic.Scene;

public interface ICompoundShapeSplitter
{
    GeometricScene Split(GeometricScene scene);
}

/// <summary>
/// Splits SVG compound paths into independent geometric components while
/// preserving contours that are connected or contained inside one another.
///
/// This is deliberately a geometric operation. It does not try to decide
/// whether two disconnected components belong to the same musical symbol.
/// Later recognition stages may group nearby primitives again if needed.
/// </summary>
public sealed class CompoundShapeSplitter : ICompoundShapeSplitter
{
    private const double Epsilon = 1e-9;

    public GeometricScene Split(GeometricScene scene)
    {
        var shapes = new List<GeometricShape>();

        foreach (var shape in scene.Shapes)
        {
            shapes.AddRange(SplitShape(shape));
        }

        return new GeometricScene(shapes);
    }

    private static IReadOnlyList<GeometricShape> SplitShape(GeometricShape shape)
    {
        var contours = shape.EffectiveContours
            .Where(contour => contour.Points.Count >= 2)
            .ToList();

        if (contours.Count <= 1)
        {
            return [shape];
        }

        var components = BuildComponents(contours);

        if (components.Count == 1)
        {
            return [shape];
        }

        var result = new List<GeometricShape>(components.Count);

        for (var componentIndex = 0; componentIndex < components.Count; componentIndex++)
        {
            var componentContours = components[componentIndex]
                .Select(index => contours[index])
                .ToList();

            var points = componentContours
                .SelectMany(contour => contour.Points)
                .ToList();

            var id = $"{shape.Id}.{componentIndex + 1}";

            result.Add(new GeometricShape(
                id,
                shape.SourceKind,
                points,
                BoundsD.FromPoints(points),
                shape.SourceId,
                shape.SourceIndex,
                componentContours.Count == 1 && componentContours[0].IsClosed,
                shape.StrokeWidth,
                componentContours));
        }

        return result;
    }

    private static IReadOnlyList<IReadOnlyList<int>> BuildComponents(
        IReadOnlyList<GeometricContour> contours)
    {
        var parent = Enumerable.Range(0, contours.Count).ToArray();

        int Find(int value)
        {
            while (parent[value] != value)
            {
                parent[value] = parent[parent[value]];
                value = parent[value];
            }

            return value;
        }

        void Union(int left, int right)
        {
            var leftRoot = Find(left);
            var rightRoot = Find(right);

            if (leftRoot != rightRoot)
            {
                parent[rightRoot] = leftRoot;
            }
        }

        for (var left = 0; left < contours.Count; left++)
        {
            for (var right = left + 1; right < contours.Count; right++)
            {
                if (BelongToSameComponent(contours[left], contours[right]))
                {
                    Union(left, right);
                }
            }
        }

        return Enumerable.Range(0, contours.Count)
            .GroupBy(Find)
            .Select(group => (IReadOnlyList<int>)group.ToList())
            .ToList();
    }

    private static bool BelongToSameComponent(
        GeometricContour left,
        GeometricContour right)
    {
        var leftBounds = BoundsD.FromPoints(left.Points);
        var rightBounds = BoundsD.FromPoints(right.Points);

        if (!BoundsOverlap(leftBounds, rightBounds))
        {
            return false;
        }

        if (ContoursIntersect(left, right))
        {
            return true;
        }

        if (left.IsClosed && ContainsAnyPoint(left, right))
        {
            return true;
        }

        if (right.IsClosed && ContainsAnyPoint(right, left))
        {
            return true;
        }

        return false;
    }

    private static bool BoundsOverlap(BoundsD left, BoundsD right) =>
        left.MaxX + Epsilon >= right.MinX
        && right.MaxX + Epsilon >= left.MinX
        && left.MaxY + Epsilon >= right.MinY
        && right.MaxY + Epsilon >= left.MinY;

    private static bool ContoursIntersect(
        GeometricContour left,
        GeometricContour right)
    {
        foreach (var leftSegment in Segments(left))
        {
            foreach (var rightSegment in Segments(right))
            {
                if (SegmentsIntersect(
                    leftSegment.Start,
                    leftSegment.End,
                    rightSegment.Start,
                    rightSegment.End))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool ContainsAnyPoint(
        GeometricContour container,
        GeometricContour candidate)
    {
        foreach (var point in candidate.Points)
        {
            if (PointInPolygon(point, container.Points))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<(PointD Start, PointD End)> Segments(
        GeometricContour contour)
    {
        for (var index = 0; index + 1 < contour.Points.Count; index++)
        {
            yield return (
                contour.Points[index],
                contour.Points[index + 1]);
        }

        if (contour.IsClosed
            && contour.Points.Count > 2
            && contour.Points[^1] != contour.Points[0])
        {
            yield return (
                contour.Points[^1],
                contour.Points[0]);
        }
    }

    private static bool SegmentsIntersect(
        PointD a,
        PointD b,
        PointD c,
        PointD d)
    {
        var abC = Cross(a, b, c);
        var abD = Cross(a, b, d);
        var cdA = Cross(c, d, a);
        var cdB = Cross(c, d, b);

        if (OppositeSigns(abC, abD) && OppositeSigns(cdA, cdB))
        {
            return true;
        }

        return Math.Abs(abC) <= Epsilon && PointOnSegment(c, a, b)
            || Math.Abs(abD) <= Epsilon && PointOnSegment(d, a, b)
            || Math.Abs(cdA) <= Epsilon && PointOnSegment(a, c, d)
            || Math.Abs(cdB) <= Epsilon && PointOnSegment(b, c, d);
    }

    private static bool OppositeSigns(double left, double right) =>
        left > Epsilon && right < -Epsilon
        || left < -Epsilon && right > Epsilon;

    private static double Cross(PointD a, PointD b, PointD point) =>
        (b.X - a.X) * (point.Y - a.Y)
        - (b.Y - a.Y) * (point.X - a.X);

    private static bool PointOnSegment(PointD point, PointD start, PointD end) =>
        point.X + Epsilon >= Math.Min(start.X, end.X)
        && point.X - Epsilon <= Math.Max(start.X, end.X)
        && point.Y + Epsilon >= Math.Min(start.Y, end.Y)
        && point.Y - Epsilon <= Math.Max(start.Y, end.Y);

    private static bool PointInPolygon(
        PointD point,
        IReadOnlyList<PointD> polygon)
    {
        if (polygon.Count < 3)
        {
            return false;
        }

        var inside = false;
        var previous = polygon.Count - 1;

        for (var current = 0; current < polygon.Count; current++)
        {
            var a = polygon[current];
            var b = polygon[previous];

            if (Math.Abs(Cross(a, b, point)) <= Epsilon
                && PointOnSegment(point, a, b))
            {
                return true;
            }

            var crossesRay = (a.Y > point.Y) != (b.Y > point.Y);

            if (crossesRay)
            {
                var crossingX = (b.X - a.X)
                    * (point.Y - a.Y)
                    / (b.Y - a.Y)
                    + a.X;

                if (point.X < crossingX)
                {
                    inside = !inside;
                }
            }

            previous = current;
        }

        return inside;
    }
}
