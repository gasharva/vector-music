namespace SvgMusic.Canonical;

public sealed record CanonicalComparisonPair(
    CanonicalNotation Expected,
    CanonicalNotation Actual);

public sealed class CanonicalComparisonNormalizer
{
    public CanonicalComparisonPair Normalize(
        CanonicalNotation expected,
        CanonicalNotation actual)
    {
        if (ShouldCollapseSplitGrandStaff(expected, actual))
        {
            expected = CollapseSplitGrandStaff(
                expected,
                actual.Parts[0].Id);
        }
        else if (ShouldCollapseSplitGrandStaff(actual, expected))
        {
            actual = CollapseSplitGrandStaff(
                actual,
                expected.Parts[0].Id);
        }

        expected = NormalizeInterstaffDirections(
            NormalizeMonophonicVoices(
                RemoveTrailingEmptyMeasures(expected)));
        actual = NormalizeInterstaffDirections(
            NormalizeMonophonicVoices(
                RemoveTrailingEmptyMeasures(actual)));

        actual = AlignActualVoiceLabels(expected, actual);

        return new CanonicalComparisonPair(expected, actual);
    }

    private static bool ShouldCollapseSplitGrandStaff(
        CanonicalNotation split,
        CanonicalNotation grand)
    {
        if (split.Parts.Count != 2
            || grand.Parts.Count != 1)
        {
            return false;
        }

        var grandPart = grand.Parts[0];
        var hasSecondStaff =
            grandPart.Measures.Any(measure =>
                measure.Attributes?.Staves is >= 2)
            || grandPart.Measures
                .SelectMany(measure => measure.Events)
                .Any(ev => EventStaff(ev) == 2);

        if (!hasSecondStaff)
        {
            return false;
        }

        var firstNumbers = split.Parts[0].Measures
            .Select(measure => measure.Number)
            .OrderBy(number => number)
            .ToArray();
        var secondNumbers = split.Parts[1].Measures
            .Select(measure => measure.Number)
            .OrderBy(number => number)
            .ToArray();

        if (!firstNumbers.SequenceEqual(secondNumbers))
        {
            return false;
        }

        return split.Parts.All(PartUsesOnlyLocalStaffOne);
    }

    private static bool PartUsesOnlyLocalStaffOne(Part part)
    {
        foreach (var ev in part.Measures.SelectMany(measure => measure.Events))
        {
            var staff = EventStaff(ev);

            if (staff is not null && staff != 1)
            {
                return false;
            }
        }

        foreach (var clef in part.Measures
                     .SelectMany(measure =>
                         measure.Attributes?.Clefs ?? []))
        {
            if (clef.Staff != 1)
            {
                return false;
            }
        }

        return true;
    }

    private static CanonicalNotation CollapseSplitGrandStaff(
        CanonicalNotation score,
        string targetPartId)
    {
        var upper = score.Parts[0];
        var lower = score.Parts[1];
        var lowerPartPrefix = lower.Id + ".";

        var upperMeasures = upper.Measures.ToDictionary(
            measure => measure.Number);
        var lowerMeasures = lower.Measures.ToDictionary(
            measure => measure.Number);
        var numbers = upperMeasures.Keys
            .Concat(lowerMeasures.Keys)
            .Distinct()
            .OrderBy(number => number)
            .ToArray();

        var mergedMeasures = new List<Measure>();

        foreach (var number in numbers)
        {
            upperMeasures.TryGetValue(number, out var upperMeasure);
            lowerMeasures.TryGetValue(number, out var lowerMeasure);

            var events = new List<CanonicalEvent>();

            if (upperMeasure is not null)
            {
                events.AddRange(upperMeasure.Events.Select(ev =>
                    MoveEventToStaff(ev, 1)));
            }

            if (lowerMeasure is not null)
            {
                events.AddRange(lowerMeasure.Events.Select(ev =>
                    MoveEventToStaff(ev, 2)));
            }

            var attributes = MergeAttributes(
                upperMeasure?.Attributes,
                lowerMeasure?.Attributes,
                number == numbers.FirstOrDefault());

            mergedMeasures.Add(new Measure(
                number,
                events
                    .OrderBy(ev => FractionValue(ev.At))
                    .ThenBy(ev => EventStaff(ev))
                    .ThenBy(ev => ev.Voice)
                    .ThenBy(ev => ev.Id, StringComparer.Ordinal)
                    .ToList(),
                attributes,
                FirstNonEmpty(
                    upperMeasure?.LeftBarline,
                    lowerMeasure?.LeftBarline),
                FirstNonEmpty(
                    upperMeasure?.RightBarline,
                    lowerMeasure?.RightBarline),
                upperMeasure?.Layout ?? lowerMeasure?.Layout,
                upperMeasure?.LeftRepeat ?? lowerMeasure?.LeftRepeat,
                upperMeasure?.RightRepeat ?? lowerMeasure?.RightRepeat));
        }

        var spans = score.Relations;

        var normalizedRelations = new Relations(
            spans.Beams,
            spans.Ties,
            spans.Slurs,
            spans.Tuplets,
            spans.Arpeggios,
            spans.Hairpins
                .Select(span => MoveSpanIfLower(
                    span,
                    lowerPartPrefix))
                .ToList(),
            spans.Pedals
                .Select(span => MoveSpanIfLower(
                    span,
                    lowerPartPrefix))
                .ToList(),
            spans.OctaveShifts
                .Select(span => MoveSpanIfLower(
                    span,
                    lowerPartPrefix))
                .ToList());

        return score with
        {
            Parts =
            [
                new Part(
                    targetPartId,
                    FirstNonEmpty(
                        upper.Name,
                        lower.Name)
                    ?? targetPartId,
                    mergedMeasures)
            ],
            Relations = normalizedRelations
        };
    }

