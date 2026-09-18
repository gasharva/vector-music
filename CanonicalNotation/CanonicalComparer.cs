using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SvgMusic.Canonical;

public enum CanonicalDiffCategory
{
    Metadata,
    Structure,
    Attributes,
    Rhythm,
    Pitch,
    Notation,
    Text,
    Relations
}

public sealed record CanonicalDiffIssue(
    CanonicalDiffCategory Category,
    string Code,
    string? Part,
    int? Measure,
    int? Staff,
    string? At,
    string Expected,
    string Actual,
    string Message,
    string? RootCause = null,
    string? RootCauseSummary = null,
    int? Page = null,
    int? LocalMeasure = null)
{
    [JsonIgnore]
    public string Location
    {
        get
        {
            var pieces = new List<string>();
            if (!string.IsNullOrWhiteSpace(Part)) pieces.Add(Part!);
            if (Measure is not null)
            {
                pieces.Add(
                    Page is not null && LocalMeasure is not null
                        ? $"m{Measure} (page {Page} / local m{LocalMeasure})"
                        : $"m{Measure}");
            }
            if (Staff is not null) pieces.Add($"staff {Staff}");
            if (!string.IsNullOrWhiteSpace(At)) pieces.Add($"at {At}");
            return pieces.Count == 0
                ? "score"
                : string.Join(" / ", pieces);
        }
    }
}

public sealed record CanonicalDiffRootCause(
    CanonicalDiffCategory Category,
    string Key,
    string Summary,
    IReadOnlyList<CanonicalDiffIssue> Issues);

public sealed record CanonicalDiffReport(
    IReadOnlyList<CanonicalDiffIssue> Issues)
{
    public bool IsEqual => Issues.Count == 0;

    public IReadOnlyDictionary<CanonicalDiffCategory, int> Counts =>
        Issues
            .GroupBy(issue => issue.Category)
            .ToDictionary(group => group.Key, group => group.Count());

    public IReadOnlyList<CanonicalDiffRootCause> RootCauses =>
        BuildRootCauses(Issues);

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Canonical semantic diff");
        sb.AppendLine();

        if (IsEqual)
        {
            sb.AppendLine("No semantic differences.");
            return sb.ToString();
        }

        sb.AppendLine(
            $"{Issues.Count} concrete difference(s), grouped into "
            + $"{RootCauses.Count} root cause(s).");
        sb.AppendLine();

        foreach (var category in Enum.GetValues<CanonicalDiffCategory>())
        {
            var groups = RootCauses
                .Where(group => group.Category == category)
                .ToArray();

            if (groups.Length == 0)
            {
                continue;
            }

            sb.AppendLine(
                $"## {category} — {groups.Length} root cause(s), "
                + $"{groups.Sum(group => group.Issues.Count)} detail(s)");
            sb.AppendLine();

            foreach (var group in groups)
            {
                sb.Append("### ")
                    .Append(group.Summary)
                    .Append(" (")
                    .Append(group.Issues.Count)
                    .AppendLine(")");
                sb.AppendLine();

                foreach (var issue in group.Issues
                             .OrderBy(issue => issue.Part)
                             .ThenBy(issue => issue.Measure)
                             .ThenBy(issue => FractionValue(issue.At))
                             .ThenBy(issue => issue.Staff)
                             .ThenBy(issue => issue.Code))
                {
                    sb.Append("- ")
                        .Append(issue.Location)
                        .Append(" — **")
                        .Append(issue.Code)
                        .Append("** ")
                        .AppendLine(issue.Message);
                    sb.Append("  - expected: ")
                        .AppendLine(issue.Expected);
                    sb.Append("  - actual: ")
                        .AppendLine(issue.Actual);
                }

                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    public string ToJson() =>
        JsonSerializer.Serialize(
            this,
            new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new JsonStringEnumConverter() }
            });

    private static IReadOnlyList<CanonicalDiffRootCause> BuildRootCauses(
        IReadOnlyList<CanonicalDiffIssue> issues)
    {
        return issues
            .GroupBy(issue => new
            {
                issue.Category,
                Key = RootCauseKey(issue)
            })
            .Select(group =>
            {
                var first = group.First();
                return new CanonicalDiffRootCause(
                    group.Key.Category,
                    group.Key.Key,
                    first.RootCauseSummary
                        ?? GenericRootCauseSummary(first),
                    group.ToArray());
            })
            .OrderBy(group => group.Category)
            .ThenBy(group => group.Summary, StringComparer.Ordinal)
            .ToArray();
    }

    private static string RootCauseKey(CanonicalDiffIssue issue)
    {
        if (!string.IsNullOrWhiteSpace(issue.RootCause))
        {
            return issue.RootCause!;
        }

        if (issue.Code is "metadata.title"
            or "metadata.subtitle"
            or "metadata.composer")
        {
            return issue.Code;
        }

        if (issue.Code is "time" or "key" or "staves" or "clef")
        {
            return issue.Code;
        }

        var location =
            $"{issue.Part}:m{issue.Measure}:s{issue.Staff}:at{issue.At}";

        return issue.Category switch
        {
            CanonicalDiffCategory.Rhythm => $"rhythm:{location}",
            CanonicalDiffCategory.Pitch => $"pitch:{location}",
            CanonicalDiffCategory.Notation => $"notation:{location}",
            CanonicalDiffCategory.Text => $"text:{location}",
            _ => $"{issue.Code}:{location}"
        };
    }

    private static string GenericRootCauseSummary(
        CanonicalDiffIssue issue)
    {
        if (issue.Code == "metadata.title") return "Title metadata differs";
        if (issue.Code == "metadata.subtitle") return "Subtitle metadata differs";
        if (issue.Code == "metadata.composer") return "Composer metadata differs";
        if (issue.Code == "time") return "Time-signature state differs";
        if (issue.Code == "key") return "Key-signature state differs";
        if (issue.Code == "staves") return "Staff-count model differs";
        if (issue.Code == "clef") return "Clef state differs";

        return issue.Category switch
        {
            CanonicalDiffCategory.Rhythm =>
                $"Rhythm/event interpretation — {CompactLocation(issue)}",
            CanonicalDiffCategory.Pitch =>
                $"Pitch/chord interpretation — {CompactLocation(issue)}",
            CanonicalDiffCategory.Notation =>
                $"Notation interpretation — {CompactLocation(issue)}",
            CanonicalDiffCategory.Text =>
                $"Text/tempo interpretation — {CompactLocation(issue)}",
            _ => $"{issue.Code} at {CompactLocation(issue)}"
        };
    }

    private static string CompactLocation(CanonicalDiffIssue issue)
    {
        var values = new List<string>();
        if (!string.IsNullOrWhiteSpace(issue.Part)) values.Add(issue.Part!);
        if (issue.Measure is not null) values.Add($"m{issue.Measure}");
        if (issue.Staff is not null) values.Add($"staff {issue.Staff}");
        return values.Count == 0
            ? "score"
            : string.Join(" / ", values);
    }

    private static double FractionValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return double.NegativeInfinity;
        }

        try
        {
            var fraction = Fraction.Parse(value);
            return fraction.Numerator / (double)fraction.Denominator;
        }
        catch
        {
            return double.PositiveInfinity;
        }
    }
}

