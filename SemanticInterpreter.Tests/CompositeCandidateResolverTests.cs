using SvgMusic.Scene;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class CompositeCandidateResolverTests
{
    [Fact]
    public void MeasureBoundaryClaim_BeatsBracketCandidate()
    {
        var notation = new NotationScene(
            [],
            [],
            [
                Stroke("horizontal", 0, 20, 100, 20),
                Stroke("barline", 100, 0, 100, 80)
            ],
            [],
            []);

        var bracket = new BracketSpannerPrimitive(
            "bracket-1",
            new PointD(0, 20),
            new PointD(100, 20),
            null,
            new PointD(100, 0),
            BracketHookDirection.None,
            BracketHookDirection.Up,
            false,
            1,
            0.95,
            ["horizontal", "barline"]);

        var boundary = new MeasureBoundary(
            100,
            0,
            80,
            ["barline"]);

        var pair = new StaffPairLayout(
            "pair-1",
            "upper",
            "lower",
            new BoundsD(0, 0, 100, 80),
            [],
            [boundary]);

        var layout = new ScoreLayout(
            [
                new ScoreSystem(
                    "system-1",
                    new BoundsD(0, 0, 100, 80),
                    [pair])
            ],
            []);

        var resolver = new CompositeCandidateResolver();
        var result = resolver.Resolve(
            notation,
            new CompositeGeometryCandidates(
                [],
                [bracket],
                []),
            layout);

        Assert.Empty(result.BracketSpanners);
        Assert.Equal(2, result.Strokes.Count);

        var decision = Assert.Single(resolver.LastDecisions);
        Assert.False(decision.Accepted);
        Assert.Contains("score layout", decision.Reason);
        Assert.Contains("barline", decision.Reason);
    }

    [Fact]
    public void UnclaimedBracketCandidate_SuppressesItsGenericStrokesLate()
    {
        var notation = new NotationScene(
            [],
            [],
            [
                Stroke("horizontal", 0, 20, 100, 20),
                Stroke("hook", 100, 20, 100, 30)
            ],
            [],
            []);

        var bracket = new BracketSpannerPrimitive(
            "bracket-1",
            new PointD(0, 20),
            new PointD(100, 20),
            null,
            new PointD(100, 30),
            BracketHookDirection.None,
            BracketHookDirection.Down,
            false,
            1,
            0.95,
            ["horizontal", "hook"]);

        var result = new CompositeCandidateResolver().Resolve(
            notation,
            new CompositeGeometryCandidates(
                [],
                [bracket],
                []),
            new ScoreLayout([], []));

        Assert.Single(result.BracketSpanners);
        Assert.Empty(result.Strokes);
    }

    private static Stroke Stroke(
        string id,
        double x1,
        double y1,
        double x2,
        double y2) =>
        new(
            id,
            new PointD(x1, y1),
            new PointD(x2, y2),
            1,
            "test",
            null);
}
