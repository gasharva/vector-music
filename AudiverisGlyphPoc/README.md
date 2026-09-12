# AudiverisGlyphPoc

Standalone .NET 8 experiment for reusing the tiny CPU-only Audiveris `BasicClassifier` outside the full Audiveris application.

## Goal

Input will eventually be one already-isolated glyph from our SVG pipeline. The PoC intentionally does **not** perform staff detection, segmentation or OMR page analysis.

Target flow:

```text
SVG contour / prototype
    -> raster foreground pixels
    -> Audiveris MixGlyphDescriptor
       (ART moments + geometric moments + aspect)
    -> normalize with means/stds
    -> shallow sigmoid neural network
    -> ranked Audiveris shape labels
```

## Current state

Implemented:

- on-demand download/cache of the official Audiveris `basic-classifier.zip`;
- reading `model.xml`, `means.xml`, `stds.xml`;
- CPU forward pass compatible with Audiveris' one-hidden-layer sigmoid network;
- inspection CLI that prints model dimensions and output labels.

Still to port before we can classify our contours:

- Audiveris `MixGlyphDescriptor`;
- ART moments;
- geometric moments;
- conversion of our contour/prototype into the foreground point set expected by those descriptors;
- golden tests against Audiveris itself.

## Run

```bash
dotnet run --project AudiverisGlyphPoc/AudiverisGlyphPoc.csproj
```

The first run downloads the official model to the local application-data cache. A local model can be supplied explicitly:

```bash
dotnet run --project AudiverisGlyphPoc/AudiverisGlyphPoc.csproj -- path/to/basic-classifier.zip
```

## Why this branch exists

This is deliberately isolated from `SvgScene`. We first want a byte-for-byte / score-for-score faithful classifier port. Only after that will we connect it to `ShapePrototype`.

## Upstream and license

The model format and classifier algorithm are derived from Audiveris. Audiveris is licensed under AGPL-3.0-or-later. This PoC must not be treated as license-neutral code when reused elsewhere.

Upstream repository: `Audiveris/audiveris`.
