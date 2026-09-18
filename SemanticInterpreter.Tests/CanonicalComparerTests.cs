using SvgMusic.Canonical;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class CanonicalComparerTests
{
    [Fact]
    public void EventIdsDoNotMatterWhenMusicIsTheSame()
    {
        var expected = Score(
            Chord("source-id", "0", "1/4", "C4"),
            new TieRelation(
                "source-tie",
                new NoteAnchor("source-id", "C4"),
                new NoteAnchor("source-id", "C4")));

        var actual = Score(
            Chord("parser-id", "0", "1/4", "C4"),
            new TieRelation(
                "parser-tie",
                new NoteAnchor("parser-id", "C4"),
                new NoteAnchor("parser-id", "C4")));

        var report = new CanonicalComparer().Compare(
            expected,
            actual);

        Assert.True(report.IsEqual);
    }

    [Fact]
    public void WrongOnsetIsReportedAtExactMusicalLocation()
    {
        var expected = Score(
            Chord("e1", "0", "1/4", "C4"));
        var actual = Score(
            Chord("a1", "1/4", "1/4", "C4"));

        var report = new CanonicalComparer().Compare(
            expected,
            actual);

        var issue = Assert.Single(
            report.Issues,
            item => item.Code == "event.onset");

        Assert.Equal("P1", issue.Part);
        Assert.Equal(1, issue.Measure);
        Assert.Equal(1, issue.Staff);
        Assert.Equal("0", issue.At);
        Assert.Equal("0", issue.Expected);
        Assert.Equal("1/4", issue.Actual);
    }

    [Fact]
    public void WrongPitchIsReportedWithoutDependingOnEventId()
    {
        var expected = Score(
            Chord("e1", "0", "1/4", "C4"));
        var actual = Score(
            Chord("a1", "0", "1/4", "D4"));

        var report = new CanonicalComparer().Compare(
            expected,
            actual);

        var issue = Assert.Single(
            report.Issues,
            item => item.Code == "note.pitch");

        Assert.Equal(CanonicalDiffCategory.Pitch, issue.Category);
        Assert.Equal(1, issue.Measure);
        Assert.Equal(1, issue.Staff);
        Assert.Equal("0", issue.At);
        Assert.Equal("C4", issue.Expected);
        Assert.Equal("D4", issue.Actual);
    }

    [Fact]
    public void MarkdownGroupsIssuesByCategoryAndRootCause()
    {
        var expected = Score(
            Chord("e1", "0", "1/4", "C4"));
        var actual = Score(
            Chord("a1", "0", "1/8", "D4"));

        var report = new CanonicalComparer()
            .Compare(expected, actual);
        var markdown = report.ToMarkdown();

        Assert.Contains("## Rhythm", markdown);
        Assert.Contains("## Pitch", markdown);
        Assert.Contains("root cause", markdown);
        Assert.Contains("event.duration", markdown);
        Assert.Contains("note.pitch", markdown);
        Assert.NotEmpty(report.RootCauses);
    }

    [Fact]
    public void CollapsedSecondVoiceAtSameOnset_DoesNotInventWrongOnset()
    {
        var expected = ScoreWithEvents(
            Chord("e1", "0", "1/4", "C4") with
            {
                Voice = 1
            },
            Chord("e2", "1/4", "1/4", "D4") with
            {
                Voice = 2
            });
        var actual = ScoreWithEvents(
            Chord("a1", "0", "1/4", "C4") with
            {
                Voice = 1
            },
            Chord("a2", "1/4", "1/4", "D4") with
            {
                Voice = 1
            });

        var report = new CanonicalComparer().Compare(
            expected,
            actual);

        Assert.Contains(
            report.Issues,
            issue => issue.Code == "event.voice");
        Assert.DoesNotContain(
            report.Issues,
            issue => issue.Code == "event.onset");
    }

    [Fact]
    public void UnspecifiedSlurPlacement_DoesNotProduceMissingAndExtraPair()
    {
        var expected = ScoreWithSlur(
            placement: null);
        var actual = ScoreWithSlur(
            placement: "above");

        var report = new CanonicalComparer().Compare(
            expected,
            actual);

        Assert.DoesNotContain(
            report.Issues,
            issue => issue.Code.StartsWith(
                "relation.slur",
                StringComparison.Ordinal));
    }

    [Fact]
    public void EmptyTrailingMeasureWithFinalBarline_DoesNotCreateStructuralNoise()
    {
        var ev = Chord("e1", "0", "1/4", "C4");

        var expected = new CanonicalNotation(
            "CanonicalNotation",
            "0.4",
            new Metadata(),
            [
                new Part(
                    "P1",
                    "Piano",
                    [
                        new Measure(
                            1,
                            [ev],
                            new MeasureAttributes(
                                new TimeSignature(4, 4),
                                new KeySignature(0),
                                1,
                                [new Clef(1, "G", 2)]),
                            RightBarline: "final")
                    ])
            ],
            EmptyRelations());

        var actual = new CanonicalNotation(
            "CanonicalNotation",
            "0.4",
            new Metadata(),
            [
                new Part(
                    "P1",
                    "Piano",
                    [
                        new Measure(
                            1,
                            [ev with { Id = "a1" }],
                            new MeasureAttributes(
                                new TimeSignature(4, 4),
                                new KeySignature(0),
                                1,
                                [new Clef(1, "G", 2)])),
                        new Measure(
                            2,
                            [],
                            RightBarline: "final")
                    ])
            ],
            EmptyRelations());

        var report = new CanonicalComparer().Compare(
            expected,
            actual);

        Assert.True(
            report.IsEqual,
            report.ToMarkdown());
    }

    [Fact]
    public void SplitPianoParts_NormalizeToOneGrandStaff()
    {
        var upper = Chord(
            "P1.m1.e1",
            "0",
            "1/4",
            "C5");
        var lower = Chord(
            "P2.m1.e1",
            "0",
            "1/4",
            "C3");

        var expected = new CanonicalNotation(
            "CanonicalNotation",
            "0.4",
            new Metadata(),
            [
                new Part(
                    "P1",
                    "Piano RH",
                    [
                        new Measure(
                            1,
                            [upper],
                            new MeasureAttributes(
                                new TimeSignature(4, 4),
                                new KeySignature(0),
                                null,
                                [new Clef(1, "G", 2)]))
                    ]),
                new Part(
                    "P2",
                    "Piano LH",
                    [
                        new Measure(
                            1,
                            [lower],
                            new MeasureAttributes(
                                new TimeSignature(4, 4),
                                new KeySignature(0),
                                null,
                                [new Clef(1, "F", 4)]))
                    ])
            ],
            EmptyRelations());

        var actual = new CanonicalNotation(
            "CanonicalNotation",
            "0.4",
            new Metadata(),
            [
                new Part(
                    "P1",
                    "Piano",
                    [
                        new Measure(
                            1,
                            [
                                upper with
                                {
                                    Id = "actual-upper",
                                    Notes = [new CanonicalNote("C5", 1)]
                                },
                                lower with
                                {
                                    Id = "actual-lower",
                                    Voice = 5,
                                    Notes = [new CanonicalNote("C3", 2)]
                                }
                            ],
                            new MeasureAttributes(
                                new TimeSignature(4, 4),
                                new KeySignature(0),
                                2,
                                [
                                    new Clef(1, "G", 2),
                                    new Clef(2, "F", 4)
                                ]))
                    ])
            ],
            EmptyRelations());

        var report = new CanonicalComparer().Compare(
            expected,
            actual);

        Assert.True(
            report.IsEqual,
            report.ToMarkdown());
    }

    private static CanonicalNotation ScoreWithSlur(
        string? placement)
    {
        var first = Chord(
            "e1",
            "0",
            "1/4",
            "C4");
        var second = Chord(
            "e2",
            "1/4",
            "1/4",
            "D4");

        return new CanonicalNotation(
            "CanonicalNotation",
            "0.4",
            new Metadata(),
            [
                new Part(
                    "P1",
                    "Piano",
                    [
                        new Measure(
                            1,
                            [first, second],
                            new MeasureAttributes(
                                new TimeSignature(4, 4),
                                new KeySignature(0),
                                1,
                                [new Clef(1, "G", 2)]))
                    ])
            ],
            new Relations(
                [],
                [],
                [
                    new SlurRelation(
                        "slur-1",
                        first.Id,
                        second.Id,
                        placement)
                ],
                [],
                [],
                [],
                [],
                []));
    }

    private static Relations EmptyRelations() =>
        new(
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            []);

    private static CanonicalEvent Chord(
        string id,
        string at,
        string duration,
        string pitch) =>
        new()
        {
            Id = id,
            Type = "chord",
            At = at,
            Voice = 1,
            Duration = duration,
            Notes = [new CanonicalNote(pitch, 1)]
        };

    private static CanonicalNotation ScoreWithEvents(
        params CanonicalEvent[] events) =>
        new(
            "CanonicalNotation",
            "0.4",
            new Metadata(),
            [
                new Part(
                    "P1",
                    "Piano",
                    [
                        new Measure(
                            1,
                            events.ToList(),
                            new MeasureAttributes(
                                new TimeSignature(4, 4),
                                new KeySignature(0),
                                1,
                                [new Clef(1, "G", 2)]))
                    ])
            ],
            EmptyRelations());

    private static CanonicalNotation Score(
        CanonicalEvent ev,
        TieRelation? tie = null) =>
        new(
            "CanonicalNotation",
            "0.4",
            new Metadata(),
            [
                new Part(
                    "P1",
                    "Piano",
                    [
                        new Measure(
                            1,
                            [ev],
                            new MeasureAttributes(
                                new TimeSignature(4, 4),
                                new KeySignature(0),
                                1,
                                [new Clef(1, "G", 2)]))
                    ])
            ],
            new Relations(
                [],
                tie is null ? [] : [tie],
                [],
                [],
                [],
                [],
                [],
                []));
}