public sealed class CanonicalComparer
{
    public CanonicalDiffReport Compare(
        CanonicalNotation expected,
        CanonicalNotation actual)
    {
        var normalized = new CanonicalComparisonNormalizer()
            .Normalize(expected, actual);
        expected = normalized.Expected;
        actual = normalized.Actual;

        var issues = new List<CanonicalDiffIssue>();

        CompareMetadata(expected.Metadata, actual.Metadata, issues);

        var partPairs = MatchParts(expected, actual, issues);

        foreach (var pair in partPairs)
        {
            ComparePart(pair.Expected, pair.Actual, issues);
        }

        CompareRelations(expected, actual, issues);

        return new CanonicalDiffReport(
            issues
                .OrderBy(issue => issue.Category)
                .ThenBy(issue => issue.Part)
                .ThenBy(issue => issue.Measure)
                .ThenBy(issue => issue.At)
                .ThenBy(issue => issue.Staff)
                .ThenBy(issue => issue.Code)
                .ToArray());
    }

    private static void CompareMetadata(
        Metadata expected,
        Metadata actual,
        ICollection<CanonicalDiffIssue> issues)
    {
        CompareScalar(issues, CanonicalDiffCategory.Metadata, "metadata.title",
            null, null, null, null, expected.Title, actual.Title,
            "Score title differs.");
        CompareScalar(issues, CanonicalDiffCategory.Metadata, "metadata.subtitle",
            null, null, null, null, expected.Subtitle, actual.Subtitle,
            "Score subtitle differs.");
        CompareScalar(issues, CanonicalDiffCategory.Metadata, "metadata.composer",
            null, null, null, null, expected.Composer, actual.Composer,
            "Composer differs.");
    }

    private static IReadOnlyList<PartPair> MatchParts(
        CanonicalNotation expected,
        CanonicalNotation actual,
        ICollection<CanonicalDiffIssue> issues)
    {
        var result = new List<PartPair>();
        var actualById = actual.Parts.ToDictionary(
            part => part.Id,
            StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);

        foreach (var expectedPart in expected.Parts)
        {
            if (actualById.TryGetValue(expectedPart.Id, out var exact))
            {
                result.Add(new PartPair(expectedPart, exact));
                used.Add(exact.Id);
                continue;
            }

            var fallback = actual.Parts
                .Where(part => !used.Contains(part.Id))
                .FirstOrDefault(part =>
                    string.Equals(
                        Normalize(part.Name),
                        Normalize(expectedPart.Name),
                        StringComparison.OrdinalIgnoreCase));

            if (fallback is not null)
            {
                result.Add(new PartPair(expectedPart, fallback));
                used.Add(fallback.Id);
                issues.Add(new CanonicalDiffIssue(
                    CanonicalDiffCategory.Structure,
                    "part.id",
                    expectedPart.Id,
                    null,
                    null,
                    null,
                    expectedPart.Id,
                    fallback.Id,
                    $"Part name matched, but part id differs ({expectedPart.Id} -> {fallback.Id})."));
                continue;
            }

            issues.Add(new CanonicalDiffIssue(
                CanonicalDiffCategory.Structure,
                "part.missing",
                expectedPart.Id,
                null,
                null,
                null,
                expectedPart.Name,
                "<missing>",
                $"Expected part '{expectedPart.Name}' is missing."));
        }

        foreach (var extra in actual.Parts.Where(part => !used.Contains(part.Id)))
        {
            issues.Add(new CanonicalDiffIssue(
                CanonicalDiffCategory.Structure,
                "part.extra",
                extra.Id,
                null,
                null,
                null,
                "<none>",
                extra.Name,
                $"Unexpected part '{extra.Name}' is present."));
        }

        return result;
    }

    private static void ComparePart(
        Part expected,
        Part actual,
        ICollection<CanonicalDiffIssue> issues)
    {
        var expectedMeasures = expected.Measures.ToDictionary(
            measure => measure.Number);
        var actualMeasures = actual.Measures.ToDictionary(
            measure => measure.Number);
        var allNumbers = expectedMeasures.Keys
            .Concat(actualMeasures.Keys)
            .Distinct()
            .OrderBy(number => number)
            .ToArray();

        var expectedState = new AttributeState();
        var actualState = new AttributeState();

        foreach (var number in allNumbers)
        {
            expectedMeasures.TryGetValue(number, out var expectedMeasure);
            actualMeasures.TryGetValue(number, out var actualMeasure);

            if (expectedMeasure is null)
            {
                issues.Add(new CanonicalDiffIssue(
                    CanonicalDiffCategory.Structure,
                    "measure.extra",
                    expected.Id,
                    number,
                    null,
                    null,
                    "<none>",
                    "measure",
                    $"Unexpected measure {number} is present."));
                Apply(actualMeasure?.Attributes, actualState);
                continue;
            }

            if (actualMeasure is null)
            {
                issues.Add(new CanonicalDiffIssue(
                    CanonicalDiffCategory.Structure,
                    "measure.missing",
                    expected.Id,
                    number,
                    null,
                    null,
                    "measure",
                    "<missing>",
                    $"Expected measure {number} is missing."));
                Apply(expectedMeasure.Attributes, expectedState);
                continue;
            }

            Apply(expectedMeasure.Attributes, expectedState);
            Apply(actualMeasure.Attributes, actualState);

            CompareAttributes(
                expected.Id,
                number,
                expectedState,
                actualState,
                issues);
            CompareBarlines(
                expected.Id,
                expectedMeasure,
                actualMeasure,
                issues);
            CompareEvents(
                expected.Id,
                expectedMeasure,
                actualMeasure,
                issues);
        }
    }

    private static void CompareAttributes(
        string part,
        int measure,
        AttributeState expected,
        AttributeState actual,
        ICollection<CanonicalDiffIssue> issues)
    {
        CompareScalar(
            issues, CanonicalDiffCategory.Attributes, "time",
            part, measure, null, null,
            expected.Time is null ? null : $"{expected.Time.Beats}/{expected.Time.BeatType}",
            actual.Time is null ? null : $"{actual.Time.Beats}/{actual.Time.BeatType}",
            "Effective time signature differs.");

        CompareScalar(
            issues, CanonicalDiffCategory.Attributes, "key",
            part, measure, null, null,
            expected.Key is null ? null : $"{expected.Key.Fifths}:{expected.Key.Mode ?? "-"}",
            actual.Key is null ? null : $"{actual.Key.Fifths}:{actual.Key.Mode ?? "-"}",
            "Effective key signature differs.");

        CompareScalar(
            issues, CanonicalDiffCategory.Attributes, "staves",
            part, measure, null, null,
            expected.Staves?.ToString(),
            actual.Staves?.ToString(),
            "Effective staff count differs.");

        CompareSet(
            issues, CanonicalDiffCategory.Attributes, "clef",
            part, measure, null, null,
            ClefSignatures(expected.Clefs.Values),
            ClefSignatures(actual.Clefs.Values),
            "Effective clefs differ.");
    }

