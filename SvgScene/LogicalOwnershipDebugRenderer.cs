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
        var conflicts = 0;
        var unmapped = 0;

        foreach (var pair in shapesBySourceElement)
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

            // One original SVG element may contain several independent subpaths.
            // If the splitter assigned those subpaths to different staff/measure
            // coordinates, recoloring the original path would lie. Keep it black
            // instead of drawing replacement geometry on top of it.
            if (starts.Length != 1)
            {
                conflicts++;
                sourceElement.SetAttributeValue("data-ownership-debug", "conflict");
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
            $"recolored={recolored}; conflicts={conflicts}; unmapped={unmapped}");

        document.Save(output, SaveOptions.DisableFormatting);
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
                    if (!CouldProduceGeometry(source))
                    {
                        continue;
                    }

                    result[++number] = element;
                }

                continue;
            }

            if (!IsGeometryElement(element)
                || !CouldProduceGeometry(element))
            {
                continue;
            }

            result[++number] = element;
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
