# Kancheli internet SVG CI fixture

This directory stores one SVG test fixture as gzip-compressed Base64 chunks.

The chunks are intentionally machine data. CI reconstructs them byte-for-byte before running the `SvgScene` pipeline.

Expected SHA-256 of the reconstructed SVG:

```text
50ebc277329e20ca2cc1f7efc055eb7d9f04b8765cca34b5a90509e0f7fdd2fe
```

Reconstruction on Linux/macOS:

```bash
cat part-* \
  | base64 --decode \
  | gzip --decompress \
  > almonds-internet.svg
```

The workflow verifies the checksum before using the fixture, so a missing, duplicated, or reordered chunk fails early instead of producing misleading geometry diagnostics.
