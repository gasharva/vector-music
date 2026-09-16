from pathlib import Path


def replace_once(text: str, old: str, new: str, name: str) -> str:
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{name}: expected one match, got {count}")
    return text.replace(old, new, 1)


# 1. Carry owned vertical zigzags into the measure-oriented semantic scene.
path = Path("SemanticInterpreter/MeasureSceneBuilder.cs")
text = path.read_text(encoding="utf-8")
anchor = """        foreach (var instance in notation.Instances)\n"""
insert = """        foreach (var zigZag in notation.VerticalZigZags)\n        {\n            if (zigZag.Ownership is null)\n            {\n                continue;\n            }\n\n            result.Add(new VerticalZigZagElement\n            {\n                ShapeId = zigZag.ShapeId,\n                Bounds = zigZag.Bounds,\n                Ownership = zigZag.Ownership,\n                Source = zigZag\n            });\n        }\n\n"""
text = replace_once(
    text,
    anchor,
    insert + anchor,
    "MeasureSceneBuilder vertical zigzag projection")
path.write_text(text, encoding="utf-8")


# 2. Run ArpeggioPass after rhythmic onsets/hairpins are stable but before the
# residual classifier. The pass only consumes deterministic zigzag primitives.
path = Path("SemanticInterpreter/SemanticPipeline.cs")
text = path.read_text(encoding="utf-8")
anchor = """        // Curves are already extracted geometrically. SlurPass classifies their\n"""
insert = """        // Vertical zigzags are already proven geometrically. ArpeggioPass only\n        // decides which already-built chord event(s) at one onset they span.\n        if (materialized.Any(pass => pass is OnsetPass)\n            && materialized.Any(pass => pass is ChordPass)\n            && materialized.All(pass => pass is not ArpeggioPass))\n        {\n            var hairpinIndex = materialized.FindLastIndex(pass => pass is HairpinPass);\n            var insertionIndex = hairpinIndex >= 0\n                ? hairpinIndex + 1\n                : materialized.FindLastIndex(pass => pass is OnsetPass) + 1;\n            materialized.Insert(\n                insertionIndex,\n                new ArpeggioPass());\n        }\n\n"""
text = replace_once(
    text,
    anchor,
    insert + anchor,
    "SemanticPipeline ArpeggioPass insertion")
path.write_text(text, encoding="utf-8")


# 3. Project ArpeggioFact through the existing canonical ArpeggioRelation type.
path = Path("SemanticInterpreter/CanonicalNotationBuilder.cs")
text = path.read_text(encoding="utf-8")
text = replace_once(
    text,
    """        var tupletRelations = BuildTupletRelations(\n            facts,\n            noteheadToEventId,\n            eventX);\n        var hairpinRelations = BuildHairpinRelations(facts);\n""",
    """        var tupletRelations = BuildTupletRelations(\n            facts,\n            noteheadToEventId,\n            eventX);\n        var arpeggioRelations = BuildArpeggioRelations(\n            facts,\n            noteheadToEventId,\n            eventX);\n        var hairpinRelations = BuildHairpinRelations(facts);\n""",
    "CanonicalBuilder arpeggio relation creation")

text = replace_once(
    text,
    """            + $\"slur-relations={slurRelations.Count}; tuplet-relations={tupletRelations.Count}; \"\n            + $\"hairpins={hairpinRelations.Count}; pedals={pedalRelations.Count}; \"\n""",
    """            + $\"slur-relations={slurRelations.Count}; tuplet-relations={tupletRelations.Count}; \"\n            + $\"arpeggios={arpeggioRelations.Count}; \"\n            + $\"hairpins={hairpinRelations.Count}; pedals={pedalRelations.Count}; \"\n""",
    "CanonicalBuilder arpeggio trace")

text = replace_once(
    text,
    """                slurRelations,\n                tupletRelations,\n                [],\n                hairpinRelations,\n""",
    """                slurRelations,\n                tupletRelations,\n                arpeggioRelations,\n                hairpinRelations,\n""",
    "CanonicalBuilder relations arpeggio slot")

anchor = """    private static List<SpanRelation> BuildHairpinRelations(\n"""
helper = """    private static List<ArpeggioRelation> BuildArpeggioRelations(\n        SemanticFacts facts,\n        IReadOnlyDictionary<string, string> noteheadToEventId,\n        IReadOnlyDictionary<string, double> eventX)\n    {\n        var result = new List<ArpeggioRelation>();\n\n        foreach (var arpeggio in facts\n                     .OfType<ArpeggioFact>()\n                     .OrderBy(fact => fact.MeasureNumber)\n                     .ThenBy(fact => fact.AnchorX)\n                     .ThenBy(fact => fact.ZigZagShapeId, StringComparer.Ordinal))\n        {\n            var events = arpeggio.NoteheadIds\n                .Where(noteheadToEventId.ContainsKey)\n                .Select(noteheadId => noteheadToEventId[noteheadId])\n                .Distinct(StringComparer.Ordinal)\n                .OrderBy(eventId => eventX.TryGetValue(eventId, out var x)\n                    ? x\n                    : double.MaxValue)\n                .ThenBy(eventId => eventId, StringComparer.Ordinal)\n                .ToList();\n\n            if (events.Count == 0)\n            {\n                continue;\n            }\n\n            result.Add(new ArpeggioRelation(\n                $\"arpeggio-{arpeggio.ZigZagShapeId}\",\n                events,\n                arpeggio.Direction));\n        }\n\n        return result;\n    }\n\n"""
text = replace_once(
    text,
    anchor,
    helper + anchor,
    "CanonicalBuilder arpeggio projector")
