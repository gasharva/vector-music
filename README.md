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

## Semantic interpreter artifacts

The current Kancheli integration fixture is published after successful `master` workflow runs.

- [Open rendered artifact index](https://raw.githack.com/gasharva/vector-music/semantic-artifacts/kancheli-mangled/index.html) — rendered HTML entry point with links to the published results and visual diagnostics.
- [Published artifact folder](https://github.com/gasharva/vector-music/tree/semantic-artifacts/kancheli-mangled) — direct access to generated MusicXML, canonical JSON and SVG overlays in GitHub.
- [Semantic interpreter workflow](https://github.com/gasharva/vector-music/actions/workflows/semantic-canonical-poc.yml) — latest runs, logs and downloadable workflow artifacts.

The curated visual set currently includes classified symbols, ownership, vertical zigzags/arpeggio candidates, noteheads, accidentals, stems, beams and augmentation dots. Lower-level parser diagnostics are still generated locally when needed, but are intentionally not published in the stable artifact folder.

> GitHub's normal `blob/.../index.html` view displays HTML source rather than rendering it. The rendered index link above uses raw.githack to serve the same files from the `semantic-artifacts` branch as static web content; relative links from the index to SVG/MXL/JSON artifacts remain usable.
