using SvgMusic.Canonical;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class MusicXmlOttavaPitchTests
{
    [Fact]
    public void Writer_CompensatesSerializedPitchInsideOctaveShift_WithoutChangingCanonicalPitch()
    {
        var first = new CanonicalEvent
        {
            Id = "event-1",
            Type = "chord",
            At = "0",
            Voice = 1,
            Duration = "1/4",
            Notes =
            [
                new CanonicalNote("C4", 1),
                new CanonicalNote("E4", 2)
            ]
        };
        var atStop = new CanonicalEvent
        {
            Id = "event-2",
            Type = "chord",
            At = "1/4",
            Voice = 1,
            Duration = "1/4",
            Notes =
            [
                new CanonicalNote("D4", 1),
                new CanonicalNote("F4", 2)
            ]
        };
        var score = new CanonicalNotation(
            "canonical-notation",
            "0.3",
            new Metadata("Ottava test"),
            [
                new Part(
                    "P1",
                    "Piano",
                    [new Measure(1, [first, atStop])])
            ],
            new Relations(
                [],
                [],
                [],
                [],
                [],
                [],
                [],
                [
                    new SpanRelation
                    {
                        Id = "octaveShift-1",
                        Kind = "octaveShift",
                        From = new TimeAnchor(1, "0", 1),
                        To = new TimeAnchor(1, "1/4", 1),
                        Direction = "down",
                        Size = 8,
                        Placement = "above"
                    },
                    new SpanRelation
                    {
                        Id = "octaveShift-2",
                        Kind = "octaveShift",
                        From = new TimeAnchor(1, "0", 2),
                        To = new TimeAnchor(1, "1/4", 2),
                        Direction = "up",
                        Size = 8,
                        Placement = "below"
                    }
                ]));

        var xml = new MusicXmlWriter().Write(score);
        var notes = xml.Descendants("note")
            .Where(note => note.Element("pitch") is not null)
            .ToArray();

        Assert.Equal("5", Octave(notes, "C", 1));
        Assert.Equal("3", Octave(notes, "E", 2));

        // The stop direction is emitted before notes at the same time,
        // so notes starting exactly at the stop anchor are no longer shifted.
        Assert.Equal("4", Octave(notes, "D", 1));
        Assert.Equal("4", Octave(notes, "F", 2));

        Assert.Equal("C4", first.Notes![0].Pitch);
        Assert.Equal("E4", first.Notes![1].Pitch);
    }

    private static string Octave(
        IReadOnlyList<System.Xml.Linq.XElement> notes,
        string step,
        int staff)
    {
        var note = notes.Single(item =>
            item.Element("pitch")?.Element("step")?.Value == step
            && item.Element("staff")?.Value == staff.ToString());

        return note.Element("pitch")!.Element("octave")!.Value;
    }
}
