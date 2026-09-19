namespace SvgMusic.Scene;

public sealed class ScenePipeline
{
    private readonly ISvgNormalizer _normalizer;
    private readonly ICompoundShapeSplitter _splitter;
    private readonly IShapeClusterer _clusterer;
    private readonly SvgSourceMetadataAnnotator _metadataAnnotator;
    private readonly CompositeCandidateDetector _compositeDetector;
    private readonly CompositeCandidateResolver _compositeResolver;

    public ScenePipeline(
        ISvgNormalizer normalizer,
        IShapeClusterer clusterer,
        ICompoundShapeSplitter? splitter = null,
        SvgSourceMetadataAnnotator? metadataAnnotator = null,
        CompositeCandidateDetector? compositeDetector = null,
        CompositeCandidateResolver? compositeResolver = null)
    {
        _normalizer = normalizer;
        _clusterer = clusterer;
        _splitter = splitter ?? new CompoundShapeSplitter();
        _metadataAnnotator = metadataAnnotator ?? new SvgSourceMetadataAnnotator();
        _compositeDetector = compositeDetector ?? new CompositeCandidateDetector();
        _compositeResolver = compositeResolver ?? new CompositeCandidateResolver();
    }

    public (GeometricScene Geometry, NotationScene Notation) Run(string svgFile)
    {
        var normalized = _normalizer.Normalize(svgFile);
        var geometry = _splitter.Split(normalized);
        geometry = _metadataAnnotator.Annotate(svgFile, geometry);

        // Composite notation must see the original split geometry before generic
        // primitive extraction simplifies it. These are hypotheses only: nothing
        // is consumed until score layout has claimed structural geometry.
        var compositeCandidates = _compositeDetector.Detect(geometry);

        var notation = _clusterer.Cluster(geometry);
        var layout = new ScoreLayoutAnalyzer().Analyze(notation);

        notation = _compositeResolver.Resolve(
            notation,
            compositeCandidates,
            layout);

        // Primitive extraction intentionally sees only split geometry. Once layout
        // and composite conflicts are resolved, add small reconstructed whole-path
        // alternatives exclusively for symbol classification/ownership/semantics.
        (geometry, notation) = new CompoundClassificationHypothesisBuilder()
            .Augment(
                geometry,
                notation,
                layout,
                _compositeResolver.LastAcceptedSourceShapeIds);

        return (geometry, notation);
    }
}
