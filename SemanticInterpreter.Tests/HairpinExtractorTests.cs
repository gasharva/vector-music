using SvgMusic.Scene;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class HairpinExtractorTests
{
    [Fact]
    public void MuseScoreStylePolyline_IsRecognizedAsCrescendo()
    {
        var shape = Shape(
            "musescore-hairpin",
            [
                new PointD(1832.38, 1802.76),
                new PointD(968.99, 1790.54),
                new PointD(1832.38, 1778.33)
            ],
            strokeWidth: 2.55,
            sourceKind: "polyline");

        var accepted = new HairpinExtractor().TryCreateHairpin(
            shape,
            out var hairpin);

        Assert.True(accepted);
        Assert.Equal(HairpinKind.Crescendo, hairpin.Kind);
        Assert.Equal(968.99, hairpin.Apex.X, 2);
        Assert.True(hairpin.Opening > 20);
        Assert.True(hairpin.Confidence >= 0.90);
    }

    [Fact]
    public void LongShallowMuseScoreHairpin_IsResolvedAfterPrimitiveExtraction()
    {
        // This is the actual hairpin below measures 7-8. Generic PCA is allowed
        // to see it as a stroke, but the non-destructive hairpin candidate survives
        // and wins later when layout has not reserved that source geometry.
        var shape = Shape(
            "m7-hairpin",
            [
                new PointD(1832.38, 1802.76),
                new PointD(968.99, 1790.54),
                new PointD(1832.38, 1778.33)
            ],
            strokeWidth: 2.55,
            sourceKind: "polyline");

        var scene = new GeometricScene([shape]);
        var candidates = new CompositeCandidateDetector().Detect(scene);
        var primitiveNotation = new ShapeClusterer().Cluster(scene);
        var layout = new ScoreLayout([], []);

        var notation = new CompositeCandidateResolver().Resolve(
            primitiveNotation,
            candidates,
            layout);

        var hairpin = Assert.Single(notation.Hairpins);
        Assert.Equal(HairpinKind.Crescendo, hairpin.Kind);
        Assert.Empty(notation.Strokes);
    }

    [Fact]
    public void FlattenedInternetSubpath_IsRecognizedAfterExistingSubpathSplit()
    {
        // Same geometry as the flattened internet SVG after SvgNormalizer and
        // CompoundShapeSplitter have isolated its moveto-created subpath.
        var shape = Shape(
            "internet-hairpin",
            [
                new PointD(509.3, 336.7),
                new PointD(309.5, 340.1),
                new PointD(509.3, 343.6)
            ],
            strokeWidth: 0.7,
            sourceKind: "path");

        var accepted = new HairpinExtractor().TryCreateHairpin(
            shape,
            out var hairpin);

        Assert.True(accepted);
        Assert.Equal(HairpinKind.Crescendo, hairpin.Kind);
        Assert.InRange(hairpin.Length, 199, 201);
    }

    [Fact]
    public void HookedPedalBracket_IsNotMisclassifiedAsHairpin()
    {
        var shape = Shape(
            "pedal-bracket",
            [
                new PointD(974.9, 1892.3),
                new PointD(1931.1, 1892.3),
                new PointD(1931.1, 1866.81)
            ],
            strokeWidth: 2.55,
            sourceKind: "polyline");

        var accepted = new HairpinExtractor().TryCreateHairpin(
            shape,
            out _);

        Assert.False(accepted);
    }

    private static GeometricShape Shape(
        string id,
        IReadOnlyList<PointD> points,
        double strokeWidth,
        string sourceKind) =>
        new(
            id,
            sourceKind,
            points,
            BoundsD.FromPoints(points),
            null,
            null,
            false,
            strokeWidth,
            [new GeometricContour(points, false)],
            false,
            true);
}
