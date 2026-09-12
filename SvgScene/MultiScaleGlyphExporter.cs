using System.Text.Json;

namespace SvgMusic.Scene;

public sealed class MultiScaleGlyphExporter
{
    private static readonly int[] Interlines = [20, 30, 40];
    private const int PreviewInterline = 30;
    private const int Padding = 4;

    public MultiScaleGlyphExportManifest Export(
        GeometricScene geometry,
        NotationScene notation,
        ScoreLayout layout,
        string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        var shapesById = geometry.Shapes.ToDictionary(
            shape => shape.Id,
            StringComparer.Ordinal);

        var sourceInterline = layout.Staffs
            .Select(staff => staff.AverageLineSpacing)
            .Where(value => value > 0)
            .OrderBy(value => value)
            .DefaultIfEmpty(PreviewInterline)
            .ElementAt(layout.Staffs.Count > 0 ? layout.Staffs.Count / 2 : 0);

        if (sourceInterline <= 0)
        {
            sourceInterline = PreviewInterline;
        }

        var entries = new List<MultiScaleGlyphExportEntry>();

        var prototypeGroups = notation.Instances
            .GroupBy(instance => instance.PrototypeId, StringComparer.Ordinal)
            .ToArray();

        foreach (var prototypeGroup in prototypeGroups)
        {
            var instance = prototypeGroup.First();

            if (!shapesById.TryGetValue(instance.ShapeId, out var shape))
            {
                continue;
            }

            var baseFileName = MakeSafeFileName(instance.ShapeId)
                + "__"
                + MakeSafeFileName(instance.PrototypeId)
                + ".png";

            var variants = new List<GlyphRasterVariant>();

            foreach (var interline in Interlines)
            {
                var scale = interline / sourceInterline;
                var raster = Rasterize(shape, scale);

                var relativePath = interline == PreviewInterline
                    ? baseFileName
                    : Path.Combine(
                        ".scales",
                        interline.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        baseFileName);

                var fullPath = Path.Combine(outputDirectory, relativePath);
                var directory = Path.GetDirectoryName(fullPath);

                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                SimplePngWriter.WriteGrayscale(
                    fullPath,
                    raster.Width,
                    raster.Height,
                    raster.Pixels);

                variants.Add(new GlyphRasterVariant(
                    interline,
                    relativePath.Replace('\\', '/'),
                    raster.Width,
                    raster.Height,
                    raster.ForegroundCount));
            }

            entries.Add(new MultiScaleGlyphExportEntry(
                instance.ShapeId,
                instance.PrototypeId,
                prototypeGroup.Count(),
                baseFileName,
                variants));
        }

        var manifest = new MultiScaleGlyphExportManifest(
            PreviewInterline,
            sourceInterline,
            entries);

        File.WriteAllText(
            Path.Combine(outputDirectory, "multiscale-manifest.json"),
            JsonSerializer.Serialize(
                manifest,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                }));

