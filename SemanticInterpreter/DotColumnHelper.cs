namespace SvgMusic.Semantics;

/// <summary>
/// Ephemeral geometry view of small filled ellipses that occupy one vertical
/// dot column. Horizontal double-dots remain separate columns because their
/// physical x-intervals do not overlap.
/// </summary>
public sealed record DotColumn(
    int MeasureNumber,
    int Staff,
    double MinX,
    double MaxX,
    IReadOnlyList<DotCandidate> Dots)
{
    public double CenterX => (MinX + MaxX) / 2.0;
}

public sealed class DotColumnHelper
{
    private const double OverlapEpsilon = 1e-9;

    public IReadOnlyList<DotColumn> Build(
        IReadOnlyList<DotCandidate> dots)
    {
        var result = new List<DotColumn>();

        foreach (var group in dots
                     .GroupBy(dot => (dot.MeasureNumber, dot.StaffNumber)))
        {
            var items = group
                .Select(dot =>
                {
                    var radius = Math.Max(
                        dot.Ellipse.Source.MajorRadius,
                        dot.Ellipse.Source.MinorRadius);
                    return new ColumnItem(
                        dot,
                        dot.Ellipse.CenterX - radius,
                        dot.Ellipse.CenterX + radius);
                })
                .OrderBy(item => item.Left)
                .ThenBy(item => item.Right)
                .ThenBy(item => item.Dot.Ellipse.ShapeId, StringComparer.Ordinal)
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
            .ToArray();
    }

    private static DotColumn CreateColumn(
        IReadOnlyList<ColumnItem> items)
    {
        var first = items[0].Dot;

        return new DotColumn(
            first.MeasureNumber,
            first.StaffNumber,
            items.Min(item => item.Left),
            items.Max(item => item.Right),
            items
                .Select(item => item.Dot)
                .OrderBy(dot => dot.Ellipse.CenterY)
                .ThenBy(dot => dot.Ellipse.ShapeId, StringComparer.Ordinal)
                .ToArray());
    }

    private sealed record ColumnItem(
        DotCandidate Dot,
        double Left,
        double Right);
}
