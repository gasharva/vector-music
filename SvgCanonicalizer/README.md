# SvgCanonicalizer

Geometry-only preprocessor for arbitrary SVG notation.

It deliberately does **not** use SVG producer hints (classes, ids, data attributes) as recognition evidence. The existing `SvgScene.SvgNormalizer` resolves visible geometry, including `<use>` instances and transforms. This project then writes a flat SVG containing only explicit path geometry and paint attributes.

## Current V1

- resolves `<use>` through SvgScene geometry normalization;
- flattens transforms into coordinates;
- removes defs/groups/classes/ids/data-* from the output;
- converts supported visible geometry to flat `<path>` elements;
- samples curves into explicit line geometry;
- preserves multiple fill contours together with even-odd fill so holes survive.

No musical semantics are performed here.

## CLI

Single file:

```powershell
dotnet run --project SvgCanonicalizer -- input.svg output.svg
```

Directory:

```powershell
dotnet run --project SvgCanonicalizer -- `
  Samples/cairo-yellow-leaves/optimized `
  artifacts/cairo-yellow-leaves-canonical
```
