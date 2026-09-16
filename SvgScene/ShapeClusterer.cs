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
    private readonly IHairpinExtractor _hairpinExtractor;
    private readonly BracketSpannerExtractor _bracketSpannerExtractor;
    private readonly VerticalZigZagExtractor _verticalZigZagExtractor;
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
        IHairpinExtractor? hairpinExtractor = null,
        BracketSpannerExtractor? bracketSpannerExtractor = null,
        VerticalZigZagExtractor? verticalZigZagExtractor = null,
        double distanceThreshold = 0.035)
    {
        _geometryAnalyzer = geometryAnalyzer ?? new GeometryAnalyzer();
        _hairpinExtractor = hairpinExtractor ?? new HairpinExtractor();
        _bracketSpannerExtractor = bracketSpannerExtractor ?? new BracketSpannerExtractor();
        _verticalZigZagExtractor = verticalZigZagExtractor ?? new VerticalZigZagExtractor();
        _arcExtractor = arcExtractor ?? new ArcExtractor();
        _ellipseExtractor = ellipseExtractor ?? new EllipseLikeExtractor();
        _descriptorMatcher = new ShapeDescriptorMatcher();
        _distanceThreshold = distanceThreshold;
    }

    public NotationScene Cluster(GeometricScene scene)
    {
        _geometryAnalyzer.ClearDiagnostics();
        _arcExtractor.ClearDiagnostics();

        var bracketExtraction = _bracketSpannerExtractor.Extract(scene);
        var consumedByBrackets = bracketExtraction.ConsumedShapeIds;
        var prototypes = new List<ShapePrototype>();
        var instances = new List<ShapeInstance>();
        var strokes = new List<Stroke>();
        var curvedStrokes = new List<CurvedStroke>();
        var ellipses = new List<EllipseLike>();
        var hairpins = new List<HairpinPrimitive>();
        var verticalZigZags = new List<VerticalZigZagPrimitive>();

        foreach (var shape in scene.Shapes)
        {
            if (consumedByBrackets.Contains(shape.Id))
                continue;

            // A regular vertical zigzag is much more specific than a generic arc,
            // stroke or residual glyph. Give it first refusal before those broader
            // geometric recognizers so arpeggio-like geometry never reaches the
            // contour classifier merely because it is not a standard glyph shape.
            if (_verticalZigZagExtractor.TryCreateVerticalZigZag(
                shape,
                out var verticalZigZag))
            {
                verticalZigZags.Add(verticalZigZag);
                continue;
            }

            // A very long, shallow hairpin can look almost straight to the generic
            // PCA stroke detector: its opening is tiny compared with its horizontal
            // span. Hairpin geometry is more specific than a generic stroke, so give
            // it first refusal. The extractor itself is deliberately strict about
            // requiring two straight branches, vertically separated endpoints and a
            // single opposite apex, so ordinary staff/ledger/bar lines are rejected.
            if (_hairpinExtractor.TryCreateHairpin(
                shape,
                out var hairpin))
            {
                hairpins.Add(hairpin);
                continue;
            }

            if (_geometryAnalyzer.TryCreateStroke(
                shape,
                out var stroke))
            {
                strokes.Add(stroke);
                continue;
            }

            if (_arcExtractor.TryCreateArc(
                shape,
                out var curvedStroke))
            {
                curvedStrokes.Add(curvedStroke);
                continue;
            }

            if (_ellipseExtractor.TryCreateEllipse(
                shape,
                out var ellipse))
            {
                ellipses.Add(ellipse);
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
            ellipses)
        {
            Hairpins = hairpins,
            BracketSpanners = bracketExtraction.Brackets,
            VerticalZigZags = verticalZigZags
        };
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
