from pathlib import Path


def replace_once(path: str, old: str, new: str, label: str) -> None:
    p = Path(path)
    text = p.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected one match, got {count}")
    p.write_text(text.replace(old, new, 1), encoding="utf-8")


# 1. Grace-note semantics: accepted noteheads get a second, conservative size split.
Path("SemanticInterpreter/GraceNotePass.cs").write_text(r'''namespace SvgMusic.Semantics;

public sealed record GraceNoteFact(
    int MeasureNumber,
    int Staff,
    string NoteheadId,
    double NormalizedSize,
    double RegularNoteheadMedian,
    double Threshold,
    double Confidence,
    string Reason,
    IReadOnlyList<string> SourceShapeIds)
    : SemanticFact(
        "GraceNotePass",
        Reason,
        SourceShapeIds);

public sealed record GraceNoteSizeProfile(
    bool HasGraceCluster,
    double? Threshold,
    int GraceCount,
    int RegularCount,
    double? GraceMedian,
    double? RegularMedian,
    IReadOnlyList<NoteheadFact> RankedNoteheads);

public sealed record GraceNoteAnalysisResult(
    GraceNoteSizeProfile SizeProfile,
    IReadOnlyList<GraceNoteFact> GraceNotes);

/// <summary>
/// Tags a reduced-size subcluster inside already accepted noteheads.
/// Dots have already been removed by NoteheadPass, so this is deliberately a
/// second split: dot-sized ellipses never reach this pass, while grace heads keep
/// using the normal pitch/stem/beam pipeline.
/// </summary>
public sealed class GraceNotePass : ISemanticPass
{
    private const double MinimumGapRatio = 1.25;
    private const double MaximumGraceToRegularMedianRatio = 0.82;
    private const double MinimumGraceMedian = 0.60;
    private const double MaximumGraceMedian = 0.95;
    private const double MinimumRegularMedian = 0.90;
    private const double MaximumRegularMedian = 1.60;
    private const double MaximumGraceFraction = 0.25;
    private const double MaximumGraceSpreadRatio = 1.18;

    public string Name => nameof(GraceNotePass);

    public GraceNoteAnalysisResult? LastAnalysis { get; private set; }

    public void Run(
        SemanticDocument document,
        SemanticFacts facts)
    {
        var ranked = facts
            .OfType<NoteheadFact>()
            .Where(notehead => notehead.NormalizedSize > 0)
            .OrderBy(notehead => notehead.NormalizedSize)
            .ThenBy(notehead => notehead.ShapeId, StringComparer.Ordinal)
            .ToArray();

        var profile = Analyze(ranked);
        var graceNotes = new List<GraceNoteFact>();

        if (profile.HasGraceCluster
            && profile.Threshold is not null
            && profile.RegularMedian is not null)
        {
            foreach (var notehead in ranked
                         .Where(notehead => notehead.NormalizedSize < profile.Threshold.Value))
            {
                var gapRatio = profile.RegularMedian.Value
                    / Math.Max(notehead.NormalizedSize, 0.001);
                var confidence = 0.88 + Math.Clamp(
                    (gapRatio - MinimumGapRatio) / 0.50,
                    0,
                    1) * 0.11;
                var reason =
                    $"accepted notehead belongs to reduced-size notehead cluster; "
                    + $"size={notehead.NormalizedSize:F3}sp; "
                    + $"regular-median={profile.RegularMedian.Value:F3}sp; "
                    + $"threshold={profile.Threshold.Value:F3}sp";

                var fact = new GraceNoteFact(
                    notehead.MeasureNumber,
                    notehead.Staff,
                    notehead.ShapeId,
                    notehead.NormalizedSize,
                    profile.RegularMedian.Value,
                    profile.Threshold.Value,
                    Math.Clamp(confidence, 0, 0.99),
                    reason,
                    notehead.SourceShapeIds);
                facts.Add(fact);
                graceNotes.Add(fact);
            }
        }

        LastAnalysis = new GraceNoteAnalysisResult(profile, graceNotes);
        facts.AddTrace(
            $"GraceNotePass: noteheads={ranked.Length}; "
            + $"cluster={profile.HasGraceCluster}; grace={graceNotes.Count}; "
            + $"threshold={(profile.Threshold is null ? "-" : profile.Threshold.Value.ToString("F3", System.Globalization.CultureInfo.InvariantCulture))}; "
            + $"grace-median={(profile.GraceMedian is null ? "-" : profile.GraceMedian.Value.ToString("F3", System.Globalization.CultureInfo.InvariantCulture))}; "
            + $"regular-median={(profile.RegularMedian is null ? "-" : profile.RegularMedian.Value.ToString("F3", System.Globalization.CultureInfo.InvariantCulture))}");
    }

    private static GraceNoteSizeProfile Analyze(
        IReadOnlyList<NoteheadFact> ranked)
    {
        if (ranked.Count < 3)
        {
            return NoSplit(ranked);
        }

        SplitCandidate? best = null;

        for (var splitIndex = 1; splitIndex < ranked.Count; splitIndex++)
        {
            var lower = ranked.Take(splitIndex).ToArray();
            var upper = ranked.Skip(splitIndex).ToArray();

            if (upper.Length < 2
                || lower.Length / (double)ranked.Count > MaximumGraceFraction)
            {
                continue;
            }

            var graceMedian = Median(lower.Select(notehead => notehead.NormalizedSize));
            var regularMedian = Median(upper.Select(notehead => notehead.NormalizedSize));
            var lowerMin = lower.Min(notehead => notehead.NormalizedSize);
            var lowerMax = lower.Max(notehead => notehead.NormalizedSize);
            var gapRatio = upper[0].NormalizedSize / Math.Max(lower[^1].NormalizedSize, 0.001);
            var spreadRatio = lowerMax / Math.Max(lowerMin, 0.001);

            if (gapRatio < MinimumGapRatio
                || graceMedian < MinimumGraceMedian
                || graceMedian > MaximumGraceMedian
                || regularMedian < MinimumRegularMedian
                || regularMedian > MaximumRegularMedian
                || graceMedian > regularMedian * MaximumGraceToRegularMedianRatio
                || spreadRatio > MaximumGraceSpreadRatio)
            {
                continue;
            }

            var score = Math.Log(gapRatio)
                - Math.Log(Math.Max(spreadRatio, 1.0)) * 0.25;
            if (best is null || score > best.Score)
            {
                best = new SplitCandidate(
                    splitIndex,
                    graceMedian,
                    regularMedian,
                    score);
            }
        }

        if (best is null)
        {
            return NoSplit(ranked);
        }

        var lowerEdge = ranked[best.SplitIndex - 1].NormalizedSize;
        var upperEdge = ranked[best.SplitIndex].NormalizedSize;
        var threshold = Math.Sqrt(lowerEdge * upperEdge);

        return new GraceNoteSizeProfile(
            true,
            threshold,
            best.SplitIndex,
            ranked.Count - best.SplitIndex,
            best.GraceMedian,
            best.RegularMedian,
            ranked);
    }

    private static GraceNoteSizeProfile NoSplit(
        IReadOnlyList<NoteheadFact> ranked)
    {
        var median = ranked.Count == 0
            ? (double?)null
            : Median(ranked.Select(notehead => notehead.NormalizedSize));
        return new GraceNoteSizeProfile(
            false,
            null,
            0,
            ranked.Count,
            null,
            median,
            ranked);
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.OrderBy(value => value).ToArray();
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 1
            ? ordered[middle]
            : (ordered[middle - 1] + ordered[middle]) / 2.0;
    }

    private sealed record SplitCandidate(
        int SplitIndex,
        double GraceMedian,
        double RegularMedian,
        double Score);
}
''', encoding="utf-8")


