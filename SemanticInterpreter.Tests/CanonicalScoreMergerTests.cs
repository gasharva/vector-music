using SvgMusic.Canonical;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class CanonicalScoreMergerTests
{
    [Fact]
    public void MergeRenumbersMeasuresPrefixesEventsAndShiftsSpans()
    {
        var page1 = Score(
            [
                MeasureWithChord(1, "m1.e1", "C4"),
                MeasureWithChord(2, "m2.e1", "D4")
            ],
            new Relations(
                [],
                [
                    new TieRelation(
                        "tie-1",
                        new NoteAnchor("m1.e1", "C4"),
                        new NoteAnchor("m2.e1", "D4"))
                ],
                [],
                [],
                [],
                [],
                [],
                []));

        var page2 = Score(
            [
                MeasureWithChord(1, "m1.e1", "E4")
            ],
            new Relations(
                [],
                [],
                [],
                [],
                [],
                [],
                [
                    new SpanRelation
                    {
                        Id = "pedal-1",
                        Kind = "pedal",
                        From = new TimeAnchor(1, "0", 2),
                        To = new TimeAnchor(1, "3/4", 2)
                    }
                ],
                []));

        var merged = new CanonicalScoreMerger().Merge(
            [page1, page2]);

        var measures = merged.Parts.Single().Measures;
        Assert.Equal(
            [1, 2, 3],
            measures.Select(measure => measure.Number).ToArray());
        Assert.Equal(
            "p001:m1.e1",
            measures[0].Events.Single().Id);
        Assert.Equal(
            "p002:m1.e1",
            measures[2].Events.Single().Id);
        Assert.Equal(
            "page",
            measures[2].Layout?.BreakBefore);

        var tie = Assert.Single(merged.Relations.Ties);
        Assert.Equal("p001:m1.e1", tie.From.Event);
        Assert.Equal("p001:m2.e1", tie.To.Event);

        var pedal = Assert.Single(merged.Relations.Pedals);
        Assert.Equal(3, pedal.From.Measure);
        Assert.Equal(3, pedal.To.Measure);
    }

    private static CanonicalNotation Score(
        List<Measure> measures,
        Relations relations) =>
        new(
            "CanonicalNotation",
            "0.4",
            new Metadata("Title", "Composer"),
            [new Part("P1", "Piano", measures)],
            relations);

    private static Measure MeasureWithChord(
        int number,
        string id,
        string pitch) =>
        new(
            number,
            [
                new CanonicalEvent
                {
                    Id = id,
                    Type = "chord",
                    At = "0",
                    Voice = 1,
                    Duration = "1/4",
                    Notes = [new CanonicalNote(pitch, 1)]
                }
            ]);
}
