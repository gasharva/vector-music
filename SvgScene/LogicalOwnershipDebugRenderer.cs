using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SvgMusic.Scene;

public sealed class LogicalOwnershipDebugRenderer
{
    private const string NeutralLayoutColor = "#000000";

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
        var neutralShapeIds = BuildNeutralShapeIds(layout);
        var assignments = ownership.Assignments.ToDictionary(
            assignment => assignment.ShapeId,
            StringComparer.Ordinal);
        var shapeKinds = BuildShapeKinds(notation);

        var sourceElementsByShapeNumber = BuildSourceElementMap(root);
        var shapesBySourceElement = new Dictionary<XElement, List<GeometricShape>>();
        var sourceElementByShapeId = new Dictionary<string, XElement>(StringComparer.Ordinal);

        foreach (var shape in geometry.Shapes)
        {
            var baseNumber = ParseBaseShapeNumber(shape.Id);

            if (baseNumber is null
                || !sourceElementsByShapeNumber.TryGetValue(baseNumber.Value, out var sourceElement))
            {
                continue;
            }

            sourceElementByShapeId[shape.Id] = sourceElement;

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

        var renderResults = new Dictionary<string, ShapeRenderResult>(StringComparer.Ordinal);
        var recolored = 0;
        var componentRendered = 0;
        var compoundConflicts = 0;
        var neutralSources = 0;
        var unmapped = 0;

        foreach (var pair in shapesBySourceElement.ToArray())
        {
            var sourceElement = pair.Key;
            var sourceShapes = pair.Value;
            var ownedShapes = sourceShapes
                .Select(shape => new OwnedSourceShape(
                    shape,
                    assignments.GetValueOrDefault(shape.Id)))
                .Where(item => item.Assignment is not null)
                .ToArray();

            if (ownedShapes.Length == 0)
            {
                continue;
            }

            var allNeutral = sourceShapes.All(shape =>
                neutralShapeIds.Contains(shape.Id));

            if (allNeutral)
            {
                neutralSources++;

                sourceElement.SetAttributeValue(
                    "data-ownership-debug",
                    "neutral-layout");

                foreach (var item in ownedShapes)
                {
                    var coordinate = item.Assignment!.Ownership.Start;

                    renderResults[item.Shape.Id] = new ShapeRenderResult(
                        true,
                        "neutral-layout",
                        coordinate,
                        true,
                        DescribeSource(sourceElement));
                }

                continue;
            }

            var coordinateGroups = ownedShapes
                .GroupBy(item => item.Assignment!.Ownership.Start)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key.StaffId, StringComparer.Ordinal)
                .ThenBy(group => group.Key.MeasureId, StringComparer.Ordinal)
                .ToArray();

            var hasNeutral = sourceShapes.Any(shape =>
                neutralShapeIds.Contains(shape.Id));
            var hasColored = sourceShapes.Any(shape =>
                !neutralShapeIds.Contains(shape.Id));
            var mixedNeutral = hasNeutral && hasColored;
            var hasOwnershipConflict = coordinateGroups.Length > 1;

            if (hasOwnershipConflict)
            {
                compoundConflicts++;
            }

            if ((hasOwnershipConflict || mixedNeutral)
                && TryRenderCompoundComponents(
                    root,
                    sourceElement,
                    sourceShapes,
                    assignments,
                    colors,
                    neutralShapeIds,
                    renderResults))
            {
                componentRendered++;
                recolored++;
                continue;
            }

            var chosenCoordinate = coordinateGroups[0].Key;
            var color = colors.GetValueOrDefault(chosenCoordinate, "#616161");
            var hasFill = ownedShapes.Any(item => item.Shape.HasFill);
            var hasStroke = ownedShapes.Any(item => item.Shape.HasStroke);
            var mode = hasOwnershipConflict
                ? "compound-dominant"
                : "direct";

            if (hasOwnershipConflict)
            {
                sourceElement.SetAttributeValue(
                    "data-ownership-debug",
                    "compound-conflict");

                sourceElement.SetAttributeValue(
                    "data-ownership-alternatives",
                    string.Join(
                        ",",
                        coordinateGroups.Select(group =>
                            $"{group.Key.StaffId}+{group.Key.MeasureId}:{group.Count()}")));
            }

            var rendered = true;

            if (sourceElement.Name.LocalName == "use")
            {
                rendered = TryRecolorUse(
                    sourceElement,
                    color,
                    hasFill,
                    hasStroke,
                    definitions,
                    defs,
                    recolored);
            }
            else
            {
                RecolorElement(
                    sourceElement,
                    color,
                    hasFill,
                    hasStroke);
            }

            if (!rendered)
            {
                unmapped++;

                foreach (var item in ownedShapes)
                {
                    renderResults[item.Shape.Id] = new ShapeRenderResult(
                        false,
                        "use-recolor-failed",
                        null,
                        false,
                        DescribeSource(sourceElement));
                }

                continue;
            }

            sourceElement.SetAttributeValue(
                "data-ownership",
                $"{chosenCoordinate.StaffId}+{chosenCoordinate.MeasureId}");

            foreach (var item in ownedShapes)
            {
                var ownCoordinate = item.Assignment!.Ownership.Start;

                renderResults[item.Shape.Id] = new ShapeRenderResult(
                    true,
                    mode,
                    chosenCoordinate,
                    ownCoordinate == chosenCoordinate,
                    DescribeSource(sourceElement));
            }

            recolored++;
        }