    private static void CompareBarlines(
        string part,
        Measure expected,
        Measure actual,
        ICollection<CanonicalDiffIssue> issues)
    {
        CompareScalar(issues, CanonicalDiffCategory.Structure, "barline.left",
            part, expected.Number, null, null,
            expected.LeftBarline, actual.LeftBarline, "Left barline differs.");
        CompareScalar(issues, CanonicalDiffCategory.Structure, "barline.right",
            part, expected.Number, null, null,
            expected.RightBarline, actual.RightBarline, "Right barline differs.");
        CompareScalar(issues, CanonicalDiffCategory.Structure, "repeat.left",
            part, expected.Number, null, null,
            RepeatSignature(expected.LeftRepeat),
            RepeatSignature(actual.LeftRepeat),
            "Left repeat mark differs.");
        CompareScalar(issues, CanonicalDiffCategory.Structure, "repeat.right",
            part, expected.Number, null, null,
            RepeatSignature(expected.RightRepeat),
            RepeatSignature(actual.RightRepeat),
            "Right repeat mark differs.");
    }

    private static void CompareEvents(
        string part,
        Measure expectedMeasure,
        Measure actualMeasure,
        ICollection<CanonicalDiffIssue> issues)
    {
        var unmatchedActual = actualMeasure.Events
            .Select((ev, index) => new IndexedEvent(index, ev))
            .ToList();

        foreach (var expected in expectedMeasure.Events)
        {
            var exactCandidates = unmatchedActual
                .Where(item => SameCoordinate(expected, item.Event))
                .OrderByDescending(item => Similarity(expected, item.Event))
                .ToArray();

            if (exactCandidates.Length > 0)
            {
                var match = exactCandidates[0];
                unmatchedActual.Remove(match);
                CompareEvent(
                    part,
                    expectedMeasure.Number,
                    expected,
                    match.Event,
                    issues);
                continue;
            }

            var relocated = unmatchedActual
                .Where(item =>
                    string.Equals(
                        item.Event.Type,
                        expected.Type,
                        StringComparison.Ordinal)
                    && string.Equals(
                        item.Event.At,
                        expected.At,
                        StringComparison.Ordinal)
                    && SameSemanticContent(
                        expected,
                        item.Event))
                .OrderByDescending(item =>
                    Similarity(expected, item.Event))
                .FirstOrDefault();

            if (relocated is not null)
            {
                unmatchedActual.Remove(relocated);
                CompareEvent(
                    part,
                    expectedMeasure.Number,
                    expected,
                    relocated.Event,
                    issues);
                continue;
            }

            var moved = unmatchedActual
                .Where(item => string.Equals(
                    item.Event.Type,
                    expected.Type,
                    StringComparison.Ordinal))
                .Select(item => new
                {
                    Item = item,
                    Score = Similarity(expected, item.Event)
                })
                .Where(item => item.Score >= 0.72)
                .OrderByDescending(item => item.Score)
                .ThenBy(item => FractionDistance(
                    expected.At,
                    item.Item.Event.At))
                .FirstOrDefault();

            if (moved is not null)
            {
                unmatchedActual.Remove(moved.Item);

                string? rootCause = null;
                string? rootCauseSummary = null;

                if (!string.Equals(
                        expected.At,
                        moved.Item.Event.At,
                        StringComparison.Ordinal))
                {
                    rootCause = TimingRootCauseKey(
                        part,
                        expectedMeasure.Number,
                        expected);
                    rootCauseSummary = TimingRootCauseSummary(
                        part,
                        expectedMeasure.Number,
                        expected);

                    issues.Add(new CanonicalDiffIssue(
                        CanonicalDiffCategory.Rhythm,
                        "event.onset",
                        part,
                        expectedMeasure.Number,
                        EventStaff(expected),
                        expected.At,
                        expected.At,
                        moved.Item.Event.At,
                        $"{DescribeEvent(expected)} was found, but at the wrong musical onset.",
                        rootCause,
                        rootCauseSummary));
                }

                CompareEvent(
                    part,
                    expectedMeasure.Number,
                    expected,
                    moved.Item.Event,
                    issues,
                    rootCause,
                    rootCauseSummary);
                continue;
            }

            issues.Add(new CanonicalDiffIssue(
                CategoryFor(expected),
                "event.missing",
                part,
                expectedMeasure.Number,
                EventStaff(expected),
                expected.At,
                DescribeEvent(expected),
                "<missing>",
                $"Expected {DescribeEvent(expected)} is missing."));
        }

        foreach (var extra in unmatchedActual)
        {
            var ev = extra.Event;
            issues.Add(new CanonicalDiffIssue(
                CategoryFor(ev),
                "event.extra",
                part,
                actualMeasure.Number,
                EventStaff(ev),
                ev.At,
                "<none>",
                DescribeEvent(ev),
                $"Unexpected {DescribeEvent(ev)} is present."));
        }
    }

    private static void CompareEvent(
        string part,
        int measure,
        CanonicalEvent expected,
        CanonicalEvent actual,
        ICollection<CanonicalDiffIssue> issues,
        string? rootCause = null,
        string? rootCauseSummary = null)
    {
        var staff = EventStaff(expected) ?? EventStaff(actual);
        var at = expected.At;

        CompareScalar(issues, CanonicalDiffCategory.Rhythm, "event.duration",
            part, measure, staff, at,
            expected.Duration, actual.Duration,
            $"{DescribeEvent(expected)} duration differs.",
            rootCause: rootCause,
            rootCauseSummary: rootCauseSummary);

        CompareScalar(issues, CanonicalDiffCategory.Rhythm, "event.voice",
            part, measure, staff, at,
            expected.Voice?.ToString(), actual.Voice?.ToString(),
            $"{DescribeEvent(expected)} voice differs.",
            rootCause: rootCause,
            rootCauseSummary: rootCauseSummary);

        CompareScalar(issues, CanonicalDiffCategory.Rhythm, "event.staff",
            part, measure, staff, at,
            expected.Staff?.ToString(), actual.Staff?.ToString(),
            $"{DescribeEvent(expected)} staff differs.",
            rootCause: rootCause,
            rootCauseSummary: rootCauseSummary);

        CompareScalar(issues, CanonicalDiffCategory.Notation, "event.grace",
            part, measure, staff, at,
            expected.Grace?.ToString(), actual.Grace?.ToString(),
            $"{DescribeEvent(expected)} grace status differs.");

        if (expected.Type == "chord")
        {
            CompareNotes(part, measure, at,
                expected.Notes ?? [], actual.Notes ?? [], issues);
            CompareNotation(part, measure, staff, at,
                expected.Notation, actual.Notation, issues);
            return;
        }

        if (expected.Type == "rest")
        {
            CompareNotation(part, measure, staff, at,
                expected.Notation, actual.Notation, issues);
            return;
        }

        if (expected.Type == "text")
        {
            CompareScalar(issues, CanonicalDiffCategory.Text, "text.value",
                part, measure, staff, at,
                expected.Text, actual.Text, "Text content differs.");
            CompareScalar(issues, CanonicalDiffCategory.Text, "text.role",
                part, measure, staff, at,
                expected.TextRole, actual.TextRole,
                "Text semantic role differs.",
                expectedNullIsWildcard: true);
        }
        else if (expected.Type is "dynamic" or "navigation")
        {
            CompareScalar(issues, CanonicalDiffCategory.Notation,
                $"{expected.Type}.value",
                part, measure, staff, at,
                expected.Value, actual.Value,
                $"{expected.Type} value differs.");
        }
        else if (expected.Type == "tempo")
        {
            CompareScalar(issues, CanonicalDiffCategory.Text,
                "tempo.beat-unit",
                part, measure, staff, at,
                expected.BeatUnit, actual.BeatUnit,
                "Tempo beat unit differs.");
            CompareScalar(issues, CanonicalDiffCategory.Text,
                "tempo.bpm",
                part, measure, staff, at,
                expected.Bpm?.ToString(), actual.Bpm?.ToString(),
                "Tempo BPM differs.");
        }

        CompareScalar(issues, CategoryFor(expected), "event.placement",
            part, measure, staff, at,
            expected.Placement, actual.Placement,
            $"{DescribeEvent(expected)} placement differs.",
            expectedNullIsWildcard: true);
    }

