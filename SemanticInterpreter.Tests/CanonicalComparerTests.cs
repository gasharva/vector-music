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
    public void MarkdownGroupsIssuesByCategoryAndLocation()
    {
        var expected = Score(
            Chord("e1", "0", "1/4", "C4"));
        var actual = Score(
            Chord("a1", "0", "1/8", "D4"));

        var markdown = new CanonicalComparer()
            .Compare(expected, actual)
            .ToMarkdown();

        Assert.Contains("## Rhythm", markdown);
        Assert.Contains("## Pitch", markdown);
        Assert.Contains("### P1 / m1 / staff 1 / at 0", markdown);
        Assert.Contains("event.duration", markdown);
        Assert.Contains("note.pitch", markdown);
    }

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
