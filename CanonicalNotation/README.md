# CanonicalNotation v0.2 spike

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

The writer still accepts old v0.1 JSON during migration: if a note has no `staff`, it falls
back to the event-level `staff`. Newly generated v0.2 JSON writes staff explicitly on every
pitched note.

## What the writer reconstructs in v0.2

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
- text directions
- dynamics
- metronome marks
- hairpins
- pedal spans
- octave shifts
- system/page breaks
- barlines

## Expected losses

These are deliberate for the canonical format:

- page dimensions and margins
- fonts
- exact x/y placement
- MusicXML Bézier control points
- application-specific encoding/support metadata
- playback-only `sound` duplicates

## Important limitation

This is still a spike. The canonical model does not yet contain every MusicXML engraving
construct (for example arbitrary articulations/ornaments and manual beam geometry).
The Kancheli sample is the first integration fixture. Existing checked-in v0.1 fixtures should
be regenerated with `to-json` before using them as v0.2 golden files.

The main round-trip test remains:

```text
original.musicxml
   -> Canonical A
   -> roundtrip.musicxml
   -> Canonical B

Assert Canonical A == Canonical B
```

followed by rendering `roundtrip.musicxml` in MuseScore and comparing the resulting SVG.
