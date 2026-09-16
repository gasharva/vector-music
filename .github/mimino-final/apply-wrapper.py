from pathlib import Path
import subprocess

script = Path('.github/mimino-final/apply.py')
text = script.read_text()
old = '''def replace_once(path: str, old: str, new: str) -> None:\n    p = Path(path)\n    text = p.read_text()\n    count = text.count(old)\n    if count != 1:\n        raise RuntimeError(f"{path}: expected one occurrence, found {count}\\n--- needle ---\\n{old}")\n    p.write_text(text.replace(old, new, 1))\n'''
new = '''def replace_once(path: str, old: str, new: str) -> None:\n    p = Path(path)\n    text = p.read_text()\n    count = text.count(old)\n    if count == 1:\n        p.write_text(text.replace(old, new, 1))\n        return\n    if count == 2 and "reason,\\n            [element.ShapeId]));\\n        decisions.Add(new ClassifiedSymbolDecision(" in old:\n        index = text.rfind(old)\n        p.write_text(text[:index] + new + text[index + len(old):])\n        return\n    raise RuntimeError(f"{path}: expected one occurrence, found {count}\\n--- needle ---\\n{old}")\n'''
if old not in text:
    raise RuntimeError('replace_once helper definition not found')
script.write_text(text.replace(old, new, 1))
subprocess.check_call(['python3', str(script)])
