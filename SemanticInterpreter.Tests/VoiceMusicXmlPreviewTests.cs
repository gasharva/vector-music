using SvgMusic.Canonical;
using SvgMusic.Scene;
using SvgMusic.Semantics;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class VoiceMusicXmlPreviewTests
{
    [Fact]
    public void BuilderMapsLocalVoicesToMuseScoreCompatiblePartWideIds()
    {
        var document = new SemanticDocument(
        [
            new MeasureScene(
                1,
                "system-1",
                "pair-1",
                "measure-1",
                0,
                300,
                false,
                new StaffMeasureScene(
                    1,
                    "staff-1",
                    new BoundsD(0, 80, 300, 120),
                    10,
                    []),
                new StaffMeasureScene(
                    2,
                    "staff-2",
                    new BoundsD(0, 200, 300, 240),
                    10,
                    []))
        ]);

        var facts = new SemanticFacts();
        AddNote(facts, "s1v1", 1, 80, "C5", 1);
        AddNote(facts, "s1v2", 1, 120, "D5", 2);
        AddNote(facts, "s2v1", 2, 80, "E3", 1);
        AddNote(facts, "s2v2", 2, 120, "F3", 2);

        var canonical = new CanonicalNotationBuilder().Build(
            document,
            facts,
            "Voice preview",
            null);
        var voices = canonical.Parts.Single().Measures.Single().Events
            .Select(ev => ev.Voice)
            .OrderBy(voice => voice)
            .ToArray();

        Assert.Equal(new int?[] { 1, 2, 5, 6 }, voices);

        var xml = new MusicXmlWriter().Write(canonical);
        var xmlVoices = xml.Descendants("note")
            .Select(note => int.Parse(note.Element("voice")!.Value))
            .OrderBy(voice => voice)
            .ToArray();

        Assert.Equal(new[] { 1, 2, 5, 6 }, xmlVoices);
    }

    private static void AddNote(
        SemanticFacts facts,
        string id,
        int staff,
        double x,
        string pitch,
        int localVoice)
    {
        facts.Add(new NoteheadFact(
            1,
            staff,
            id,
            x,
            staff == 1 ? 100 : 220,
            5,
            4,
            "hollow",
            1,
            0,
            0,
            0.99,
            "test notehead",
            [id]));
        facts.Add(new PitchFact(
            1,
            staff,
            id,
            pitch[..1],
            int.Parse(pitch[^1..]),
            0,
            pitch,
            0,
            staff == 1 ? "G" : "F",
            staff == 1 ? 2 : 4,
            "clef",
            0,
            null,
            null,
            false,
            0.99,
            "test pitch",
            [id]));
        facts.Add(new DurationFact(
            1,
            staff,
            id,
            null,
            "1/4",
            "1/4",
            "quarter",
            0,
            0,
            null,
            null,
            0.99,
            "test duration",
            [id]));
        facts.Add(new VoiceFact(
            1,
            staff,
            VoiceTargetKind.Notehead,
            id,
            localVoice,
            true,
            x,
            staff == 1 ? 100 : 220,
            0.99,
            "test voice",
            [id]));
    }
}
