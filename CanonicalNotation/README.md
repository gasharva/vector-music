# CanonicalNotation v0.3 spike

This spike supports both directions:

```text
MusicXML -> CanonicalNotation JSON -> MusicXML
```

The goal is semantic/notation round-trip, not byte-identical MusicXML. Page size, margins,
fonts and exact coordinates are intentionally not preserved.

## CLI

```powershell
dotnet run -- to-json input.musicxml output.canonical.json
dotnet run -- to-musicxml output.canonical.json roundtrip.musicxml
```

Then open `roundtrip.musicxml` in MuseScore and visually compare it with the source score/SVG.

## Cross-staff model

A measure belongs to the whole part, not to an individual staff. A chord is one timed event
inside that measure, and each notehead carries its own `staff` value:

```json
{
  "id": "m5.e3",
  "type": "chord",
  "at": "1/4",
  "duration": "1/4",
  "voice": 1,
  "notes": [
    { "pitch": "C4", "staff": 2 },
    { "pitch": "E4", "staff": 1 },
    { "pitch": "G4", "staff": 1 }
  ]
}
```

This allows one chord to span several staves. Beams, ties and tuplets are relations over
voice/events and are intentionally not split merely because the voice moves between staves.
Rests and directions still keep `staff` on the event itself.

The writer still accepts old JSON during migration: if a note has no `staff`, it falls back
to the event-level `staff`. Newly generated JSON writes staff explicitly on every pitched note.

## Local notation model

v0.3 reserves explicit locations for the common classes of local engraving symbols so that
new symbols can be added without reshaping the canonical format:

- event/chord level: `notation.articulations[]`, `notation.ornaments[]`, `notation.fermatas[]`
- individual notehead level: `notes[].technical[]` (for example fingering)
- timed multi-event constructs: `relations` (beam, tie, slur, tuplet, arpeggio, hairpin, pedal, ottava)
- measure/barline level: `leftRepeat` / `rightRepeat`
- direction level: `navigation` events for coda / segno marks

The mark records are intentionally small and semantic: a mark keeps its type plus optional
subtype/value/placement rather than exporter-specific coordinates.

The `koldunstvo.musicxml` test case motivated the first v0.3 set: tenuto, strong accent,
fingering, fermata, inverted turn, trill mark, coda and backward repeat.

## What the writer reconstructs in v0.3

- parts / measures
- key, time signature, staves, clefs
- notes, cross-staff chords and rests
- rational durations via generated MusicXML `divisions`
- voices and staffs
- explicit accidentals
- note type, dots, stem, notehead
- beams, including cross-staff beam groups and hooks
- ties
- slurs
- tuplets
- arpeggios
- articulations
- ornaments
- fermatas
- notehead-local technical marks such as fingering
- text directions
- dynamics
- metronome marks
- coda / segno navigation marks
- hairpins
- pedal spans
- octave shifts
- system/page breaks
- barlines and forward/backward repeats

## Expected losses

These are deliberate for the canonical format:

- page dimensions and margins
- fonts
- exact x/y placement
- MusicXML Bézier control points
- application-specific encoding/support metadata
- most playback-only `sound` data (navigation target identifiers are preserved)

## Still intentionally open

This is still a spike, but the extension points are now explicit. Less common constructs can
be added inside the same categories instead of changing the object hierarchy. Examples still
to cover include grace notes, tremolo, glissando/slide, volta endings and more specialized
technical/ornament marks.

The main round-trip test remains:

```text
original.musicxml
   -> Canonical A
   -> roundtrip.musicxml
   -> Canonical B

Assert Canonical A == Canonical B
```

followed by rendering `roundtrip.musicxml` in MuseScore and comparing the resulting SVG.
