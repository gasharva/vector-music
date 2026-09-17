using SvgMusic.Canonical;
using SvgMusic.Scene;

namespace SvgMusic.Semantics;

public enum ClassifiedNotationFamily
{
    Articulation,
    Ornament,
    Fermata
}

public sealed record ClassifiedNotationMarkFact(
    int MeasureNumber,
    int Staff,
    string ShapeId,
    string TargetNoteheadId,
    ClassifiedNotationFamily Family,
    string Type,
    string Placement,
    string ClassificationLabel,
    double ClassificationConfidence,
    double CenterX,
    double CenterY,
    double Confidence,
    string Reason,
    IReadOnlyList<string> SourceShapeIds)
    : SemanticFact(
        "ClassifiedSymbolPass",
        Reason,
        SourceShapeIds);

public sealed record DynamicDirectionFact(
    int MeasureNumber,
    int Staff,
    string ShapeId,
    string Value,
    string At,
    string Placement,
    string ClassificationLabel,
    double ClassificationConfidence,
    double CenterX,
    double CenterY,
    double Confidence,
    string Reason,
    IReadOnlyList<string> SourceShapeIds)
    : SemanticFact(
        "ClassifiedSymbolPass",
        Reason,
        SourceShapeIds);

public sealed record ClassifiedSymbolDecision(
    string ShapeId,
    string Label,
    bool Accepted,
    string Decision,
    int MeasureNumber,
    int Staff,
    string? Target,
    string? At,
    double Confidence,
    string Reason);

public sealed record ClassifiedSymbolAnalysisResult(
    IReadOnlyList<ClassifiedSymbolDecision> Decisions)
{
    public IReadOnlyList<ClassifiedSymbolDecision> Accepted =>
        Decisions.Where(decision => decision.Accepted).ToArray();
}

/// <summary>
/// Last-resort semantic pass for simple, confidently classified symbols that survived
/// all specialized passes. It deliberately uses a small whitelist: geometric/rhythmic
/// constructs remain owned by their dedicated passes, while simple notation marks and
/// complete dynamic glyphs can be projected directly.
/// </summary>
public sealed class ClassifiedSymbolPass : ISemanticPass
{
    private const double MinimumConfidence = 0.90;
    private const double MaximumMarkDxInSpacings = 2.5;
    private const double MaximumDynamicDxInSpacings = 4.0;

    public string Name => nameof(ClassifiedSymbolPass);

    public ClassifiedSymbolAnalysisResult? LastAnalysis { get; private set; }

