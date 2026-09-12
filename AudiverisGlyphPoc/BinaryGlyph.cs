namespace AudiverisGlyphPoc;

/// <summary>
/// A glyph represented exactly as Audiveris moment extractors expect it:
/// integer coordinates of every foreground pixel.
/// </summary>
public sealed class BinaryGlyph
{
    public BinaryGlyph(
        IReadOnlyList<int> x,
        IReadOnlyList<int> y)
    {
        if (x.Count == 0 || x.Count != y.Count)
        {
            throw new ArgumentException("Glyph must contain equally sized, non-empty X and Y arrays.");
        }

        X = x.ToArray();
        Y = y.ToArray();

        MinX = X.Min();
        MaxX = X.Max();
        MinY = Y.Min();
        MaxY = Y.Max();
    }

    public int[] X { get; }

    public int[] Y { get; }

    public int Mass => X.Length;

    public int MinX { get; }

    public int MaxX { get; }

    public int MinY { get; }

    public int MaxY { get; }

    public int Width => MaxX - MinX + 1;

    public int Height => MaxY - MinY + 1;

    public static BinaryGlyph FromRows(params string[] rows)
    {
        if (rows.Length == 0)
        {
            throw new ArgumentException("At least one bitmap row is required.", nameof(rows));
        }

        var width = rows.Max(row => row.Length);
        var x = new List<int>();
        var y = new List<int>();

        for (var row = 0; row < rows.Length; row++)
        {
            for (var column = 0; column < width; column++)
            {
                var value = column < rows[row].Length
                    ? rows[row][column]
                    : ' ';

                if (value is '#' or 'X' or '1')
                {
                    x.Add(column);
                    y.Add(row);
                }
            }
        }

        return new BinaryGlyph(x, y);
    }
}
