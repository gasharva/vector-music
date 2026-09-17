from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    p = Path(path)
    text = p.read_text()
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected one occurrence, found {count}\n--- needle ---\n{old}")
    p.write_text(text.replace(old, new, 1))


path = "SemanticInterpreter/ClassifiedSymbolPass.cs"

replace_once(path,
'''        var noteheads = facts.OfType<NoteheadFact>().ToArray();
        var onsets = facts.OfType<OnsetFact>().ToArray();
        var shapeGroups = document.Measures
            .SelectMany(measure => new[] { measure.Upper, measure.Lower })
            .SelectMany(staff => staff.Elements.OfType<ShapeElement>())
            .GroupBy(shape => shape.ShapeId, StringComparer.Ordinal)
            .Select(group => group.First())
            .GroupBy(shape => BaseShapeId(shape.ShapeId), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(shape => shape.CenterX).ToArray(),
                StringComparer.Ordinal);
        var decisions = new List<ClassifiedSymbolDecision>();
''',
'''        var noteheads = facts.OfType<NoteheadFact>().ToArray();
        var onsets = facts.OfType<OnsetFact>().ToArray();
        var decisions = new List<ClassifiedSymbolDecision>();
''')

replace_once(path,
'''                    var classification = element.Classification;
                    var label = NormalizeLabel(classification.Label);
                    var effectiveConfidence = classification.Confidence;
                    IReadOnlyList<string> sourceShapeIds = [element.ShapeId];

                    if (!TryMap(label, out var mapping)
                        && !TryComposeSplitDynamic(
                            element,
                            label,
                            staff.LineSpacing,
                            shapeGroups,
                            out label,
                            out mapping,
                            out effectiveConfidence,
                            out sourceShapeIds))
                    {
                        continue;
                    }

                    if (effectiveConfidence < MinimumConfidence)
''',
'''                    var classification = element.Classification;
                    var label = NormalizeLabel(classification.Label);

                    if (!TryMap(label, out var mapping))
                    {
                        continue;
                    }

                    if (classification.Confidence < MinimumConfidence)
''')

replace_once(path,
'''                            effectiveConfidence,
                            $"{label} confidence {effectiveConfidence:P0} is below {MinimumConfidence:P0}"));
''',
'''                            classification.Confidence,
                            $"{label} confidence {classification.Confidence:P0} is below {MinimumConfidence:P0}"));
''')

replace_once(path,
'''                    if (sourceShapeIds.Any(consumed.Contains))
''',
'''                    if (consumed.Contains(element.ShapeId))
''')

replace_once(path,
'''                            effectiveConfidence,
                            $"{string.Join(",", sourceShapeIds)} includes geometry already referenced by an earlier semantic fact"));
''',
'''                            classification.Confidence,
                            $"{element.ShapeId} is already referenced by an earlier semantic fact"));
''')

replace_once(path,
'''                            mapping.Value,
                            effectiveConfidence,
                            sourceShapeIds,
                            onsets,
''',
'''                            mapping.Value,
                            classification.Confidence,
                            onsets,
''')

replace_once(path,
'''        string value,
        double classificationConfidence,
        IReadOnlyList<string> sourceShapeIds,
        IReadOnlyList<OnsetFact> onsets,
''',
'''        string value,
        double classificationConfidence,
        IReadOnlyList<OnsetFact> onsets,
''')

replace_once(path,
'''            classificationConfidence,
            reason,
            sourceShapeIds));
        decisions.Add(new ClassifiedSymbolDecision(
            element.ShapeId,
            label,
            true,
            "dynamic",
''',
'''            classificationConfidence,
            reason,
            [element.ShapeId]));
        decisions.Add(new ClassifiedSymbolDecision(
            element.ShapeId,
            label,
            true,
            "dynamic",
''')

p = Path(path)
text = p.read_text()
start = text.index("    private static bool TryComposeSplitDynamic(")
end = text.index("    private static bool TryMap(\n", start)
p.write_text(text[:start] + text[end:])

path = "SemanticInterpreter.Tests/ClassifiedSymbolPassTests.cs"
p = Path(path)
text = p.read_text()
for method_name in (
    "SplitMp_UsesLowConfidenceLeftSiblingFromSameSourceShape",
    "SplitMp_AllowsAdditionalSiblingFromSameSourceShape",
):
    marker = f"    [Fact]\n    public void {method_name}()"
    start = text.index(marker)
    next_fact = text.index("    [Fact]\n", start + len(marker))
    text = text[:start] + text[next_fact:]
p.write_text(text)

print("Removed split-dynamic semantic reconstruction")