# 2. GraceNotePass runs immediately after noteheads, before anything rhythmic.
replace_once(
    "SemanticInterpreter/SemanticPipeline.cs",
    """        var materialized = passes.ToList();\n\n        // Rest facts must exist before DotAttachmentPass: augmentation dots can\n""",
    """        var materialized = passes.ToList();\n\n        // Grace heads are not a parallel note-recognition path. NoteheadPass first\n        // accepts every notehead; this pass only tags a reduced-size subcluster so\n        // pitch, stems, beams and accidentals keep using the normal facts.\n        if (materialized.Any(pass => pass is NoteheadPass)\n            && materialized.All(pass => pass is not GraceNotePass))\n        {\n            var noteheadIndex = materialized.FindLastIndex(pass => pass is NoteheadPass);\n            materialized.Insert(noteheadIndex + 1, new GraceNotePass());\n        }\n\n        // Rest facts must exist before DotAttachmentPass: augmentation dots can\n""",
    "SemanticPipeline grace insertion")


# 3. Duration keeps the written note type but grace notes consume zero metric time.
replace_once(
    "SemanticInterpreter/DurationPass.cs",
    """        var tuplets = facts.OfType<TupletFact>().ToArray();\n        var dots = facts.OfType<DotAttachmentFact>().ToArray();\n\n        var decisions = new List<DurationFact>(noteheads.Length);\n""",
    """        var tuplets = facts.OfType<TupletFact>().ToArray();\n        var dots = facts.OfType<DotAttachmentFact>().ToArray();\n        var graceNoteheadIds = facts\n            .OfType<GraceNoteFact>()\n            .Select(grace => grace.NoteheadId)\n            .ToHashSet(StringComparer.Ordinal);\n\n        var decisions = new List<DurationFact>(noteheads.Length);\n""",
    "DurationPass grace ids")

