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
        return new BoundsD(points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y));
    }
}

public sealed record GeometricShape(string Id, string SourceKind, IReadOnlyList<PointD> Points, BoundsD Bounds,
    string? SourceId = null, string? SourceIndex = null, bool IsClosed = false, double StrokeWidth = 0);
public sealed record GeometricScene(IReadOnlyList<GeometricShape> Shapes);
public sealed record ShapePrototype(string Id, string RepresentativeShapeId, ShapeDescriptor Descriptor);
public sealed record ShapeInstance(string ShapeId, string PrototypeId, double X, double Y, double Width, double Height,
    string SourceKind, string? SourceIndex);
public sealed record Stroke(string ShapeId, PointD Start, PointD End, double Width, string SourceKind, string? SourceIndex);

/// <summary>
/// A thin, smooth, one-sided curved mark recovered from a closed contour.
/// This is the primitive itself; it is not required to be a mathematical arc.
/// </summary>
public sealed record CurvedStroke(
    string ShapeId,
    IReadOnlyList<PointD> Centerline,
    IReadOnlyList<double> WidthProfile,
    double Bend,
    double SameSideRatio,
    QuadraticApproximation? Quadratic,
    string SourceKind,
    string? SourceIndex);

/// <summary>Optional compact approximation used for rendering/storage, never for deciding whether a shape is curved.</summary>
public sealed record QuadraticApproximation(PointD Start, PointD Control, PointD End, double FitError);

public sealed record NotationScene(IReadOnlyList<ShapePrototype> Prototypes, IReadOnlyList<ShapeInstance> Instances,
    IReadOnlyList<Stroke> Strokes, IReadOnlyList<CurvedStroke> CurvedStrokes);
public sealed record ShapeDescriptor(double AspectRatio, double RelativeArea, IReadOnlyList<PointD> NormalizedPoints);