    private static void CompareNotes(
        string part,
        int measure,
        string at,
        IReadOnlyList<CanonicalNote> expected,
        IReadOnlyList<CanonicalNote> actual,
        ICollection<CanonicalDiffIssue> issues)
    {
        if (expected.Count != actual.Count)
        {
            issues.Add(new CanonicalDiffIssue(
                CanonicalDiffCategory.Pitch,
                "chord.note-count",
                part,
                measure,
                expected.FirstOrDefault()?.Staff,
                at,
                expected.Count.ToString(),
                actual.Count.ToString(),
                "Chord note count differs."));
        }

        var remaining = actual
            .Select((note, index) => new IndexedNote(index, note))
            .ToList();

        foreach (var expectedNote in expected
                     .OrderBy(note => note.Staff)
                     .ThenBy(note => PitchValue(note.Pitch))
                     .ThenBy(note => note.Pitch))
        {
            var match = remaining.FirstOrDefault(item =>
                item.Note.Staff == expectedNote.Staff
                && string.Equals(
                    item.Note.Pitch,
                    expectedNote.Pitch,
                    StringComparison.Ordinal));

            match ??= remaining.FirstOrDefault(item =>
                string.Equals(
                    item.Note.Pitch,
                    expectedNote.Pitch,
                    StringComparison.Ordinal));

            match ??= remaining
                .Where(item => item.Note.Staff == expectedNote.Staff)
                .OrderBy(item => Math.Abs(
                    PitchValue(item.Note.Pitch)
                    - PitchValue(expectedNote.Pitch)))
                .FirstOrDefault();

            match ??= remaining
                .OrderBy(item => Math.Abs(
                    PitchValue(item.Note.Pitch)
                    - PitchValue(expectedNote.Pitch)))
                .FirstOrDefault();

            if (match is null)
            {
                issues.Add(new CanonicalDiffIssue(
                    CanonicalDiffCategory.Pitch,
                    "note.missing",
                    part,
                    measure,
                    expectedNote.Staff,
                    at,
                    NoteSignature(expectedNote),
                    "<missing>",
                    $"Expected note {expectedNote.Pitch} is missing from chord."));
                continue;
            }

            remaining.Remove(match);
            CompareNote(
                part,
                measure,
                at,
                expectedNote,
                match.Note,
                issues);
        }

        foreach (var extra in remaining
                     .OrderBy(item => item.Note.Staff)
                     .ThenBy(item => PitchValue(item.Note.Pitch)))
        {
            issues.Add(new CanonicalDiffIssue(
                CanonicalDiffCategory.Pitch,
                "note.extra",
                part,
                measure,
                extra.Note.Staff,
                at,
                "<none>",
                NoteSignature(extra.Note),
                $"Unexpected note {extra.Note.Pitch} is present in chord."));
        }
    }

    private static void CompareNote(
        string part,
        int measure,
        string at,
        CanonicalNote expected,
        CanonicalNote actual,
        ICollection<CanonicalDiffIssue> issues)
    {
        var staff = expected.Staff ?? actual.Staff;

        CompareScalar(
            issues,
            CanonicalDiffCategory.Pitch,
            "note.pitch",
            part,
            measure,
            staff,
            at,
            expected.Pitch,
            actual.Pitch,
            $"Pitch differs: expected {expected.Pitch}, got {actual.Pitch}.");

        CompareScalar(
            issues,
            CanonicalDiffCategory.Pitch,
            "note.staff",
            part,
            measure,
            staff,
            at,
            expected.Staff?.ToString(),
            actual.Staff?.ToString(),
            $"Staff differs for note {expected.Pitch}.");

        CompareScalar(
            issues,
            CanonicalDiffCategory.Notation,
            "note.accidental",
            part,
            measure,
            staff,
            at,
            AccidentalSignature(expected.Accidental),
            AccidentalSignature(actual.Accidental),
            $"Explicit accidental differs for {expected.Pitch}.");

        CompareTechnicalMarks(
            issues,
            part,
            measure,
            staff,
            at,
            expected.Technical,
            actual.Technical,
            expected.Pitch);
    }

    private static void CompareNotation(
        string part,
        int measure,
        int? staff,
        string at,
        EventNotation? expected,
        EventNotation? actual,
        ICollection<CanonicalDiffIssue> issues)
    {
        CompareScalar(issues, CanonicalDiffCategory.Notation,
            "notation.note-type",
            part, measure, staff, at,
            expected?.NoteType, actual?.NoteType,
            "Note/rest type differs.");
        CompareScalar(issues, CanonicalDiffCategory.Notation,
            "notation.dots",
            part, measure, staff, at,
            expected?.Dots?.ToString(), actual?.Dots?.ToString(),
            "Augmentation-dot count differs.");
        CompareScalar(issues, CanonicalDiffCategory.Notation,
            "notation.stem",
            part, measure, staff, at,
            expected?.Stem, actual?.Stem,
            "Stem direction differs.");
        CompareScalar(issues, CanonicalDiffCategory.Notation,
            "notation.notehead",
            part, measure, staff, at,
            expected?.Notehead, actual?.Notehead,
            "Notehead style differs.");

        CompareNotationMarks(
            issues,
            "notation.articulations",
            "Articulations",
            part,
            measure,
            staff,
            at,
            expected?.Articulations,
            actual?.Articulations);
        CompareNotationMarks(
            issues,
            "notation.ornaments",
            "Ornaments",
            part,
            measure,
            staff,
            at,
            expected?.Ornaments,
            actual?.Ornaments);
        CompareNotationMarks(
            issues,
            "notation.fermatas",
            "Fermatas",
            part,
            measure,
            staff,
            at,
            expected?.Fermatas,
            actual?.Fermatas);
    }

