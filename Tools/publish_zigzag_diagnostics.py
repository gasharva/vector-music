#!/usr/bin/env python3

import html
import json
import sys
from pathlib import Path
import xml.etree.ElementTree as ET


def svg_tag(namespace: str, local: str) -> str:
    return f"{{{namespace}}}{local}" if namespace else local


def add_svg_overlay(source_svg: Path, zigzags: list[dict], output_svg: Path) -> None:
    tree = ET.parse(source_svg)
    root = tree.getroot()
    namespace = root.tag.split("}", 1)[0][1:] if root.tag.startswith("{") else ""
    if namespace:
        ET.register_namespace("", namespace)

    group = ET.SubElement(root, svg_tag(namespace, "g"), {
        "id": "vertical-zigzag-debug",
        "fill": "none",
        "stroke": "#ff00aa",
        "stroke-width": "2",
        "vector-effect": "non-scaling-stroke",
    })

    for zigzag in zigzags:
        bounds = zigzag["Bounds"]
        min_x = float(bounds["MinX"])
        min_y = float(bounds["MinY"])
        max_x = float(bounds["MaxX"])
        max_y = float(bounds["MaxY"])
        width = max_x - min_x
        height = max_y - min_y
        pad = max(1.5, min(width, height) * 0.15)

        ET.SubElement(group, svg_tag(namespace, "rect"), {
            "x": f"{min_x - pad:.3f}",
            "y": f"{min_y - pad:.3f}",
            "width": f"{width + pad * 2:.3f}",
            "height": f"{height + pad * 2:.3f}",
            "rx": "2",
            "stroke-dasharray": "6,3",
        })

        ET.SubElement(group, svg_tag(namespace, "circle"), {
            "cx": f"{(min_x + max_x) / 2:.3f}",
            "cy": f"{(min_y + max_y) / 2:.3f}",
            "r": "2.5",
            "fill": "#ff00aa",
            "stroke": "none",
        })

        text = ET.SubElement(group, svg_tag(namespace, "text"), {
            "x": f"{max_x + pad + 2:.3f}",
            "y": f"{min_y:.3f}",
            "fill": "#ff00aa",
            "stroke": "none",
            "font-size": "12",
            "font-family": "monospace",
        })
        text.text = (
            f"zigzag {zigzag['ShapeId']} "
            f"turns={zigzag['TurnCount']} "
            f"conf={float(zigzag['Confidence']):.0%}"
        )

    tree.write(output_svg, encoding="utf-8", xml_declaration=True)


def append_summary(summary_path: Path, count: int, svg_path: Path, scene_path: Path) -> None:
    text = summary_path.read_text(encoding="utf-8")
    lines = [line for line in text.splitlines() if not line.startswith("parser.verticalZigZags=") and not line.startswith("parser.zigzags=") and not line.startswith("parser.zigzagScene=")]
    lines.extend([
        f"parser.verticalZigZags={count}",
        f"parser.zigzags={svg_path.resolve()}",
        f"parser.zigzagScene={scene_path.resolve()}",
    ])
    summary_path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def add_index_link(index_path: Path, count: int) -> None:
    text = index_path.read_text(encoding="utf-8")
    marker = "<!-- zigzag-diagnostics -->"
    block = f"""
  {marker}
  <section style="margin-top:2rem">
    <h2>Vertical zigzag diagnostics</h2>
    <p>Detected geometric vertical zigzags: <strong>{count}</strong>.</p>
    <p><a href="parser.zigzags.svg">Open parser.zigzags.svg</a> &mdash; magenta boxes mark candidates removed before generic glyph classification.</p>
    <p><a href="zigzag-scene.json">Open zigzag-scene.json</a> &mdash; raw primitive geometry and confidence.</p>
  </section>
"""
    if marker in text:
        start = text.index(marker)
        section_start = text.rfind("<section", 0, start)
        section_end = text.find("</section>", start)
        if section_start >= 0 and section_end >= 0:
            text = text[:section_start] + block + text[section_end + len("</section>"):]
        else:
            text = text.replace(marker, block)
    elif "</body>" in text:
        text = text.replace("</body>", block + "</body>")
    else:
        text += block
    index_path.write_text(text, encoding="utf-8")


def add_readme_link(readme_path: Path, count: int) -> None:
    text = readme_path.read_text(encoding="utf-8")
    heading = "## Vertical zigzag diagnostics"
    if heading in text:
        text = text.split(heading, 1)[0].rstrip() + "\n\n"
    text += (
        f"{heading}\n\n"
        f"Detected geometric vertical zigzags: **{count}**.\n\n"
        "- [parser.zigzags.svg](parser.zigzags.svg) — visual overlay of detected candidates.\n"
        "- [zigzag-scene.json](zigzag-scene.json) — raw primitive geometry and confidence.\n"
    )
    readme_path.write_text(text, encoding="utf-8")


def main() -> int:
    if len(sys.argv) != 4:
        print("usage: publish_zigzag_diagnostics.py <zigzag-scene.json> <source.svg> <artifact-dir>", file=sys.stderr)
        return 2

    scene_path = Path(sys.argv[1])
    source_svg = Path(sys.argv[2])
    artifact_dir = Path(sys.argv[3])
    data = json.loads(scene_path.read_text(encoding="utf-8-sig"))
    zigzags = data.get("verticalZigZags") or []

    output_svg = artifact_dir / "parser.zigzags.svg"
    add_svg_overlay(source_svg, zigzags, output_svg)
    append_summary(artifact_dir / "semantic-summary.txt", len(zigzags), output_svg, scene_path)
    add_index_link(artifact_dir / "index.html", len(zigzags))
    add_readme_link(artifact_dir / "README.md", len(zigzags))

    print(f"vertical zigzags: {len(zigzags)}")
    print(f"wrote: {output_svg}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
