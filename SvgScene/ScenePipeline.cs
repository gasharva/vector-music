namespace SvgMusic.Scene;

public sealed class ScenePipeline
{
    private readonly ISvgNormalizer _normalizer;
    private readonly ICompoundShapeSplitter _splitter;
    private readonly IShapeClusterer _clusterer;
    private readonly SvgSourceMetadataAnnotator _metadataAnnotator;

    public ScenePipeline(
        ISvgNormalizer normalizer,
        IShapeClusterer clusterer,
        ICompoundShapeSplitter? splitter = null,
        SvgSourceMetadataAnnotator? metadataAnnotator = null)
    {
        _normalizer = normalizer;
        _clusterer = clusterer;
        _splitter = splitter ?? new CompoundShapeSplitter();
        _metadataAnnotator = metadataAnnotator ?? new SvgSourceMetadataAnnotator();
    }

    public (GeometricScene Geometry, NotationScene Notation) Run(string svgFile)
    {
        var normalized = _normalizer.Normalize(svgFile);
        var geometry = _splitter.Split(normalized);
        geometry = _metadataAnnotator.Annotate(svgFile, geometry);
        var notation = _clusterer.Cluster(geometry);

        // Primitive extraction intentionally sees only split geometry. Once layout is
        // known, add small reconstructed whole-path alternatives exclusively for
        // symbol classification/ownership/semantic selection.
        var layout = new ScoreLayoutAnalyzer().Analyze(notation);
        (geometry, notation) = new CompoundClassificationHypothesisBuilder()
            .Augment(geometry, notation, layout);

        return (geometry, notation);
    }
}
