# SvgCanonicalizer

Geometry-only SVG preprocessor for arbitrary notation sources.

The canonicalizer deliberately ignores producer hints such as source classes,
ids and data attributes as recognition evidence. Its first invariant is visual:
the generated SVG should render the same picture as the input.

## Current approach

The input is parsed and rendered by Svg.Skia into an Skia picture. That picture
is replayed into `SKSvgCanvas`, which writes a fresh SVG from visible drawing
operations rather than copying the producer DOM.

This removes source-specific `<use>` indirection and producer metadata while
preserving curves, fills, holes and strokes much more faithfully than the
analysis-oriented `SvgScene.SvgNormalizer` round-trip.

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

The output is intended to be fed unchanged into SvgScene / SemanticInterpreter.