replace_once(
    "SemanticInterpreter/DurationPass.cs",
    """            var effectiveDuration = tuplet is null\n                ? dottedDuration\n                : Multiply(\n                    dottedDuration,\n                    tuplet.NormalNotes,\n                    tuplet.ActualNotes);\n\n            var confidenceValues = new List<double>\n""",
    """            var nominalEffectiveDuration = tuplet is null\n                ? dottedDuration\n                : Multiply(\n                    dottedDuration,\n                    tuplet.NormalNotes,\n                    tuplet.ActualNotes);\n            var isGrace = graceNoteheadIds.Contains(notehead.ShapeId);\n            var effectiveDuration = isGrace\n                ? Fraction.Zero\n                : nominalEffectiveDuration;\n\n            var confidenceValues = new List<double>\n""",
    "DurationPass zero grace duration")

replace_once(
    "SemanticInterpreter/DurationPass.cs",
    """            var tupletText = tuplet is null\n                ? \"no tuplet scaling\"\n                : $\"tuplet={tuplet.ActualNotes}:{tuplet.NormalNotes}\";\n\n            decisions.Add(new DurationFact(\n""",
    """            var tupletText = tuplet is null\n                ? \"no tuplet scaling\"\n                : $\"tuplet={tuplet.ActualNotes}:{tuplet.NormalNotes}\";\n            var graceText = isGrace\n                ? \"grace note -> zero metrical duration\"\n                : \"metric note\";\n\n            decisions.Add(new DurationFact(\n""",
    "DurationPass grace reason")

replace_once(
    "SemanticInterpreter/DurationPass.cs",
    """                $\"{evidence}; {dotText}; {tupletText}; \"\n                + $\"base={baseDuration}; effective={effectiveDuration}\",\n""",
    """                $\"{evidence}; {dotText}; {tupletText}; {graceText}; \"\n                + $\"base={baseDuration}; effective={effectiveDuration}\",\n""",
    "DurationPass grace reason text")

replace_once(
    "SemanticInterpreter/DurationPass.cs",
    """            + $\"subdivided={decisions.Count(decision => decision.SubdivisionLevel > 0)}; \"\n            + $\"filled-without-stem-fallback={decisions.Count(decision => decision.Reason.Contains(\"quarter fallback\", StringComparison.Ordinal))}\");\n""",
    """            + $\"subdivided={decisions.Count(decision => decision.SubdivisionLevel > 0)}; \"\n            + $\"grace={decisions.Count(decision => graceNoteheadIds.Contains(decision.NoteheadId))}; \"\n            + $\"filled-without-stem-fallback={decisions.Count(decision => decision.Reason.Contains(\"quarter fallback\", StringComparison.Ordinal))}\");\n""",
    "DurationPass grace trace")


# 4. Canonical event can explicitly carry non-metrical grace semantics.
replace_once(
    "CanonicalNotation/CanonicalNotation.cs",
    """    public string? Duration { get; init; }\n    public List<CanonicalNote>? Notes { get; init; }\n""",
    """    public string? Duration { get; init; }\n    public bool? Grace { get; init; }\n    public List<CanonicalNote>? Notes { get; init; }\n""",
    "CanonicalEvent grace property")


