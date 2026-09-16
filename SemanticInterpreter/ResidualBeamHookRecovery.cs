using SvgMusic.Scene;

namespace SvgMusic.Semantics;

public sealed record ResidualBeamHookRecoveryResult(
    SemanticDocument Document,
    int RecoveredCount);

/// <summary>
/// Filled beam rectangles can fail the generic stroke extractor and survive as
/// residual ShapeElements. This happens both for compact hooks and for ordinary
/// two-note beams. Recover horizontal beam-like boxes across that useful length
/// range; BeamAttachmentAnalyzer still has to prove stem intersections, endpoint
/// support and a musically valid beam level before accepting them.
/// </summary>
public sealed class ResidualBeamHookRecovery
{
    private const double MinimumLengthInSpacings = 0.85;
    private const double MaximumLengthInSpacings = 4.50;
    private const double MinimumThicknessInSpacings = 0.22;
    private const double MaximumThicknessInSpacings = 0.80;
    private const double MinimumAspectRatio = 1.75;

    public ResidualBeamHookRecoveryResult Recover(
        SemanticDocument document)
    {
        var recoveredCount = 0;
        var measures = document.Measures
            .Select(measure => measure with
            {
                Upper = RecoverStaff(
                    measure.Upper,
                    ref recoveredCount),
                Lower = RecoverStaff(
                    measure.Lower,
                    ref recoveredCount)
            })
            .ToArray();

        return new ResidualBeamHookRecoveryResult(
            new SemanticDocument(measures),
            recoveredCount);
    }

    private static StaffMeasureScene RecoverStaff(
        StaffMeasureScene staff,
        ref int recoveredCount)
    {
        if (staff.LineSpacing <= 0)
        {
            return staff;
        }

        var existingStrokeIds = staff.Elements
            .OfType<StrokeElement>()
            .Select(element => element.ShapeId)
            .ToHashSet(StringComparer.Ordinal);
        var recovered = new List<SemanticElement>();

        foreach (var shape in staff.Elements.OfType<ShapeElement>())
        {
            if (existingStrokeIds.Contains(shape.ShapeId)
                || !IsResidualBeamBox(
                    shape.Bounds,
                    staff.LineSpacing))
            {
                continue;
            }

            var bounds = shape.Bounds;
            var source = new Stroke(
                shape.ShapeId,
                new PointD(bounds.MinX, bounds.CenterY),
                new PointD(bounds.MaxX, bounds.CenterY),
                bounds.Height,
                "residual-beam",
                shape.Source.SourceIndex,
                shape.Ownership);

            recovered.Add(new StrokeElement
            {
                ShapeId = shape.ShapeId,
                Bounds = bounds,
                Ownership = shape.Ownership,
                Source = source
            });
            recoveredCount++;
        }

        if (recovered.Count == 0)
        {
            return staff;
        }

        return staff with
        {
            Elements = staff.Elements
                .Concat(recovered)
                .OrderBy(element => element.Bounds.MinX)
                .ThenBy(element => element.Bounds.MinY)
                .ToArray()
        };
    }

    private static bool IsResidualBeamBox(
        BoundsD bounds,
        double lineSpacing)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return false;
        }

        var lengthInSpacings = bounds.Width / lineSpacing;
        var thicknessInSpacings = bounds.Height / lineSpacing;
        var aspectRatio = bounds.Width / bounds.Height;

        return lengthInSpacings >= MinimumLengthInSpacings
            && lengthInSpacings <= MaximumLengthInSpacings
            && thicknessInSpacings >= MinimumThicknessInSpacings
            && thicknessInSpacings <= MaximumThicknessInSpacings
            && aspectRatio >= MinimumAspectRatio;
    }
}
