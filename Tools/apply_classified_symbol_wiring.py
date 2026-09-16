from pathlib import Path


def replace_once(text: str, old: str, new: str, name: str) -> str:
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{name}: expected one match, got {count}")
    return text.replace(old, new, 1)


pipeline_path = Path("SemanticInterpreter/SemanticPipeline.cs")
pipeline = pipeline_path.read_text(encoding="utf-8")
pipeline = replace_once(
    pipeline,
    "        _passes = materialized;\n",
    """        // Simple classifier leftovers run last, after every specialized pass has had\n        // a chance to claim its source shapes through SemanticFact.SourceShapeIds.\n        if (materialized.Any(pass => pass is NoteheadPass)\n            && materialized.All(pass => pass is not ClassifiedSymbolPass))\n        {\n            materialized.Add(new ClassifiedSymbolPass());\n        }\n\n        _passes = materialized;\n""",
    "SemanticPipeline classified pass insertion")
pipeline_path.write_text(pipeline, encoding="utf-8")

builder_path = Path("SemanticInterpreter/CanonicalNotationBuilder.cs")
builder = builder_path.read_text(encoding="utf-8")
builder = replace_once(
    builder,
    """                eventX);\n\n            measures.Add(new Measure(\n""",
    """                eventX);\n\n            events.AddRange(BuildClassifiedDynamicEvents(\n                measure.Number,\n                facts));\n\n            measures.Add(new Measure(\n""",
    "CanonicalBuilder dynamic projection")

old_notation = """        var notation = new EventNotation(\n            NoteType: selectedDuration.NoteType,\n            Dots: selectedDuration.Dots > 0\n                ? selectedDuration.Dots\n                : null,\n            Stem: stem is null\n                ? null\n                : stem.Direction switch\n                {\n                    StemDirection.Up => \"up\",\n                    StemDirection.Down => \"down\",\n                    _ => null\n                });\n"""
new_notation = """        var classifiedMarks = facts\n            .OfType<ClassifiedNotationMarkFact>()\n            .Where(mark => noteheads.Any(note =>\n                note.ShapeId == mark.TargetNoteheadId))\n            .OrderBy(mark => mark.CenterX)\n            .ThenBy(mark => mark.ShapeId, StringComparer.Ordinal)\n            .ToArray();\n\n        var notation = new EventNotation(\n            NoteType: selectedDuration.NoteType,\n            Dots: selectedDuration.Dots > 0\n                ? selectedDuration.Dots\n                : null,\n            Stem: stem is null\n                ? null\n                : stem.Direction switch\n                {\n                    StemDirection.Up => \"up\",\n                    StemDirection.Down => \"down\",\n                    _ => null\n                },\n            Articulations: BuildClassifiedMarks(\n                classifiedMarks,\n                ClassifiedNotationFamily.Articulation),\n            Ornaments: BuildClassifiedMarks(\n                classifiedMarks,\n                ClassifiedNotationFamily.Ornament),\n            Fermatas: BuildClassifiedMarks(\n                classifiedMarks,\n                ClassifiedNotationFamily.Fermata));\n"""
builder = replace_once(
    builder,
    old_notation,
    new_notation,
    "CanonicalBuilder notation mark projection")

helper_anchor = """    private static int ResolveCanonicalVoice(\n"""
helpers = """    private static List<NotationMark>? BuildClassifiedMarks(\n        IReadOnlyList<ClassifiedNotationMarkFact> marks,\n        ClassifiedNotationFamily family)\n    {\n        var result = marks\n            .Where(mark => mark.Family == family)\n            .Select(mark => new NotationMark(\n                mark.Type,\n                Placement: mark.Placement))\n            .Distinct()\n            .ToList();\n\n        return result.Count > 0\n            ? result\n            : null;\n    }\n\n    private static IEnumerable<CanonicalEvent> BuildClassifiedDynamicEvents(\n        int measureNumber,\n        SemanticFacts facts)\n    {\n        return facts\n            .OfType<DynamicDirectionFact>()\n            .Where(fact => fact.MeasureNumber == measureNumber)\n            .OrderBy(fact => Fraction.Parse(fact.At).Numerator\n                / (double)Fraction.Parse(fact.At).Denominator)\n            .ThenBy(fact => fact.CenterX)\n            .ThenBy(fact => fact.ShapeId, StringComparer.Ordinal)\n            .Select(fact => new CanonicalEvent\n            {\n                Id = $\"m{measureNumber}-dynamic-{fact.ShapeId}\",\n                Type = \"dynamic\",\n                At = fact.At,\n                Staff = fact.Staff,\n                Value = fact.Value,\n                Placement = fact.Placement\n            });\n    }\n\n"""
builder = replace_once(
    builder,
    helper_anchor,
    helpers + helper_anchor,
    "CanonicalBuilder classified helper insertion")
