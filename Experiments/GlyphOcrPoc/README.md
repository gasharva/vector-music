# GlyphOcrPoc

Small isolated experiment: feed individual SVG glyph candidates into PP-OCRv5 Latin and inspect what it predicts.

The PoC intentionally does **not** change the production vector-music pipeline.

## Input

Use a directory containing standalone glyph SVG files exported from diagnostics / classifier input.

## Run

```powershell
dotnet run --project Experiments/GlyphOcrPoc -- `
  <glyph-svg-dir> `
  artifacts\glyph-ocr
```

The `RapidOcrNet` NuGet package supplies the PP-OCRv5 Latin model set, so no separate Python environment is needed.

## What it does

For every `.svg` under the input directory:

1. render the vector glyph to a white 256x128 PNG with padding;
2. run PP-OCRv5 Latin OCR on the PNG;
3. print predicted text, mean character confidence and inference time;
4. write all rows to `results.tsv`;
5. keep the rendered PNGs in `png/` for visual inspection;
6. write `results.html` with a compact visual gallery of glyphs and predictions.

This first experiment deliberately keeps the OCR detector/classifier/recognizer pipeline intact. If single-glyph detection turns out to be the weak link, the production `SvgScene` integration can keep the `ITextRecognizer` abstraction and swap the engine later.

## Expected output

```text
[   1/123] glyph-0001.svg => '4'  conf=0.982  6.1 ms
[   2/123] glyph-0002.svg => 'p'  conf=0.947  5.4 ms
[   3/123] glyph-0003.svg => ''   conf=0.000  4.9 ms
```

`results.tsv` columns:

```text
glyph    predicted    confidence    ms    png    error
```

The empty predictions are useful too: we want to see whether OCR ignores musical notation or confidently hallucinates letters/digits from it.