# 5. Project GraceNoteFact onto canonical events, preserving nominal note type.
replace_once(
    "SemanticInterpreter/CanonicalNotationBuilder.cs",
    """        var voices = facts.OfType<VoiceFact>().ToArray();\n        var onsets = facts.OfType<OnsetFact>().ToArray();\n\n        if (noteheads.Length > 0 && durations.Length == 0)\n""",
    """        var voices = facts.OfType<VoiceFact>().ToArray();\n        var onsets = facts.OfType<OnsetFact>().ToArray();\n        var graceNoteheadIds = facts\n            .OfType<GraceNoteFact>()\n            .Select(grace => grace.NoteheadId)\n            .ToHashSet(StringComparer.Ordinal);\n\n        if (noteheads.Length > 0 && durations.Length == 0)\n""",
    "CanonicalBuilder grace ids")

replace_once(
    "SemanticInterpreter/CanonicalNotationBuilder.cs",
    """                voices,\n                onsets,\n                facts,\n""",
    """                voices,\n                onsets,\n                graceNoteheadIds,\n                facts,\n""",
    "CanonicalBuilder grace arg call")

replace_once(
    "SemanticInterpreter/CanonicalNotationBuilder.cs",
    """        IReadOnlyList<VoiceFact> allVoices,\n        IReadOnlyList<OnsetFact> allOnsets,\n        SemanticFacts facts,\n""",
    """        IReadOnlyList<VoiceFact> allVoices,\n        IReadOnlyList<OnsetFact> allOnsets,\n        IReadOnlySet<string> graceNoteheadIds,\n        SemanticFacts facts,\n""",
    "CanonicalBuilder grace arg signature")

replace_once(
    "SemanticInterpreter/CanonicalNotationBuilder.cs",
    """                pitchesByNotehead,\n                durationsByNotehead,\n                facts);\n            drafts.Add(draft);\n""",
    """                pitchesByNotehead,\n                durationsByNotehead,\n                graceNoteheadIds,\n                facts);\n            drafts.Add(draft);\n""",
    "CanonicalBuilder chord BuildDraft grace arg")

replace_once(
    "SemanticInterpreter/CanonicalNotationBuilder.cs",
    """                pitchesByNotehead,\n                durationsByNotehead,\n                facts));\n            noteheadToEventId[notehead.ShapeId] = eventId;\n""",
    """                pitchesByNotehead,\n                durationsByNotehead,\n                graceNoteheadIds,\n                facts));\n            noteheadToEventId[notehead.ShapeId] = eventId;\n""",
    "CanonicalBuilder note BuildDraft grace arg")

replace_once(
    "SemanticInterpreter/CanonicalNotationBuilder.cs",
    """        IReadOnlyDictionary<string, PitchFact> pitchesByNotehead,\n        IReadOnlyDictionary<string, DurationFact> durationsByNotehead,\n        SemanticFacts facts)\n""",
    """        IReadOnlyDictionary<string, PitchFact> pitchesByNotehead,\n        IReadOnlyDictionary<string, DurationFact> durationsByNotehead,\n        IReadOnlySet<string> graceNoteheadIds,\n        SemanticFacts facts)\n""",
    "CanonicalBuilder BuildDraft grace signature")

replace_once(
    "SemanticInterpreter/CanonicalNotationBuilder.cs",
    """        var x = noteheads.Average(note => note.CenterX);\n\n        var classifiedMarks = facts\n""",
    """        var x = noteheads.Average(note => note.CenterX);\n        var graceCount = noteheads.Count(note => graceNoteheadIds.Contains(note.ShapeId));\n        var isGrace = graceCount > 0 && graceCount == noteheads.Count;\n        if (graceCount > 0 && !isGrace)\n        {\n            facts.AddTrace(\n                $\"CanonicalBuilder: {eventId} mixes grace and metric noteheads; \"\n                + \"keeping the event metric\");\n        }\n\n        var classifiedMarks = facts\n""",
    "CanonicalBuilder grace decision")

replace_once(
    "SemanticInterpreter/CanonicalNotationBuilder.cs",
    """            Voice = voice,\n            Duration = selectedDuration.EffectiveDuration,\n            Notes = notes,\n""",
    """            Voice = voice,\n            Duration = isGrace ? null : selectedDuration.EffectiveDuration,\n            Grace = isGrace ? true : null,\n            Notes = notes,\n""",
    "CanonicalBuilder grace event")

