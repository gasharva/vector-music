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

        return (geometry, notation);
    }
}
