# SvgScene PoC

First SVG-side pipeline for `vector-music`:

```text
SvgNormalizer
    ↓
GeometricScene
    ↓
ShapeClusterer
    ↓
NotationScene
       ├── prototypes
       └── instances
```

This project deliberately stops **before** musical symbol recognition. A prototype is only a repeated geometric shape family; it is not yet known to be a notehead, accidental, clef, dot, etc.

## Current PoC

The first generated fixture contains 100 visually identical filled-notehead-like shapes:

- 25 `<use>` instances of one path;
- 25 duplicated paths with `translate(...)`;
- 25 duplicated paths with a matrix translation;
- 25 paths whose Bézier control points are slightly perturbed.

The expected result is:

```text
GeometricScene shapes:       100
NotationScene prototypes:      1
NotationScene instances:      100
```

Generate the deterministic fixture:

```powershell
python SvgScene/Tools/generate_shape_clustering_noteheads.py
```

Then run the PoC:

```powershell
dotnet run --project SvgScene -- SvgScene/Fixtures/shape-clustering-noteheads.svg
```

A compact `notation-scene.json` file is written by default. It keeps prototype summaries and instances, but omits the very large normalized point arrays.

For the full descriptor JSON:

```powershell
dotnet run --project SvgScene -- score.svg --verbose-json
```

This additionally writes `notation-scene.verbose.json`.

## Visual cluster debugging

For real scores, JSON is not the most useful way to inspect clusters. Use:

```powershell
dotnet run --project SvgScene -- score.svg --debug
```

This writes `score.clusters.svg` next to the source SVG. Every shape instance gets:

- a bounding box;
- a stable color derived from the prototype number;
- a label such as `p7`, `p12`, etc.

All instances with the same label belong to the same prototype. The debug overlay is appended as a separate SVG group and does not modify the original file.

Flags can be combined:

```powershell
dotnet run --project SvgScene -- score.svg result.json --debug --verbose-json
```

## Responsibilities

### SvgNormalizer

Converts exporter-specific SVG structure into absolute geometry. The PoC currently resolves:

- `<path>`
- `<use href="#...">`
- `<defs>`
- `translate(...)`
- `matrix(...)`
- path commands `M`, `L`, `C`, `Z`

The parser support is intentionally narrow for the first fixture; `GeometricScene` is not tied to those limitations.

### GeometricScene

Contains independent absolute geometric shapes. There is no music semantics at this layer.

### ShapeClusterer

Normalizes translation and scale, builds a simple geometry descriptor and greedily groups near-identical shapes.

The initial distance metric is deterministic and intentionally simple. It is a baseline, not the final matching algorithm.

### NotationScene

Stores:

- `prototypes`: one representative shape plus its normalized descriptor;
- `instances`: where each shape instance occurs in the score.

A future `SymbolClassifier` can classify one prototype and thereby label all of its instances.

## Why Python is in the repository

Fixture generation and later geometry/data experiments do not need to be forced into .NET. Python tools are first-class repository utilities; production pipeline components can still live in focused .NET projects where that is useful.

## Next fixture

Add negative examples: hollow noteheads, staccato dots, stems, beams and accidentals. The next important assertion is not only "merge duplicates", but also "do not merge visually different symbols".