    private static void CompareNotationMarks(
        ICollection<CanonicalDiffIssue> issues,
        string code,
        string label,
        string part,
        int measure,
        int? staff,
        string at,
        IReadOnlyList<NotationMark>? expected,
        IReadOnlyList<NotationMark>? actual)
    {
        var remaining = (actual ?? [])
            .ToList();

        foreach (var expectedMark in expected ?? [])
        {
            var index = remaining.FindIndex(mark =>
                string.Equals(
                    mark.Type,
                    expectedMark.Type,
                    StringComparison.Ordinal)
                && string.Equals(
                    mark.Subtype,
                    expectedMark.Subtype,
                    StringComparison.Ordinal));

            if (index < 0)
            {
                issues.Add(new CanonicalDiffIssue(
                    CanonicalDiffCategory.Notation,
                    code,
                    part,
                    measure,
                    staff,
                    at,
                    $"{expectedMark.Type}:{expectedMark.Subtype ?? "-"}",
                    "<missing>",
                    $"{label}: expected mark is missing."));
                continue;
            }

            var actualMark = remaining[index];
            remaining.RemoveAt(index);

            CompareScalar(
                issues,
                CanonicalDiffCategory.Notation,
                code + ".placement",
                part,
                measure,
                staff,
                at,
                expectedMark.Placement,
                actualMark.Placement,
                $"{label}: placement differs for {expectedMark.Type}.",
                expectedNullIsWildcard: true);
        }

        foreach (var extra in remaining)
        {
            issues.Add(new CanonicalDiffIssue(
                CanonicalDiffCategory.Notation,
                code,
                part,
                measure,
                staff,
                at,
                "<none>",
                $"{extra.Type}:{extra.Subtype ?? "-"}",
                $"{label}: unexpected mark is present."));
        }
    }

    private static void CompareTechnicalMarks(
        ICollection<CanonicalDiffIssue> issues,
        string part,
        int measure,
        int? staff,
        string at,
        IReadOnlyList<TechnicalMark>? expected,
        IReadOnlyList<TechnicalMark>? actual,
        string pitch)
    {
        var remaining = (actual ?? [])
            .ToList();

        foreach (var expectedMark in expected ?? [])
        {
            var index = remaining.FindIndex(mark =>
                string.Equals(
                    mark.Type,
                    expectedMark.Type,
                    StringComparison.Ordinal)
                && string.Equals(
                    mark.Value,
                    expectedMark.Value,
                    StringComparison.Ordinal));

            if (index < 0)
            {
                issues.Add(new CanonicalDiffIssue(
                    CanonicalDiffCategory.Notation,
                    "note.technical",
                    part,
                    measure,
                    staff,
                    at,
                    $"{expectedMark.Type}:{expectedMark.Value ?? "-"}",
                    "<missing>",
                    $"Technical mark differs for {pitch}."));
                continue;
            }

            var actualMark = remaining[index];
            remaining.RemoveAt(index);

            CompareScalar(
                issues,
                CanonicalDiffCategory.Notation,
                "note.technical.placement",
                part,
                measure,
                staff,
                at,
                expectedMark.Placement,
                actualMark.Placement,
                $"Technical-mark placement differs for {pitch}.",
                expectedNullIsWildcard: true);
        }

        foreach (var extra in remaining)
        {
            issues.Add(new CanonicalDiffIssue(
                CanonicalDiffCategory.Notation,
                "note.technical",
                part,
                measure,
                staff,
                at,
                "<none>",
                $"{extra.Type}:{extra.Value ?? "-"}",
                $"Unexpected technical mark is present for {pitch}."));
        }
    }

    private static void CompareRelations(
        CanonicalNotation expected,
        CanonicalNotation actual,
        ICollection<CanonicalDiffIssue> issues)
    {
        var expectedEvents = BuildEventAddressMap(expected);
        var actualEvents = BuildEventAddressMap(actual);

        CompareRelationSet(
            "beam",
            expected.Relations.Beams.Select(relation =>
                $"L{relation.Level}:{relation.Hook ?? "-"}:"
                + string.Join(
                    ">",
                    relation.Events.Select(id =>
                        Address(expectedEvents, id)))),
            actual.Relations.Beams.Select(relation =>
                $"L{relation.Level}:{relation.Hook ?? "-"}:"
                + string.Join(
                    ">",
                    relation.Events.Select(id =>
                        Address(actualEvents, id)))),
            issues);

        ComparePlacedRelationSet(
            "tie",
            expected.Relations.Ties.Select(relation =>
                new PlacedRelation(
                    $"{Address(expectedEvents, relation.From.Event)}[{relation.From.Note}]"
                    + $"->{Address(expectedEvents, relation.To.Event)}[{relation.To.Note}]",
                    relation.Placement)),
            actual.Relations.Ties.Select(relation =>
                new PlacedRelation(
                    $"{Address(actualEvents, relation.From.Event)}[{relation.From.Note}]"
                    + $"->{Address(actualEvents, relation.To.Event)}[{relation.To.Note}]",
                    relation.Placement)),
            issues);

        ComparePlacedRelationSet(
            "slur",
            expected.Relations.Slurs.Select(relation =>
                new PlacedRelation(
                    $"{Address(expectedEvents, relation.From)}"
                    + $"->{Address(expectedEvents, relation.To)}",
                    relation.Placement)),
            actual.Relations.Slurs.Select(relation =>
                new PlacedRelation(
                    $"{Address(actualEvents, relation.From)}"
                    + $"->{Address(actualEvents, relation.To)}",
                    relation.Placement)),
            issues);

        CompareTupletRelations(
            expected.Relations.Tuplets,
            actual.Relations.Tuplets,
            expectedEvents,
            actualEvents,
            issues);

        CompareArpeggioRelations(
            expected.Relations.Arpeggios,
            actual.Relations.Arpeggios,
            expectedEvents,
            actualEvents,
            issues);

        CompareSpanRelations(
            "hairpin",
            expected.Relations.Hairpins,
            actual.Relations.Hairpins,
            issues);
        CompareSpanRelations(
            "pedal",
            expected.Relations.Pedals,
            actual.Relations.Pedals,
            issues);
        CompareSpanRelations(
            "octave-shift",
            expected.Relations.OctaveShifts,
            actual.Relations.OctaveShifts,
            issues);
    }

    private static void ComparePlacedRelationSet(
        string kind,
        IEnumerable<PlacedRelation> expected,
        IEnumerable<PlacedRelation> actual,
        ICollection<CanonicalDiffIssue> issues)
    {
        var remaining = actual.ToList();

        foreach (var expectedRelation in expected)
        {
            var index = remaining.FindIndex(relation =>
                string.Equals(
                    relation.Core,
                    expectedRelation.Core,
                    StringComparison.Ordinal));

            if (index < 0)
            {
                AddRelationIssue(
                    issues,
                    kind,
                    "missing",
                    expectedRelation.Core,
                    "<missing>",
                    $"Expected {kind} relation is missing.");
                continue;
            }

            var actualRelation = remaining[index];
            remaining.RemoveAt(index);

            if (!string.IsNullOrWhiteSpace(expectedRelation.Placement)
                && !string.Equals(
                    expectedRelation.Placement,
                    actualRelation.Placement,
                    StringComparison.Ordinal))
            {
                AddRelationIssue(
                    issues,
                    kind,
                    "placement",
                    expectedRelation.Placement!,
                    actualRelation.Placement ?? "<null>",
                    $"{kind} placement differs.",
                    expectedRelation.Core);
            }
        }

        foreach (var extra in remaining)
        {
            AddRelationIssue(
                issues,
                kind,
                "extra",
                "<none>",
                extra.Core,
                $"Unexpected {kind} relation is present.");
        }
    }

