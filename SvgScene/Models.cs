namespace SvgMusic.Scene;

public readonly record struct PointD(double X, double Y);

public readonly record struct BoundsD(double MinX, double MinY, double MaxX, double MaxY)
{
    public double Width => MaxX - MinX;
    public double Height => MaxY - MinY;
    public double CenterX => (MinX + MaxX) / 2.0;
    public double CenterY => (MinY + MaxY) / 2.0;

    public static BoundsD FromPoints(IReadOnlyList<PointD> points)
    {
        if (points.Count == 0) return new BoundsD(0, 0, 0, 0);

        var minX = points.Min(p => p.X);
        var minY = points.Min(p => p.Y);
        var maxX = points.Max(p => p.X);
        var maxY = points.Max(p => p.Y);
        return new BoundsD(minX, minY, maxX, maxY);
    }
}

public sealed record GeometricShape(
    string Id,
    string SourceKind,
    IReadOnlyList<PointD> Points,
    BoundsD Bounds,
    string? SourceId = null,
    string? SourceIndex = null,
    bool IsClosed = false,
    double StrokeWidth = 0);

public sealed record GeometricScene(IReadOnlyList<GeometricShape> Shapes);

public sealed record ShapePrototype(
    string Id,
    string RepresentativeShapeId,
    ShapeDescriptor Descriptor);

public sealed record ShapeInstance(
    string ShapeId,
    string PrototypeId,
    double X,
    double Y,
    double Width,
    double Height,
    string SourceKind,
    string? SourceIndex);

/// <summary>
/// Source-independent representation of line-like geometry.  A staff line may
/// arrive as SVG line/polyline, a thin rectangle, polygon or even a closed path;
/// after normalization they all become the same primitive.
/// </summary>
public sealed record Stroke(
    string ShapeId,
    PointD Start,
    PointD End,
    double Width,
    string SourceKind,
    string? SourceIndex);

public sealed record NotationScene(
    IReadOnlyList<ShapePrototype> Prototypes,
    IReadOnlyList<ShapeInstance> Instances,
    IReadOnlyList<Stroke> Strokes);

public sealed record ShapeDescriptor(
    double AspectRatio,
    double RelativeArea,
    IReadOnlyList<PointD> NormalizedPoints);
