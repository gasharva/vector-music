# When Almonds Blossomed - mangled - artifacts

Commit: `2d989646dca108501bc611470e59da9c37b230ab`

- [Compressed MusicXML (.mxl)](kancheli.semantic.mxl) — Best choice for MuseScore; the browser should download it instead of displaying XML.
- [MusicXML (.musicxml)](kancheli.semantic.musicxml) — Generated MusicXML before compression.
- [CanonicalNotation JSON](kancheli.semantic.canonical.json) — Canonical v0.3 produced by the semantic passes.
- [Semantic facts](semantic-facts.txt) — Facts and evidence emitted by individual semantic passes.
- [Run summary](semantic-summary.txt) — Counts and output paths from the semantic run.
- [Run log](semantic-run.log) — Console trace from the semantic pipeline.
- [Detected noteheads (SVG)](semantic.noteheads.svg) — Semantic NoteheadPass overlay: accepted filled noteheads are green, hollow noteheads blue, small-dot size cluster orange, off-grid ellipses gray.
- [Notehead size ranking and decisions](semantic.noteheads.txt) — All ellipse candidates ranked by normalized size with small-dot split, staff-grid alignment and final NoteheadPass decision.
- [Detected local accidentals (SVG)](semantic.accidentals.svg) — AccidentalPass overlay on top of notehead diagnostics: accepted accidentals are orange, explicit target noteheads have a solid orange ring, inherited targets a dashed orange ring.
- [Accidental targets and propagation](semantic.accidentals.txt) — Classifier candidates, logical anchor matching, explicit notehead target and all noteheads affected until the next accidental on the same staff-step or measure end.
- [Detected stem attachments (SVG)](semantic.stems.svg) — StemAttachmentPass overlay: accepted stems are magenta, cross-staff stems purple, attached noteheads are ringed, unmatched vertical candidates are faint gray dashed lines.
- [Stem attachment decisions](semantic.stems.txt) — Per-stem geometry, attached noteheads, inferred stem direction, cross-staff status and unmatched vertical stroke candidates.
- [Detected flag attachments (SVG)](semantic.flags.svg) — FlagAttachmentPass overlay on top of stem diagnostics: attached classifier flags are blue, their matched free stem tip is marked, rejected classified flags are dashed.
- [Flag attachment decisions](semantic.flags.txt) — Per-flag classifier label, level, matched stem free tip, geometric distances, ambiguity and rejected candidates.
- [Detected beam attachments (SVG)](semantic.beams.svg) — BeamAttachmentPass overlay: primary/secondary beam levels are colored separately, stem intersections are marked, free hook ends are hollow red, and cross-staff beams are purple.
- [Beam attachment decisions](semantic.beams.txt) — Beam-like stroke geometry, attached stems, inferred beam level, supported ends, hooks, cross-staff status and rejected candidates.
- [Detected tuplets (SVG)](semantic.tuplets.svg) — TupletPass overlay on top of beam diagnostics: classifier tuplet digits are attached to nearby primary beam groups; corrected classifier numbers are highlighted and annotated with the inferred actual:normal ratio.
- [Tuplet decisions](semantic.tuplets.txt) — Classifier tuplet candidates, matched beam groups, stem counts, actual:normal ratios and any classifier-number correction inferred from the rhythmic stem group.
- [Detected augmentation dots (SVG)](semantic.dots.svg) — DotAttachmentPass overlay on top of tuplet diagnostics: accepted augmentation dots and their target noteheads are pink; rejected small ellipse candidates are faint gray.
- [Augmentation-dot decisions](semantic.dots.txt) — Small filled ellipse candidates, size band, target noteheads, horizontal/vertical geometry and rejected dot-like marks such as articulation dots.
- [Ownership coloring (SVG)](parser.ownership.svg) — Final staff+measure ownership coloring after G1-G4. Best visual oracle for semantic ownership.
- [Classified symbols (SVG)](parser.classified-symbols.svg) — Audiveris symbol classifications overlaid on the original SVG.
- [Primitive strokes (SVG)](parser.strokes.svg) — Straight strokes extracted by the SVG parser.
- [Primitive arcs (SVG)](parser.arcs.svg) — Curved strokes and slur-like primitives extracted by the SVG parser.
- [Ellipse-like primitives (SVG)](parser.ellipses.svg) — Filled and hollow ellipse-like primitives, including notehead candidates.
- [Clustered contours (SVG)](parser.contours.svg) — Remaining contour instances with prototype labels.
- [Ownership renderer diagnostics](parser.ownership.render-diagnostics.txt) — Per-shape ownership/rendering diagnostics used to distinguish analyzer misses from visualization problems.
- [Source SVG](source.svg) — Exact SVG input used for this run.
- [Reference source MusicXML](source.musicxml) — MuseScore source used as a visual and semantic oracle for this fixture.