replace_once(
    "SemanticInterpreter/CanonicalNotationBuilder.cs",
    """            + $\"octave-shifts={octaveShiftRelations.Count}; \"\n            + $\"onsets={onsets.Length}; dotted-rests={restDots.Length}\");\n""",
    """            + $\"octave-shifts={octaveShiftRelations.Count}; \"\n            + $\"grace-events={measures.Sum(measure => measure.Events.Count(ev => ev.Grace == true))}; \"\n            + $\"onsets={onsets.Length}; dotted-rests={restDots.Length}\");\n""",
    "CanonicalBuilder grace trace")


# 6. MusicXML writer: <grace/>, no <duration>, and preserve x-order at equal onset.
replace_once(
    "CanonicalNotation/MusicXmlWriter.cs",
    """        var streams = noteEvents\n            .GroupBy(e => e.Voice ?? 1)\n""",
    """        var eventOrder = measure.Events\n            .Select((ev, index) => (ev.Id, index))\n            .GroupBy(item => item.Id, StringComparer.Ordinal)\n            .ToDictionary(\n                group => group.Key,\n                group => group.Min(item => item.index),\n                StringComparer.Ordinal);\n\n        var streams = noteEvents\n            .GroupBy(e => e.Voice ?? 1)\n""",
    "MusicXmlWriter event order map")

replace_once(
    "CanonicalNotation/MusicXmlWriter.cs",
    """                         .OrderBy(e => Fraction.Parse(e.At).Numerator / (double)Fraction.Parse(e.At).Denominator)\n                         .ThenBy(EventStaff)\n                         .ThenBy(e => e.Id, StringComparer.Ordinal))\n""",
    """                         .OrderBy(e => Fraction.Parse(e.At).Numerator / (double)Fraction.Parse(e.At).Denominator)\n                         .ThenBy(e => eventOrder.GetValueOrDefault(e.Id, int.MaxValue)))\n""",
    "MusicXmlWriter equal-onset geometry order")

replace_once(
    "CanonicalNotation/MusicXmlWriter.cs",
    """                cursor = at + Units(ev.Duration ?? \"0\");\n""",
    """                cursor = at + (ev.Grace == true\n                    ? 0\n                    : Units(ev.Duration ?? \"0\"));\n""",
    "MusicXmlWriter grace cursor")

replace_once(
    "CanonicalNotation/MusicXmlWriter.cs",
    """        var nx = new XElement(\"note\");\n        if (chordContinuation) nx.Add(new XElement(\"chord\"));\n\n        if (note is null)\n""",
    """        var nx = new XElement(\"note\");\n        if (ev.Grace == true) nx.Add(new XElement(\"grace\"));\n        if (chordContinuation) nx.Add(new XElement(\"chord\"));\n\n        if (note is null)\n""",
    "MusicXmlWriter grace element")

replace_once(
    "CanonicalNotation/MusicXmlWriter.cs",
    """        nx.Add(new XElement(\"duration\", durationUnits));\n\n        if (note is not null)\n""",
    """        if (ev.Grace != true)\n            nx.Add(new XElement(\"duration\", durationUnits));\n\n        if (note is not null)\n""",
    "MusicXmlWriter omit grace duration")


# 7. Canonicalizer preserves grace when reading reference MusicXML too.
replace_once(
    "CanonicalNotation/MusicXmlCanonicalizer.cs",
    """                    var voice = Int(node, \"voice\", 1);\n                    var isChordContinuation = node.Element(\"chord\") is not null;\n\n                    CanonicalEvent ev;\n""",
    """                    var voice = Int(node, \"voice\", 1);\n                    var isChordContinuation = node.Element(\"chord\") is not null;\n                    var isGrace = node.Element(\"grace\") is not null;\n\n                    CanonicalEvent ev;\n""",
    "MusicXmlCanonicalizer grace flag")

replace_once(
    "CanonicalNotation/MusicXmlCanonicalizer.cs",
    """                                Id = id, Type = \"chord\", At = at,\n                                Duration = duration, Voice = voice,\n                                Notes = [ReadPitch(node, staff)!], Notation = notation\n""",
    """                                Id = id, Type = \"chord\", At = at,\n                                Duration = isGrace ? null : duration,\n                                Grace = isGrace ? true : null,\n                                Voice = voice,\n                                Notes = [ReadPitch(node, staff)!], Notation = notation\n""",
    "MusicXmlCanonicalizer grace event")


