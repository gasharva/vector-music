from pathlib import Path

path = Path("SemanticInterpreter/ClassifiedSymbolPass.cs")
text = path.read_text(encoding="utf-8")

old = '''    private static void ProjectDynamic(
        MeasureScene measure,
        StaffMeasureScene staff,
        ShapeElement element,
        string label,
        string value,
        double classificationConfidence,
        IReadOnlyList<OnsetFact> onsets,
        SemanticFacts facts,
        ICollection<ClassifiedSymbolDecision> decisions)
    {
        var anchor = onsets
            .Where(onset =>
                onset.MeasureNumber == measure.Number
                && onset.Staff == staff.StaffNumber)
            .OrderBy(onset => Math.Abs(onset.AnchorX - element.CenterX))
            .ThenBy(onset => FractionValue(Fraction.Parse(onset.At)))
            .ThenBy(onset => onset.TargetId, StringComparer.Ordinal)
            .FirstOrDefault();

        if (anchor is null)
        {
            decisions.Add(Reject(
                element,
                label,
                measure.Number,
                staff.StaffNumber,
                "no-rhythmic-anchor",
                classificationConfidence,
                $"{label} has no rhythmic onset on owned staff/measure"));
            return;
        }

        var dx = Math.Abs(anchor.AnchorX - element.CenterX);
        var maxDx = Math.Max(1.0, staff.LineSpacing * MaximumDynamicDxInSpacings);
        if (dx > maxDx)
        {
            decisions.Add(Reject(
                element,
                label,
                measure.Number,
                staff.StaffNumber,
                "dynamic-anchor-too-far",
                classificationConfidence,
                $"{label} nearest onset is {dx / Math.Max(staff.LineSpacing, 0.001):F2} spacings away in X"));
            return;
        }

        var placement = PlacementAgainstStaff(element.CenterY, staff.StaffBounds);
        var reason =
            $"{element.ShapeId}: {label} {classificationConfidence:P0} -> dynamic {value}; "
            + $"m{measure.Number}:{anchor.At}; staff={staff.StaffNumber}; placement={placement}; "
            + $"dx={dx / Math.Max(staff.LineSpacing, 0.001):F2}sp";

        facts.Add(new DynamicDirectionFact(
            measure.Number,
            staff.StaffNumber,
            element.ShapeId,
            value,
            anchor.At,
            placement,
            label,
            classificationConfidence,
            element.CenterX,
            element.CenterY,
            classificationConfidence,
            reason,
            [element.ShapeId]));
        decisions.Add(new ClassifiedSymbolDecision(
            element.ShapeId,
            label,
            true,
            "dynamic",
            measure.Number,
            staff.StaffNumber,
            null,
            anchor.At,
            classificationConfidence,
            reason));
    }
'''

new = '''    private static void ProjectDynamic(
        MeasureScene measure,
        StaffMeasureScene ownedStaff,
        ShapeElement element,
        string label,
        string value,
        double classificationConfidence,
        IReadOnlyList<OnsetFact> onsets,
        SemanticFacts facts,
        ICollection<ClassifiedSymbolDecision> decisions)
    {
        // Directions engraved between the two piano staves are especially easy for
        // generic ownership to assign to the wrong side. Ownership still identifies
        // the correct measure/piano pair; the semantic staff is the nearest stave.
        var staff = ResolveNearestStaff(
            measure,
            element.CenterY,
            ownedStaff.StaffNumber);
        var anchor = onsets
            .Where(onset =>
                onset.MeasureNumber == measure.Number
                && onset.Staff == staff.StaffNumber)
            .OrderBy(onset => Math.Abs(onset.AnchorX - element.CenterX))
            .ThenBy(onset => FractionValue(Fraction.Parse(onset.At)))
            .ThenBy(onset => onset.TargetId, StringComparer.Ordinal)
            .FirstOrDefault();

        if (anchor is null)
        {
            decisions.Add(Reject(
                element,
                label,
                measure.Number,
                staff.StaffNumber,
                "no-rhythmic-anchor",
                classificationConfidence,
                $"{label} has no rhythmic onset on resolved staff/measure"));
            return;
        }

        var dx = Math.Abs(anchor.AnchorX - element.CenterX);
        var maxDx = Math.Max(1.0, staff.LineSpacing * MaximumDynamicDxInSpacings);
        if (dx > maxDx)
        {
            decisions.Add(Reject(
                element,
                label,
                measure.Number,
                staff.StaffNumber,
                "dynamic-anchor-too-far",
                classificationConfidence,
                $"{label} nearest onset is {dx / Math.Max(staff.LineSpacing, 0.001):F2} spacings away in X"));
            return;
        }

        var placement = PlacementAgainstStaff(element.CenterY, staff.StaffBounds);
        var reason =
            $"{element.ShapeId}: {label} {classificationConfidence:P0} -> dynamic {value}; "
            + $"m{measure.Number}:{anchor.At}; genericStaff={ownedStaff.StaffNumber}; "
            + $"semanticStaff={staff.StaffNumber}; placement={placement}; "
            + $"dx={dx / Math.Max(staff.LineSpacing, 0.001):F2}sp";

        facts.Add(new DynamicDirectionFact(
            measure.Number,
            staff.StaffNumber,
            element.ShapeId,
            value,
            anchor.At,
            placement,
            label,
            classificationConfidence,
            element.CenterX,
            element.CenterY,
            classificationConfidence,
            reason,
            [element.ShapeId]));
        decisions.Add(new ClassifiedSymbolDecision(
            element.ShapeId,
            label,
            true,
            "dynamic",
            measure.Number,
            staff.StaffNumber,
            null,
            anchor.At,
            classificationConfidence,
            reason));
    }
'''

if text.count(old) != 1:
    raise RuntimeError(f"ProjectDynamic replacement expected 1 match, got {text.count(old)}")
text = text.replace(old, new, 1)

anchor = '''    private static string PlacementAgainstStaff(
        double y,
        BoundsD bounds)
'''
helper = '''    private static StaffMeasureScene ResolveNearestStaff(
        MeasureScene measure,
        double y,
        int ownedStaffNumber)
    {
        return new[] { measure.Upper, measure.Lower }
            .OrderBy(staff => DistanceToStaff(y, staff.StaffBounds))
            .ThenByDescending(staff => staff.StaffNumber == ownedStaffNumber)
            .ThenBy(staff => staff.StaffNumber)
            .First();
    }

    private static double DistanceToStaff(
        double y,
        BoundsD bounds)
    {
        if (y < bounds.MinY)
        {
            return bounds.MinY - y;
        }

        if (y > bounds.MaxY)
        {
            return y - bounds.MaxY;
        }

        return 0;
    }

'''
if text.count(anchor) != 1:
    raise RuntimeError(f"staff helper anchor expected 1 match, got {text.count(anchor)}")
text = text.replace(anchor, helper + anchor, 1)
path.write_text(text, encoding="utf-8")
