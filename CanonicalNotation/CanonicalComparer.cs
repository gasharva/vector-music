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
    string Message)
{
    [JsonIgnore]
    public string Location
    {
        get
        {
            var pieces = new List<string>();
            if (!string.IsNullOrWhiteSpace(Part)) pieces.Add(Part!);
            if (Measure is not null) pieces.Add($"m{Measure}");
            if (Staff is not null) pieces.Add($"staff {Staff}");
            if (!string.IsNullOrWhiteSpace(At)) pieces.Add($"at {At}");
            return pieces.Count == 0
                ? "score"
                : string.Join(" / ", pieces);
        }
    }
}

public sealed record CanonicalDiffReport(
    IReadOnlyList<CanonicalDiffIssue> Issues)
{
    public bool IsEqual => Issues.Count == 0;

    public IReadOnlyDictionary<CanonicalDiffCategory, int> Counts =>
        Issues
            .GroupBy(issue => issue.Category)
            .ToDictionary(group => group.Key, group => group.Count());

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Canonical semantic diff");
        sb.AppendLine();
        sb.AppendLine(IsEqual
            ? "No semantic differences."
            : $"{Issues.Count} semantic difference(s).");
        sb.AppendLine();

        foreach (var category in Enum.GetValues<CanonicalDiffCategory>())
        {
            var categoryIssues = Issues
                .Where(issue => issue.Category == category)
                .OrderBy(issue => issue.Part)
                .ThenBy(issue => issue.Measure)
                .ThenBy(issue => FractionValue(issue.At))
                .ThenBy(issue => issue.Staff)
                .ThenBy(issue => issue.Code)
                .ToArray();

            if (categoryIssues.Length == 0)
            {
                continue;
            }

            sb.AppendLine($"## {category} ({categoryIssues.Length})");
            sb.AppendLine();

            foreach (var locationGroup in categoryIssues
                         .GroupBy(issue => issue.Location))
            {
                sb.AppendLine($"### {locationGroup.Key}");
                sb.AppendLine();

                foreach (var issue in locationGroup)
                {
                    sb.Append("- **")
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
            ClefSignatures(expected.Clefs),
            ClefSignatures(actual.Clefs),
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

                issues.Add(new CanonicalDiffIssue(
                    CanonicalDiffCategory.Rhythm,
                    "event.onset",
                    part,
                    expectedMeasure.Number,
                    EventStaff(expected),
                    expected.At,
                    expected.At,
                    moved.Item.Event.At,
                    $"{DescribeEvent(expected)} was found, but at the wrong musical onset."));

                CompareEvent(
                    part,
                    expectedMeasure.Number,
                    expected,
                    moved.Item.Event,
                    issues);
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
        ICollection<CanonicalDiffIssue> issues)
    {
        var staff = EventStaff(expected) ?? EventStaff(actual);
        var at = expected.At;

        CompareScalar(issues, CanonicalDiffCategory.Rhythm, "event.duration",
            part, measure, staff, at,
            expected.Duration, actual.Duration,
            $"{DescribeEvent(expected)} duration differs.");

        CompareScalar(issues, CanonicalDiffCategory.Rhythm, "event.voice",
            part, measure, staff, at,
            expected.Voice?.ToString(), actual.Voice?.ToString(),
            $"{DescribeEvent(expected)} voice differs.");

        CompareScalar(issues, CanonicalDiffCategory.Rhythm, "event.staff",
            part, measure, staff, at,
            expected.Staff?.ToString(), actual.Staff?.ToString(),
            $"{DescribeEvent(expected)} staff differs.");

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
                "Text semantic role differs.");
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
            $"{DescribeEvent(expected)} placement differs.");
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

        CompareSet(
            issues,
            CanonicalDiffCategory.Notation,
            "note.technical",
            part,
            measure,
            staff,
            at,
            TechnicalSignatures(expected.Technical),
            TechnicalSignatures(actual.Technical),
            $"Technical marks differ for {expected.Pitch}.");
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

        CompareSet(issues, CanonicalDiffCategory.Notation,
            "notation.articulations",
            part, measure, staff, at,
            MarkSignatures(expected?.Articulations),
            MarkSignatures(actual?.Articulations),
            "Articulations differ.");
        CompareSet(issues, CanonicalDiffCategory.Notation,
            "notation.ornaments",
            part, measure, staff, at,
            MarkSignatures(expected?.Ornaments),
            MarkSignatures(actual?.Ornaments),
            "Ornaments differ.");
        CompareSet(issues, CanonicalDiffCategory.Notation,
            "notation.fermatas",
            part, measure, staff, at,
            MarkSignatures(expected?.Fermatas),
            MarkSignatures(actual?.Fermatas),
            "Fermatas differ.");
    }

    private static void CompareRelations(
        CanonicalNotation expected,
        CanonicalNotation actual,
        ICollection<CanonicalDiffIssue> issues)
    {
        var expectedEvents = BuildEventAddressMap(expected);
        var actualEvents = BuildEventAddressMap(actual);

        CompareRelationSet("beam",
            expected.Relations.Beams.Select(relation =>
                $"L{relation.Level}:{relation.Hook ?? "-"}:{string.Join(">", relation.Events.Select(id => Address(expectedEvents, id)))}"),
            actual.Relations.Beams.Select(relation =>
                $"L{relation.Level}:{relation.Hook ?? "-"}:{string.Join(">", relation.Events.Select(id => Address(actualEvents, id)))}"),
            issues);

        CompareRelationSet("tie",
            expected.Relations.Ties.Select(relation =>
                $"{Address(expectedEvents, relation.From.Event)}[{relation.From.Note}]->{Address(expectedEvents, relation.To.Event)}[{relation.To.Note}]:{relation.Placement ?? "-"}"),
            actual.Relations.Ties.Select(relation =>
                $"{Address(actualEvents, relation.From.Event)}[{relation.From.Note}]->{Address(actualEvents, relation.To.Event)}[{relation.To.Note}]:{relation.Placement ?? "-"}"),
            issues);

        CompareRelationSet("slur",
            expected.Relations.Slurs.Select(relation =>
                $"{Address(expectedEvents, relation.From)}->{Address(expectedEvents, relation.To)}:{relation.Placement ?? "-"}"),
            actual.Relations.Slurs.Select(relation =>
                $"{Address(actualEvents, relation.From)}->{Address(actualEvents, relation.To)}:{relation.Placement ?? "-"}"),
            issues);

        CompareRelationSet("tuplet",
            expected.Relations.Tuplets.Select(relation =>
                $"{relation.Actual}:{relation.Normal}:{relation.Bracket?.ToString() ?? "-"}:{string.Join(">", relation.Events.Select(id => Address(expectedEvents, id)))}"),
            actual.Relations.Tuplets.Select(relation =>
                $"{relation.Actual}:{relation.Normal}:{relation.Bracket?.ToString() ?? "-"}:{string.Join(">", relation.Events.Select(id => Address(actualEvents, id)))}"),
            issues);

        CompareRelationSet("arpeggio",
            expected.Relations.Arpeggios.Select(relation =>
                $"{relation.Direction ?? "-"}:{string.Join("+", relation.Events.Select(id => Address(expectedEvents, id)).OrderBy(x => x))}"),
            actual.Relations.Arpeggios.Select(relation =>
                $"{relation.Direction ?? "-"}:{string.Join("+", relation.Events.Select(id => Address(actualEvents, id)).OrderBy(x => x))}"),
            issues);

        CompareRelationSet("hairpin",
            expected.Relations.Hairpins.Select(SpanSignature),
            actual.Relations.Hairpins.Select(SpanSignature),
            issues);
        CompareRelationSet("pedal",
            expected.Relations.Pedals.Select(SpanSignature),
            actual.Relations.Pedals.Select(SpanSignature),
            issues);
        CompareRelationSet("octave-shift",
            expected.Relations.OctaveShifts.Select(SpanSignature),
            actual.Relations.OctaveShifts.Select(SpanSignature),
            issues);
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
                issues.Add(new CanonicalDiffIssue(
                    CanonicalDiffCategory.Relations,
                    $"relation.{kind}.missing",
                    null,
                    ExtractMeasure(signature),
                    null,
                    null,
                    signature,
                    "<missing>",
                    $"Expected {kind} relation is missing."));
            }

            for (var index = expectedCount; index < actualCount; index++)
            {
                issues.Add(new CanonicalDiffIssue(
                    CanonicalDiffCategory.Relations,
                    $"relation.{kind}.extra",
                    null,
                    ExtractMeasure(signature),
                    null,
                    null,
                    "<none>",
                    signature,
                    $"Unexpected {kind} relation is present."));
            }
        }
    }

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
                    Address = $"{part.Id}/m{measure.Number}@{ev.At}/v{ev.Voice?.ToString() ?? "-"}/s{EventStaff(ev)?.ToString() ?? "-"}/{ev.Type}/{ContentSignature(ev)}"
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

    private static bool SameCoordinate(
        CanonicalEvent expected,
        CanonicalEvent actual) =>
        string.Equals(expected.Type, actual.Type, StringComparison.Ordinal)
        && string.Equals(expected.At, actual.At, StringComparison.Ordinal)
        && expected.Voice == actual.Voice
        && EventStaff(expected) == EventStaff(actual);

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
        IReadOnlyList<Clef>? values) =>
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
        string message)
    {
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
            message));
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