    private static void CompareTupletRelations(
        IReadOnlyList<TupletRelation> expected,
        IReadOnlyList<TupletRelation> actual,
        IReadOnlyDictionary<string, string> expectedEvents,
        IReadOnlyDictionary<string, string> actualEvents,
        ICollection<CanonicalDiffIssue> issues)
    {
        var remaining = actual
            .Select(relation => new TupletComparison(
                TupletCore(relation, actualEvents),
                relation.Bracket))
            .ToList();

        foreach (var relation in expected)
        {
            var core = TupletCore(relation, expectedEvents);
            var index = remaining.FindIndex(item =>
                item.Core == core);

            if (index < 0)
            {
                AddRelationIssue(
                    issues,
                    "tuplet",
                    "missing",
                    core,
                    "<missing>",
                    "Expected tuplet relation is missing.");
                continue;
            }

            var match = remaining[index];
            remaining.RemoveAt(index);

            if (relation.Bracket is not null
                && relation.Bracket != match.Bracket)
            {
                AddRelationIssue(
                    issues,
                    "tuplet",
                    "bracket",
                    relation.Bracket.ToString()!,
                    match.Bracket?.ToString() ?? "<null>",
                    "Tuplet bracket presentation differs.",
                    core);
            }
        }

        foreach (var extra in remaining)
        {
            AddRelationIssue(
                issues,
                "tuplet",
                "extra",
                "<none>",
                extra.Core,
                "Unexpected tuplet relation is present.");
        }
    }

    private static string TupletCore(
        TupletRelation relation,
        IReadOnlyDictionary<string, string> events) =>
        $"{relation.Actual}:{relation.Normal}:"
        + string.Join(
            ">",
            relation.Events.Select(id =>
                Address(events, id)));

    private static void CompareArpeggioRelations(
        IReadOnlyList<ArpeggioRelation> expected,
        IReadOnlyList<ArpeggioRelation> actual,
        IReadOnlyDictionary<string, string> expectedEvents,
        IReadOnlyDictionary<string, string> actualEvents,
        ICollection<CanonicalDiffIssue> issues)
    {
        var remaining = actual
            .Select(relation => new PlacedRelation(
                ArpeggioCore(relation, actualEvents),
                relation.Direction))
            .ToList();

        foreach (var relation in expected)
        {
            var core = ArpeggioCore(
                relation,
                expectedEvents);
            var index = remaining.FindIndex(item =>
                item.Core == core);

            if (index < 0)
            {
                AddRelationIssue(
                    issues,
                    "arpeggio",
                    "missing",
                    core,
                    "<missing>",
                    "Expected arpeggio relation is missing.");
                continue;
            }

            var match = remaining[index];
            remaining.RemoveAt(index);

            if (!string.IsNullOrWhiteSpace(relation.Direction)
                && !string.Equals(
                    relation.Direction,
                    match.Placement,
                    StringComparison.Ordinal))
            {
                AddRelationIssue(
                    issues,
                    "arpeggio",
                    "direction",
                    relation.Direction!,
                    match.Placement ?? "<null>",
                    "Arpeggio direction differs.",
                    core);
            }
        }

        foreach (var extra in remaining)
        {
            AddRelationIssue(
                issues,
                "arpeggio",
                "extra",
                "<none>",
                extra.Core,
                "Unexpected arpeggio relation is present.");
        }
    }

    private static string ArpeggioCore(
        ArpeggioRelation relation,
        IReadOnlyDictionary<string, string> events) =>
        string.Join(
            "+",
            relation.Events
                .Select(id => Address(events, id))
                .OrderBy(value => value, StringComparer.Ordinal));

    private static void CompareSpanRelations(
        string kind,
        IReadOnlyList<SpanRelation> expected,
        IReadOnlyList<SpanRelation> actual,
        ICollection<CanonicalDiffIssue> issues)
    {
        var remaining = actual.ToList();

        foreach (var expectedSpan in expected)
        {
            var core = SpanCore(expectedSpan);
            var index = remaining.FindIndex(span =>
                SpanCore(span) == core);

            if (index < 0)
            {
                AddRelationIssue(
                    issues,
                    kind,
                    "missing",
                    core,
                    "<missing>",
                    $"Expected {kind} relation is missing.");
                continue;
            }

            var actualSpan = remaining[index];
            remaining.RemoveAt(index);

            CompareOptionalRelationPresentation(
                issues,
                kind,
                core,
                "line",
                expectedSpan.Line?.ToString(),
                actualSpan.Line?.ToString());
            CompareOptionalRelationPresentation(
                issues,
                kind,
                core,
                "start-mark",
                expectedSpan.StartMark?.ToString(),
                actualSpan.StartMark?.ToString());
            CompareOptionalRelationPresentation(
                issues,
                kind,
                core,
                "placement",
                expectedSpan.Placement,
                actualSpan.Placement);
        }

        foreach (var extra in remaining)
        {
            AddRelationIssue(
                issues,
                kind,
                "extra",
                "<none>",
                SpanCore(extra),
                $"Unexpected {kind} relation is present.");
        }
    }

    private static string SpanCore(SpanRelation relation) =>
        $"{relation.Kind}:{Anchor(relation.From)}->{Anchor(relation.To)}:"
        + $"{relation.Type ?? "-"}:{relation.Direction ?? "-"}:"
        + $"{relation.Size?.ToString() ?? "-"}";

    private static void CompareOptionalRelationPresentation(
        ICollection<CanonicalDiffIssue> issues,
        string kind,
        string core,
        string property,
        string? expected,
        string? actual)
    {
        if (string.IsNullOrWhiteSpace(expected)
            || string.Equals(
                expected,
                actual,
                StringComparison.Ordinal))
        {
            return;
        }

        AddRelationIssue(
            issues,
            kind,
            property,
            expected!,
            actual ?? "<null>",
            $"{kind} {property} presentation differs.",
            core);
    }

    private static void CompareRelationSet(
        string kind,
        IEnumerable<string> expected,
        IEnumerable<string> actual,
        ICollection<CanonicalDiffIssue> issues)
    {
        var expectedBag = ToBag(expected);
        var actualBag = ToBag(actual);
        var all = expectedBag.Keys
            .Concat(actualBag.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal);

        foreach (var signature in all)
        {
            var expectedCount = expectedBag.GetValueOrDefault(signature);
            var actualCount = actualBag.GetValueOrDefault(signature);

            for (var index = actualCount; index < expectedCount; index++)
            {
                AddRelationIssue(
                    issues,
                    kind,
                    "missing",
                    signature,
                    "<missing>",
                    $"Expected {kind} relation is missing.");
            }

            for (var index = expectedCount; index < actualCount; index++)
            {
                AddRelationIssue(
                    issues,
                    kind,
                    "extra",
                    "<none>",
                    signature,
                    $"Unexpected {kind} relation is present.");
            }
        }
    }

