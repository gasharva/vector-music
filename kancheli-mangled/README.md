# When Almonds Blossomed - mangled - artifacts

Commit: `dd67bd75f4d7e0ffa208b7cf71bde9cc9a528058`

- [Compressed MusicXML (.mxl)](kancheli.semantic.mxl) — Best choice for MuseScore; the browser should download it instead of displaying XML.
- [MusicXML (.musicxml)](kancheli.semantic.musicxml) — Generated MusicXML before compression.
- [CanonicalNotation JSON](kancheli.semantic.canonical.json) — Canonical v0.3 produced by the semantic passes.
- [Semantic facts](semantic-facts.txt) — Facts and evidence emitted by individual semantic passes.
- [Run summary](semantic-summary.txt) — Counts and output paths from the semantic run.
- [Run log](semantic-run.log) — Console trace from the semantic pipeline.
- [Detected noteheads (SVG)](semantic.noteheads.svg) — Semantic NoteheadPass overlay: accepted filled noteheads are green, hollow noteheads blue, small-dot size cluster orange, off-grid ellipses gray.
- [Notehead size ranking and decisions](semantic.noteheads.txt) — All ellipse candidates ranked by normalized size with small-dot split, staff-grid alignment and final NoteheadPass decision.
- [Ownership coloring (SVG)](parser.ownership.svg) — Final staff+measure ownership coloring after G1-G4. Best visual oracle for semantic ownership.
- [Classified symbols (SVG)](parser.classified-symbols.svg) — Audiveris symbol classifications overlaid on the original SVG.
- [Primitive strokes (SVG)](parser.strokes.svg) — Straight strokes extracted by the SVG parser.
- [Primitive arcs (SVG)](parser.arcs.svg) — Curved strokes and slur-like primitives extracted by the SVG parser.
- [Ellipse-like primitives (SVG)](parser.ellipses.svg) — Filled and hollow ellipse-like primitives, including notehead candidates.
- [Clustered contours (SVG)](parser.contours.svg) — Remaining contour instances with prototype labels.
- [Ownership renderer diagnostics](parser.ownership.render-diagnostics.txt) — Per-shape ownership/rendering diagnostics used to distinguish analyzer misses from visualization problems.
- [Source SVG](source.svg) — Exact SVG input used for this run.
- [Reference source MusicXML](source.musicxml) — MuseScore source used as a visual and semantic oracle for this fixture.