    public void Run(
        SemanticDocument document,
        SemanticFacts facts)
    {
        // A shape cited by an earlier fact has already been interpreted by a more
        // specialized pass and must never be re-used by this residual pass.
        var consumed = facts.Items
            .SelectMany(fact => fact.SourceShapeIds)
            .ToHashSet(StringComparer.Ordinal);
        var noteheads = facts.OfType<NoteheadFact>().ToArray();
        var onsets = facts.OfType<OnsetFact>().ToArray();
        var shapeGroups = document.Measures
            .SelectMany(measure => new[] { measure.Upper, measure.Lower })
            .SelectMany(staff => staff.Elements.OfType<ShapeElement>())
            .GroupBy(shape => shape.ShapeId, StringComparer.Ordinal)
            .Select(group => group.First())
            .GroupBy(shape => BaseShapeId(shape.ShapeId), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(shape => shape.CenterX).ToArray(),
                StringComparer.Ordinal);
        var decisions = new List<ClassifiedSymbolDecision>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var measure in document.Measures.OrderBy(item => item.Number))
        {
            foreach (var staff in new[] { measure.Upper, measure.Lower })
            {
                foreach (var element in staff.Elements
                             .OfType<ShapeElement>()
                             .OrderBy(item => item.CenterX)
                             .ThenBy(item => item.CenterY)
                             .ThenBy(item => item.ShapeId, StringComparer.Ordinal))
                {
                    if (!seen.Add(element.ShapeId)
                        || element.Classification is null)
                    {
                        continue;
                    }

                    var classification = element.Classification;
                    var label = NormalizeLabel(classification.Label);
                    var effectiveConfidence = classification.Confidence;
                    IReadOnlyList<string> sourceShapeIds = [element.ShapeId];

                    if (!TryMap(label, out var mapping)
                        && !TryComposeSplitDynamic(
                            element,
                            label,
                            staff.LineSpacing,
                            shapeGroups,
                            out label,
                            out mapping,
                            out effectiveConfidence,
                            out sourceShapeIds))
                    {
                        continue;
                    }

                    if (effectiveConfidence < MinimumConfidence)
                    {
                        decisions.Add(Reject(
                            element,
                            label,
                            measure.Number,
                            staff.StaffNumber,
                            "low-confidence",
                            effectiveConfidence,
                            $"{label} confidence {effectiveConfidence:P0} is below {MinimumConfidence:P0}"));
                        continue;
                    }

                    if (sourceShapeIds.Any(consumed.Contains))
                    {
                        decisions.Add(Reject(
                            element,
                            label,
                            measure.Number,
                            staff.StaffNumber,
                            "already-consumed",
                            effectiveConfidence,
                            $"{string.Join(",", sourceShapeIds)} includes geometry already referenced by an earlier semantic fact"));
                        continue;
                    }

                    if (mapping.Family == SymbolFamily.Dynamic)
                    {
                        ProjectDynamic(
                            measure,
                            staff,
                            element,
                            label,
                            mapping.Value,
                            effectiveConfidence,
                            sourceShapeIds,
                            onsets,
                            facts,
                            decisions);
                        continue;
                    }

                    ProjectNotationMark(
                        measure,
                        staff,
                        element,
                        label,
                        mapping,
                        classification.Confidence,
                        noteheads,
                        facts,
                        decisions);
                }
            }
        }

