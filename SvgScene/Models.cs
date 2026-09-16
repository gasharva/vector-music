namespace SvgMusic.Scene;

public readonly record struct PointD(
    double X,
    double Y);

public readonly record struct BoundsD(
    double MinX,
    double MinY,
    double MaxX,
    double MaxY)
{
    public double Width => MaxX - MinX;
    public double Height => MaxY - MinY;
    public double CenterX => (MinX + MaxX) / 2.0;
    public double CenterY => (MinY + MaxY) / 2.0;

    public static BoundsD FromPoints(
        IReadOnlyList<PointD> points)
    {
        if (points.Count == 0)
        {
            return new BoundsD(0, 0, 0, 0);
        }

        return new BoundsD(
            points.Min(point => point.X),
            points.Min(point => point.Y),
            points.Max(point => point.X),
            points.Max(point => point.Y));
    }
}

public sealed record GeometricContour(
    IReadOnlyList<PointD> Points,
    bool IsClosed);

public sealed record GeometricShape(
    string Id,
    string SourceKind,
    IReadOnlyList<PointD> Points,
    BoundsD Bounds,
    string? SourceId = null,
    string? SourceIndex = null,
    bool IsClosed = false,
    double StrokeWidth = 0,
    IReadOnlyList<GeometricContour>? Contours = null,
    bool HasFill = false,
    bool HasStroke = false,
    string? SourceClass = null,
    string? StrokeDashArray = null)
{
    public IReadOnlyList<GeometricContour> EffectiveContours =>
        Contours is { Count: > 0 }
            ? Contours
            : [new GeometricContour(Points, IsClosed)];
}

public sealed record GeometricScene(
    IReadOnlyList<GeometricShape> Shapes);

public sealed record ShapePrototype(
    string Id,
    string RepresentativeShapeId,
    ShapeDescriptor Descriptor,
    SymbolClassification? Classification = null);

public sealed record ShapeInstance(
    string ShapeId,
    string PrototypeId,
    double X,
    double Y,
    double Width,
    double Height,
    string SourceKind,
    string? SourceIndex,
    SymbolClassification? Classification = null,
    IReadOnlyList<string>? AbsorbedPrimitiveShapeIds = null,
    LogicalOwnership? Ownership = null);

public sealed record Stroke(
    string ShapeId,
    PointD Start,
    PointD End,
    double Width,
    string SourceKind,
    string? SourceIndex,
    LogicalOwnership? Ownership = null);

public sealed record CurvedStroke(
    string ShapeId,
    IReadOnlyList<PointD> Centerline,
    IReadOnlyList<double> WidthProfile,
    double Bend,
    double SameSideRatio,
    QuadraticApproximation? Quadratic,
    string SourceKind,
    string? SourceIndex,
    LogicalOwnership? Ownership = null);

public sealed record QuadraticApproximation(
    PointD Start,
    PointD Control,
    PointD End,
    double FitError);

/// <summary>
/// Approximately circular/elliptical geometry; rotation is radians.
/// Hollow describes nested ellipse-like contours, not musical meaning.
/// </summary>
public sealed record EllipseLike(
    string ShapeId,
    PointD Center,
    double MajorRadius,
    double MinorRadius,
    double Rotation,
    bool IsHollow,
    double? HoleRatio,
    double FitError,
    string SourceKind,
    string? SourceIndex,
    LogicalOwnership? Ownership = null);

public enum HairpinKind
{
    Crescendo,
    Diminuendo
}

/// <summary>
/// A purely geometric wedge primitive. Musical attachment and exact dynamic
/// meaning are deliberately left to a later semantic pass.
/// </summary>
public sealed record HairpinPrimitive(
    string ShapeId,
    HairpinKind Kind,
    PointD Apex,
    PointD OpenUpper,
    PointD OpenLower,
    double Length,
    double Opening,
    double Confidence,
    string SourceKind,
    string? SourceIndex,
    LogicalOwnership? Ownership = null);

public enum BracketHookDirection
{
    None,
    Up,
    Down
}

/// <summary>
/// A generic horizontal bracket/spanner with zero, one or two short terminal
/// hooks. It deliberately does not decide whether the engraving means pedal,
/// ottava, volta, or another musical construct. Dashed versus solid stroke is
/// retained as geometric/style evidence for later semantic passes.
/// </summary>
public sealed record BracketSpannerPrimitive(
    string Id,
    PointD Start,
    PointD End,
    PointD? LeftHookEnd,
    PointD? RightHookEnd,
    BracketHookDirection LeftHookDirection,
    BracketHookDirection RightHookDirection,
    bool IsDashed,
    double StrokeWidth,
    double Confidence,
    IReadOnlyList<string> SourceShapeIds,
    LogicalOwnership? Ownership = null);

/// <summary>
/// A narrow, mostly vertical, oscillating geometric primitive. It intentionally
/// carries no musical label: semantic code may later decide that it is an
/// arpeggio or another notation mark.
/// </summary>
public sealed record VerticalZigZagPrimitive(
    string ShapeId,
    BoundsD Bounds,
    int TurnCount,
    double MeanAmplitude,
    double MeanHalfWaveHeight,
    double Confidence,
    string SourceKind,
    string? SourceIndex,
    LogicalOwnership? Ownership = null);

public sealed record NotationScene(
    IReadOnlyList<ShapePrototype> Prototypes,
    IReadOnlyList<ShapeInstance> Instances,
    IReadOnlyList<Stroke> Strokes,
    IReadOnlyList<CurvedStroke> CurvedStrokes,
    IReadOnlyList<EllipseLike> Ellipses)
{
    public IReadOnlyList<HairpinPrimitive> Hairpins { get; init; } =
        Array.Empty<HairpinPrimitive>();

    public IReadOnlyList<BracketSpannerPrimitive> BracketSpanners { get; init; } =
        Array.Empty<BracketSpannerPrimitive>();

    public IReadOnlyList<VerticalZigZagPrimitive> VerticalZigZags { get; init; } =
        Array.Empty<VerticalZigZagPrimitive>();
}

public sealed record ShapeDescriptor(
    double AspectRatio,
    double RelativeArea,
    IReadOnlyList<PointD> NormalizedPoints);
