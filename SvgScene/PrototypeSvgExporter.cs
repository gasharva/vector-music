using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace SvgMusic.Scene;

/// <summary>
/// Exports one vector glyph per ShapePrototype using the same representative
/// shape that PrototypeSymbolClassifier feeds to Audiveris. The SVGs retain
/// vector geometry so downstream experiments (OCR, alternative classifiers,
/// diagnostics) can choose their own rasterization strategy.
/// </summary>
public sealed class PrototypeSvgExporter
{
    private const double MinimumPadding = 2.0;

    public PrototypeSvgExportManifest Export(
        GeometricScene geometry,
        NotationScene notation,
        string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        var shapesById = geometry.Shapes.ToDictionary(
            shape => shape.Id,
            StringComparer.Ordinal);

        var instanceCounts = notation.Instances
            .GroupBy(instance => instance.PrototypeId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Count(),
                StringComparer.Ordinal);

        var entries = new List<PrototypeSvgExportEntry>();

        foreach (var prototype in notation.Prototypes
                     .OrderBy(prototype => prototype.Id, StringComparer.Ordinal))
        {
            if (!shapesById.TryGetValue(
                    prototype.RepresentativeShapeId,
                    out var shape))
            {
                continue;
            }

            var fileName = MakeSafeFileName(prototype.Id)
                + "__"
                + MakeSafeFileName(shape.Id)
                + ".svg";

            File.WriteAllText(
                Path.Combine(outputDirectory, fileName),
                BuildSvg(shape, prototype));

            entries.Add(new PrototypeSvgExportEntry(
                prototype.Id,
                shape.Id,
                instanceCounts.GetValueOrDefault(prototype.Id),
                fileName,
                shape.SourceKind,
                shape.SourceId,
                shape.SourceIndex,
                shape.SourceClass,
                shape.Bounds,
                shape.HasFill,
                shape.HasStroke,
                shape.StrokeWidth,
                prototype.Classification?.Label,
                prototype.Classification?.Confidence,
                prototype.Classification?.Interline));
        }

        var manifest = new PrototypeSvgExportManifest(entries);
        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        File.WriteAllText(
            Path.Combine(outputDirectory, "manifest.json"),
            JsonSerializer.Serialize(manifest, jsonOptions));

        File.WriteAllText(
            Path.Combine(outputDirectory, "index.html"),
            BuildIndexHtml(manifest));

        return manifest;
    }

    private static string BuildSvg(
        GeometricShape shape,
        ShapePrototype prototype)
    {
        var strokeWidth = shape.StrokeWidth > 0
            ? shape.StrokeWidth
            : 1.0;
        var padding = Math.Max(MinimumPadding, strokeWidth * 2.0);
        var width = Math.Max(1.0, shape.Bounds.Width + 2.0 * padding);
        var height = Math.Max(1.0, shape.Bounds.Height + 2.0 * padding);

        var contours = shape.EffectiveContours
            .Where(contour => contour.Points.Count > 0)
            .ToArray();
        var closedContours = contours
            .Where(contour => contour.IsClosed)
            .ToArray();
        var openContours = contours
            .Where(contour => !contour.IsClosed)
            .ToArray();

        var useFill = shape.HasFill
            || (!shape.HasFill && !shape.HasStroke && closedContours.Length > 0);
        var useClosedStroke = shape.HasStroke;

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" ")
            .Append("version=\"1.1\" ")
            .Append("width=\"").Append(Format(width)).Append("\" ")
            .Append("height=\"").Append(Format(height)).Append("\" ")
            .Append("viewBox=\"0 0 ").Append(Format(width)).Append(' ')
            .Append(Format(height)).AppendLine("\">");
        sb.Append("  <metadata>")
            .Append("prototype=").Append(Xml(prototype.Id))
            .Append("; representativeShape=").Append(Xml(shape.Id));

        if (prototype.Classification is not null)
        {
            sb.Append("; audiveris=")
                .Append(Xml(prototype.Classification.Label))
                .Append("; confidence=")
                .Append(Format(prototype.Classification.Confidence))
                .Append("; interline=")
                .Append(prototype.Classification.Interline);
        }

        sb.AppendLine("</metadata>");

        if (closedContours.Length > 0)
        {
            var d = string.Join(
                " ",
                closedContours.Select(contour => BuildPathData(
                    contour,
                    shape.Bounds.MinX,
                    shape.Bounds.MinY,
                    padding)));

            sb.Append("  <path d=\"").Append(Xml(d)).Append("\"")
                .Append(" fill=\"").Append(useFill ? "black" : "none").Append("\"")
                .Append(" fill-rule=\"evenodd\"")
                .Append(" stroke=\"").Append(useClosedStroke ? "black" : "none").Append("\"");

            if (useClosedStroke)
            {
                AppendStrokeStyle(sb, shape, strokeWidth);
            }

            sb.AppendLine("/>");
        }

