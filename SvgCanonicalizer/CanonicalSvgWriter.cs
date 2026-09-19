using System.Globalization;
using System.Text;
using System.Xml.Linq;
using SvgMusic.Scene;

namespace SvgMusic.Canonicalization;

public sealed record CanonicalSvgWriteResult(
    int ShapeCount,
    int ContourCount,
    BoundsD Bounds);

public sealed class CanonicalSvgWriter
{
    private static readonly XNamespace SvgNs = "http://www.w3.org/2000/svg";

    public CanonicalSvgWriteResult Write(
        GeometricScene scene,
        string outputPath)
    {
        if (scene.Shapes.Count == 0)
        {
            throw new InvalidDataException(
                "Cannot write canonical SVG because no visible geometry was found.");
        }

        var bounds = UnionBounds(scene.Shapes);
        var width = Math.Max(1e-6, bounds.Width);
        var height = Math.Max(1e-6, bounds.Height);

        var root = new XElement(
            SvgNs + "svg",
            new XAttribute("viewBox", string.Join(
                " ",
                F(bounds.MinX),
                F(bounds.MinY),
                F(width),
                F(height))),
            new XAttribute("width", F(width)),
            new XAttribute("height", F(height)));

        var contourCount = 0;

        foreach (var shape in scene.Shapes)
        {
            var contours = shape.EffectiveContours
                .Where(contour => contour.Points.Count >= 2)
                .ToArray();

            if (contours.Length == 0)
            {
                continue;
            }

            contourCount += contours.Length;

            var pathData = string.Join(
                " ",
                contours.Select(BuildContourPath));

            var element = new XElement(
                SvgNs + "path",
                new XAttribute("d", pathData));

            if (shape.HasFill)
            {
                element.SetAttributeValue("fill", "black");
                element.SetAttributeValue("fill-rule", "evenodd");
            }
            else
            {
                element.SetAttributeValue("fill", "none");
            }

            if (shape.HasStroke || !shape.HasFill)
            {
                element.SetAttributeValue("stroke", "black");
                element.SetAttributeValue(
                    "stroke-width",
                    F(Math.Max(shape.StrokeWidth, 0.01)));
                element.SetAttributeValue("stroke-linecap", "butt");
                element.SetAttributeValue("stroke-linejoin", "miter");
            }
            else
            {
                element.SetAttributeValue("stroke", "none");
            }

            root.Add(element);
        }

        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            root);

        var directory = Path.GetDirectoryName(
            Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        document.Save(outputPath);

        return new CanonicalSvgWriteResult(
            scene.Shapes.Count,
            contourCount,
            bounds);
    }

    private static string BuildContourPath(
        GeometricContour contour)
    {
        var points = contour.Points;
        var builder = new StringBuilder();

        builder.Append("M ")
            .Append(F(points[0].X))
            .Append(' ')
            .Append(F(points[0].Y));

        for (var index = 1; index < points.Count; index++)
        {
            builder.Append(" L ")
                .Append(F(points[index].X))
                .Append(' ')
                .Append(F(points[index].Y));
        }

        if (contour.IsClosed)
        {
            builder.Append(" Z");
        }

        return builder.ToString();
    }

    private static BoundsD UnionBounds(
        IReadOnlyList<GeometricShape> shapes)
    {
        return new BoundsD(
            shapes.Min(shape => shape.Bounds.MinX),
            shapes.Min(shape => shape.Bounds.MinY),
            shapes.Max(shape => shape.Bounds.MaxX),
            shapes.Max(shape => shape.Bounds.MaxY));
    }

    private static string F(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);
}
