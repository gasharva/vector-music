from pathlib import Path

path = Path("SemanticInterpreter/PedalPass.cs")
text = path.read_text(encoding="utf-8")
old = """            var startAt = timing.ResolveStartAt(
                startContext.MeasureNumber,
                startContext.StaffNumber,
                source.Start.X);
"""
new = """            // The PEDAL_MARK itself is the musical down-point. The horizontal
            // continuation line starts to its right and can already be closer to the
            // next note, so using the line start would shift the pedal onset late.
            var startAt = timing.ResolveStartAt(
                startContext.MeasureNumber,
                startContext.StaffNumber,
                match.Label.CenterX);
"""
if old not in text:
    raise RuntimeError("Pedal start timing anchor not found")
path.write_text(text.replace(old, new, 1), encoding="utf-8")
