from pathlib import Path


def replace(path: str, old: str, new: str) -> None:
    p = Path(path)
    text = p.read_text(encoding="utf-8")
    if old not in text:
        raise RuntimeError(f"expected patch anchor not found in {path}: {old[:120]!r}")
    p.write_text(text.replace(old, new, 1), encoding="utf-8")


replace(
    "SemanticInterpreter/SemanticModels.cs",
    """public sealed record BracketSpannerElement : SemanticElement
{
    public required BracketSpannerPrimitive Source { get; init; }
}
""",
    """public sealed record HairpinElement : SemanticElement
{
    public required HairpinPrimitive Source { get; init; }
}

public sealed record BracketSpannerElement : SemanticElement
{
    public required BracketSpannerPrimitive Source { get; init; }
}
""")

replace(
    "SemanticInterpreter/MeasureSceneBuilder.cs",
    """        foreach (var instance in notation.Instances)
        {
""",
    """        foreach (var hairpin in notation.Hairpins)
        {
            if (hairpin.Ownership is null)
            {
                continue;
            }

            result.Add(new HairpinElement
            {
                ShapeId = hairpin.ShapeId,
                Bounds = BoundsD.FromPoints([
                    hairpin.Apex,
                    hairpin.OpenUpper,
                    hairpin.OpenLower
                ]),
                Ownership = hairpin.Ownership,
                Source = hairpin
            });
        }

        foreach (var instance in notation.Instances)
        {
""")

pedal_block = """        // Pedal semantics mirrors ottava: a classified PEDAL_MARK plus a generic
        // solid bracket becomes a timed span only after rhythmic onsets are stable.
        if (materialized.Any(pass => pass is OnsetPass)
            && materialized.All(pass => pass is not PedalPass))
        {
            var ottavaIndex = materialized.FindLastIndex(pass => pass is OttavaPass);
            var refinementIndex = materialized.FindLastIndex(pass => pass is MeasureEndOnsetRefinementPass);
            var insertionIndex = ottavaIndex >= 0
                ? ottavaIndex + 1
                : refinementIndex >= 0
                    ? refinementIndex + 1
                    : materialized.FindLastIndex(pass => pass is OnsetPass) + 1;
            materialized.Insert(
                insertionIndex,
                new PedalPass());
        }
"""
replace(
    "SemanticInterpreter/SemanticPipeline.cs",
    pedal_block,
    pedal_block + """
        // Hairpins are already proven geometrically by HairpinExtractor. Once onsets
        // are stable, only their horizontal endpoints need projection onto musical time.
        if (materialized.Any(pass => pass is OnsetPass)
            && materialized.All(pass => pass is not HairpinPass))
        {
            var pedalIndex = materialized.FindLastIndex(pass => pass is PedalPass);
            var ottavaIndex = materialized.FindLastIndex(pass => pass is OttavaPass);
            var refinementIndex = materialized.FindLastIndex(pass => pass is MeasureEndOnsetRefinementPass);
            var insertionIndex = pedalIndex >= 0
                ? pedalIndex + 1
                : ottavaIndex >= 0
                    ? ottavaIndex + 1
                    : refinementIndex >= 0
                        ? refinementIndex + 1
                        : materialized.FindLastIndex(pass => pass is OnsetPass) + 1;
            materialized.Insert(
                insertionIndex,
                new HairpinPass());
        }
""")

replace(
    "SemanticInterpreter/CanonicalNotationBuilder.cs",
    """        var pedalRelations = BuildPedalRelations(facts);
        var octaveShiftRelations = BuildOctaveShiftRelations(facts);
""",
    """        var hairpinRelations = BuildHairpinRelations(facts);
        var pedalRelations = BuildPedalRelations(facts);
        var octaveShiftRelations = BuildOctaveShiftRelations(facts);
""")

replace(
    "SemanticInterpreter/CanonicalNotationBuilder.cs",
    """            + $\"slur-relations={slurRelations.Count}; tuplet-relations={tupletRelations.Count}; \"
            + $\"pedals={pedalRelations.Count}; octave-shifts={octaveShiftRelations.Count}; \"
""",
    """            + $\"slur-relations={slurRelations.Count}; tuplet-relations={tupletRelations.Count}; \"
            + $\"hairpins={hairpinRelations.Count}; pedals={pedalRelations.Count}; \"
            + $\"octave-shifts={octaveShiftRelations.Count}; \"
""")

replace(
    "SemanticInterpreter/CanonicalNotationBuilder.cs",
    """                [],
                [],
                pedalRelations,
                octaveShiftRelations));
""",
    """                [],
                hairpinRelations,
                pedalRelations,
                octaveShiftRelations));
""")

replace(
    "SemanticInterpreter/CanonicalNotationBuilder.cs",
    """    private static List<SpanRelation> BuildPedalRelations(
        SemanticFacts facts)
""",
    """    private static List<SpanRelation> BuildHairpinRelations(
        SemanticFacts facts)
    {
        return facts
            .OfType<HairpinFact>()
            .OrderBy(fact => fact.StartMeasureNumber)
            .ThenBy(fact => Fraction.Parse(fact.StartAt).Numerator / (double)Fraction.Parse(fact.StartAt).Denominator)
            .ThenBy(fact => fact.Staff)
            .ThenBy(fact => fact.ShapeId, StringComparer.Ordinal)
            .Select((fact, index) => new SpanRelation
            {
                Id = $\"hairpin-{index + 1}\",
                Kind = \"hairpin\",
                From = new TimeAnchor(
                    fact.StartMeasureNumber,
                    fact.StartAt,
                    fact.Staff),
                To = new TimeAnchor(
                    fact.EndMeasureNumber,
                    fact.EndAt,
                    fact.Staff),
                Type = fact.Type,
                Placement = fact.Placement
            })
            .ToList();
    }

    private static List<SpanRelation> BuildPedalRelations(
        SemanticFacts facts)
""")

replace(
    "SemanticInterpreter/Program.cs",
    """        $\"semantic.slurs={facts.OfType<SlurFact>().Count()}\",
        $\"semantic.pedals={facts.OfType<PedalFact>().Count()}\",
        $\"canonical.ties={canonical.Relations.Ties.Count}\",
        $\"canonical.slurs={canonical.Relations.Slurs.Count}\",
        $\"canonical.pedals={canonical.Relations.Pedals.Count}\",
""",
    """        $\"semantic.slurs={facts.OfType<SlurFact>().Count()}\",
        $\"semantic.hairpins={facts.OfType<HairpinFact>().Count()}\",
        $\"semantic.pedals={facts.OfType<PedalFact>().Count()}\",
        $\"canonical.ties={canonical.Relations.Ties.Count}\",
        $\"canonical.slurs={canonical.Relations.Slurs.Count}\",
        $\"canonical.hairpins={canonical.Relations.Hairpins.Count}\",
        $\"canonical.pedals={canonical.Relations.Pedals.Count}\",
""")

replace(
    "SemanticInterpreter/Program.cs",
    """Console.WriteLine($\"  slurs      : {facts.OfType<SlurFact>().Count()}\");
Console.WriteLine($\"  pedals     : {facts.OfType<PedalFact>().Count()}\");
""",
    """Console.WriteLine($\"  slurs      : {facts.OfType<SlurFact>().Count()}\");
Console.WriteLine($\"  hairpins   : {facts.OfType<HairpinFact>().Count()}\");
Console.WriteLine($\"  pedals     : {facts.OfType<PedalFact>().Count()}\");
""")

print("HairpinPass wiring patches applied")