    private static MeasureAttributes? MergeAttributes(
        MeasureAttributes? upper,
        MeasureAttributes? lower,
        bool firstMeasure)
    {
        if (upper is null
            && lower is null
            && !firstMeasure)
        {
            return null;
        }

        var clefs = new List<Clef>();

        if (upper?.Clefs is not null)
        {
            clefs.AddRange(
                upper.Clefs.Select(clef => clef with
                {
                    Staff = 1
                }));
        }

        if (lower?.Clefs is not null)
        {
            clefs.AddRange(
                lower.Clefs.Select(clef => clef with
                {
                    Staff = 2
                }));
        }

        return new MeasureAttributes(
            upper?.Time ?? lower?.Time,
            upper?.Key ?? lower?.Key,
            firstMeasure
                ? 2
                : upper?.Staves ?? lower?.Staves,
            clefs.Count > 0
                ? clefs
                : null,
            upper?.At ?? lower?.At);
    }

    private static CanonicalEvent MoveEventToStaff(
        CanonicalEvent ev,
        int staff)
    {
        var notes = ev.Notes?
            .Select(note => note with
            {
                Staff = staff
            })
            .ToList();

        return ev with
        {
            Staff = ev.Staff is null
                ? null
                : staff,
            Notes = notes
        };
    }

    private static SpanRelation MoveSpanIfLower(
        SpanRelation span,
        string lowerPartPrefix)
    {
        if (!span.Id.StartsWith(
                lowerPartPrefix,
                StringComparison.Ordinal))
        {
            return span;
        }

        return span with
        {
            From = span.From with
            {
                Staff = 2
            },
            To = span.To with
            {
                Staff = 2
            }
        };
    }

    private static CanonicalNotation NormalizeMonophonicVoices(
        CanonicalNotation score)
    {
        return score with
        {
            Parts = score.Parts
                .Select(part => part with
                {
                    Measures = part.Measures
                        .Select(measure =>
                        {
                            var voiceCountsByStaff = measure.Events
                                .Where(ev =>
                                    ev.Voice is not null
                                    && EventStaff(ev) is not null)
                                .GroupBy(ev => EventStaff(ev)!.Value)
                                .ToDictionary(
                                    group => group.Key,
                                    group => group
                                        .Select(ev => ev.Voice!.Value)
                                        .Distinct()
                                        .Count());

                            return measure with
                            {
                                Events = measure.Events
                                    .Select(ev =>
                                    {
                                        var staff = EventStaff(ev);

                                        if (ev.Voice is null
                                            || staff is null
                                            || !voiceCountsByStaff.TryGetValue(
                                                staff.Value,
                                                out var count)
                                            || count != 1)
                                        {
                                            return ev;
                                        }

                                        return ev with
                                        {
                                            Voice = 1
                                        };
                                    })
                                    .ToList()
                            };
                        })
                        .ToList()
                })
                .ToList()
        };
    }

