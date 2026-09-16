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
