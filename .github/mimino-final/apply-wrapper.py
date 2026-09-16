from pathlib import Path

path = Path('SemanticInterpreter/ClassifiedSymbolPass.cs')
text = path.read_text()
old = '            || siblings.Length != 2)\n'
new = '            || siblings.Length < 2)\n'
if text.count(old) != 1:
    raise RuntimeError(f'expected one strict sibling-count guard, found {text.count(old)}')
path.write_text(text.replace(old, new, 1))
print('Relaxed split dynamic sibling count')
