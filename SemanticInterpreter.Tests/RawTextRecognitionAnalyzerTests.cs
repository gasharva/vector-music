using SvgMusic.Scene;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class RawTextRecognitionAnalyzerTests
{
    [Fact]
    public void Analyze_BuildsRunOnlyWhenAProbedGlyphIsUncertain()
    {
        var recognizer = new FakeRecognizer(candidate =>
        {
            if (candidate.Kind == TextCandidateKind.HorizontalRun)
                return new TextRecognition("YELLOW", 0.999, "test");

            return candidate.SourceShapeIds.Single() switch
            {
                "Y" => new TextRecognition("Y", 0.99, "test"),
                "E" => new TextRecognition("E", 0.99, "test"),
                "L" => new TextRecognition("l", 0.61, "test"),
                "O" => null,
                "W" => new TextRecognition("W", 0.98, "test"),
                _ => null
            };
        });

        var analysis = new RawTextRecognitionAnalyzer(recognizer).Analyze(
            new GeometricScene([
                Shape("Y", 0, 0, 5, 10),
                Shape("E", 7, 0, 12, 10),
                Shape("L", 14, 0, 18, 10),
                Shape("O", 20, 0, 26, 10),
                Shape("W", 28, 0, 36, 10)
            ]),
            Layout(10));

        Assert.Equal(5, recognizer.RawGlyphCalls.Count);
        Assert.Contains("O", recognizer.RawGlyphCalls);

        var run = Assert.Single(analysis.Observations.Where(item =>
            item.Kind == TextCandidateKind.HorizontalRun));
        Assert.Equal("YELLOW", run.Recognition?.Text);
        Assert.Equal(["Y", "E", "L", "O", "W"], run.SourceShapeIds);
    }

    [Fact]
    public void Analyze_DoesNotBuildRunWhenEveryProbedGlyphIsClear()
    {
        var recognizer = new FakeRecognizer(candidate =>
            candidate.Kind == TextCandidateKind.HorizontalRun
                ? new TextRecognition("ABC", 0.99, "test")
                : new TextRecognition(candidate.SourceShapeIds.Single(), 0.99, "test"));

        var analysis = new RawTextRecognitionAnalyzer(recognizer).Analyze(
            new GeometricScene([
                Shape("A", 0, 0, 5, 10),
                Shape("B", 7, 0, 12, 10),
                Shape("C", 14, 0, 20, 10)
            ]),
            Layout(10));

        Assert.Equal(3, recognizer.RawGlyphCalls.Count);
        Assert.DoesNotContain(analysis.Observations, item =>
            item.Kind == TextCandidateKind.HorizontalRun);
    }

    [Fact]
    public void Analyze_DoesNotProbeGiantStaffLine()
    {
        var recognizer = new FakeRecognizer(_ => null);

        new RawTextRecognitionAnalyzer(recognizer).Analyze(
            new GeometricScene([
                Shape("glyph", 0, 0, 5, 10),
                Shape("staff-line", 0, 20, 200, 21)
            ]),
            Layout(10));

        Assert.Contains("glyph", recognizer.RawGlyphCalls);
        Assert.DoesNotContain("staff-line", recognizer.RawGlyphCalls);
    }

    private static GeometricShape Shape(
        string id,
        double minX,
        double minY,
        double maxX,
        double maxY)
    {
        var points = new[]
        {
            new PointD(minX, minY),
            new PointD(maxX, minY),
            new PointD(maxX, maxY),
            new PointD(minX, maxY)
        };

        return new GeometricShape(
            id,
            "path",
            points,
            new BoundsD(minX, minY, maxX, maxY),
            IsClosed: true,
            Contours: [new GeometricContour(points, true)],
            HasFill: true);
    }

    private static ScoreLayout Layout(double spacing) =>
        new(
            [],
            [new StaffLayout(
                "staff-1",
                "system-1",
                new BoundsD(0, 0, 300, 50),
                [],
                spacing,
                [])]);

    private sealed class FakeRecognizer(
        Func<TextRecognitionCandidate, TextRecognition?> handler)
        : ITextRecognizer
    {
        public List<string> RawGlyphCalls { get; } = [];

        public TextRecognition? Recognize(TextRecognitionCandidate candidate)
        {
            if (candidate.Kind == TextCandidateKind.Prototype)
                RawGlyphCalls.Add(candidate.SourceShapeIds.Single());

            return handler(candidate);
        }
    }
}
