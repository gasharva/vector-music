from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    file = Path(path)
    text = file.read_text(encoding="utf-8")
    if old not in text:
        raise RuntimeError(f"Patch anchor not found in {path}: {old[:100]!r}")
    file.write_text(text.replace(old, new, 1), encoding="utf-8")


# SemanticPipeline: run pedal interpretation after rhythmic timing and ottava.
replace_once(
    "SemanticInterpreter/SemanticPipeline.cs",
    """        // Curves are already extracted geometrically. SlurPass classifies their
""",
    """        // Pedal semantics mirrors ottava: a classified PEDAL_MARK plus a generic
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

        // Curves are already extracted geometrically. SlurPass classifies their
""",
)

# Canonical builder: project accepted pedal facts to generic span relations.
replace_once(
    "SemanticInterpreter/CanonicalNotationBuilder.cs",
    """        var octaveShiftRelations = BuildOctaveShiftRelations(facts);
""",
    """        var pedalRelations = BuildPedalRelations(facts);
        var octaveShiftRelations = BuildOctaveShiftRelations(facts);
""",
)
replace_once(
    "SemanticInterpreter/CanonicalNotationBuilder.cs",
    """            + $\"octave-shifts={octaveShiftRelations.Count}; \"
""",
    """            + $\"pedals={pedalRelations.Count}; octave-shifts={octaveShiftRelations.Count}; \"
""",
)
replace_once(
    "SemanticInterpreter/CanonicalNotationBuilder.cs",
    """                [],
                [],
                [],
                octaveShiftRelations));
""",
    """                [],
                [],
                pedalRelations,
                octaveShiftRelations));
""",
)
replace_once(
    "SemanticInterpreter/CanonicalNotationBuilder.cs",
    """    private static List<SpanRelation> BuildOctaveShiftRelations(
""",
    """    private static List<SpanRelation> BuildPedalRelations(
        SemanticFacts facts)
    {
        return facts
            .OfType<PedalFact>()
            .OrderBy(fact => fact.StartMeasureNumber)
            .ThenBy(fact => Fraction.Parse(fact.StartAt).Numerator / (double)Fraction.Parse(fact.StartAt).Denominator)
            .ThenBy(fact => fact.Staff)
            .ThenBy(fact => fact.BracketId, StringComparer.Ordinal)
            .Select((fact, index) => new SpanRelation
            {
                Id = $\"pedal-{index + 1}\",
                Kind = \"pedal\",
                From = new TimeAnchor(
                    fact.StartMeasureNumber,
                    fact.StartAt,
                    fact.Staff),
                To = new TimeAnchor(
                    fact.EndMeasureNumber,
                    fact.EndAt,
                    fact.Staff),
                Line = fact.Line,
                StartMark = fact.StartMark,
                Placement = fact.Placement
            })
            .ToList();
    }

    private static List<SpanRelation> BuildOctaveShiftRelations(
""",
)

# MusicXML: MuseScore-style pedal line uses resume+sign at start and stop without sign.
replace_once(
    "CanonicalNotation/MusicXmlWriter.cs",
    """            \"pedal\" => Pedal(\"start\", span),
""",
    """            \"pedal\" => Pedal(
                span.StartMark == true ? \"resume\" : \"start\",
                span,
                span.StartMark == true),
""",
)
replace_once(
    "CanonicalNotation/MusicXmlWriter.cs",
    """            \"pedal\" => Pedal(\"stop\", span),
""",
    """            \"pedal\" => Pedal(\"stop\", span, false),
""",
)
replace_once(
    "CanonicalNotation/MusicXmlWriter.cs",
    """    private static XElement Pedal(string type, SpanRelation span)
""",
    """    private static XElement Pedal(
        string type,
        SpanRelation span,
        bool sign)
""",
)
replace_once(
    "CanonicalNotation/MusicXmlWriter.cs",
    """        SetYesNo(px, \"sign\", span.StartMark);
""",
    """        SetYesNo(px, \"sign\", sign);
""",
)

# Human-readable integration summary.
replace_once(
    "SemanticInterpreter/Program.cs",
    """        $\"semantic.slurs={facts.OfType<SlurFact>().Count()}\",
        $\"canonical.ties={canonical.Relations.Ties.Count}\",
""",
    """        $\"semantic.slurs={facts.OfType<SlurFact>().Count()}\",
        $\"semantic.pedals={facts.OfType<PedalFact>().Count()}\",
        $\"canonical.ties={canonical.Relations.Ties.Count}\",
""",
)
replace_once(
    "SemanticInterpreter/Program.cs",
    """        $\"canonical.slurs={canonical.Relations.Slurs.Count}\",
        $\"canonical.measures={canonical.Parts.Single().Measures.Count}\",
""",
    """        $\"canonical.slurs={canonical.Relations.Slurs.Count}\",
        $\"canonical.pedals={canonical.Relations.Pedals.Count}\",
        $\"canonical.measures={canonical.Parts.Single().Measures.Count}\",
""",
)
replace_once(
    "SemanticInterpreter/Program.cs",
    """Console.WriteLine($\"  slurs      : {facts.OfType<SlurFact>().Count()}\");
""",
    """Console.WriteLine($\"  slurs      : {facts.OfType<SlurFact>().Count()}\");
Console.WriteLine($\"  pedals     : {facts.OfType<PedalFact>().Count()}\");
""",
)
