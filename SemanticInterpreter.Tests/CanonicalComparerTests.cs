using SvgMusic.Canonical;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class CanonicalComparerTests
{
    [Fact]
    public void DiffSeverity_DefaultFilteringSilencesVoiceAndCautionaryNoise()
    {
        var voice = new CanonicalDiffIssue(
            CanonicalDiffCategory.Rhythm,
            "event.voice",
            "P1",
            1,
            1,
            "0",
            "1",
            "2",
            "voice differs");
        var accidental = new CanonicalDiffIssue(
            CanonicalDiffCategory.Notation,
            "note.accidental",
            "P1",
            2,
            1,
            "0",
            "natural:True::False:",
            "natural::::",
            "cautionary metadata differs");
        var tempo = new CanonicalDiffIssue(
            CanonicalDiffCategory.Text,
            "event.missing",
            "P1",
            1,
            1,
            "0",
            "tempo eighth=62",
            "<missing>",
            "tempo is missing");
        var hairpin = new CanonicalDiffIssue(
            CanonicalDiffCategory.Relations,
            "relation.hairpin.missing",
            null,
            4,
            null,
            null,
            "hairpin",
            "<missing>",
            "hairpin is missing");

        Assert.Equal(
            CanonicalDiffSeverity.Warning,
            voice.Severity);
        Assert.Equal(
            CanonicalDiffSeverity.Warning,
            accidental.Severity);
        Assert.Equal(
            CanonicalDiffSeverity.Error,
            tempo.Severity);
        Assert.Equal(
            CanonicalDiffSeverity.Critical,
            hairpin.Severity);

        var report = new CanonicalDiffReport(
            [voice, accidental, tempo, hairpin]);

        Assert.Equal(
            2,
            report.Filter(
                CanonicalDiffSeverity.Error)
                .Issues.Count);
        Assert.Equal(
            2,
            report.HiddenBelow(
                CanonicalDiffSeverity.Error));
        Assert.Equal(
            4,
            report.Filter(
                CanonicalDiffSeverity.Warning)
                .Issues.Count);
    }

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
    public void SameRestOnWrongStaff_IsOneStaffIssueNotMissingPlusExtra()
    {
        var expectedRest = new CanonicalEvent
        {
            Id = "expected-rest",
            Type = "rest",
            At = "0",
            Staff = 1,
            Voice = 2,
            Duration = "1/8",
            Notation = new EventNotation("eighth")
        };
        var actualRest = expectedRest with
        {
            Id = "actual-rest",
            Staff = 2,
            Voice = 5
        };

        var expected = ScoreWithEvents(expectedRest);
        var actual = ScoreWithEvents(actualRest);

        var report = new CanonicalComparer().Compare(
            expected,
            actual);

        Assert.Contains(
            report.Issues,
            issue => issue.Code == "event.staff");
        Assert.DoesNotContain(
            report.Issues,
            issue => issue.Code is "event.missing" or "event.extra");
    }

    [Fact]
    public void GloballySwappedPolyphonicVoiceLabels_AreCompensated()
    {
        var expected = PolyphonicVoiceScore(
            swapVoices: false);
        var actual = PolyphonicVoiceScore(
            swapVoices: true);

        var report = new CanonicalComparer().Compare(
            expected,
            actual);

        Assert.True(
            report.IsEqual,
            report.ToMarkdown());
    }

    [Fact]
    public void RelationIdentity_IgnoresVoiceLabelButVoiceDifferenceRemainsVisible()
    {
        var expected = RelationVoiceScore(
            changedFirstVoice: false);
        var actual = RelationVoiceScore(
            changedFirstVoice: true);

        var report = new CanonicalComparer().Compare(
            expected,
            actual);

        Assert.Contains(
            report.Issues,
            issue => issue.Code == "event.voice");

        Assert.DoesNotContain(
            report.Issues,
            issue => issue.Code.StartsWith(
                "relation.beam",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            report.Issues,
            issue => issue.Code.StartsWith(
                "relation.tie",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            report.Issues,
            issue => issue.Code.StartsWith(
                "relation.slur",
                StringComparison.Ordinal));
    }

    [Fact]
    public void InterstaffDynamicBelowUpper_EqualsAboveLowerStaff()
    {
        var expected = DirectionScore(
            staff: 1,
            placement: "below");
        var actual = DirectionScore(
            staff: 2,
            placement: "above");

        var report = new CanonicalComparer().Compare(
            expected,
            actual);

        Assert.True(
            report.IsEqual,
            report.ToMarkdown());
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

    private static CanonicalNotation PolyphonicVoiceScore(
        bool swapVoices)
    {
        int Voice(int expected) =>
            swapVoices
                ? expected == 1 ? 2 : 1
                : expected;

        var measures = new List<Measure>();

        for (var measure = 1; measure <= 2; measure++)
        {
            measures.Add(new Measure(
                measure,
                [
                    Chord($"v1-{measure}", "0", "1/4", measure == 1 ? "C4" : "D4") with
                    {
                        Voice = Voice(1)
                    },
                    Chord($"v2-{measure}", "1/4", "1/4", measure == 1 ? "G4" : "A4") with
                    {
                        Voice = Voice(2)
                    }
                ],
                measure == 1
                    ? new MeasureAttributes(
                        new TimeSignature(4, 4),
                        new KeySignature(0),
                        1,
                        [new Clef(1, "G", 2)])
                    : null));
        }

        return new CanonicalNotation(
            "CanonicalNotation",
            "0.4",
            new Metadata(),
            [new Part("P1", "Piano", measures)],
            EmptyRelations());
    }

    private static CanonicalNotation RelationVoiceScore(
        bool changedFirstVoice)
    {
        var first = Chord(
            changedFirstVoice ? "a1" : "e1",
            "0",
            "1/8",
            "C4") with
        {
            Voice = changedFirstVoice ? 3 : 1
        };
        var second = Chord(
            changedFirstVoice ? "a2" : "e2",
            "1/8",
            "1/8",
            "D4") with
        {
            Voice = 2
        };
        var third = Chord(
            changedFirstVoice ? "a3" : "e3",
            "1/4",
            "1/8",
            "E4") with
        {
            Voice = 2
        };

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
                            [first, second, third],
                            new MeasureAttributes(
                                new TimeSignature(4, 4),
                                new KeySignature(0),
                                1,
                                [new Clef(1, "G", 2)]))
                    ])
            ],
            new Relations(
                [
                    new BeamRelation(
                        "beam-1",
                        1,
                        [first.Id, second.Id, third.Id])
                ],
                [
                    new TieRelation(
                        "tie-1",
                        new NoteAnchor(first.Id, "C4"),
                        new NoteAnchor(second.Id, "D4"))
                ],
                [
                    new SlurRelation(
                        "slur-1",
                        first.Id,
                        third.Id)
                ],
                [],
                [],
                [],
                [],
                []));
    }

    private static CanonicalNotation DirectionScore(
        int staff,
        string placement)
    {
        var dynamic = new CanonicalEvent
        {
            Id = "dynamic",
            Type = "dynamic",
            At = "0",
            Staff = staff,
            Value = "ppp",
            Placement = placement
        };

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
                            [dynamic],
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
