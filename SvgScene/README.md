# SvgScene PoC

First SVG-side pipeline for `vector-music`:

```text
SvgNormalizer
    ↓
GeometricScene
    ↓
GeometryAnalyzer
    ├── Stroke
    └── contour
            ↓
      ShapeClusterer
            ↓
      NotationScene
         ├── prototypes
         ├── instances
         └── strokes
```

This project deliberately stops **before** musical symbol recognition. A prototype is only a repeated geometric shape family; it is not yet known to be a notehead, accidental, clef, dot, etc.

## Current PoC

The first generated fixture contains 100 visually identical filled-notehead-like shapes:

- 25 `<use>` instances of one path;
- 25 duplicated paths with `translate(...)`;
- 25 duplicated paths with a matrix translation;
- 25 paths whose Bézier control points are slightly perturbed.

Generate the deterministic fixture:

```powershell
python SvgScene/Tools/generate_shape_clustering_noteheads.py
```

Then run the PoC:

```powershell
dotnet run --project SvgScene -- SvgScene/Fixtures/shape-clustering-noteheads.svg
```

A compact `notation-scene.json` file is written by default. It keeps prototype summaries, instances and normalized strokes, but omits the very large normalized point arrays.

For the full descriptor JSON:

```powershell
dotnet run --project SvgScene -- score.svg --verbose-json
```

This additionally writes `notation-scene.verbose.json`.

## Stroke normalization

Line-like geometry is intentionally separated from symbol contours before clustering.

`SvgNormalizer` currently understands:

- `<path>`
- `<use href="#...">`
- `<line>`
- `<polyline>`
- `<polygon>`
- `<rect>`
- `<defs>`
- `translate(...)`
- `matrix(...)`
- path commands `M`, `L`, `C`, `Z`

`GeometryAnalyzer` then converts anything sufficiently line-like into a source-independent `Stroke(Start, End, Width)`.

Two-point lines/polylines are direct strokes. Closed contours such as thin rectangles, polygons and paths are analyzed with a small 2D PCA implementation: their principal axis gives stroke direction, projection along that axis gives length, and projection onto the perpendicular axis gives thickness. Strong elongation plus a high oriented-box fill ratio is required so curved symbols such as slurs are not accidentally reduced to strokes.

Strokes are **not clustered**. Their musical role (staff line, stem, barline, beam, ledger line, etc.) is a later semantic problem.

Everything that does not pass the stroke detector remains a contour and participates in shape clustering.

## Visual cluster debugging

For real scores, JSON is not the most useful way to inspect clusters. Use:

```powershell
dotnet run --project SvgScene -- score.svg --debug
```

This writes `score.clusters.svg` next to the source SVG.

Contour instances get:

- a bounding box;
- a stable color derived from the prototype number;
- a label such as `p7`, `p12`, etc.

Normalized strokes are drawn as semi-transparent cyan centre lines. This makes it visually obvious which geometry was removed from contour clustering.

All contour instances with the same label belong to the same prototype. The debug overlay is appended as a separate SVG group and does not modify the original file.

Flags can be combined:

```powershell
dotnet run --project SvgScene -- score.svg result.json --debug --verbose-json
```

## Responsibilities

### SvgNormalizer

Converts exporter-specific SVG structure into absolute geometry. SVG syntax is treated as input syntax only; an SVG rectangle is not a semantic rectangle in the internal model.

The parser support is still intentionally narrow; `GeometricScene` is not tied to those limitations.

### GeometricScene

Contains independent absolute geometric shapes with source metadata, closure information and declared stroke width. There is no music semantics at this layer.

### GeometryAnalyzer

Separates line-like geometry from symbol contours. Its output is independent of whether the source used `<line>`, `<polyline>`, a thin `<rect>`, `<polygon>` or a closed `<path>`.

### ShapeClusterer

Only contours reach this stage. It normalizes translation and scale, builds a simple geometry descriptor and greedily groups near-identical shapes.

The initial distance metric is deterministic and intentionally simple. It is a baseline, not the final matching algorithm.

### NotationScene

Stores:

- `prototypes`: one representative contour plus its normalized descriptor;
- `instances`: where each contour instance occurs in the score;
- `strokes`: normalized line-like geometry.

A future `SymbolClassifier` can classify one prototype and thereby label all of its contour instances. A later structural interpreter can reason about strokes using score context.

## Why Python is in the repository

Fixture generation and later geometry/data experiments do not need to be forced into .NET. Python tools are first-class repository utilities; production pipeline components can still live in focused .NET projects where that is useful.

## Next fixture

Add negative examples and stroke examples: hollow noteheads, staccato dots, stems, beams and accidentals. The next important assertion is not only "merge duplicates", but also "do not merge visually different symbols" and "normalize equivalent line-like SVG encodings to strokes".
