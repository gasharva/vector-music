using SvgMusic.Canonical;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class MusicXmlHairpinTests
{
    [Fact]
    public void HairpinSpan_WritesCrescendoAndStopWedgesOnSameStaff()
    {
        var span = new SpanRelation
        {
            Id = "hairpin-1",
            Kind = "hairpin",
            From = new TimeAnchor(1, "1/8", 2),
            To = new TimeAnchor(2, "5/8", 2),
            Type = "crescendo",
            Placement = "below"
        };
        var score = new CanonicalNotation(
            "CanonicalNotation",
            "0.3",
            new Metadata(),
            [new Part("P1", "Piano", [
                new Measure(1, []),
                new Measure(2, [])
            ])],
            new Relations([], [], [], [], [], [span], [], []));

        var xml = new MusicXmlWriter().Write(score);
        var directions = xml
            .Descendants("direction")
            .Where(direction => direction.Descendants("wedge").Any())
            .ToArray();

        Assert.Equal(2, directions.Length);

        var start = directions[0];
        Assert.Equal("below", start.Attribute("placement")?.Value);
        Assert.Equal("2", start.Element("staff")?.Value);
        Assert.Equal(
            "crescendo",
            start.Descendants("wedge").Single().Attribute("type")?.Value);

        var stop = directions[1];
        Assert.Equal("below", stop.Attribute("placement")?.Value);
        Assert.Equal("2", stop.Element("staff")?.Value);
        Assert.Equal(
            "stop",
            stop.Descendants("wedge").Single().Attribute("type")?.Value);
    }
}
