from pathlib import Path
import subprocess

script = Path('.github/mimino-final/apply.py')
text = script.read_text()
old = '''def replace_once(path: str, old: str, new: str) -> None:\n    p = Path(path)\n    text = p.read_text()\n    count = text.count(old)\n    if count != 1:\n        raise RuntimeError(f"{path}: expected one occurrence, found {count}\\n--- needle ---\\n{old}")\n    p.write_text(text.replace(old, new, 1))\n'''
new = '''def replace_once(path: str, old: str, new: str) -> None:\n    p = Path(path)\n    text = p.read_text()\n    count = text.count(old)\n    if count == 1:\n        p.write_text(text.replace(old, new, 1))\n        return\n    if count == 2 and "reason,\\n            [element.ShapeId]));\\n        decisions.Add(new ClassifiedSymbolDecision(" in old:\n        index = text.rfind(old)\n        p.write_text(text[:index] + new + text[index + len(old):])\n        return\n    if (count == 2\n        and path.endswith("ClassifiedSymbolPassTests.cs")\n        and old == "        double x,\\n        double y)\\n    {\\n"):\n        method = text.index("    private static ShapeElement Symbol(")\n        index = text.index(old, method)\n        p.write_text(text[:index] + new + text[index + len(old):])\n        return\n    raise RuntimeError(f"{path}: expected one occurrence, found {count}\\n--- needle ---\\n{old}")\n'''
if old not in text:
    raise RuntimeError('replace_once helper definition not found')
script.write_text(text.replace(old, new, 1))
subprocess.check_call(['python3', str(script)])

# Preserve the old semantic guard diagnostic for brackets that are not actually
# below a lower stave. Geometry recovery should ignore a wrong owner, but an
# above-staff bracket must still map to a nearby lower stave and be rejected by
# IsBelowStaff rather than looking "unmapped".
pedal = Path('SemanticInterpreter/PedalPass.cs')
pedal_text = pedal.read_text()
old_method = '''    private static CoordinateContext? ResolvePedalContextAtX(
        IReadOnlyList<CoordinateContext> contexts,
        double x,
        double baselineY,
        bool preferLaterAtBoundary)
    {
        var candidates = contexts
            .Where(context => context.StaffNumber == 2)
            .Where(context =>
                x >= context.XStart - CoordinateEpsilon
                && x <= context.XEnd + CoordinateEpsilon)
            .Select(context => new
            {
                Context = context,
                Gap = baselineY - context.StaffBounds.MaxY,
                Tolerance = Math.Max(context.LineSpacing, 0.001)
                    * OutsideStaffToleranceInSpacings
            })
            // A pedal line is semantically below the lower stave. This deliberately
            // ignores a generic owner inherited from nearby geometry in the next
            // system: the closest lower stave above the line is the only plausible one.
            .Where(candidate => candidate.Gap >= -candidate.Tolerance)
            .OrderBy(candidate =>
                candidate.Gap / Math.Max(candidate.Context.LineSpacing, 0.001))
            .ThenBy(candidate => preferLaterAtBoundary
                ? -candidate.Context.XStart
                : candidate.Context.XEnd)
            .ThenBy(candidate => candidate.Context.MeasureNumber)
            .Select(candidate => candidate.Context)
            .FirstOrDefault();

        return candidates;
    }
'''
new_method = '''    private static CoordinateContext? ResolvePedalContextAtX(
        IReadOnlyList<CoordinateContext> contexts,
        double x,
        double baselineY,
        bool preferLaterAtBoundary)
    {
        var candidates = contexts
            .Where(context => context.StaffNumber == 2)
            .Where(context =>
                x >= context.XStart - CoordinateEpsilon
                && x <= context.XEnd + CoordinateEpsilon)
            .Select(context => new
            {
                Context = context,
                Gap = baselineY - context.StaffBounds.MaxY,
                Tolerance = Math.Max(context.LineSpacing, 0.001)
                    * OutsideStaffToleranceInSpacings
            })
            .ToArray();

        // Prefer the closest lower stave physically above the pedal baseline. This
        // recovers inter-system pedals whose generic owner leaked into the next row.
        var belowStaff = candidates
            .Where(candidate => candidate.Gap >= -candidate.Tolerance)
            .OrderBy(candidate =>
                candidate.Gap / Math.Max(candidate.Context.LineSpacing, 0.001))
            .ThenBy(candidate => preferLaterAtBoundary
                ? -candidate.Context.XStart
                : candidate.Context.XEnd)
            .ThenBy(candidate => candidate.Context.MeasureNumber)
            .Select(candidate => candidate.Context)
            .FirstOrDefault();

        if (belowStaff is not null)
        {
            return belowStaff;
        }

        // Keep enough context for IsBelowStaff to reject genuinely above-staff
        // brackets with the precise semantic reason instead of "unmapped".
        return candidates
            .OrderBy(candidate =>
                Math.Abs(candidate.Gap)
                / Math.Max(candidate.Context.LineSpacing, 0.001))
            .ThenBy(candidate => candidate.Context.MeasureNumber)
            .Select(candidate => candidate.Context)
            .FirstOrDefault();
    }
'''
if pedal_text.count(old_method) != 1:
    raise RuntimeError('patched ResolvePedalContextAtX method not found exactly once')
pedal.write_text(pedal_text.replace(old_method, new_method, 1))
