# CanonicalNotation v0.2 spike

This spike now supports both directions:

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

## What the writer reconstructs in v0.2

- parts / measures
- key, time signature, staves, clefs
- notes, chords and rests
- rational durations via generated MusicXML `divisions`
- voices and staffs
- explicit accidentals
- note type, dots, stem, notehead
- beams, including hooks
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

This is still a spike. The current canonical v0.1 model does not yet contain every MusicXML
engraving construct (for example arbitrary articulations/ornaments and manual beam geometry).
The Kancheli sample is the first integration fixture. The next useful test is:

```text
original.musicxml
   -> Canonical A
   -> roundtrip.musicxml
   -> Canonical B

Assert Canonical A == Canonical B
```

followed by rendering `roundtrip.musicxml` in MuseScore and comparing the resulting SVG.
