using SvgMusic.Scene;
using SvgMusic.Semantics;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class TimeSignaturePassTests
{
    [Fact]
    public void LaterPrintedTimeSignature_ProducesNewFact_WhileEmptyMeasuresInherit()
    {
        var document = new SemanticDocument(
        [
            Measure(
                1,
                [
                    TimeShape(1, 1, "m1-u-3", "TIME_THREE", new BoundsD(40, 115, 50, 125)),
                    TimeShape(1, 1, "m1-u-4", "TIME_FOUR", new BoundsD(40, 155, 50, 165))
                ],
                [
                    TimeShape(1, 2, "m1-l-3", "TIME_THREE", new BoundsD(40, 235, 50, 245)),
                    TimeShape(1, 2, "m1-l-4", "TIME_FOUR", new BoundsD(40, 275, 50, 285))
                ]),
            Measure(2, [], []),
            Measure(
                3,
                [TimeShape(3, 1, "m3-u-common", "COMMON_TIME", new BoundsD(40, 120, 55, 160))],
                [TimeShape(3, 2, "m3-l-common", "COMMON_TIME", new BoundsD(40, 240, 55, 280))])
        ]);

        var facts = new SemanticFacts();

        new TimeSignaturePass().Run(document, facts);

        var signatures = facts
            .OfType<TimeSignatureFact>()
            .OrderBy(signature => signature.MeasureNumber)
            .ToArray();

        Assert.Equal(2, signatures.Length);

        Assert.Equal(1, signatures[0].MeasureNumber);
        Assert.Equal(3, signatures[0].Beats);
        Assert.Equal(4, signatures[0].BeatType);

        Assert.Equal(3, signatures[1].MeasureNumber);
        Assert.Equal(4, signatures[1].Beats);
        Assert.Equal(4, signatures[1].BeatType);

        Assert.DoesNotContain(
            signatures,
            signature => signature.MeasureNumber == 2);
    }

    [Fact]
    public void ContinuationPageWithoutPrintedSignature_UsesInheritedTime()
    {
        var document = new SemanticDocument(
        [
            Measure(1, [], []),
            Measure(2, [], [])
        ]);
        var facts = new SemanticFacts();

        new TimeSignaturePass((3, 4)).Run(
            document,
            facts);

        var signature = Assert.Single(
            facts.OfType<TimeSignatureFact>());

        Assert.Equal(1, signature.MeasureNumber);
        Assert.Equal(3, signature.Beats);
        Assert.Equal(4, signature.BeatType);
        Assert.True(signature.IsInherited);
        Assert.Empty(signature.SourceShapeIds);
        Assert.Contains(
            facts.Trace,
            line => line.Contains(
                "inherited 3/4",
                StringComparison.Ordinal));
    }

    [Fact]
    public void NoisyExtraTimeDigits_SelectSupportedAlignedSignature()
    {
        var document = new SemanticDocument(
        [
            Measure(
                1,
                [
                    TimeShape(1, 1, "upper-real-3", "TIME_THREE", new BoundsD(40, 115, 50, 125)),
                    TimeShape(1, 1, "upper-real-4", "TIME_FOUR", new BoundsD(40, 155, 50, 165)),
                    TimeShape(1, 1, "upper-noise-6a", "TIME_SIX", new BoundsD(80, 115, 90, 125)),
                    TimeShape(1, 1, "upper-noise-4", "TIME_FOUR", new BoundsD(100, 115, 110, 125)),
                    TimeShape(1, 1, "upper-noise-6b", "TIME_SIX", new BoundsD(120, 115, 130, 125))
                ],
                [
                    TimeShape(1, 2, "lower-real-3", "TIME_THREE", new BoundsD(40, 235, 50, 245)),
                    TimeShape(1, 2, "lower-real-4", "TIME_FOUR", new BoundsD(40, 275, 50, 285))
                ])
        ]);

        var facts = new SemanticFacts();

        new TimeSignaturePass().Run(document, facts);

        var signature = Assert.Single(facts.OfType<TimeSignatureFact>());
        Assert.Equal(3, signature.Beats);
        Assert.Equal(4, signature.BeatType);
        Assert.Equal(
            new[]
            {
                "lower-real-3",
                "lower-real-4",
                "upper-real-3",
                "upper-real-4"
            },
            signature.SourceShapeIds.OrderBy(id => id, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(
            signature.SourceShapeIds,
            id => id.Contains("noise", StringComparison.Ordinal));
        Assert.Contains(
            facts.Trace,
            line => line.Contains(
                "selected 3/4 from 2 of 5 TIME_* glyphs",
                StringComparison.Ordinal));
    }

    [Fact]
    public void LaterPartialTimeLikeGlyphs_AreIgnoredInsteadOfBreakingPipeline()
    {
        var document = new SemanticDocument(
        [
            Measure(
                1,
                [
                    TimeShape(1, 1, "m1-u-3", "TIME_THREE", new BoundsD(40, 115, 50, 125)),
                    TimeShape(1, 1, "m1-u-4", "TIME_FOUR", new BoundsD(40, 155, 50, 165))
                ],
                [
                    TimeShape(1, 2, "m1-l-3", "TIME_THREE", new BoundsD(40, 235, 50, 245)),
                    TimeShape(1, 2, "m1-l-4", "TIME_FOUR", new BoundsD(40, 275, 50, 285))
                ]),
            Measure(
                2,
                [
                    TimeShape(2, 1, "noise-1", "TIME_THREE", new BoundsD(50, 112, 60, 122)),
                    TimeShape(2, 1, "noise-2", "TIME_TWO", new BoundsD(68, 118, 78, 128))
                ],
                [])
        ]);

        var facts = new SemanticFacts();

        new TimeSignaturePass().Run(document, facts);

        var signatures = facts.OfType<TimeSignatureFact>().ToArray();
        var signature = Assert.Single(signatures);
        Assert.Equal(1, signature.MeasureNumber);
        Assert.Equal(3, signature.Beats);
        Assert.Equal(4, signature.BeatType);
        Assert.Contains(
            facts.Trace,
            line => line.Contains("ignored later time-signature candidate", StringComparison.Ordinal));
    }

    private static MeasureScene Measure(
        int number,
        IReadOnlyList<SemanticElement> upper,
        IReadOnlyList<SemanticElement> lower)
    {
        return new MeasureScene(
            number,
            $"system-{number}",
            $"pair-{number}",
            $"measure-{number}",
            0,
            300,
            false,
            new StaffMeasureScene(
                1,
                $"staff-{number}-upper",
                new BoundsD(0, 100, 300, 180),
                20,
                upper),
            new StaffMeasureScene(
                2,
                $"staff-{number}-lower",
                new BoundsD(0, 220, 300, 300),
                20,
                lower));
    }

    private static ShapeElement TimeShape(
        int measure,
        int staff,
        string id,
        string label,
        BoundsD bounds)
    {
        var ownership = new LogicalOwnership(
            new LogicalCoordinate($"staff-{staff}", $"measure-{measure}"),
            new LogicalCoordinate($"staff-{staff}", $"measure-{measure}"),
            1,
            null,
            0,
            "test");
        var classification = new SymbolClassification(
            label,
            0.99,
            20,
            []);
        var source = new ShapeInstance(
            id,
            "prototype-time",
            bounds.MinX,
            bounds.MinY,
            bounds.Width,
            bounds.Height,
            "path",
            null,
            Classification: classification,
            Ownership: ownership);

        return new ShapeElement
        {
            ShapeId = id,
            Bounds = bounds,
            Ownership = ownership,
            Source = source
        };
    }
}
