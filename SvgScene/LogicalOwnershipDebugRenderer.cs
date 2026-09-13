using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SvgMusic.Scene;

public sealed class LogicalOwnershipDebugRenderer
{
    private static readonly string[] Palette =
    [
        "#d32f2f",
        "#1976d2",
        "#388e3c",
        "#f57c00",
        "#7b1fa2",
        "#00838f",
        "#5d4037",
        "#455a64",
        "#c2185b",
        "#00796b",
        "#512da8",
        "#689f38"
    ];

    private static readonly Regex StylePropertyRegex = new(
        @"(?<name>[a-zA-Z-]+)\s*:\s*(?<value>[^;]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public void Render(
        string input,
        GeometricScene geometry,
        NotationScene notation,
        ScoreLayout layout,
        LogicalOwnershipScene ownership,
        string output)
    {
        var document = XDocument.Load(input, LoadOptions.PreserveWhitespace);
        var root = document.Root
            ?? throw new InvalidOperationException("SVG has no root element.");

        var colors = BuildColorMap(layout);
        var assignments = ownership.Assignments.ToDictionary(
            assignment => assignment.ShapeId,
            StringComparer.Ordinal);

        var sourceElementsByShapeNumber = BuildSourceElementMap(root);
        var shapesBySourceElement = new Dictionary<XElement, List<GeometricShape>>();

        foreach (var shape in geometry.Shapes)
        {
            var baseNumber = ParseBaseShapeNumber(shape.Id);

            if (baseNumber is null
                || !sourceElementsByShapeNumber.TryGetValue(baseNumber.Value, out var sourceElement))
            {
                continue;
            }

            if (!shapesBySourceElement.TryGetValue(sourceElement, out var shapes))
            {
                shapes = [];
                shapesBySourceElement[sourceElement] = shapes;
            }

            shapes.Add(shape);
        }

        var definitions = root.Descendants()
            .Where(element => element.Attribute("id") is not null)
            .GroupBy(
                element => (string)element.Attribute("id")!,
                StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.Ordinal);

        var defs = root.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "defs");

        var recolored = 0;
        var reconstructedConflicts = 0;
        var unresolvedConflicts = 0;
        var unmapped = 0;

        foreach (var pair in shapesBySourceElement.ToArray())
        {
            var sourceElement = pair.Key;
            var ownedShapes = pair.Value
                .Select(shape => new
                {
                    Shape = shape,
                    Assignment = assignments.GetValueOrDefault(shape.Id)
                })
                .Where(item => item.Assignment is not null)
                .ToArray();

            if (ownedShapes.Length == 0)
            {
                continue;
            }

            var starts = ownedShapes
                .Select(item => item.Assignment!.Ownership.Start)
                .Distinct()
                .ToArray();

            if (starts.Length != 1)
            {
                if (TryRenderCompoundPathFromGeometry(
                        root,
                        sourceElement,
                        pair.Value,
                        assignments,
                        colors))
                {
                    reconstructedConflicts++;
                }
                else
                {
                    unresolvedConflicts++;
                    sourceElement.SetAttributeValue(
                        "data-ownership-debug",
                        "conflict");
                }

                continue;
            }

            var color = colors.GetValueOrDefault(starts[0], "#616161");
            var hasFill = ownedShapes.Any(item => item.Shape.HasFill);
            var hasStroke = ownedShapes.Any(item => item.Shape.HasStroke);

            if (sourceElement.Name.LocalName == "use")
            {
                if (!TryRecolorUse(
                        sourceElement,
                        color,
                        hasFill,
                        hasStroke,
                        definitions,
                        defs,
                        recolored))
                {
                    unmapped++;
                    continue;
                }
            }
            else
            {
                RecolorElement(
                    sourceElement,
                    color,
                    hasFill,
                    hasStroke);
            }

            sourceElement.SetAttributeValue(
                "data-ownership",
                $"{starts[0].StaffId}+{starts[0].MeasureId}");

            recolored++;
        }

