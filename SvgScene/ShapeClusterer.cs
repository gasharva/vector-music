namespace SvgMusic.Scene;

public interface IShapeClusterer
{
    NotationScene Cluster(GeometricScene scene);

    IReadOnlyList<ArcDiagnostic> ArcDiagnostics { get; }

    IReadOnlyList<StrokeDiagnostic> StrokeDiagnostics { get; }
}

/// <summary>
/// Runs deterministic primitive extraction first and clusters only the residual
/// contour shapes that could not be represented as simple primitives.
/// </summary>
public sealed class ShapeClusterer : IShapeClusterer
{
    private readonly double _distanceThreshold;
    private readonly IGeometryAnalyzer _geometryAnalyzer;
    private readonly IArcExtractor _arcExtractor;
    private readonly IEllipseLikeExtractor _ellipseExtractor;
    private readonly ShapeDescriptorMatcher _descriptorMatcher;

    public IReadOnlyList<ArcDiagnostic> ArcDiagnostics =>
        _arcExtractor.Diagnostics;

    public IReadOnlyList<StrokeDiagnostic> StrokeDiagnostics =>
        _geometryAnalyzer.Diagnostics;

    public ShapeClusterer(
        IGeometryAnalyzer? geometryAnalyzer = null,
        IArcExtractor? arcExtractor = null,
        IEllipseLikeExtractor? ellipseExtractor = null,
        double distanceThreshold = 0.035)
    {
        _geometryAnalyzer = geometryAnalyzer ?? new GeometryAnalyzer();
        _arcExtractor = arcExtractor ?? new ArcExtractor();
        _ellipseExtractor = ellipseExtractor ?? new EllipseLikeExtractor();
        _descriptorMatcher = new ShapeDescriptorMatcher();
        _distanceThreshold = distanceThreshold;
    }

    public NotationScene Cluster(GeometricScene scene)
    {
        _geometryAnalyzer.ClearDiagnostics();
        _arcExtractor.ClearDiagnostics();

        var prototypes = new List<ShapePrototype>();
        var instances = new List<ShapeInstance>();
        var strokes = new List<Stroke>();
        var curvedStrokes = new List<CurvedStroke>();
        var ellipses = new List<EllipseLike>();

        foreach (var shape in scene.Shapes)
        {

            // Specific primitive models get first refusal. In particular an
            // open shallow arc can satisfy the generic PCA straightness tolerance,
            // so generic Stroke must remain the fallback rather than stealing it.
            if (_ellipseExtractor.TryCreateEllipse(
                shape,
                out var ellipse))
            {
                ellipses.Add(ellipse);
                continue;
            }

            if (_arcExtractor.TryCreateArc(
                shape,
                out var curvedStroke))
            {
                curvedStrokes.Add(curvedStroke);
                continue;
            }

            if (_geometryAnalyzer.TryCreateStroke(
                shape,
                out var stroke))
            {
                strokes.Add(stroke);
                continue;
            }

            AddResidualShape(
                shape,
                prototypes,
                instances);
        }

        return new NotationScene(
            prototypes,
            instances,
            strokes,
            curvedStrokes,
            ellipses);
    }

    private void AddResidualShape(
        GeometricShape shape,
        List<ShapePrototype> prototypes,
        List<ShapeInstance> instances)
    {
        var descriptor = _descriptorMatcher.Describe(shape);

        var bestPrototype = FindClosestPrototype(
            descriptor,
            prototypes,
            out var bestDistance);

        if (bestPrototype is null ||
            bestDistance > _distanceThreshold)
        {
            bestPrototype = new ShapePrototype(
                $"prototype-{prototypes.Count + 1}",
                shape.Id,
                descriptor);

            prototypes.Add(bestPrototype);
        }

        instances.Add(new ShapeInstance(
            shape.Id,
            bestPrototype.Id,
            shape.Bounds.MinX,
            shape.Bounds.MinY,
            shape.Bounds.Width,
            shape.Bounds.Height,
            shape.SourceKind,
            shape.SourceIndex));
    }

    private ShapePrototype? FindClosestPrototype(
        ShapeDescriptor descriptor,
        IReadOnlyList<ShapePrototype> prototypes,
        out double bestDistance)
    {
        ShapePrototype? bestPrototype = null;
        bestDistance = double.PositiveInfinity;

        foreach (var prototype in prototypes)
        {
            var distance = _descriptorMatcher.Distance(
                descriptor,
                prototype.Descriptor);

            if (distance >= bestDistance)
            {
                continue;
            }

            bestDistance = distance;
            bestPrototype = prototype;
        }

        return bestPrototype;
    }
}
