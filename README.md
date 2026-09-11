# vector-music

Experimental toolkit for converting vector music notation into a normalized canonical representation and back to MusicXML.

## Current focus

The repository currently contains the first implementation of **CanonicalNotation** — a compact, human-readable model intended to preserve musical and engraving semantics while discarding exporter-specific page/layout noise.

The current round-trip is:

```text
MusicXML -> CanonicalNotation JSON -> MusicXML
```

The canonical model keeps things such as notes/chords, voices, staves, accidentals, durations, stems, beams, ties, slurs, tuplets, arpeggios, dynamics, pedal, octave shifts and other notation relations.

The longer-term goal is to recognize the same canonical notation from arbitrary SVG scores:

```text
SVG -> CanonicalNotation <- MusicXML
                    |
                    v
                 MusicXML
```

This lets SVG recognition be tested against a stable notation-level ground truth instead of against raw MusicXML or exporter-specific SVG structure.
