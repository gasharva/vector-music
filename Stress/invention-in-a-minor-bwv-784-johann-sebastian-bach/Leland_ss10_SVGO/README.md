# SVG Music stress-test variant

Source score: invention-in-a-minor-bwv-784-johann-sebastian-bach.mscz
Source SHA256: 90587BDB8BAD7066E937BC464A3A0BD956D9F36AB66BA52FA0AA3D217A9ECFC4
Generated: 2026-09-20 22:39:48 +03:00
Random seed: 1358917307
Variant index: 1
Max systems: 2
Max measures: 10
Reference measures: 10

## Style

Staff-space factor: 1
Original spatium: 1.75
Effective spatium: 1.75
Musical symbol font: Leland
Musical text font: Leland Text

## Rendering

Renderer: SVGO
Pipeline: MuseScore -> PDF -> pdftocairo -> SVG -> SVGO
SVG pages: 1

Files:
- reference.musicxml
- page-001.svg ... page-001.svg

## Tools

MuseScore: D:\Program Files\MuseScore 4\bin\MuseScore4.exe
pdftocairo: D:\musica\svg\poppler-26.07.0\Library\bin\pdftocairo.exe
pdfinfo: D:\musica\svg\poppler-26.07.0\Library\bin\pdfinfo.exe
SVGO: C:\Users\Zakirovl\AppData\Roaming\npm\npx.cmd --yes svgo

## Notes

- Staff-space values 0.8 / 0.9 / 1.0 are treated as multipliers of the imported score's spatium.
- If MaxSystems and/or MaxMeasures are set, reference.musicxml is truncated before any rendering variant is produced.
- When both limits are present, the script keeps the smaller scope: first N systems OR first M measures, whichever ends earlier.
- MuseScore 4 style is edited in the generated score_style.mss sidecar; the complete uncompressed score folder is then packed into render-source.mscz before rendering.
- MuseScore converter calls use -f/--force, the CLI equivalent of GUI 'Open anyway', so recoverable score-validation warnings do not abort rendering.
- MuseScore first exports the source MSCZ to reference.musicxml.
- The rendering source is then re-imported from that same MusicXML into a temporary MSCX.
- This deliberately avoids comparing an MSCZ rendering against MuseScore's lossy/normalized MSCZ -> MusicXML export.
- Style values are modified directly in that temporary MSCX:
  - spatium / Spatium
  - musicalSymbolFont
  - musicalTextFont
- reference.musicxml is exported once from the source MSCZ; every rendered variant is then re-imported from that exact MusicXML.
- No MuseScore SVG class/id metadata is intentionally used by the recognition pipeline.
