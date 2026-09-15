using SvgMusic.Scene;
using SvgMusic.Semantics;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class BeamAttachmentPassTests
{
    [Fact]
    public void CompactResidualBox_BecomesSecondLevelRightHandHook()
    {
        const int measure = 14;
        const int staff = 1;
        const double spacing = 24.0945;

        var ownership = new LogicalOwnership(
            new LogicalCoordinate("staff-3", "measure-2"),
            new LogicalCoordinate("staff-3", "measure-2"),
            1,
            null,
            0,
            "test");

        var primary = StrokeElement(
            "primary-beam",
            new PointD(1027.244, 1858.949),
            new PointD(1123.446, 1860.991),
            14.07,
            ownership);
        var hook = ResidualShape(
            "short-hook",
            new BoundsD(
                1092.02,
                1872.02,
                1123.34,
                1884.06),
            ownership);

        var document = new SemanticDocument(
        [
            new MeasureScene(
                measure,
                "system-3",
                "pair-3",
                "measure-2",
                950,
                1200,
                false,
                new StaffMeasureScene(
                    staff,
                    "staff-3",
                    new BoundsD(950, 1900, 1200, 2020),
                    spacing,
                    [primary, hook]),
                new StaffMeasureScene(
                    2,
                    "staff-4",
                    new BoundsD(950, 2140, 1200, 2260),
                    spacing,
                    Array.Empty<SemanticElement>()))
        ]);

        var facts = new SemanticFacts();
        facts.Add(Stem(
            measure,
            "left-stem",
            1028.68,
            1859.97,
            1928.40,
            spacing));
        facts.Add(Stem(
            measure,
            "right-stem",
            1122.02,
            1859.97,
            1928.40,
            spacing));

        var pass = new BeamAttachmentPass();
        pass.Run(document, facts);

        var beams = facts.OfType<BeamAttachmentFact>().ToArray();
        Assert.Equal(2, beams.Length);

        var primaryFact = Assert.Single(beams, beam => beam.BeamShapeId == "primary-beam");
        Assert.Equal(1, primaryFact.Level);
        Assert.False(primaryFact.IsHook);

        var hookFact = Assert.Single(beams, beam => beam.BeamShapeId == "short-hook");
        Assert.Equal(2, hookFact.Level);
        Assert.True(hookFact.IsHook);
        Assert.False(hookFact.LeftEndSupported);
        Assert.True(hookFact.RightEndSupported);
        Assert.Equal(new[] { "right-stem" }, hookFact.AttachedStemIds);

        Assert.Contains(
            facts.Trace,
            line => line.Contains("recovered-compact-hooks=1", StringComparison.Ordinal));
    }

    [Fact]
    public void ResidualBoxOutsideHookLengthBand_IsNotRecovered()
    {
        const double spacing = 24.0;
        var ownership = new LogicalOwnership(
            new LogicalCoordinate("staff-1", "measure-1"),
            new LogicalCoordinate("staff-1", "measure-1"),
            1,
            null,
            0,
            "test");
        var longBox = ResidualShape(
            "long-box",
            new BoundsD(100, 100, 180, 112),
            ownership);
        var document = new SemanticDocument(
        [
            new MeasureScene(
                1,
                "system-1",
                "pair-1",
                "measure-1",
                0,
                300,
                false,
                new StaffMeasureScene(
                    1,
                    "staff-1",
                    new BoundsD(0, 80, 300, 180),
                    spacing,
                    [longBox]),
                new StaffMeasureScene(
                    2,
                    "staff-2",
                    new BoundsD(0, 220, 300, 320),
                    spacing,
                    Array.Empty<SemanticElement>()))
        ]);

        var recovery = new ResidualBeamHookRecovery().Recover(document);

        Assert.Equal(0, recovery.RecoveredCount);
        Assert.Single(recovery.Document.Measures[0].Upper.Elements);
    }

    private static StrokeElement StrokeElement(
        string id,
        PointD start,
        PointD end,
        double width,
        LogicalOwnership ownership)
    {
        var source = new Stroke(
            id,
            start,
            end,
            width,
            "path",
            null,
            ownership);
        var half = width / 2.0;

        return new StrokeElement
        {
            ShapeId = id,
            Bounds = new BoundsD(
                Math.Min(start.X, end.X) - half,
                Math.Min(start.Y, end.Y) - half,
                Math.Max(start.X, end.X) + half,
                Math.Max(start.Y, end.Y) + half),
            Ownership = ownership,
            Source = source
        };
    }

    private static ShapeElement ResidualShape(
        string id,
        BoundsD bounds,
        LogicalOwnership ownership)
    {
        return new ShapeElement
        {
            ShapeId = id,
            Bounds = bounds,
            Ownership = ownership,
            Source = new ShapeInstance(
                id,
                "prototype-test",
                bounds.MinX,
                bounds.MinY,
                bounds.Width,
                bounds.Height,
                "path",
                null,
                Ownership: ownership)
        };
    }

    private static StemAttachmentFact Stem(
        int measure,
        string id,
        double x,
        double tipY,
        double noteY,
        double spacing)
    {
        return new StemAttachmentFact(
            measure,
            id,
            StemDirection.Up,
            [$"note-{id}"],
            [1],
            false,
            x,
            tipY,
            x,
            noteY,
            Math.Abs(noteY - tipY) / spacing,
            0.11,
            1.0,
            "test stem",
            [id]);
    }
}
