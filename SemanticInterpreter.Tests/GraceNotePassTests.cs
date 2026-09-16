using System.Reflection;
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