        root.SetAttributeValue(
            "data-ownership-debug-summary",
            $"recolored={recolored}; component-rendered={componentRendered}; "
            + $"neutral-sources={neutralSources}; compound-conflicts={compoundConflicts}; "
            + $"unmapped={unmapped}");

        document.Save(output, SaveOptions.DisableFormatting);

        WriteRenderDiagnostics(
            output,
            shapeKinds,
            assignments,
            sourceElementByShapeId,
            renderResults);
    }

    private static bool TryRenderCompoundComponents(
        XElement root,
        XElement sourceElement,
        IReadOnlyList<GeometricShape> shapes,
        IReadOnlyDictionary<string, LogicalOwnershipAssignment> assignments,
        IReadOnlyDictionary<LogicalCoordinate, string> colors,
        IReadOnlySet<string> neutralShapeIds,
        IDictionary<string, ShapeRenderResult> renderResults)
    {
        if (sourceElement.Name.LocalName != "path"
            || shapes.Count <= 1)
        {
            return false;
        }

        var prepared = new List<(GeometricShape Shape, string PathData)>();

        foreach (var shape in shapes)
        {
            if (!shape.HasFill && !shape.HasStroke)
            {
                return false;
            }

            var pathData = BuildPathData(shape.EffectiveContours);

            if (string.IsNullOrWhiteSpace(pathData))
            {
                return false;
            }

            prepared.Add((shape, pathData));
        }

        var sourceDescription = DescribeSource(sourceElement);
        var group = new XElement(
            root.Name.Namespace + "g",
            new XAttribute(
                "data-ownership-debug",
                "compound-components"));

        foreach (var item in prepared)
        {
            var shape = item.Shape;
            var path = new XElement(sourceElement);

            path.Attribute("id")?.Remove();
            path.Attribute("transform")?.Remove();
            path.SetAttributeValue("d", item.PathData);
            path.SetAttributeValue("data-source-shape", shape.Id);

            var isNeutral = neutralShapeIds.Contains(shape.Id);
            LogicalCoordinate? displayedCoordinate = null;
            var mode = isNeutral
                ? "neutral-component"
                : "compound-component";
            var color = NeutralLayoutColor;

            if (!isNeutral
                && assignments.TryGetValue(shape.Id, out var assignment))
            {
                displayedCoordinate = assignment.Ownership.Start;
                color = colors.GetValueOrDefault(
                    displayedCoordinate,
                    "#616161");

                path.SetAttributeValue(
                    "data-ownership",
                    FormatCoordinate(displayedCoordinate));
            }
            else if (isNeutral)
            {
                path.SetAttributeValue(
                    "data-ownership-debug",
                    "neutral-layout-component");
            }

            SetShapePaint(
                path,
                shape,
                color);

            group.Add(path);

            if (assignments.TryGetValue(shape.Id, out var shapeAssignment))
            {
                var ownCoordinate = shapeAssignment.Ownership.Start;

                renderResults[shape.Id] = new ShapeRenderResult(
                    true,
                    mode,
                    isNeutral ? ownCoordinate : displayedCoordinate,
                    true,
                    sourceDescription);
            }
        }

        sourceElement.Remove();
        root.Add(group);
        return true;
    }

    private static void SetShapePaint(
        XElement element,
        GeometricShape shape,
        string color)
    {
        SetPaint(
            element,
            "fill",
            shape.HasFill ? color : "none");

        SetPaint(
            element,
            "stroke",
            shape.HasStroke ? color : "none");

        if (shape.HasStroke && shape.StrokeWidth > 0)
        {
            SetStyleProperty(
                element,
                "stroke-width",
                FormatNumber(shape.StrokeWidth));
        }
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

            parts.Add(
                $"M {FormatPoint(contour.Points[0])}");

            for (var index = 1; index < contour.Points.Count; index++)
            {
                parts.Add(
                    $"L {FormatPoint(contour.Points[index])}");
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

    private static IReadOnlySet<string> BuildNeutralShapeIds(
        ScoreLayout layout)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);

        foreach (var staff in layout.Staffs)
        {
            foreach (var line in staff.Lines)
            {
                result.Add(line.StrokeId);
            }
        }

        foreach (var system in layout.Systems)
        {
            foreach (var pair in system.StaffPairs)
            {
                foreach (var boundary in pair.Boundaries)
                {
                    foreach (var strokeId in boundary.StrokeIds)
                    {
                        result.Add(strokeId);
                    }
                }
            }
        }

        return result;
    }

    private static IReadOnlyDictionary<string, string> BuildShapeKinds(
        NotationScene notation)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var stroke in notation.Strokes)
        {
            result[stroke.ShapeId] = "Stroke";
        }

        foreach (var curve in notation.CurvedStrokes)
        {
            result[curve.ShapeId] = "CurvedStroke";
        }

        foreach (var ellipse in notation.Ellipses)
        {
            result[ellipse.ShapeId] = "EllipseLike";
        }

        foreach (var instance in notation.Instances)
        {
            result[instance.ShapeId] = "ShapeInstance";
        }

        return result;
    }

    private static void WriteRenderDiagnostics(
        string output,
        IReadOnlyDictionary<string, string> shapeKinds,
        IReadOnlyDictionary<string, LogicalOwnershipAssignment> assignments,
        IReadOnlyDictionary<string, XElement> sourceElementByShapeId,
        IReadOnlyDictionary<string, ShapeRenderResult> renderResults)
    {
        var diagnosticsPath = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(output))
                ?? Environment.CurrentDirectory,
            Path.GetFileNameWithoutExtension(output) + ".render-diagnostics.txt");

        var lines = new List<string>
        {
            "OWNERSHIP RENDER DIAGNOSTICS",
            "owned=no                   => ownership algorithm did not assign the shape",
            "owned=yes rendered=no      => renderer/source mapping problem",
            "mode=compound-component    => this split component was rendered with its own ownership color",
            "mode=neutral-layout        => staff/bar layout geometry intentionally remains black",
            "mode=compound-dominant     => component rendering was impossible; one fallback color was chosen",
            "color-match=no             => shape has ownership, but fallback compound source used another color",
            string.Empty,
            "SUMMARY"
        };

        foreach (var kind in new[] { "Stroke", "CurvedStroke", "EllipseLike", "ShapeInstance" })
        {
            var ids = shapeKinds
                .Where(pair => pair.Value == kind)
                .Select(pair => pair.Key)
                .ToArray();
            var owned = ids.Count(assignments.ContainsKey);
            var rendered = ids.Count(id =>
                renderResults.TryGetValue(id, out var result)
                && result.Rendered);
            var exactColor = ids.Count(id =>
                renderResults.TryGetValue(id, out var result)
                && result.Rendered
                && result.ColorMatchesOwnership);

            lines.Add(
                $"{kind,-13} total={ids.Length,4} owned={owned,4} rendered={rendered,4} exact-color={exactColor,4}");
        }

        lines.Add(string.Empty);
        lines.Add("DETAILS");

        foreach (var pair in shapeKinds
                     .OrderBy(pair => pair.Value, StringComparer.Ordinal)
                     .ThenBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var shapeId = pair.Key;
            var kind = pair.Value;
            var owned = assignments.TryGetValue(shapeId, out var assignment);
            var source = sourceElementByShapeId.TryGetValue(shapeId, out var sourceElement)
                ? DescribeSource(sourceElement)
                : "-";

            if (!owned)
            {
                lines.Add(
                    $"{shapeId,-12} {kind,-13} owned=no  rendered=no  source={source}");
                continue;
            }

            var ownership = assignment!.Ownership;
            var coordinate = FormatCoordinate(ownership.Start);

            if (!renderResults.TryGetValue(shapeId, out var renderResult))
            {
                var reason = sourceElement is null
                    ? "source-not-mapped"
                    : "source-not-recolored";

                lines.Add(
                    $"{shapeId,-12} {kind,-13} owned=yes {coordinate,-38} g{ownership.Generation} "
                    + $"rendered=no  reason={reason}; source={source}");
                continue;
            }

            var displayed = renderResult.DisplayedCoordinate is null
                ? "-"
                : FormatCoordinate(renderResult.DisplayedCoordinate);
            var colorMatch = renderResult.ColorMatchesOwnership
                ? "yes"
                : "no";

            lines.Add(
                $"{shapeId,-12} {kind,-13} owned=yes {coordinate,-38} g{ownership.Generation} "
                + $"rendered={(renderResult.Rendered ? "yes" : "no")}; "
                + $"mode={renderResult.Mode}; displayed={displayed}; color-match={colorMatch}; "
                + $"source={renderResult.Source}");
        }

        File.WriteAllLines(diagnosticsPath, lines);
    }

    private static string FormatCoordinate(LogicalCoordinate coordinate) =>
        $"{coordinate.StaffId}+{coordinate.MeasureId}";

    private static string DescribeSource(XElement element)
    {
        var id = (string?)element.Attribute("id");
        var href = element.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName == "href")
            ?.Value;

        if (!string.IsNullOrWhiteSpace(id))
        {
            return $"<{element.Name.LocalName} id={id}>";
        }

        if (!string.IsNullOrWhiteSpace(href))
        {
            return $"<{element.Name.LocalName} href={href}>";
        }

        return $"<{element.Name.LocalName}>";
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
        SetStyleProperty(
            element,
            property,
            color);
    }

    private static void SetStyleProperty(
        XElement element,
        string property,
        string value)
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
                    value);
                found = true;
            }

            if (!found)
            {
                properties.Add(new KeyValuePair<string, string>(
                    property,
                    value));
            }

            element.SetAttributeValue(
                "style",
                string.Join(
                    ";",
                    properties.Select(item => $"{item.Key}:{item.Value}")));

            return;
        }

        element.SetAttributeValue(property, value);
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
            "polyline" or "polygon" =>
                !string.IsNullOrWhiteSpace((string?)element.Attribute("points")),
            "rect" =>
                PositiveAttribute(element, "width")
                && PositiveAttribute(element, "height"),
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

    private sealed record OwnedSourceShape(
        GeometricShape Shape,
        LogicalOwnershipAssignment? Assignment);

    private sealed record ShapeRenderResult(
        bool Rendered,
        string Mode,
        LogicalCoordinate? DisplayedCoordinate,
        bool ColorMatchesOwnership,
        string Source);
}
