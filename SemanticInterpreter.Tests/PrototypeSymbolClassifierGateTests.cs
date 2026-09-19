using SvgMusic.Scene;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class PrototypeSymbolClassifierGateTests
{
    [Fact]
    public void GiantPrototype_IsRejectedBeforeRasterizationOrClassification()
    {
        var classifier = new CountingClassifier();
        var settings = new SvgMusicSettings
        {
            PrototypeClassifier = new PrototypeClassifierSettings
            {
                MaxMeasureWidthFraction = 0.25,
                MaxMeasureHeightFraction = 0.40
            }
        };

        var geometry = new GeometricScene(
            [FilledRectangle("giant", 0, 0, 80, 80)]);
        var notation = Notation(
            "giant",
            width: 80,
            height: 80);
        var layout = Layout(
            measureWidth: 100,
            measureHeight: 100);

        var runner = new PrototypeSymbolClassifier(
            classifier,
            settings: settings);
        var result = runner.Classify(
            geometry,
            notation,
            layout);

        Assert.Equal(0, classifier.Calls);
        Assert.Null(Assert.Single(result.Prototypes).Classification);

        var diagnostic = Assert.Single(runner.LastDiagnostics);
        Assert.True(diagnostic.Skipped);
        Assert.Contains("size gate", diagnostic.Reason);
        Assert.Empty(diagnostic.Scales);
    }

    [Fact]
    public void LocalPrototype_WithinConfiguredMeasureFractions_IsClassified()
    {
        var classifier = new CountingClassifier();
        var settings = new SvgMusicSettings
        {
            PrototypeClassifier = new PrototypeClassifierSettings
            {
                MaxMeasureWidthFraction = 0.25,
                MaxMeasureHeightFraction = 0.40
            }
        };

        var geometry = new GeometricScene(
            [FilledRectangle("small", 10, 10, 20, 20)]);
        var notation = Notation(
            "small",
            x: 10,
            y: 10,
            width: 20,
            height: 20);
        var layout = Layout(
            measureWidth: 100,
            measureHeight: 100);

        var runner = new PrototypeSymbolClassifier(
            classifier,
            settings: settings);
        var result = runner.Classify(
            geometry,
            notation,
            layout);

        Assert.Equal(3, classifier.Calls);
        Assert.NotNull(Assert.Single(result.Prototypes).Classification);

        var diagnostic = Assert.Single(runner.LastDiagnostics);
        Assert.False(diagnostic.Skipped);
    }

    [Fact]
    public void SmallGlyphOutsideStaffPair_UsesNearestMeasureScale()
    {
        var classifier = new CountingClassifier();
        var settings = new SvgMusicSettings
        {
            PrototypeClassifier = new PrototypeClassifierSettings
            {
                MaxMeasureWidthFraction = 0.25,
                MaxMeasureHeightFraction = 0.60
            }
        };

        // Pedal marks, tempo marks and dynamics legitimately live outside the
        // vertical staff-pair bounds. The measure supplies scale, not clipping.
        var geometry = new GeometricScene(
            [FilledRectangle("outside", 10, 125, 12, 18)]);
        var notation = Notation(
            "outside",
            x: 10,
            y: 125,
            width: 12,
            height: 18);
        var layout = Layout(
            measureWidth: 100,
            measureHeight: 100);

        var runner = new PrototypeSymbolClassifier(
            classifier,
            settings: settings);
        runner.Classify(
            geometry,
            notation,
            layout);

        Assert.Equal(3, classifier.Calls);
        Assert.False(Assert.Single(runner.LastDiagnostics).Skipped);
    }

    private static NotationScene Notation(
        string shapeId,
        double x = 0,
        double y = 0,
        double width = 80,
        double height = 80)
    {
        var prototype = new ShapePrototype(
            "prototype-1",
            shapeId,
            new ShapeDescriptor(
                1,
                1,
                []));

        var instance = new ShapeInstance(
            shapeId,
            prototype.Id,
            x,
            y,
            width,
            height,
            "test",
            null);

        return new NotationScene(
            [prototype],
            [instance],
            [],
            [],
            []);
    }

    private static ScoreLayout Layout(
        double measureWidth,
        double measureHeight)
    {
        var left = new MeasureBoundary(
            0,
            0,
            measureHeight,
            []);
        var right = new MeasureBoundary(
            measureWidth,
            0,
            measureHeight,
            []);
        var measure = new MeasureLayout(
            "measure-1",
            0,
            measureWidth,
            left,
            right);
        var pair = new StaffPairLayout(
            "pair-1",
            "upper",
            "lower",
            new BoundsD(0, 0, measureWidth, measureHeight),
            [measure],
            [left, right]);

        return new ScoreLayout(
            [
                new ScoreSystem(
                    "system-1",
                    pair.Bounds,
                    [pair])
            ],
            []);
    }

    private static GeometricShape FilledRectangle(
        string id,
        double x,
        double y,
        double width,
        double height)
    {
        var points = new[]
        {
            new PointD(x, y),
            new PointD(x + width, y),
            new PointD(x + width, y + height),
            new PointD(x, y + height),
            new PointD(x, y)
        };

        return new GeometricShape(
            id,
            "test",
            points,
            BoundsD.FromPoints(points),
            IsClosed: true,
            Contours: [new GeometricContour(points, true)],
            HasFill: true,
            HasStroke: false);
    }

    private sealed class CountingClassifier : ISymbolClassifier
    {
        public int Calls { get; private set; }

        public IReadOnlyList<SymbolPrediction> Classify(
            RasterGlyphData glyph,
            int top = 8)
        {
            Calls++;
            return
            [
                new SymbolPrediction(
                    "TEST_SYMBOL",
                    0.99,
                    glyph.Interline)
            ];
        }
    }
}