        LastAnalysis = new ClassifiedSymbolAnalysisResult(decisions);
        facts.AddTrace(
            $"ClassifiedSymbolPass: recognized={decisions.Count}; "
            + $"accepted={decisions.Count(decision => decision.Accepted)}; "
            + $"rejected={decisions.Count(decision => !decision.Accepted)}");
    }

    private static void ProjectNotationMark(
        MeasureScene measure,
        StaffMeasureScene staff,
        ShapeElement element,
        string label,
        SymbolMapping mapping,
        double classificationConfidence,
        IReadOnlyList<NoteheadFact> noteheads,
        SemanticFacts facts,
        ICollection<ClassifiedSymbolDecision> decisions)
    {
        var target = noteheads
            .Where(note =>
                note.MeasureNumber == measure.Number
                && note.Staff == staff.StaffNumber)
            .OrderBy(note => Math.Abs(note.CenterX - element.CenterX))
            .ThenBy(note => Math.Abs(note.CenterY - element.CenterY))
            .ThenBy(note => note.ShapeId, StringComparer.Ordinal)
            .FirstOrDefault();

        if (target is null)
        {
            decisions.Add(Reject(
                element,
                label,
                measure.Number,
                staff.StaffNumber,
                "no-note-anchor",
                classificationConfidence,
                $"{label} has no notehead on owned staff/measure"));
            return;
        }

        var dx = Math.Abs(target.CenterX - element.CenterX);
        var maxDx = Math.Max(1.0, staff.LineSpacing * MaximumMarkDxInSpacings);
        if (dx > maxDx)
        {
            decisions.Add(Reject(
                element,
                label,
                measure.Number,
                staff.StaffNumber,
                "note-anchor-too-far",
                classificationConfidence,
                $"{label} nearest notehead {target.ShapeId} is {dx / Math.Max(staff.LineSpacing, 0.001):F2} spacings away in X"));
            return;
        }

        var placement = element.CenterY < target.CenterY
            ? "above"
            : "below";
        var family = mapping.Family switch
        {
            SymbolFamily.Articulation => ClassifiedNotationFamily.Articulation,
            SymbolFamily.Ornament => ClassifiedNotationFamily.Ornament,
            SymbolFamily.Fermata => ClassifiedNotationFamily.Fermata,
            _ => throw new InvalidOperationException($"Unsupported notation family {mapping.Family}")
        };
        var reason =
            $"{element.ShapeId}: {label} {classificationConfidence:P0} -> "
            + $"{family}/{mapping.Value}; target={target.ShapeId}; "
            + $"dx={dx / Math.Max(staff.LineSpacing, 0.001):F2}sp; placement={placement}";

        facts.Add(new ClassifiedNotationMarkFact(
            measure.Number,
            staff.StaffNumber,
            element.ShapeId,
            target.ShapeId,
            family,
            mapping.Value,
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
            "notation-mark",
            measure.Number,
            staff.StaffNumber,
            target.ShapeId,
            null,
            classificationConfidence,
            reason));
    }

    private static void ProjectDynamic(
        MeasureScene measure,
        StaffMeasureScene ownedStaff,
        ShapeElement element,
        string label,
        string value,
        double classificationConfidence,
        IReadOnlyList<string> sourceShapeIds,
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
            sourceShapeIds));
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

    private static bool TryComposeSplitDynamic(
        ShapeElement element,
        string primaryLabel,
        double lineSpacing,
        IReadOnlyDictionary<string, ShapeElement[]> shapeGroups,
        out string effectiveLabel,
        out SymbolMapping mapping,
        out double effectiveConfidence,
        out IReadOnlyList<string> sourceShapeIds)
    {
        effectiveLabel = primaryLabel;
        mapping = default;
        effectiveConfidence = element.Classification?.Confidence ?? 0;
        sourceShapeIds = [element.ShapeId];

        // The Audiveris model has no reliable standalone M-dynamic component for
        // split glyphs. MuseScore-style compound paths can therefore become an
        // unrecognised left 'm' plus a very confident right DYNAMICS_P. Provenance
        // from CompoundShapeSplitter gives both fragments the same shape-N prefix.
        if (primaryLabel != "DYNAMICS_P"
            || element.Classification is null
            || element.Classification.Confidence < MinimumConfidence
            || !shapeGroups.TryGetValue(BaseShapeId(element.ShapeId), out var siblings)
            || siblings.Length < 2)
        {
            return false;
        }

        var spacing = Math.Max(lineSpacing, 0.001);
        var left = siblings
            .Where(candidate => candidate.ShapeId != element.ShapeId)
            // A reconstructed .whole glyph is a classification alternative, not a
            // letter fragment. Let direct whole-glyph semantics handle it; the split
            // fallback must reason only over the original component glyphs.
            .Where(candidate => !candidate.ShapeId.EndsWith(".whole", StringComparison.Ordinal))
            .Where(candidate => candidate.CenterX < element.CenterX)
            .Where(candidate =>
                element.CenterX - candidate.CenterX <= spacing * 2.0
                && Math.Abs(element.CenterY - candidate.CenterY) <= spacing * 1.0)
            .OrderBy(candidate => element.CenterX - candidate.CenterX)
            .FirstOrDefault();

        if (left?.Classification is null
            || left.Classification.Confidence > 0.30)
        {
            return false;
        }

        var verticalOverlap = Math.Max(
            0,
            Math.Min(left.Bounds.MaxY, element.Bounds.MaxY)
                - Math.Max(left.Bounds.MinY, element.Bounds.MinY));
        var minimumHeight = Math.Max(
            0.001,
            Math.Min(left.Bounds.Height, element.Bounds.Height));
        var broadEnough = left.Bounds.Width >= element.Bounds.Width * 0.55;

        if (verticalOverlap / minimumHeight < 0.55 || !broadEnough)
        {
            return false;
        }

        effectiveLabel = "DYNAMICS_MP";
        mapping = new SymbolMapping(SymbolFamily.Dynamic, "mp");
        // Keep this conservative: provenance+geometry are strong evidence, but the
        // classifier only proved the P component. Still high enough for the residual
        // pass while remaining below a direct whole-glyph classification.
        effectiveConfidence = Math.Min(element.Classification.Confidence, 0.92);
        sourceShapeIds = [left.ShapeId, element.ShapeId];
        return true;
    }

    private static string BaseShapeId(string shapeId)
    {
        var separator = shapeId.IndexOf('.');
        return separator < 0 ? shapeId : shapeId[..separator];
    }

    private static bool TryMap(
        string label,
        out SymbolMapping mapping)
    {
        mapping = label switch
        {
            "MARCATO" => new(SymbolFamily.Articulation, "strong-accent"),
            "ACCENT" => new(SymbolFamily.Articulation, "accent"),
            "STACCATO" => new(SymbolFamily.Articulation, "staccato"),
            "STACCATISSIMO" => new(SymbolFamily.Articulation, "staccatissimo"),
            "TENUTO" => new(SymbolFamily.Articulation, "tenuto"),

            // Audiveris/SMuFL's MORDENT glyph corresponds to MusicXML's historic
            // inverted-mordent element name (confirmed by the Kancheli oracle).
            "MORDENT" => new(SymbolFamily.Ornament, "inverted-mordent"),
            "TRILL" or "TRILL_MARK" => new(SymbolFamily.Ornament, "trill-mark"),
            "TURN" => new(SymbolFamily.Ornament, "turn"),
            "INVERTED_TURN" => new(SymbolFamily.Ornament, "inverted-turn"),
            "FERMATA" => new(SymbolFamily.Fermata, "fermata"),
            _ => default
        };

        if (mapping != default)
        {
            return true;
        }

        if (!label.StartsWith("DYNAMICS_", StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = label["DYNAMICS_".Length..];

        // Single P/F glyphs are intentionally ambiguous: they can be fragments of
        // pp, ppp, mf, etc. Composite classifier shapes are safe residual semantics.
        if (suffix is "P" or "F")
        {
            return false;
        }

        if (suffix is not (
            "PP" or "PPP" or "PPPP" or "PPPPP" or "PPPPPP"
            or "FF" or "FFF" or "FFFF" or "FFFFF" or "FFFFFF"
            or "MP" or "MF" or "FP" or "PF" or "FZ"
            or "SF" or "SFP" or "SFPP" or "SFZ" or "SFFZ"
            or "RF" or "RFZ" or "SFZP"))
        {
            return false;
        }

        mapping = new SymbolMapping(
            SymbolFamily.Dynamic,
            suffix.ToLowerInvariant());
        return true;
    }

    private static ClassifiedSymbolDecision Reject(
        ShapeElement element,
        string label,
        int measureNumber,
        int staff,
        string decision,
        double confidence,
        string reason) =>
        new(
            element.ShapeId,
            label,
            false,
            decision,
            measureNumber,
            staff,
            null,
            null,
            confidence,
            reason);

    private static StaffMeasureScene ResolveNearestStaff(
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

    private static string PlacementAgainstStaff(
        double y,
        BoundsD bounds)
    {
        if (y <= bounds.MinY)
        {
            return "above";
        }

        if (y >= bounds.MaxY)
        {
            return "below";
        }

        return Math.Abs(y - bounds.MinY) < Math.Abs(y - bounds.MaxY)
            ? "above"
            : "below";
    }

    private static string NormalizeLabel(string label)
    {
        var chars = label
            .Trim()
            .ToUpperInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray();
        return string.Join(
            '_',
            new string(chars)
                .Split('_', StringSplitOptions.RemoveEmptyEntries));
    }

    private static double FractionValue(Fraction fraction) =>
        fraction.Numerator / (double)fraction.Denominator;

    private enum SymbolFamily
    {
        Articulation,
        Ornament,
        Fermata,
        Dynamic
    }

    private readonly record struct SymbolMapping(
        SymbolFamily Family,
        string Value);
}