        if (openContours.Length > 0)
        {
            var d = string.Join(
                " ",
                openContours.Select(contour => BuildPathData(
                    contour,
                    shape.Bounds.MinX,
                    shape.Bounds.MinY,
                    padding)));

            sb.Append("  <path d=\"").Append(Xml(d)).Append("\"")
                .Append(" fill=\"none\" stroke=\"black\"");
            AppendStrokeStyle(sb, shape, strokeWidth);
            sb.AppendLine("/>");
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    private static void AppendStrokeStyle(
        StringBuilder sb,
        GeometricShape shape,
        double strokeWidth)
    {
        sb.Append(" stroke-width=\"").Append(Format(strokeWidth)).Append("\"")
            .Append(" stroke-linecap=\"round\" stroke-linejoin=\"round\"");

        if (!string.IsNullOrWhiteSpace(shape.StrokeDashArray))
        {
            sb.Append(" stroke-dasharray=\"")
                .Append(Xml(shape.StrokeDashArray))
                .Append("\"");
        }
    }

    private static string BuildPathData(
        GeometricContour contour,
        double minX,
        double minY,
        double padding)
    {
        var points = contour.Points;
        var first = points[0];
        var sb = new StringBuilder();

        sb.Append("M ")
            .Append(Format(first.X - minX + padding))
            .Append(' ')
            .Append(Format(first.Y - minY + padding));

        for (var index = 1; index < points.Count; index++)
        {
            var point = points[index];
            sb.Append(" L ")
                .Append(Format(point.X - minX + padding))
                .Append(' ')
                .Append(Format(point.Y - minY + padding));
        }

        if (contour.IsClosed)
        {
            sb.Append(" Z");
        }

        return sb.ToString();
    }

    private static string BuildIndexHtml(PrototypeSvgExportManifest manifest)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("  <meta charset=\"utf-8\">");
        sb.AppendLine("  <meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.AppendLine("  <title>SvgScene prototype glyphs</title>");
        sb.AppendLine("  <style>");
        sb.AppendLine("    body{font-family:system-ui,sans-serif;margin:24px;background:#f6f6f6;color:#222}");
        sb.AppendLine("    .top{display:flex;gap:16px;align-items:baseline;flex-wrap:wrap}");
        sb.AppendLine("    .grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(220px,1fr));gap:12px}");
        sb.AppendLine("    figure{margin:0;padding:12px;background:white;border:1px solid #ddd;border-radius:8px}");
        sb.AppendLine("    img{display:block;width:100%;height:100px;object-fit:contain;background:white}");
        sb.AppendLine("    figcaption{font-size:12px;overflow-wrap:anywhere;margin-top:8px}");
        sb.AppendLine("    .muted{color:#666}");
        sb.AppendLine("  </style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("  <div class=\"top\">");
        sb.AppendLine($"    <h1>SvgScene prototype glyphs ({manifest.Glyphs.Count})</h1>");
        sb.AppendLine("    <a href=\"manifest.json\">manifest.json</a>");
        sb.AppendLine("    <a href=\"../glyphs-svg.zip\">download ZIP</a>");
        sb.AppendLine("  </div>");
        sb.AppendLine("  <p class=\"muted\">One SVG per ShapePrototype, exported from its RepresentativeShapeId.</p>");
        sb.AppendLine("  <div class=\"grid\">");

        foreach (var glyph in manifest.Glyphs)
        {
            var prototype = WebUtility.HtmlEncode(glyph.PrototypeId);
            var shape = WebUtility.HtmlEncode(glyph.RepresentativeShapeId);
            var file = WebUtility.HtmlEncode(glyph.FileName);
            var label = WebUtility.HtmlEncode(glyph.AudiverisLabel ?? "-");
            var confidence = glyph.AudiverisConfidence is null
                ? "-"
                : glyph.AudiverisConfidence.Value.ToString("P1", CultureInfo.InvariantCulture);

            sb.AppendLine("    <figure>");
            sb.Append("      <a href=\"").Append(file).Append("\"><img src=\"")
                .Append(file).Append("\" alt=\"").Append(prototype).AppendLine("\"></a>");
            sb.Append("      <figcaption><b>").Append(prototype).Append("</b><br>")
                .Append("shape: ").Append(shape).Append("<br>")
                .Append("instances: ").Append(glyph.InstanceCount).Append("<br>")
                .Append("Audiveris: ").Append(label).Append(" (").Append(confidence).AppendLine(")</figcaption>");
            sb.AppendLine("    </figure>");
        }

        sb.AppendLine("  </div>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
        return sb.ToString();
    }

    private static string MakeSafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        return new string(value
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray());
    }

    private static string Format(double value) =>
        value.ToString("0.######", CultureInfo.InvariantCulture);

    private static string Xml(string value) =>
        WebUtility.HtmlEncode(value);
}

public sealed record PrototypeSvgExportManifest(
    IReadOnlyList<PrototypeSvgExportEntry> Glyphs);

public sealed record PrototypeSvgExportEntry(
    string PrototypeId,
    string RepresentativeShapeId,
    int InstanceCount,
    string FileName,
    string SourceKind,
    string? SourceId,
    string? SourceIndex,
    string? SourceClass,
    BoundsD SourceBounds,
    bool HasFill,
    bool HasStroke,
    double StrokeWidth,
    string? AudiverisLabel,
    double? AudiverisConfidence,
    int? AudiverisInterline);
