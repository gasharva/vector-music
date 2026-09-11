namespace SvgMusic.Scene;

public sealed class ScenePipeline
{
    private readonly ISvgNormalizer _normalizer;
    private readonly ICompoundShapeSplitter _splitter;
    private readonly IShapeClusterer _clusterer;

    public ScenePipeline(
        ISvgNormalizer normalizer,
        IShapeClusterer clusterer,
        ICompoundShapeSplitter? splitter = null)
    {
        _normalizer = normalizer;
        _clusterer = clusterer;
        _splitter = splitter ?? new CompoundShapeSplitter();
    }

    public (GeometricScene Geometry, NotationScene Notation) Run(string svgFile)
    {
        var normalized = _normalizer.Normalize(svgFile);
        var geometry = _splitter.Split(normalized);
        var notation = _clusterer.Cluster(geometry);

        return (geometry, notation);
    }
}
