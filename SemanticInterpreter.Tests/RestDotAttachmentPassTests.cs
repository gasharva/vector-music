using SvgMusic.Scene;
using SvgMusic.Semantics;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class RestDotAttachmentPassTests
{
    [Fact]
    public void RejectedNoteDot_CanAttachToNearbyRestOnItsLeft()
    {
        const double spacing = 20.0;
        var facts = new SemanticFacts();
        facts.Add(new NoteheadFact(
            1,
            1,
            "far-note",
            250,
            105,
            10,
            8,
            "filled",
            1.0,
            0,
            0,
            0.99,
            "test note",
            ["far-note"]));
        facts.Add(new RestFact(
            1,
            1,
            "rest",
            "QUARTER_REST",
            0.99,
            "quarter",
            "1/4",
            100,
            100,
            0.99,
            "test rest",
            ["rest"]));

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
                    new BoundsD(0, 60, 300, 160),
                    spacing,
                    [Dot("dot", 125, 105, spacing)]),
                new StaffMeasureScene(
                    2,
                    "staff-2",
                    new BoundsD(0, 200, 300, 280),
                    spacing,
                    []))
        ]);

        new DotAttachmentPass().Run(document, facts);

        Assert.Empty(facts.OfType<DotAttachmentFact>());
        var attachment = Assert.Single(facts.OfType<RestDotAttachmentFact>());
        Assert.Equal("rest", attachment.TargetRestShapeId);
        Assert.Equal(1, attachment.Count);
        Assert.Equal("dot", Assert.Single(attachment.DotShapeIds));
    }

    private static EllipseElement Dot(
        string id,
        double x,
        double y,
        double spacing)
    {
        const double radius = 4;
        var ownership = new LogicalOwnership(
            new LogicalCoordinate("staff-1", "measure-1"),
            new LogicalCoordinate("staff-1", "measure-1"),
            1,
            null,
            0,
            "test");
        var source = new EllipseLike(
            id,
            new PointD(x, y),
            radius,
            radius,
            0,
            false,
            null,
            0,
            "test",
            null,
            ownership);

        return new EllipseElement
        {
            ShapeId = id,
            Bounds = new BoundsD(
                x - radius,
                y - radius,
                x + radius,
                y + radius),
            Ownership = ownership,
            Source = source
        };
    }
}