# 8. User-facing summary exposes the new semantic/canonical counts.
replace_once(
    "SemanticInterpreter/Program.cs",
    """        $\"semantic.noteheads={facts.OfType<NoteheadFact>().Count()}\",\n        $\"semantic.accidentals={facts.OfType<AccidentalFact>().Count()}\",\n""",
    """        $\"semantic.noteheads={facts.OfType<NoteheadFact>().Count()}\",\n        $\"semantic.graceNotes={facts.OfType<GraceNoteFact>().Count()}\",\n        $\"semantic.accidentals={facts.OfType<AccidentalFact>().Count()}\",\n""",
    "Program grace summary")

replace_once(
    "SemanticInterpreter/Program.cs",
    """        $\"canonical.pedals={canonical.Relations.Pedals.Count}\",\n        $\"canonical.measures={canonical.Parts.Single().Measures.Count}\",\n""",
    """        $\"canonical.pedals={canonical.Relations.Pedals.Count}\",\n        $\"canonical.graceEvents={canonical.Parts.Single().Measures.Sum(measure => measure.Events.Count(ev => ev.Grace == true))}\",\n        $\"canonical.measures={canonical.Parts.Single().Measures.Count}\",\n""",
    "Program canonical grace summary")


# 9. TDD for cluster detection, duration, pipeline placement and MusicXML round-trip.
Path("SemanticInterpreter.Tests/GraceNotePassTests.cs").write_text(r'''using System.Reflection;
using SvgMusic.Canonical;
using SvgMusic.Scene;
using SvgMusic.Semantics;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class GraceNotePassTests
{
    [Fact]
    public void ReducedAcceptedNoteheadCluster_IsTaggedAsGrace()
    {
        var facts = new SemanticFacts();
        facts.Add(Notehead("grace-a", 0.802, 10));
        facts.Add(Notehead("grace-b", 0.802, 20));
        for (var i = 0; i < 8; i++)
            facts.Add(Notehead($"normal-{i}", 1.145 + (i % 2) * 0.001, 40 + i * 10));

        var pass = new GraceNotePass();
        pass.Run(new SemanticDocument([]), facts);

        var grace = facts.OfType<GraceNoteFact>()
            .OrderBy(item => item.NoteheadId)
            .ToArray();
        Assert.Equal(2, grace.Length);
        Assert.Equal(["grace-a", "grace-b"], grace.Select(item => item.NoteheadId).ToArray());
        Assert.All(grace, item => Assert.InRange(item.Threshold, 0.90, 1.05));
        Assert.NotNull(pass.LastAnalysis);
        Assert.True(pass.LastAnalysis!.SizeProfile.HasGraceCluster);
        Assert.Equal(2, pass.LastAnalysis.SizeProfile.GraceCount);
        Assert.Equal(8, pass.LastAnalysis.SizeProfile.RegularCount);
    }

    [Fact]
    public void UniformRegularNoteheads_DoNotInventGraceNotes()
    {
        var facts = new SemanticFacts();
        for (var i = 0; i < 10; i++)
            facts.Add(Notehead($"normal-{i}", 1.10 + i * 0.006, i * 10));

        var pass = new GraceNotePass();
        pass.Run(new SemanticDocument([]), facts);

        Assert.Empty(facts.OfType<GraceNoteFact>());
        Assert.False(pass.LastAnalysis!.SizeProfile.HasGraceCluster);
    }

    [Fact]
    public void GraceDuration_KeepsWrittenTypeButConsumesZeroMetricTime()
    {
        var facts = new SemanticFacts();
        facts.Add(Notehead("grace", 0.80, 10));
        facts.Add(new GraceNoteFact(
            1, 1, "grace", 0.80, 1.14, 0.96, 0.98,
            "test grace", ["grace"]));
        facts.Add(new StemAttachmentFact(
            1, "stem", StemDirection.Down, ["grace"], [1], false,
            20, 40, 20, 90, 2.0, 0.08, 0.96,
            "test stem", ["stem", "grace"]));
        facts.Add(new BeamAttachmentFact(
            1, "beam-1", 1, ["stem"], [1], true, true, false, false,
            20, 40, 40, 40, 1, 0.2, 0, 0.95,
            "test beam", ["beam-1", "stem"]));
        facts.Add(new BeamAttachmentFact(
            1, "beam-2", 2, ["stem"], [1], true, true, false, false,
            20, 45, 40, 45, 1, 0.2, 0, 0.95,
            "test beam", ["beam-2", "stem"]));

        new DurationPass().Run(new SemanticDocument([]), facts);

        var duration = Assert.Single(facts.OfType<DurationFact>());
        Assert.Equal("1/16", duration.BaseDuration);
        Assert.Equal("0", duration.EffectiveDuration);
        Assert.Equal("16th", duration.NoteType);
        Assert.Equal(2, duration.SubdivisionLevel);
        Assert.Contains("grace note -> zero metrical duration", duration.Reason);
    }

    [Fact]
    public void Pipeline_InsertsGraceImmediatelyAfterNoteheadsAndBeforeDuration()
    {
        var pipeline = new SemanticPipeline([
            new NoteheadPass(),
            new DurationPass()
        ]);
        var field = typeof(SemanticPipeline).GetField(
            "_passes",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var passes = Assert.IsAssignableFrom<IReadOnlyList<ISemanticPass>>(
            field!.GetValue(pipeline));

        var noteheadIndex = IndexOf<NoteheadPass>(passes);
        var graceIndex = IndexOf<GraceNotePass>(passes);
        var durationIndex = IndexOf<DurationPass>(passes);
        Assert.Equal(noteheadIndex + 1, graceIndex);
        Assert.True(graceIndex < durationIndex);
    }

    [Fact]
    public void CanonicalGraceNotes_WriteGraceWithoutDuration_AndRoundTrip()
    {
        var document = Document();
        var facts = new SemanticFacts();
        AddProjectedNote(facts, "g1", 10, "C5", "16th", "0", 0.80, grace: true);
        AddProjectedNote(facts, "g2", 20, "Bb4", "16th", "0", 0.80, grace: true);
        AddProjectedNote(facts, "main", 30, "C4", "half", "1/2", 1.14, grace: false);

        var canonical = new CanonicalNotationBuilder().Build(document, facts, null, null);
        var events = canonical.Parts.Single().Measures.Single().Events
            .Where(ev => ev.Type == "chord")
            .ToArray();

        Assert.Equal(3, events.Length);
        Assert.True(events[0].Grace);
        Assert.True(events[1].Grace);
        Assert.Null(events[0].Duration);
        Assert.Null(events[1].Duration);
        Assert.Null(events[2].Grace);
        Assert.Equal("1/2", events[2].Duration);

        var xml = new MusicXmlWriter().Write(canonical);
        var notes = xml.Descendants("note").ToArray();
        Assert.Equal(3, notes.Length);
        Assert.Equal(["C", "B", "C"], notes.Select(note => note.Element("pitch")!.Element("step")!.Value).ToArray());
        Assert.NotNull(notes[0].Element("grace"));
        Assert.NotNull(notes[1].Element("grace"));
        Assert.Null(notes[0].Element("duration"));
        Assert.Null(notes[1].Element("duration"));
        Assert.NotNull(notes[2].Element("duration"));

        var path = Path.Combine(Path.GetTempPath(), $"grace-{Guid.NewGuid():N}.musicxml");
        try
        {
            xml.Save(path);
            var roundTrip = new MusicXmlCanonicalizer().Read(path);
            var readEvents = roundTrip.Parts.Single().Measures.Single().Events
                .Where(ev => ev.Type == "chord")
                .ToArray();
            Assert.True(readEvents[0].Grace);
            Assert.True(readEvents[1].Grace);
            Assert.Null(readEvents[0].Duration);
            Assert.Null(readEvents[1].Duration);
            Assert.Null(readEvents[2].Grace);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static int IndexOf<T>(IReadOnlyList<ISemanticPass> passes) where T : ISemanticPass =>
        passes.Select((pass, index) => (pass, index)).Single(item => item.pass is T).index;

    private static NoteheadFact Notehead(string id, double size, double x) =>
        new(
            1, 1, id, x, 100, 5, 4, "filled", size,
            0, 0, 0.99, "test notehead", [id]);

    private static SemanticDocument Document() =>
        new([
            new MeasureScene(
                1, "system-1", "pair-1", "measure-1", 0, 100, false,
                new StaffMeasureScene(1, "upper", new BoundsD(0, 80, 100, 120), 10, []),
                new StaffMeasureScene(2, "lower", new BoundsD(0, 180, 100, 220), 10, []))
        ]);

    private static void AddProjectedNote(
        SemanticFacts facts,
        string id,
        double x,
        string pitch,
        string noteType,
        string effectiveDuration,
        double size,
        bool grace)
    {
        facts.Add(Notehead(id, size, x));
        facts.Add(new PitchFact(
            1, 1, id, pitch[..1], int.Parse(pitch[^1..]),
            pitch.Contains('b') ? -1 : 0, pitch, 0,
            "G", 2, "clef", 0, null, null, false,
            0.99, "test pitch", [id]));
        facts.Add(new DurationFact(
            1, 1, id, null,
            noteType == "16th" ? "1/16" : "1/2",
            effectiveDuration,
            noteType, 0, noteType == "16th" ? 2 : 0,
            null, null, 0.99, "test duration", [id]));
        if (grace)
        {
            facts.Add(new GraceNoteFact(
                1, 1, id, size, 1.14, 0.96, 0.98,
                "test grace", [id]));
        }
    }
}
''', encoding="utf-8")


