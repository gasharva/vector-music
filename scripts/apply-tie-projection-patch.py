from pathlib import Path


def replace_once(path: Path, old: str, new: str) -> None:
    text = path.read_text(encoding="utf-8")
    if old not in text:
        raise SystemExit(f"Expected patch anchor not found in {path}:\n{old}")
    path.write_text(text.replace(old, new, 1), encoding="utf-8")


builder = Path("SemanticInterpreter/CanonicalNotationBuilder.cs")
replace_once(
    builder,
    """        var beamRelations = BuildBeamRelations(\n            facts,\n            stemToEventId,\n            eventX);\n        var slurRelations = BuildSlurRelations(\n            facts,\n            noteheadToEventId);\n""",
    """        var beamRelations = BuildBeamRelations(\n            facts,\n            stemToEventId,\n            eventX);\n        var tieRelations = TieRelationProjector.Build(\n            facts,\n            noteheadToEventId);\n        var slurRelations = BuildSlurRelations(\n            facts,\n            noteheadToEventId);\n""",
)
replace_once(
    builder,
    """            + $\"beam-relations={beamRelations.Count}; slur-relations={slurRelations.Count}; \"\n            + $\"tuplet-relations={tupletRelations.Count}; onsets={onsets.Length}; dotted-rests={restDots.Length}\");\n""",
    """            + $\"beam-relations={beamRelations.Count}; tie-relations={tieRelations.Count}; \"\n            + $\"slur-relations={slurRelations.Count}; tuplet-relations={tupletRelations.Count}; \"\n            + $\"onsets={onsets.Length}; dotted-rests={restDots.Length}\");\n""",
)
replace_once(
    builder,
    """            new Relations(\n                beamRelations,\n                [],\n                slurRelations,\n""",
    """            new Relations(\n                beamRelations,\n                tieRelations,\n                slurRelations,\n""",
)

program = Path("SemanticInterpreter/Program.cs")
replace_once(
    program,
    """        $\"semantic.chords={facts.OfType<ChordFact>().Count()}\",\n        $\"semantic.chordNoteheads={facts.OfType<ChordFact>().Sum(chord => chord.NoteheadIds.Count)}\",\n        $\"canonical.measures={canonical.Parts.Single().Measures.Count}\",\n""",
    """        $\"semantic.chords={facts.OfType<ChordFact>().Count()}\",\n        $\"semantic.chordNoteheads={facts.OfType<ChordFact>().Sum(chord => chord.NoteheadIds.Count)}\",\n        $\"semantic.ties={facts.OfType<TieFact>().Count()}\",\n        $\"semantic.slurs={facts.OfType<SlurFact>().Count()}\",\n        $\"canonical.ties={canonical.Relations.Ties.Count}\",\n        $\"canonical.slurs={canonical.Relations.Slurs.Count}\",\n        $\"canonical.measures={canonical.Parts.Single().Measures.Count}\",\n""",
)
replace_once(
    program,
    """Console.WriteLine($\"  durations  : {facts.OfType<DurationFact>().Count()}\");\nConsole.WriteLine($\"  chords     : {facts.OfType<ChordFact>().Count()}\");\nConsole.WriteLine($\"  ownership ledger corrections: {ledgerLadderOwnership.Adjustments.Count}\");\n""",
    """Console.WriteLine($\"  durations  : {facts.OfType<DurationFact>().Count()}\");\nConsole.WriteLine($\"  chords     : {facts.OfType<ChordFact>().Count()}\");\nConsole.WriteLine($\"  ties       : {facts.OfType<TieFact>().Count()}\");\nConsole.WriteLine($\"  slurs      : {facts.OfType<SlurFact>().Count()}\");\nConsole.WriteLine($\"  ownership ledger corrections: {ledgerLadderOwnership.Adjustments.Count}\");\n""",
)
replace_once(
    program,
    """            case ChordFact chord:\n                yield return $\"chord m{chord.MeasureNumber} id={chord.ChordId} \"\n                    + $\"type={chord.NoteType} fill={chord.FillKind} \"\n                    + $\"stem={chord.StemShapeId ?? \"none\"} x={chord.AnchorX:F2} \"\n                    + $\"staffs=[{string.Join(',', chord.Staffs)}] \"\n                    + $\"noteheads=[{string.Join(',', chord.NoteheadIds)}] \"\n                    + $\"confidence={chord.Confidence:P1}; reason={chord.Reason}\";\n                break;\n\n            default:\n""",
    """            case ChordFact chord:\n                yield return $\"chord m{chord.MeasureNumber} id={chord.ChordId} \"\n                    + $\"type={chord.NoteType} fill={chord.FillKind} \"\n                    + $\"stem={chord.StemShapeId ?? \"none\"} x={chord.AnchorX:F2} \"\n                    + $\"staffs=[{string.Join(',', chord.Staffs)}] \"\n                    + $\"noteheads=[{string.Join(',', chord.NoteheadIds)}] \"\n                    + $\"confidence={chord.Confidence:P1}; reason={chord.Reason}\";\n                break;\n\n            case TieFact tie:\n                yield return $\"tie {tie.CurveShapeId} m{tie.StartMeasureNumber}->m{tie.EndMeasureNumber} \"\n                    + $\"staff={tie.Staff} pitch={tie.Pitch} \"\n                    + $\"noteheads={tie.FromNoteheadId}->{tie.ToNoteheadId} \"\n                    + $\"placement={tie.Placement ?? \"unspecified\"} \"\n                    + $\"distance={tie.StartDistanceInSpacings:F2}/{tie.EndDistanceInSpacings:F2}sp \"\n                    + $\"confidence={tie.Confidence:P1}; reason={tie.Reason}\";\n                break;\n\n            case SlurFact slur:\n                yield return $\"slur {slur.CurveShapeId} m{slur.StartMeasureNumber}->m{slur.EndMeasureNumber} \"\n                    + $\"staff={slur.StartStaff}->{slur.EndStaff} \"\n                    + $\"noteheads={slur.FromNoteheadId}->{slur.ToNoteheadId} \"\n                    + $\"placement={slur.Placement ?? \"unspecified\"} \"\n                    + $\"distance={slur.StartDistanceInSpacings:F2}/{slur.EndDistanceInSpacings:F2}sp \"\n                    + $\"confidence={slur.Confidence:P1}; reason={slur.Reason}\";\n                break;\n\n            default:\n""",
)

print("Tie projection patch applied.")