    private static void AddRelationIssue(
        ICollection<CanonicalDiffIssue> issues,
        string kind,
        string suffix,
        string expected,
        string actual,
        string message,
        string? signatureForLocation = null)
    {
        var signature = signatureForLocation
            ?? (expected == "<none>"
                ? actual
                : expected);
        var measure = ExtractMeasure(signature);
        var rootCause =
            $"relation:{kind}:m{measure?.ToString() ?? "-"}";
        var summary =
            $"{kind} relation mismatch"
            + (measure is null
                ? string.Empty
                : $" — m{measure}");

        issues.Add(new CanonicalDiffIssue(
            CanonicalDiffCategory.Relations,
            $"relation.{kind}.{suffix}",
            null,
            measure,
            null,
            null,
            expected,
            actual,
            message,
            rootCause,
            summary));
    }

    private sealed record PlacedRelation(
        string Core,
        string? Placement);

    private sealed record TupletComparison(
        string Core,
        bool? Bracket);

    private static Dictionary<string, int> ToBag(
        IEnumerable<string> values) =>
        values
            .GroupBy(value => value, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Count(),
                StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, string> BuildEventAddressMap(
        CanonicalNotation score) =>
        score.Parts
            .SelectMany(part => part.Measures.SelectMany(measure =>
                measure.Events.Select(ev => new
                {
                    ev.Id,
                    Address = $"{part.Id}/m{measure.Number}@{ev.At}/s{EventStaff(ev)?.ToString() ?? "-"}/{ev.Type}/{ContentSignature(ev)}"
                })))
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.First().Address,
                StringComparer.Ordinal);

    private static string Address(
        IReadOnlyDictionary<string, string> map,
        string id) =>
        map.GetValueOrDefault(id, $"<unresolved:{id}>");

    private static string SpanSignature(SpanRelation relation) =>
        $"{relation.Kind}:{Anchor(relation.From)}->{Anchor(relation.To)}:"
        + $"{relation.Type ?? "-"}:{relation.Direction ?? "-"}:"
        + $"{relation.Size?.ToString() ?? "-"}:{relation.Line?.ToString() ?? "-"}:"
        + $"{relation.StartMark?.ToString() ?? "-"}:{relation.Placement ?? "-"}";

    private static string Anchor(TimeAnchor anchor) =>
        $"m{anchor.Measure}@{anchor.At}/s{anchor.Staff}";

    private static int? ExtractMeasure(string signature)
    {
        var index = signature.IndexOf("/m", StringComparison.Ordinal);
        if (index >= 0)
        {
            index += 2;
        }
        else
        {
            index = signature.IndexOf('m');
            if (index >= 0) index++;
        }

        if (index < 0 || index >= signature.Length)
            return null;

        var end = index;
        while (end < signature.Length && char.IsDigit(signature[end]))
            end++;

        return int.TryParse(signature[index..end], out var value)
            ? value
            : null;
    }

    private static string TimingRootCauseKey(
        string part,
        int measure,
        CanonicalEvent expected) =>
        $"timeline:{part}:m{measure}:s{EventStaff(expected)?.ToString() ?? "-"}:"
        + $"v{expected.Voice?.ToString() ?? "-"}";

    private static string TimingRootCauseSummary(
        string part,
        int measure,
        CanonicalEvent expected) =>
        $"Voice/event timeline mismatch — {part} / m{measure} / "
        + $"staff {EventStaff(expected)?.ToString() ?? "-"} / "
        + $"voice {expected.Voice?.ToString() ?? "-"}";

    private static bool SameCoordinate(
        CanonicalEvent expected,
        CanonicalEvent actual) =>
        string.Equals(expected.Type, actual.Type, StringComparison.Ordinal)
        && string.Equals(expected.At, actual.At, StringComparison.Ordinal)
        && EventStaff(expected) == EventStaff(actual);

    private static bool SameSemanticContent(
        CanonicalEvent expected,
        CanonicalEvent actual)
    {
        if (!string.Equals(
                expected.Type,
                actual.Type,
                StringComparison.Ordinal))
        {
            return false;
        }

        return expected.Type switch
        {
            "chord" => PitchBag(expected.Notes)
                .SequenceEqual(
                    PitchBag(actual.Notes),
                    StringComparer.Ordinal),
            "rest" => string.Equals(
                expected.Duration,
                actual.Duration,
                StringComparison.Ordinal),
            "dynamic" or "navigation" => Normalize(expected.Value)
                == Normalize(actual.Value),
            "text" => Normalize(expected.Text)
                == Normalize(actual.Text),
            "tempo" => expected.Bpm == actual.Bpm
                && Normalize(expected.BeatUnit)
                    == Normalize(actual.BeatUnit),
            _ => Normalize(expected.Value ?? expected.Text)
                == Normalize(actual.Value ?? actual.Text)
        };
    }

    private static string[] PitchBag(
        IReadOnlyList<CanonicalNote>? notes) =>
        (notes ?? [])
            .Select(note => note.Pitch)
            .OrderBy(pitch => pitch, StringComparer.Ordinal)
            .ToArray();

    private static double Similarity(
        CanonicalEvent expected,
        CanonicalEvent actual)
    {
        if (!string.Equals(
                expected.Type,
                actual.Type,
                StringComparison.Ordinal))
        {
            return 0;
        }

        var score = 0.25;

        if (string.Equals(expected.Duration, actual.Duration, StringComparison.Ordinal))
            score += 0.15;
        if (expected.Voice == actual.Voice)
            score += 0.10;
        if (EventStaff(expected) == EventStaff(actual))
            score += 0.10;

        if (expected.Type == "chord")
        {
            var left = NoteContent(expected.Notes);
            var right = NoteContent(actual.Notes);
            var union = left.Union(right, StringComparer.Ordinal).Count();
            var intersection = left.Intersect(right, StringComparer.Ordinal).Count();
            score += union == 0
                ? 0.40
                : 0.40 * intersection / union;
            return score;
        }

        if (expected.Type == "text")
        {
            if (Normalize(expected.Text) == Normalize(actual.Text))
                score += 0.40;
        }
        else if (expected.Type is "dynamic" or "navigation")
        {
            if (Normalize(expected.Value) == Normalize(actual.Value))
                score += 0.40;
        }
        else if (expected.Type == "tempo")
        {
            if (expected.Bpm == actual.Bpm)
                score += 0.20;
            if (Normalize(expected.BeatUnit) == Normalize(actual.BeatUnit))
                score += 0.20;
        }
        else
        {
            score += 0.20;
        }

        return score;
    }