# 10. Mimino is the current golden: exactly its two source grace notes must survive.
replace_once(
    ".github/workflows/semantic-canonical-poc.yml",
    """          grep -q '^semantic.noteheads=' artifacts/semantic-summary.txt\n          grep -q '^semantic.chords=' artifacts/semantic-summary.txt\n""",
    """          grep -q '^semantic.noteheads=' artifacts/semantic-summary.txt\n          grep -q '^semantic.graceNotes=2$' artifacts/semantic-summary.txt\n          grep -q '^canonical.graceEvents=2$' artifacts/semantic-summary.txt\n          grep -q '^semantic.chords=' artifacts/semantic-summary.txt\n""",
    "workflow grace summary smoke")

replace_once(
    ".github/workflows/semantic-canonical-poc.yml",
    """          xml_path = Path(\"artifacts/kancheli.semantic.musicxml\")\n          root = ET.parse(xml_path).getroot()\n          assert root.tag == \"score-partwise\"\n          assert sum(1 for _ in root.iter(\"note\")) > 0\n\n          with ZipFile(\"artifacts/kancheli.semantic.mxl\") as archive:\n""",
    """          xml_path = Path(\"artifacts/kancheli.semantic.musicxml\")\n          root = ET.parse(xml_path).getroot()\n          assert root.tag == \"score-partwise\"\n          assert sum(1 for _ in root.iter(\"note\")) > 0\n\n          def grace_signature(doc_root):\n              result = []\n              for part in doc_root.findall(\"part\"):\n                  for measure in part.findall(\"measure\"):\n                      for note in measure.findall(\"note\"):\n                          if note.find(\"grace\") is None:\n                              continue\n                          pitch = note.find(\"pitch\")\n                          result.append((\n                              part.get(\"id\"),\n                              measure.get(\"number\"),\n                              pitch.findtext(\"step\"),\n                              int(pitch.findtext(\"alter\") or 0),\n                              int(pitch.findtext(\"octave\")),\n                              note.findtext(\"type\")))\n              return result\n\n          source_root = ET.parse(\n              \"Samples/kancheli-mimino/kancheli-mimino.musicxml\").getroot()\n          source_grace = grace_signature(source_root)\n          generated_grace = grace_signature(root)\n          assert source_grace == [\n              (\"P1\", \"15\", \"C\", 0, 5, \"16th\"),\n              (\"P1\", \"15\", \"B\", -1, 4, \"16th\")\n          ]\n          assert generated_grace == source_grace\n\n          import json\n          canonical = json.loads(Path(\n              \"artifacts/kancheli.semantic.canonical.json\").read_text())\n          grace_events = [\n              event\n              for part in canonical[\"parts\"]\n              for measure in part[\"measures\"]\n              for event in measure[\"events\"]\n              if event.get(\"grace\") is True\n          ]\n          assert len(grace_events) == 2\n          assert {event[\"at\"] for event in grace_events} == {\"1/4\"}\n          assert all(\"duration\" not in event for event in grace_events)\n\n          with ZipFile(\"artifacts/kancheli.semantic.mxl\") as archive:\n""",
    "workflow Mimino grace golden")

print("Grace-note patch applied")