        root.SetAttributeValue(
            "data-ownership-debug-summary",
            $"recolored={recolored}; reconstructed-conflicts={reconstructedConflicts}; "
            + $"unresolved-conflicts={unresolvedConflicts}; unmapped={unmapped}");

        document.Save(output, SaveOptions.DisableFormatting);
    }

    private static bool TryRenderCompoundPathFromGeometry(
        XElement root,
        XElement sourceElement,
        IReadOnlyList<GeometricShape> shapes,
        IReadOnlyDictionary<string, LogicalOwnershipAssignment> assignments,
        IReadOnlyDictionary<LogicalCoordinate, string> colors)
    {
        if (sourceElement.Name.LocalName != "path")
        {
            return false;
        }

        var indexedShapes = shapes
            .Select(shape => new
            {
                Shape = shape,
                ComponentIndex = ParseComponentIndex(shape.Id)
            })
            .Where(item => item.ComponentIndex is not null)
            .OrderBy(item => item.ComponentIndex)
            .ToArray();

        if (indexedShapes.Length <= 1)
        {
            return false;
        }

        // Do not split the original d string here. A later subpath may start with
        // a relative 'm', whose origin is the previous subpath's current point.
        // Cutting such a path into independent strings changes its geometry and
        // is exactly what made staff lines and barlines "walk away".
        //
        // The GeometricShape components already contain the normalized absolute
        // geometry produced by SvgNormalizer/CompoundShapeSplitter. For this
        // debug-only conflict case we render those components at root level.
        // This keeps ownership logic untouched and guarantees correct positions.
        var debugGroup = new XElement(
            root.Name.Namespace + "g",
            new XAttribute("data-ownership-debug", "compound-path-components"));

        foreach (var item in indexedShapes)
        {
            var shape = item.Shape;
            var pathData = BuildPathData(shape.EffectiveContours);

            if (string.IsNullOrWhiteSpace(pathData))
            {
                continue;
            }

            var path = new XElement(
                root.Name.Namespace + "path",
                new XAttribute("d", pathData),
                new XAttribute("data-source-shape", shape.Id));

            var color = "#000000";

            if (assignments.TryGetValue(shape.Id, out var assignment))
            {
                var coordinate = assignment.Ownership.Start;
                color = colors.GetValueOrDefault(coordinate, "#616161");

                path.SetAttributeValue(
                    "data-ownership",
                    $"{coordinate.StaffId}+{coordinate.MeasureId}");
            }

            if (shape.HasFill)
            {
                path.SetAttributeValue("fill", color);
            }
            else
            {
                path.SetAttributeValue("fill", "none");
            }

            if (shape.HasStroke)
            {
                path.SetAttributeValue("stroke", color);

                if (shape.StrokeWidth > 0)
                {
                    path.SetAttributeValue(
                        "stroke-width",
                        FormatNumber(shape.StrokeWidth));
                }
            }
            else
            {
                path.SetAttributeValue("stroke", "none");
            }

            debugGroup.Add(path);
        }

        if (!debugGroup.HasElements)
        {
            return false;
        }

        sourceElement.Remove();
        root.Add(debugGroup);
        return true;
    }

    private static string BuildPathData(
        IReadOnlyList<GeometricContour> contours)
    {
        var parts = new List<string>();

        foreach (var contour in contours)
        {
            if (contour.Points.Count == 0)
            {
                continue;
            }

            var first = contour.Points[0];
            parts.Add($"M {FormatPoint(first)}");

            for (var index = 1; index < contour.Points.Count; index++)
            {
                parts.Add($"L {FormatPoint(contour.Points[index])}");
            }

            if (contour.IsClosed)
            {
                parts.Add("Z");
            }
        }

        return string.Join(" ", parts);
    }

    private static string FormatPoint(PointD point) =>
        $"{FormatNumber(point.X)} {FormatNumber(point.Y)}";

    private static string FormatNumber(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private static int? ParseComponentIndex(string shapeId)
    {
        var separator = shapeId.IndexOf('.', StringComparison.Ordinal);

        if (separator < 0 || separator + 1 >= shapeId.Length)
        {
            return null;
        }

        return int.TryParse(
            shapeId[(separator + 1)..],
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var index)
                ? index
                : null;
    }

    private static bool TryRecolorUse(
        XElement use,
        string color,
        bool hasFill,
        bool hasStroke,
        IReadOnlyDictionary<string, XElement> definitions,
        XElement? defs,
        int sequence)
    {
        if (defs is null)
        {
            return false;
        }

        var hrefAttribute = use.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName == "href");
        var href = hrefAttribute?.Value;

        if (string.IsNullOrWhiteSpace(href)
            || !href.StartsWith('#')
            || !definitions.TryGetValue(href[1..], out var target))
        {
            return false;
        }

        var clone = new XElement(target);
        var originalId = (string?)target.Attribute("id") ?? "glyph";
        var cloneId = $"ownership-{MakeSafeId(originalId)}-{sequence + 1}";
        clone.SetAttributeValue("id", cloneId);

        RecolorTree(
            clone,
            color,
            hasFill,
            hasStroke);

        defs.Add(clone);
        hrefAttribute!.Value = "#" + cloneId;

        return true;
    }

    private static void RecolorTree(
        XElement element,
        string color,
        bool fallbackFill,
        bool fallbackStroke)
    {
        if (IsGeometryElement(element))
        {
            var explicitFill = ReadPaint(element, "fill");
            var explicitStroke = ReadPaint(element, "stroke");

            var useFill = explicitFill is null
                ? fallbackFill
                : !IsNone(explicitFill);

            var useStroke = explicitStroke is null
                ? fallbackStroke
                : !IsNone(explicitStroke);

            RecolorElement(
                element,
                color,
                useFill,
                useStroke);
        }

        foreach (var child in element.Elements())
        {
            RecolorTree(
                child,
                color,
                fallbackFill,
                fallbackStroke);
        }
    }

    private static void RecolorElement(
        XElement element,
        string color,
        bool hasFill,
        bool hasStroke)
    {
        if (hasFill)
        {
            SetPaint(element, "fill", color);
        }

        if (hasStroke)
        {
            SetPaint(element, "stroke", color);
        }
    }

    private static string? ReadPaint(
        XElement element,
        string property)
    {
        var style = (string?)element.Attribute("style");

        if (!string.IsNullOrWhiteSpace(style))
        {
            foreach (Match match in StylePropertyRegex.Matches(style))
            {
                if (string.Equals(
                        match.Groups["name"].Value,
                        property,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return match.Groups["value"].Value.Trim();
                }
            }
        }

        return (string?)element.Attribute(property);
    }

    private static void SetPaint(
        XElement element,
        string property,
        string color)
    {
        var style = (string?)element.Attribute("style");

        if (!string.IsNullOrWhiteSpace(style))
        {
            var properties = StylePropertyRegex.Matches(style)
                .Cast<Match>()
                .Select(match => new KeyValuePair<string, string>(
                    match.Groups["name"].Value.Trim(),
                    match.Groups["value"].Value.Trim()))
                .ToList();

            var found = false;

            for (var index = 0; index < properties.Count; index++)
            {
                if (!string.Equals(
                        properties[index].Key,
                        property,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                properties[index] = new KeyValuePair<string, string>(
                    properties[index].Key,
                    color);
                found = true;
            }

            if (!found)
            {
                properties.Add(new KeyValuePair<string, string>(
                    property,
                    color));
            }

            element.SetAttributeValue(
                "style",
                string.Join(
                    ";",
                    properties.Select(item => $"{item.Key}:{item.Value}")));

            return;
        }

        element.SetAttributeValue(property, color);
    }

    private static bool IsNone(string value) =>
        string.Equals(
            value.Trim(),
            "none",
            StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<int, XElement> BuildSourceElementMap(
        XElement root)
    {
        var definitions = root.Descendants()
            .Where(element => element.Attribute("id") is not null)
            .GroupBy(
                element => (string)element.Attribute("id")!,
                StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.Ordinal);

        var result = new Dictionary<int, XElement>();
        var number = 0;

        foreach (var element in root.Descendants())
        {
            if (IsInsideDefs(element))
            {
                continue;
            }

            if (element.Name.LocalName == "use")
            {
                var href = element.Attributes()
                    .FirstOrDefault(attribute => attribute.Name.LocalName == "href")
                    ?.Value;

                if (string.IsNullOrWhiteSpace(href)
                    || !href.StartsWith('#')
                    || !definitions.TryGetValue(href[1..], out var target))
                {
                    continue;
                }

                foreach (var source in GeometryElements(target))
                {
                    if (CouldProduceGeometry(source))
                    {
                        result[++number] = element;
                    }
                }

                continue;
            }

            if (IsGeometryElement(element)
                && CouldProduceGeometry(element))
            {
                result[++number] = element;
            }
        }

        return result;
    }

    private static bool CouldProduceGeometry(XElement element)
    {
        return element.Name.LocalName switch
        {
            "path" => !string.IsNullOrWhiteSpace((string?)element.Attribute("d")),
            "line" => true,
            "polyline" or "polygon" => !string.IsNullOrWhiteSpace((string?)element.Attribute("points")),
            "rect" => PositiveAttribute(element, "width") && PositiveAttribute(element, "height"),
            _ => false
        };
    }

    private static bool PositiveAttribute(
        XElement element,
        string name)
    {
        var text = (string?)element.Attribute(name);

        return double.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var value)
            && value > 0;
    }

    private static IEnumerable<XElement> GeometryElements(
        XElement target)
    {
        if (IsGeometryElement(target))
        {
            yield return target;
        }

        foreach (var element in target.Descendants())
        {
            if (IsGeometryElement(element)
                && !element.Ancestors()
                    .TakeWhile(ancestor => ancestor != target)
                    .Any(ancestor => ancestor.Name.LocalName == "defs"))
            {
                yield return element;
            }
        }
    }

    private static bool IsGeometryElement(XElement element) =>
        element.Name.LocalName is
            "path"
            or "line"
            or "polyline"
            or "polygon"
            or "rect";

    private static bool IsInsideDefs(XElement element) =>
        element.Ancestors()
            .Any(ancestor => ancestor.Name.LocalName == "defs");

    private static int? ParseBaseShapeNumber(string shapeId)
    {
        if (!shapeId.StartsWith("shape-", StringComparison.Ordinal))
        {
            return null;
        }

        var end = shapeId.IndexOf('.', StringComparison.Ordinal);
        var numberText = end >= 0
            ? shapeId[6..end]
            : shapeId[6..];

        return int.TryParse(
            numberText,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var number)
                ? number
                : null;
    }

    private static IReadOnlyDictionary<LogicalCoordinate, string> BuildColorMap(
        ScoreLayout layout)
    {
        var result = new Dictionary<LogicalCoordinate, string>();
        var regionIndex = 0;

        foreach (var system in layout.Systems)
        {
            foreach (var pair in system.StaffPairs)
            {
                foreach (var measure in pair.Measures)
                {
                    result[new LogicalCoordinate(pair.UpperStaffId, measure.Id)] =
                        Palette[regionIndex++ % Palette.Length];

                    result[new LogicalCoordinate(pair.LowerStaffId, measure.Id)] =
                        Palette[regionIndex++ % Palette.Length];
                }
            }
        }

        return result;
    }

    private static string MakeSafeId(string value) =>
        new(value
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray());
}
