# When Almonds Blossomed - mangled - artifacts

Commit: `a07016eb7ed0486ce75d3b5fda76f31632ed9194`

- [Compressed MusicXML (.mxl)](kancheli.semantic.mxl) — Primary end-to-end result for opening in MuseScore.
- [MusicXML (.musicxml)](kancheli.semantic.musicxml) — Generated MusicXML before compression.
- [CanonicalNotation JSON](kancheli.semantic.canonical.json) — Canonical representation produced by the semantic passes.
- [Run summary](semantic-summary.txt) — Compact counts and important output paths from the semantic run.
- [Run log](semantic-run.log) — Console trace from the semantic pipeline.
- [Classified symbols (SVG)](parser.classified-symbols.svg) — Global classifier overlay for residual glyphs.
- [Ownership coloring (SVG)](parser.ownership.svg) — Final staff/measure ownership coloring; best visual oracle for placement mistakes.
- [Noteheads (SVG)](semantic.noteheads.svg) — Accepted and rejected notehead candidates.
- [Accidentals (SVG)](semantic.accidentals.svg) — Local accidental recognition and target propagation.
- [Stems (SVG)](semantic.stems.svg) — Stem attachment overlay, including cross-staff candidates.
- [Beams (SVG)](semantic.beams.svg) — Beam groups and stem attachments.
- [Augmentation dots (SVG)](semantic.dots.svg) — Detected augmentation dots and their notehead targets.
- [Source SVG](source.svg) — Exact SVG input used for this run.
- [Reference source MusicXML](source.musicxml) — MuseScore source used as a semantic oracle for the fixture.
## Vertical zigzag diagnostics

Detected geometric vertical zigzags: **1**.

- [parser.zigzags.svg](parser.zigzags.svg) — visual overlay of detected candidates.
- [zigzag-scene.json](zigzag-scene.json) — raw primitive geometry and confidence.
