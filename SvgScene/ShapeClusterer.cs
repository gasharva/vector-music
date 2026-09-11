namespace SvgMusic.Scene;

public interface IShapeClusterer
{
    NotationScene Cluster(GeometricScene scene);
}

public sealed class ShapeClusterer : IShapeClusterer
{
    private readonly double _distanceThreshold;
    private readonly IGeometryAnalyzer _geometryAnalyzer;

    public ShapeClusterer(
        IGeometryAnalyzer? geometryAnalyzer = null,
        double distanceThreshold = 0.035)
    {
        _geometryAnalyzer = geometryAnalyzer ?? new GeometryAnalyzer();
        _distanceThreshold = distanceThreshold;
    }

    public NotationScene Cluster(GeometricScene scene)
    {
        var prototypes = new List<ShapePrototype>();
        var instances = new List<ShapeInstance>();
        var strokes = new List<Stroke>();

        foreach (var shape in scene.Shapes)
        {
            if (_geometryAnalyzer.TryCreateStroke(shape, out var stroke))
            {
                strokes.Add(stroke);
                continue;
            }

            var descriptor = Describe(shape);
            ShapePrototype? best = null;
            var bestDistance = double.PositiveInfinity;

            foreach (var prototype in prototypes)
            {
                var distance = Distance(descriptor, prototype.Descriptor);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = prototype;
                }
            }

            if (best is null || bestDistance > _distanceThreshold)
            {
                best = new ShapePrototype(
                    $"prototype-{prototypes.Count + 1}",
                    shape.Id,
                    descriptor);
                prototypes.Add(best);
            }

            instances.Add(new ShapeInstance(
                shape.Id,
                best.Id,
                shape.Bounds.MinX,
                shape.Bounds.MinY,
                shape.Bounds.Width,
                shape.Bounds.Height,
                shape.SourceKind,
                shape.SourceIndex));
        }

        return new NotationScene(prototypes, instances, strokes);
    }

    private static ShapeDescriptor Describe(GeometricShape shape)
    {
        var bounds = shape.Bounds;
        var width = Math.Max(bounds.Width, 1e-9);
        var height = Math.Max(bounds.Height, 1e-9);

        var normalized = shape.Points
            .Select(p => new PointD(
                (p.X - bounds.MinX) / width,
                (p.Y - bounds.MinY) / height))
            .ToList();

        var polygonArea = Math.Abs(SignedArea(normalized));
        var aspectRatio = width / height;

        return new ShapeDescriptor(
            aspectRatio,
            polygonArea,
            normalized);
    }

    private static double Distance(ShapeDescriptor a, ShapeDescriptor b)
    {
        var aspectPenalty = Math.Abs(Math.Log(Math.Max(a.AspectRatio, 1e-9) / Math.Max(b.AspectRatio, 1e-9)));
        var areaPenalty = Math.Abs(a.RelativeArea - b.RelativeArea);

        var count = Math.Min(a.NormalizedPoints.Count, b.NormalizedPoints.Count);
        if (count == 0) return double.PositiveInfinity;

        var squared = 0.0;
        for (var i = 0; i < count; i++)
        {
            var ai = SampleAt(a.NormalizedPoints, i, count);
            var bi = SampleAt(b.NormalizedPoints, i, count);
            var dx = ai.X - bi.X;
            var dy = ai.Y - bi.Y;
            squared += dx * dx + dy * dy;
        }

        var pointRms = Math.Sqrt(squared / count);
        return pointRms + 0.25 * aspectPenalty + 0.15 * areaPenalty;
    }

    private static PointD SampleAt(IReadOnlyList<PointD> points, int index, int targetCount)
    {
        if (targetCount <= 1 || points.Count == 1) return points[0];

        var position = index * (points.Count - 1.0) / (targetCount - 1.0);
        var left = (int)Math.Floor(position);
        var right = Math.Min(left + 1, points.Count - 1);
        var t = position - left;

        return new PointD(
            points[left].X + (points[right].X - points[left].X) * t,
            points[left].Y + (points[right].Y - points[left].Y) * t);
    }

    private static double SignedArea(IReadOnlyList<PointD> points)
    {
        if (points.Count < 3) return 0;

        var area = 0.0;
        for (var i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            area += a.X * b.Y - b.X * a.Y;
        }

        return area / 2.0;
    }
}
