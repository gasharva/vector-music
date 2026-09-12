from pathlib import Path
import random

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "Fixtures" / "shape-clustering-noteheads.svg"

BASE_PATH = """M 1.0 3.9 C 1.0 1.6 3.2 0.2 5.8 0.2 C 8.2 0.2 9.8 1.5 9.8 3.3 C 9.8 5.5 7.7 6.8 5.0 6.8 C 2.5 6.8 1.0 5.6 1.0 3.9 Z"""

def perturb_path(i):
    rng = random.Random(1000 + i)
    vals = [
        (1.0, 3.9),
        (1.0, 1.6), (3.2, 0.2), (5.8, 0.2),
        (8.2, 0.2), (9.8, 1.5), (9.8, 3.3),
        (9.8, 5.5), (7.7, 6.8), (5.0, 6.8),
        (2.5, 6.8), (1.0, 5.6), (1.0, 3.9)
    ]
    p = []
    for x, y in vals:
        p.append((x + rng.uniform(-0.12, 0.12),
                  y + rng.uniform(-0.12, 0.12)))
    return (
        f"M {p[0][0]:.3f} {p[0][1]:.3f} "
        f"C {p[1][0]:.3f} {p[1][1]:.3f} {p[2][0]:.3f} {p[2][1]:.3f} {p[3][0]:.3f} {p[3][1]:.3f} "
        f"C {p[4][0]:.3f} {p[4][1]:.3f} {p[5][0]:.3f} {p[5][1]:.3f} {p[6][0]:.3f} {p[6][1]:.3f} "
        f"C {p[7][0]:.3f} {p[7][1]:.3f} {p[8][0]:.3f} {p[8][1]:.3f} {p[9][0]:.3f} {p[9][1]:.3f} "
        f"C {p[10][0]:.3f} {p[10][1]:.3f} {p[11][0]:.3f} {p[11][1]:.3f} {p[12][0]:.3f} {p[12][1]:.3f} Z"
    )

items = []
for idx in range(100):
    r, c = divmod(idx, 10)
    x = 18 + c * 22
    y = 18 + r * 18

    if idx < 25:
        items.append(f'  <use href="#notehead" x="{x}" y="{y}" data-kind="use" data-index="{idx}"/>')
    elif idx < 50:
        items.append(f'  <path d="{BASE_PATH}" transform="translate({x} {y})" data-kind="duplicate-path" data-index="{idx}"/>')
    elif idx < 75:
        items.append(f'  <path d="{BASE_PATH}" transform="matrix(1 0 0 1 {x} {y})" data-kind="matrix" data-index="{idx}"/>')
    else:
        items.append(f'  <path d="{perturb_path(idx)}" transform="translate({x} {y})" data-kind="perturbed" data-index="{idx}"/>')

svg = f"""<?xml version="1.0" encoding="UTF-8"?>
<svg xmlns="http://www.w3.org/2000/svg" width="240" height="205" viewBox="0 0 240 205">
  <title>Shape clustering stress test: 100 visually identical noteheads</title>
  <defs>
    <path id="notehead" d="{BASE_PATH}"/>
  </defs>
  <!-- 0..24 use; 25..49 duplicate path; 50..74 matrix; 75..99 perturbed -->
{chr(10).join(items)}
</svg>
"""

OUT.parent.mkdir(parents=True, exist_ok=True)
OUT.write_text(svg, encoding="utf-8")
print(OUT)