    private static CanonicalNotation AlignActualVoiceLabels(
        CanonicalNotation expected,
        CanonicalNotation actual)
    {
        var expectedByPart = expected.Parts.ToDictionary(
            part => part.Id,
            StringComparer.Ordinal);
        var mappings = new Dictionary<
            (string Part, int Staff, int ActualVoice),
            int>();

        foreach (var actualPart in actual.Parts)
        {
            if (!expectedByPart.TryGetValue(
                    actualPart.Id,
                    out var expectedPart))
            {
                continue;
            }

            var evidence = CollectVoiceEvidence(
                expectedPart,
                actualPart);

            foreach (var staffGroup in evidence
                         .GroupBy(item => item.Key.Staff))
            {
                var candidates = staffGroup
                    .Select(item => new
                    {
                        item.Key.ExpectedVoice,
                        item.Key.ActualVoice,
                        Count = item.Value
                    })
                    .OrderByDescending(item => item.Count)
                    .ThenBy(item => item.ExpectedVoice)
                    .ThenBy(item => item.ActualVoice)
                    .ToArray();

                var usedExpected = new HashSet<int>();
                var usedActual = new HashSet<int>();

                foreach (var candidate in candidates)
                {
                    if (candidate.Count < 2
                        || usedExpected.Contains(candidate.ExpectedVoice)
                        || usedActual.Contains(candidate.ActualVoice))
                    {
                        continue;
                    }

                    var actualEvidence = candidates
                        .Where(item =>
                            item.ActualVoice == candidate.ActualVoice)
                        .Sum(item => item.Count);
                    var expectedEvidence = candidates
                        .Where(item =>
                            item.ExpectedVoice == candidate.ExpectedVoice)
                        .Sum(item => item.Count);

                    if (candidate.Count < actualEvidence * 0.75
                        || candidate.Count < expectedEvidence * 0.75)
                    {
                        continue;
                    }

                    usedExpected.Add(candidate.ExpectedVoice);
                    usedActual.Add(candidate.ActualVoice);
                    mappings[(
                        actualPart.Id,
                        staffGroup.Key,
                        candidate.ActualVoice)] =
                        candidate.ExpectedVoice;
                }
            }
        }

        if (mappings.Count == 0)
        {
            return actual;
        }

        return actual with
        {
            Parts = actual.Parts
                .Select(part => part with
                {
                    Measures = part.Measures
                        .Select(measure => measure with
                        {
                            Events = measure.Events
                                .Select(ev =>
                                {
                                    var staff = EventStaff(ev);

                                    if (ev.Voice is null
                                        || staff is null
                                        || !mappings.TryGetValue(
                                            (
                                                part.Id,
                                                staff.Value,
                                                ev.Voice.Value
                                            ),
                                            out var mapped))
                                    {
                                        return ev;
                                    }

                                    return ev with
                                    {
                                        Voice = mapped
                                    };
                                })
                                .ToList()
                        })
                        .ToList()
                })
                .ToList()
        };
    }

    private static Dictionary<
        (int Staff, int ExpectedVoice, int ActualVoice),
        int> CollectVoiceEvidence(
        Part expected,
        Part actual)
    {
        var result = new Dictionary<
            (int Staff, int ExpectedVoice, int ActualVoice),
            int>();
        var actualMeasures = actual.Measures.ToDictionary(
            measure => measure.Number);

        foreach (var expectedMeasure in expected.Measures)
        {
            if (!actualMeasures.TryGetValue(
                    expectedMeasure.Number,
                    out var actualMeasure))
            {
                continue;
            }

            var remaining = actualMeasure.Events.ToList();

            foreach (var expectedEvent in expectedMeasure.Events)
            {
                var staff = EventStaff(expectedEvent);

                if (staff is null
                    || expectedEvent.Voice is null)
                {
                    continue;
                }

                var signature = VoiceEvidenceSignature(
                    expectedEvent);
                var index = remaining.FindIndex(actualEvent =>
                    EventStaff(actualEvent) == staff
                    && actualEvent.Voice is not null
                    && string.Equals(
                        VoiceEvidenceSignature(actualEvent),
                        signature,
                        StringComparison.Ordinal));

                if (index < 0)
                {
                    continue;
                }

                var actualEvent = remaining[index];
                remaining.RemoveAt(index);
                var key = (
                    staff.Value,
                    expectedEvent.Voice.Value,
                    actualEvent.Voice!.Value);

                result[key] = result.GetValueOrDefault(key) + 1;
            }
        }

        return result;
    }

    private static string VoiceEvidenceSignature(
        CanonicalEvent ev)
    {
        var content = ev.Type switch
        {
            "chord" => string.Join(
                "+",
                (ev.Notes ?? [])
                    .Select(note => note.Pitch)
                    .OrderBy(pitch => pitch, StringComparer.Ordinal)),
            "rest" => "rest",
            "dynamic" => ev.Value ?? string.Empty,
            "text" => ev.Text ?? string.Empty,
            "tempo" => $"{ev.BeatUnit}:{ev.Bpm}",
            "navigation" => ev.Value ?? string.Empty,
            _ => ev.Value ?? ev.Text ?? string.Empty
        };

        return $"{ev.Type}|{ev.At}|{ev.Duration}|{ev.Grace}|{content}";
    }