path.write_text(text, encoding="utf-8")


# 4. If one geometric chord column under the zigzag contains conflicting rhythmic
# onsets, refuse to guess which chord owns the arpeggio.
path = Path("SemanticInterpreter/ArpeggioPass.cs")
text = path.read_text(encoding="utf-8")
old = """                var seed = candidates[0];\n                var sameOnset = candidates\n                    .Where(candidate => string.Equals(\n                        candidate.Onset.At,\n                        seed.Onset.At,\n                        StringComparison.Ordinal))\n                    .Where(candidate =>\n                        Math.Abs(candidate.Chord.AnchorX - seed.Chord.AnchorX)\n                        <= spacing * MaximumChordColumnDeltaInSpacings)\n                    .OrderBy(candidate => candidate.Chord.AnchorX)\n                    .ThenBy(candidate => candidate.Chord.ChordId, StringComparer.Ordinal)\n                    .ToArray();\n\n                if (sameOnset.Length == 0)\n                {\n                    decisions.Add(Reject(\n                        zigZag,\n                        measure.Number,\n                        \"no-onset-group\",\n                        \"nearby chord candidates do not form a rhythmic onset group\"));\n                    continue;\n                }\n"""
new = """                var seed = candidates[0];\n                var aligned = candidates\n                    .Where(candidate =>\n                        Math.Abs(candidate.Chord.AnchorX - seed.Chord.AnchorX)\n                        <= spacing * MaximumChordColumnDeltaInSpacings)\n                    .ToArray();\n                var distinctOnsets = aligned\n                    .Select(candidate => candidate.Onset.At)\n                    .Distinct(StringComparer.Ordinal)\n                    .ToArray();\n\n                if (distinctOnsets.Length > 1)\n                {\n                    decisions.Add(Reject(\n                        zigZag,\n                        measure.Number,\n                        \"conflicting-onsets\",\n                        $\"aligned chord column contains conflicting onsets [{string.Join(',', distinctOnsets)}]\"));\n                    continue;\n                }\n\n                var sameOnset = aligned\n                    .Where(candidate => string.Equals(\n                        candidate.Onset.At,\n                        seed.Onset.At,\n                        StringComparison.Ordinal))\n                    .OrderBy(candidate => candidate.Chord.AnchorX)\n                    .ThenBy(candidate => candidate.Chord.ChordId, StringComparer.Ordinal)\n                    .ToArray();\n"""
text = replace_once(text, old, new, "ArpeggioPass conflicting onset guard")
path.write_text(text, encoding="utf-8")


# 5. The regression now states the conservative contract explicitly.
path = Path("SemanticInterpreter.Tests/ArpeggioPassTests.cs")
text = path.read_text(encoding="utf-8")
old = """    public void NearbyChordsWithDifferentOnsets_AreNotMerged()\n    {\n        var zigZag = ZigZag(20, 24, 105, 245);\n        var document = Document(zigZag);\n        var facts = new SemanticFacts();\n\n        AddChord(\n            facts,\n            \"upper-chord\",\n            staff: 1,\n            at: \"0\",\n            x: 32,\n            ys: [115, 125, 135]);\n        AddChord(\n            facts,\n            \"lower-later\",\n            staff: 2,\n            at: \"1/4\",\n            x: 32,\n            ys: [215, 225, 235]);\n\n        new ArpeggioPass().Run(document, facts);\n\n        var arpeggio = Assert.Single(facts.OfType<ArpeggioFact>());\n        Assert.Equal([\"upper-chord\"], arpeggio.ChordIds);\n        Assert.Equal([1], arpeggio.Staffs);\n        Assert.Equal(3, arpeggio.NoteheadIds.Count);\n    }\n"""
new = """    public void NearbyChordsWithDifferentOnsets_AreRejectedAsAmbiguous()\n    {\n        var zigZag = ZigZag(20, 24, 105, 245);\n        var document = Document(zigZag);\n        var facts = new SemanticFacts();\n\n        AddChord(\n            facts,\n            \"upper-chord\",\n            staff: 1,\n            at: \"0\",\n            x: 32,\n            ys: [115, 125, 135]);\n        AddChord(\n            facts,\n            \"lower-later\",\n            staff: 2,\n            at: \"1/4\",\n            x: 32,\n            ys: [215, 225, 235]);\n\n        var pass = new ArpeggioPass();\n        pass.Run(document, facts);\n\n        Assert.Empty(facts.OfType<ArpeggioFact>());\n        var decision = Assert.Single(pass.LastAnalysis!.Decisions);\n        Assert.False(decision.Accepted);\n        Assert.Equal(\"conflicting-onsets\", decision.Decision);\n    }\n"""
text = replace_once(text, old, new, "ArpeggioPass ambiguous onset regression")
path.write_text(text, encoding="utf-8")