        return manifest;
    }

    private static RasterGlyph Rasterize(
        GeometricShape shape,
        double scale)
    {
        var width = Math.Max(
            1,
            (int)Math.Ceiling(shape.Bounds.Width * scale) + 1 + 2 * Padding);

        var height = Math.Max(
            1,
            (int)Math.Ceiling(shape.Bounds.Height * scale) + 1 + 2 * Padding);

        var pixels = Enumerable.Repeat((byte)255, width * height).ToArray();

        var contours = shape.EffectiveContours
            .Where(contour => contour.Points.Count > 0)
            .Select(contour => new RasterContour(
                contour.Points
                    .Select(point => new PointD(
                        (point.X - shape.Bounds.MinX) * scale + Padding,
                        (point.Y - shape.Bounds.MinY) * scale + Padding))
                    .ToArray(),
                contour.IsClosed))
            .ToArray();

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sample = new PointD(x + 0.5, y + 0.5);
                var inside = false;

                foreach (var contour in contours.Where(contour => contour.IsClosed))
                {
                    if (PointInPolygon(sample, contour.Points))
                    {
                        inside = !inside;
                    }
                }

                if (inside)
                {
                    pixels[y * width + x] = 0;
                }
            }
        }

        var strokeWidth = shape.StrokeWidth > 0
            ? Math.Max(1.0, shape.StrokeWidth * scale)
            : 1.0;

        foreach (var contour in contours.Where(contour => !contour.IsClosed))
        {
            DrawPolyline(
                pixels,
                width,
                height,
                contour.Points,
                strokeWidth);
        }

        return new RasterGlyph(
            width,
            height,
            pixels,
            pixels.Count(value => value < 128));
    }

    private static bool PointInPolygon(
        PointD point,
        IReadOnlyList<PointD> polygon)
    {
        if (polygon.Count < 3)
        {
            return false;
        }

        var inside = false;
        var previous = polygon.Count - 1;

        for (var current = 0; current < polygon.Count; current++)
        {
            var a = polygon[current];
            var b = polygon[previous];
            var crosses = (a.Y > point.Y) != (b.Y > point.Y);

            if (crosses)
            {
                var xAtY = (b.X - a.X)
                    * (point.Y - a.Y)
                    / (b.Y - a.Y)
                    + a.X;

                if (point.X < xAtY)
                {
                    inside = !inside;
                }
            }

            previous = current;
        }

        return inside;
    }

    private static void DrawPolyline(
        byte[] pixels,
        int width,
        int height,
        IReadOnlyList<PointD> points,
        double strokeWidth)
    {
        if (points.Count < 2)
        {
            return;
        }

        var radius = strokeWidth / 2.0;

        for (var index = 1; index < points.Count; index++)
        {
            DrawSegment(
                pixels,
                width,
                height,
                points[index - 1],
                points[index],
                radius);
        }
    }

    private static void DrawSegment(
        byte[] pixels,
        int width,
        int height,
        PointD start,
        PointD end,
        double radius)
    {
        var minX = Math.Max(0, (int)Math.Floor(Math.Min(start.X, end.X) - radius - 1));
        var maxX = Math.Min(width - 1, (int)Math.Ceiling(Math.Max(start.X, end.X) + radius + 1));
        var minY = Math.Max(0, (int)Math.Floor(Math.Min(start.Y, end.Y) - radius - 1));
        var maxY = Math.Min(height - 1, (int)Math.Ceiling(Math.Max(start.Y, end.Y) + radius + 1));

        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                var sample = new PointD(x + 0.5, y + 0.5);

                if (DistanceToSegment(sample, start, end) <= radius)
                {
                    pixels[y * width + x] = 0;
                }
            }
        }
    }

    private static double DistanceToSegment(
        PointD point,
        PointD start,
        PointD end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var lengthSquared = dx * dx + dy * dy;

        if (lengthSquared <= 1e-12)
        {
            var x = point.X - start.X;
            var y = point.Y - start.Y;
            return Math.Sqrt(x * x + y * y);
        }

        var t = ((point.X - start.X) * dx + (point.Y - start.Y) * dy)
            / lengthSquared;

        t = Math.Clamp(t, 0.0, 1.0);

        var projectedX = start.X + t * dx;
        var projectedY = start.Y + t * dy;
        var deltaX = point.X - projectedX;
        var deltaY = point.Y - projectedY;

        return Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
    }

    private static string MakeSafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();

        return new string(value
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray());
    }

    private sealed record RasterContour(
        IReadOnlyList<PointD> Points,
        bool IsClosed);

    private sealed record RasterGlyph(
        int Width,
        int Height,
        byte[] Pixels,
        int ForegroundCount);
}

public sealed record MultiScaleGlyphExportManifest(
    int PreviewInterline,
    double SourceInterline,
    IReadOnlyList<MultiScaleGlyphExportEntry> Glyphs);

public sealed record MultiScaleGlyphExportEntry(
    string ShapeId,
    string PrototypeId,
    int PrototypeInstanceCount,
    string PreviewFileName,
    IReadOnlyList<GlyphRasterVariant> Variants);

public sealed record GlyphRasterVariant(
    int Interline,
    string FileName,
    int Width,
    int Height,
    int ForegroundPixels);
