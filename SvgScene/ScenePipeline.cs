namespace SvgMusic.Scene;

public sealed class ScenePipeline
{
    private readonly ISvgNormalizer _normalizer;
    private readonly IShapeClusterer _clusterer;

    public ScenePipeline(ISvgNormalizer normalizer, IShapeClusterer clusterer)
    {
        _normalizer = normalizer;
        _clusterer = clusterer;
    }

    public (GeometricScene Geometry, NotationScene Notation) Run(string svgFile)
    {
        var geometry = _normalizer.Normalize(svgFile);
        var notation = _clusterer.Cluster(geometry);
        return (geometry, notation);
    }
}