    private static CanonicalNotation NormalizeInterstaffDirections(
        CanonicalNotation score)
    {
        var twoStaffParts = score.Parts
            .Where(HasTwoStaves)
            .Select(part => part.Id)
            .ToHashSet(StringComparer.Ordinal);

        if (twoStaffParts.Count == 0)
        {
            return score;
        }

        var parts = score.Parts
            .Select(part =>
            {
                if (!twoStaffParts.Contains(part.Id))
                {
                    return part;
                }

                return part with
                {
                    Measures = part.Measures
                        .Select(measure => measure with
                        {
                            Events = measure.Events
                                .Select(NormalizeInterstaffEvent)
                                .ToList()
                        })
                        .ToList()
                };
            })
            .ToList();

        return score with
        {
            Parts = parts,
            Relations = score.Relations with
            {
                Hairpins = score.Relations.Hairpins
                    .Select(NormalizeInterstaffSpan)
                    .ToList()
            }
        };
    }

    private static bool HasTwoStaves(Part part) =>
        part.Measures.Any(measure =>
            measure.Attributes?.Staves is >= 2)
        || part.Measures
            .SelectMany(measure => measure.Events)
            .Any(ev => EventStaff(ev) == 2);

    private static CanonicalEvent NormalizeInterstaffEvent(
        CanonicalEvent ev)
    {
        if (ev.Type is not (
                "dynamic"
                or "text"
                or "tempo"
                or "navigation"))
        {
            return ev;
        }

        if (ev.Staff == 1
            && string.Equals(
                ev.Placement,
                "below",
                StringComparison.Ordinal))
        {
            return ev with
            {
                Staff = 1,
                Placement = "between"
            };
        }

        if (ev.Staff == 2
            && string.Equals(
                ev.Placement,
                "above",
                StringComparison.Ordinal))
        {
            return ev with
            {
                Staff = 1,
                Placement = "between"
            };
        }

        return ev;
    }

    private static SpanRelation NormalizeInterstaffSpan(
        SpanRelation span)
    {
        if (span.From.Staff == 1
            && span.To.Staff == 1
            && string.Equals(
                span.Placement,
                "below",
                StringComparison.Ordinal))
        {
            return span with
            {
                From = span.From with { Staff = 1 },
                To = span.To with { Staff = 1 },
                Placement = "between"
            };
        }

        if (span.From.Staff == 2
            && span.To.Staff == 2
            && string.Equals(
                span.Placement,
                "above",
                StringComparison.Ordinal))
        {
            return span with
            {
                From = span.From with { Staff = 1 },
                To = span.To with { Staff = 1 },
                Placement = "between"
            };
        }

        return span;
    }

    private static CanonicalNotation RemoveTrailingEmptyMeasures(
        CanonicalNotation score)
    {
        var referencedMeasures = score.Relations.Hairpins
            .Concat(score.Relations.Pedals)
            .Concat(score.Relations.OctaveShifts)
            .SelectMany(span => new[]
            {
                span.From.Measure,
                span.To.Measure
            })
            .ToHashSet();

        var parts = score.Parts
            .Select(part =>
            {
                var measures = part.Measures
                    .OrderBy(measure => measure.Number)
                    .ToList();

                while (measures.Count > 1)
                {
                    var tail = measures[^1];

                    if (tail.Events.Count > 0
                        || referencedMeasures.Contains(tail.Number)
                        || HasMeaningfulAttributes(tail.Attributes))
                    {
                        break;
                    }

                    var previous = measures[^2];

                    measures[^2] = previous with
                    {
                        RightBarline =
                            tail.RightBarline
                            ?? previous.RightBarline,
                        RightRepeat =
                            tail.RightRepeat
                            ?? previous.RightRepeat
                    };

                    measures.RemoveAt(measures.Count - 1);
                }

                return part with
                {
                    Measures = measures
                };
            })
            .ToList();

        return score with
        {
            Parts = parts
        };
    }

    private static bool HasMeaningfulAttributes(
        MeasureAttributes? attributes) =>
        attributes is not null
        && (attributes.Time is not null
            || attributes.Key is not null
            || attributes.Clefs is { Count: > 0 });

    private static int? EventStaff(CanonicalEvent ev)
    {
        if (ev.Staff is not null)
        {
            return ev.Staff;
        }

        var staffs = (ev.Notes ?? [])
            .Select(note => note.Staff)
            .Where(staff => staff is not null)
            .Distinct()
            .ToArray();

        return staffs.Length == 1
            ? staffs[0]
            : null;
    }

    private static double FractionValue(string value)
    {
        try
        {
            var fraction = Fraction.Parse(value);
            return fraction.Numerator
                / (double)fraction.Denominator;
        }
        catch
        {
            return double.PositiveInfinity;
        }
    }

    private static string? FirstNonEmpty(
        string? first,
        string? second) =>
        !string.IsNullOrWhiteSpace(first)
            ? first
            : !string.IsNullOrWhiteSpace(second)
                ? second
                : null;
}
