namespace SvgMusic.Semantics;

/// <summary>
/// Ephemeral geometry view of noteheads that occupy one vertical x-column.
/// Filled and hollow heads are deliberately kept separate: they normally belong
/// to different rhythmic roles even when their horizontal spans overlap.
/// </summary>
public sealed record NoteheadColumn(
    int MeasureNumber,
    int Staff,
    string FillKind,
    double MinX,
    double MaxX,
    IReadOnlyList<NoteheadFact> Noteheads)
{
    public double CenterX => (MinX + MaxX) / 2.0;
}

public sealed class NoteheadColumnHelper
{
    private const double OverlapEpsilon = 1e-9;

    public IReadOnlyList<NoteheadColumn> Build(
        IReadOnlyList<NoteheadFact> noteheads)
    {
        var result = new List<NoteheadColumn>();

        foreach (var group in noteheads
                     .GroupBy(notehead =>
                         (notehead.MeasureNumber, notehead.Staff, notehead.FillKind)))
        {
            var items = group
                .Select(notehead =>
                {
                    var radius = Math.Max(
                        notehead.MajorRadius,
                        notehead.MinorRadius);
                    return new ColumnItem(
                        notehead,
                        notehead.CenterX - radius,
                        notehead.CenterX + radius);
                })
                .OrderBy(item => item.Left)
                .ThenBy(item => item.Right)
                .ThenBy(item => item.Notehead.ShapeId, StringComparer.Ordinal)
                .ToArray();

            var current = new List<ColumnItem>();
            var currentRight = double.NegativeInfinity;

            foreach (var item in items)
            {
                if (current.Count == 0
                    || item.Left <= currentRight + OverlapEpsilon)
                {
                    current.Add(item);
                    currentRight = Math.Max(currentRight, item.Right);
                    continue;
                }

                result.Add(CreateColumn(current));
                current.Clear();
                current.Add(item);
                currentRight = item.Right;
            }

            if (current.Count > 0)
            {
                result.Add(CreateColumn(current));
            }
        }

        return result
            .OrderBy(column => column.MeasureNumber)
            .ThenBy(column => column.Staff)
            .ThenBy(column => column.CenterX)
            .ThenBy(column => column.FillKind, StringComparer.Ordinal)
            .ToArray();
    }

    private static NoteheadColumn CreateColumn(
        IReadOnlyList<ColumnItem> items)
    {
        var first = items[0].Notehead;

        return new NoteheadColumn(
            first.MeasureNumber,
            first.Staff,
            first.FillKind,
            items.Min(item => item.Left),
            items.Max(item => item.Right),
            items
                .Select(item => item.Notehead)
                .OrderBy(notehead => notehead.CenterY)
                .ThenBy(notehead => notehead.ShapeId, StringComparer.Ordinal)
                .ToArray());
    }

    private sealed record ColumnItem(
        NoteheadFact Notehead,
        double Left,
        double Right);
}