builder_path.write_text(builder, encoding="utf-8")

# Preserve the hairpin contract established from the source MusicXML: wedge stops
# are rhythmic positions themselves, not the end of the preceding duration.
hairpin_path = Path("SemanticInterpreter/HairpinPass.cs")
hairpin = hairpin_path.read_text(encoding="utf-8")
old_end = """        public string ResolveEndAt(\n            int measureNumber,\n            int staff,\n            double x)\n        {\n            var measureDuration = MeasureDuration(measureNumber);\n            var candidate = _onsets\n                .Where(onset =>\n                    onset.MeasureNumber == measureNumber\n                    && onset.Staff == staff\n                    && onset.AnchorX <= x + CoordinateEpsilon)\n                .OrderByDescending(onset => onset.AnchorX)\n                .ThenByDescending(onset => HairpinPass.FractionValue(Fraction.Parse(onset.At)))\n                .FirstOrDefault();\n\n            if (candidate is null)\n            {\n                return ResolveStartAt(measureNumber, staff, x);\n            }\n\n            var end = Fraction.Parse(candidate.At) + Duration(candidate);\n            if (HairpinPass.FractionValue(measureDuration) > 0\n                && HairpinPass.FractionValue(end) > HairpinPass.FractionValue(measureDuration))\n            {\n                end = measureDuration;\n            }\n\n            return end.ToString();\n        }\n"""
new_end = """        public string ResolveEndAt(\n            int measureNumber,\n            int staff,\n            double x)\n        {\n            // A wedge stop is engraved at the rhythmic position where the change\n            // stops. Snap to the nearest onset or measure boundary; do not add the\n            // previous note duration (that rule belongs to pedal-like spans).\n            return ResolveStartAt(measureNumber, staff, x);\n        }\n"""
hairpin = replace_once(hairpin, old_end, new_end, "Hairpin end onset anchor")
hairpin_path.write_text(hairpin, encoding="utf-8")

hairpin_tests_path = Path("SemanticInterpreter.Tests/HairpinPassTests.cs")
hairpin_tests = hairpin_tests_path.read_text(encoding="utf-8")
hairpin_tests = replace_once(
    hairpin_tests,
    '        AddNote(facts, 2, 2, "end", 150, "1/2", "1/8");\n',
    '        AddNote(facts, 2, 2, "end", 170, "5/8", "1/8");\n',
    "Hairpin cross-measure onset fixture")
hairpin_tests_path.write_text(hairpin_tests, encoding="utf-8")

staff_tests_path = Path("SemanticInterpreter.Tests/HairpinStaffResolutionTests.cs")
staff_tests = staff_tests_path.read_text(encoding="utf-8")
staff_tests = replace_once(
    staff_tests,
    '        AddNote(facts, 2, 1, "u-end", 150, "1/4", "1/4");\n',
    '        AddNote(facts, 2, 1, "u-end", 170, "1/2", "1/4");\n',
    "Hairpin upper staff stop fixture")
staff_tests = replace_once(
    staff_tests,
    '        AddNote(facts, 2, 2, "l-end", 150, "1/2", "1/4");\n',
    '        AddNote(facts, 2, 2, "l-end", 170, "3/4", "1/4");\n',
    "Hairpin lower staff distractor fixture")
staff_tests_path.write_text(staff_tests, encoding="utf-8")