    private static double FractionDistance(
        string left,
        string right)
    {
        try
        {
            var a = Fraction.Parse(left);
            var b = Fraction.Parse(right);
            return Math.Abs(
                a.Numerator / (double)a.Denominator
                - b.Numerator / (double)b.Denominator);
        }
        catch
        {
            return double.PositiveInfinity;
        }
    }

    private static HashSet<string> NoteContent(
        IReadOnlyList<CanonicalNote>? notes) =>
        (notes ?? [])
            .Select(note =>
                $"{note.Pitch}@s{note.Staff?.ToString() ?? "-"}")
            .ToHashSet(StringComparer.Ordinal);

    private static CanonicalDiffCategory CategoryFor(
        CanonicalEvent ev) =>
        ev.Type switch
        {
            "chord" => CanonicalDiffCategory.Pitch,
            "rest" => CanonicalDiffCategory.Rhythm,
            "text" or "tempo" => CanonicalDiffCategory.Text,
            _ => CanonicalDiffCategory.Notation
        };

    private static int? EventStaff(CanonicalEvent ev)
    {
        if (ev.Staff is not null)
            return ev.Staff;

        var staffs = (ev.Notes ?? [])
            .Select(note => note.Staff)
            .Where(staff => staff is not null)
            .Distinct()
            .ToArray();

        return staffs.Length == 1
            ? staffs[0]
            : null;
    }

    private static string DescribeEvent(CanonicalEvent ev) =>
        $"{ev.Type} {ContentSignature(ev)}";

    private static string ContentSignature(CanonicalEvent ev) =>
        ev.Type switch
        {
            "chord" => string.Join(
                "+",
                (ev.Notes ?? [])
                    .OrderBy(note => note.Staff)
                    .ThenBy(note => PitchValue(note.Pitch))
                    .Select(NoteSignature)),
            "rest" => $"rest/{ev.Duration ?? "-"}",
            "text" => $"'{ev.Text ?? ""}'/{ev.TextRole ?? "-"}",
            "dynamic" => ev.Value ?? "-",
            "tempo" => $"{ev.BeatUnit ?? "-"}={ev.Bpm?.ToString() ?? "-"}",
            "navigation" => ev.Value ?? "-",
            _ => ev.Value ?? ev.Text ?? "-"
        };

    private static string NoteSignature(CanonicalNote note) =>
        $"{note.Pitch}@s{note.Staff?.ToString() ?? "-"}";

    private static int PitchValue(string pitch)
    {
        if (string.IsNullOrWhiteSpace(pitch))
            return int.MaxValue;

        var step = pitch[0] switch
        {
            'C' => 0,
            'D' => 2,
            'E' => 4,
            'F' => 5,
            'G' => 7,
            'A' => 9,
            'B' => 11,
            _ => 0
        };

        var octaveIndex = -1;
        for (var index = 1; index < pitch.Length; index++)
        {
            if (char.IsDigit(pitch[index]) || pitch[index] == '-')
            {
                octaveIndex = index;
                break;
            }
        }

        if (octaveIndex < 0
            || !int.TryParse(pitch[octaveIndex..], out var octave))
        {
            return int.MaxValue;
        }

        var accidental = pitch[1..octaveIndex];
        var alter = accidental switch
        {
            "bb" => -2,
            "b" => -1,
            "#" => 1,
            "##" => 2,
            _ => 0
        };

        return (octave + 1) * 12 + step + alter;
    }

    private static string? AccidentalSignature(Accidental? value) =>
        value is null
            ? null
            : $"{value.Type}:{value.Cautionary}:{value.Editorial}:{value.Parentheses}:{value.Bracket}";

    private static IEnumerable<string> TechnicalSignatures(
        IReadOnlyList<TechnicalMark>? values) =>
        (values ?? [])
            .Select(value =>
                $"{value.Type}:{value.Value ?? "-"}:{value.Placement ?? "-"}");

    private static IEnumerable<string> MarkSignatures(
        IReadOnlyList<NotationMark>? values) =>
        (values ?? [])
            .Select(value =>
                $"{value.Type}:{value.Subtype ?? "-"}:{value.Placement ?? "-"}");

    private static IEnumerable<string> ClefSignatures(
        IEnumerable<Clef>? values) =>
        (values ?? [])
            .Select(value =>
                $"{value.Staff}:{value.Sign}:{value.Line}:{value.OctaveChange?.ToString() ?? "-"}");

    private static string? RepeatSignature(RepeatMark? value) =>
        value is null
            ? null
            : $"{value.Direction}:{value.Times?.ToString() ?? "-"}";

    private static void CompareScalar(
        ICollection<CanonicalDiffIssue> issues,
        CanonicalDiffCategory category,
        string code,
        string? part,
        int? measure,
        int? staff,
        string? at,
        string? expected,
        string? actual,
        string message,
        bool expectedNullIsWildcard = false,
        string? rootCause = null,
        string? rootCauseSummary = null)
    {
        if (expectedNullIsWildcard
            && string.IsNullOrWhiteSpace(expected))
        {
            return;
        }

        if (Normalize(expected) == Normalize(actual))
            return;

        issues.Add(new CanonicalDiffIssue(
            category,
            code,
            part,
            measure,
            staff,
            at,
            expected ?? "<null>",
            actual ?? "<null>",
            message,
            rootCause,
            rootCauseSummary));
    }

    private static void CompareSet(
        ICollection<CanonicalDiffIssue> issues,
        CanonicalDiffCategory category,
        string code,
        string? part,
        int? measure,
        int? staff,
        string? at,
        IEnumerable<string> expected,
        IEnumerable<string> actual,
        string message)
    {
        var left = expected
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var right = actual
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        if (left.SequenceEqual(right, StringComparer.Ordinal))
            return;

        issues.Add(new CanonicalDiffIssue(
            category,
            code,
            part,
            measure,
            staff,
            at,
            string.Join(", ", left),
            string.Join(", ", right),
            message));
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : string.Join(
                " ",
                value
                    .Trim()
                    .Split(
                        (char[]?)null,
                        StringSplitOptions.RemoveEmptyEntries))
                .ToLowerInvariant();

    private static void Apply(
        MeasureAttributes? attributes,
        AttributeState state)
    {
        if (attributes is null)
            return;

        if (attributes.Time is not null)
            state.Time = attributes.Time;
        if (attributes.Key is not null)
            state.Key = attributes.Key;
        if (attributes.Staves is not null)
            state.Staves = attributes.Staves;
        if (attributes.Clefs is not null)
        {
            foreach (var clef in attributes.Clefs)
            {
                state.Clefs[clef.Staff] = clef;
            }
        }
    }

    private sealed class AttributeState
    {
        public TimeSignature? Time { get; set; }
        public KeySignature? Key { get; set; }
        public int? Staves { get; set; }
        public Dictionary<int, Clef> Clefs { get; } = [];
    }

    private sealed record PartPair(
        Part Expected,
        Part Actual);

    private sealed record IndexedEvent(
        int Index,
        CanonicalEvent Event);

    private sealed record IndexedNote(
        int Index,
        CanonicalNote Note);
}
