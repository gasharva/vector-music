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
        var curves = document.Measures
            .SelectMany(measure => new[] { measure.Upper, measure.Lower })
            .SelectMany(staff => staff.Elements.OfType<CurveElement>())
            .GroupBy(curve => curve.ShapeId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();

        if (curves.Length == 0)
        {
            facts.AddTrace("TieSpatialRecoveryPass: no curves");
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
                        .Concat(curves)
                        .ToArray()
                },
                measure.Lower with
                {
                    Elements = measure.Lower.Elements
                        .Where(element => element is not CurveElement)
                        .ToArray()
                }))
            .ToArray();

        var before = facts.OfType<TieFact>().Count();
        new TieOwnershipRecoveryPass().Run(
            new SemanticDocument(expandedMeasures),
            facts);
        var recovered = facts.OfType<TieFact>().Count() - before;

        facts.AddTrace(
            $"TieSpatialRecoveryPass: curves={curves.Length}; recovered={recovered}");
    }
}
