namespace SvgMusic.Semantics;

/// <summary>
/// Runs tie ownership recovery in a spatial view of the score where raw curves are
/// visible to every measure. Curves that cross a barline are sometimes logically
/// owned by only one side of that barline; endpoint geometry, not that ownership,
/// must decide a tie. TieOwnershipRecoveryPass still enforces close same-pitch,
/// same-staff, same-local-voice endpoints, so making curves measure-agnostic here is
/// safe and fixes ties whose visual span crosses the ownership boundary.
/// </summary>
public sealed class TieSpatialRecoveryPass : ISemanticPass
{
    public string Name => nameof(TieSpatialRecoveryPass);

    public void Run(
        SemanticDocument document,
        SemanticFacts facts)
    {
        var claimedCurveShapeIds = facts.OfType<SlurFact>()
            .SelectMany(slur => slur.SourceShapeIds.Append(slur.CurveShapeId))
            .Concat(
                facts.OfType<TieFact>()
                    .SelectMany(tie => tie.SourceShapeIds.Append(tie.CurveShapeId)))
            .ToHashSet(StringComparer.Ordinal);

        var curves = document.Measures
            .SelectMany(measure => new[]
            {
                (Staff: measure.Upper.StaffNumber, Elements: measure.Upper.Elements),
                (Staff: measure.Lower.StaffNumber, Elements: measure.Lower.Elements)
            })
            .SelectMany(item => item.Elements
                .OfType<CurveElement>()
                .Select(curve => (item.Staff, Curve: curve)))
            .GroupBy(item => item.Curve.ShapeId, StringComparer.Ordinal)
            .Select(group => new SpatialCurve(
                group.First().Curve,
                group.Select(item => item.Staff).ToHashSet()))
            .Where(item => !claimedCurveShapeIds.Contains(item.Curve.ShapeId))
            .ToArray();

        if (curves.Length == 0)
        {
            facts.AddTrace("TieSpatialRecoveryPass: no unclaimed curves");
            return;
        }

        var expandedMeasures = document.Measures
            .Select(measure => new MeasureScene(
                measure.Number,
                measure.SystemId,
                measure.PairId,
                measure.LayoutMeasureId,
                measure.XStart,
                measure.XEnd,
                measure.BreakBefore,
                measure.Upper with
                {
                    Elements = measure.Upper.Elements
                        .Where(element => element is not CurveElement)
                        .Concat(curves
                            .Where(item => item.StaffNumbers.Contains(measure.Upper.StaffNumber))
                            .Select(item => (SemanticElement)item.Curve))
                        .ToArray()
                },
                measure.Lower with
                {
                    Elements = measure.Lower.Elements
                        .Where(element => element is not CurveElement)
                        .Concat(curves
                            .Where(item => item.StaffNumbers.Contains(measure.Lower.StaffNumber))
                            .Select(item => (SemanticElement)item.Curve))
                        .ToArray()
                }))
            .ToArray();

        var spatialDocument = new SemanticDocument(expandedMeasures);

        // The normal SlurPass can miss a curve whose parser ownership exposes it on
        // only one side of a barline. Once the spatial view makes that same curve
        // visible to both measures, let the ordinary slur/tie classifier inspect it
        // before the deliberately aggressive same-pitch tie recovery.
        var slursBefore = facts.OfType<SlurFact>().Count();
        new SlurPass(includeCrossSystemCurves: false).Run(
            spatialDocument,
            facts);
        var recoveredSlurs = facts.OfType<SlurFact>().Count() - slursBefore;

        var tiesBefore = facts.OfType<TieFact>().Count();
        new TieOwnershipRecoveryPass().Run(
            spatialDocument,
            facts);
        var recoveredTies = facts.OfType<TieFact>().Count() - tiesBefore;

        facts.AddTrace(
            $"TieSpatialRecoveryPass: unclaimed-curves={curves.Length}; "
            + $"recovered-slurs={recoveredSlurs}; recovered-ties={recoveredTies}");
    }

    private sealed record SpatialCurve(
        CurveElement Curve,
        IReadOnlySet<int> StaffNumbers);
}