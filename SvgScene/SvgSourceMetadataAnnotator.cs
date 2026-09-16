using System.Xml.Linq;

namespace SvgMusic.Scene;

/// <summary>
/// Adds lightweight SVG-source metadata that is intentionally not part of the
/// geometric normalization itself. Shape numbering mirrors SvgNormalizer, so
/// metadata survives CompoundShapeSplitter through the shared shape-N prefix.
///
/// The recognizers must not depend on MuseScore class names, but keeping the
/// original class is useful for diagnostics. Stroke dash style is real visual
/// evidence and is needed to distinguish solid from dashed bracket primitives.
/// </summary>
public sealed class SvgSourceMetadataAnnotator
{
    public GeometricScene Annotate(
        string svgFile,
        GeometricScene scene)
    {
        var document = XDocument.Load(svgFile);
        var root = document.Root
            ?? throw new InvalidDataException("SVG root element is missing.");

        var definitions = root.Descendants()
            .Where(element => element.Attribute("id") is not null)
            .GroupBy(
                element => (string)element.Attribute("id")!,
                StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.Ordinal);

        var metadata = new Dictionary<string, SourceMetadata>(StringComparer.Ordinal);
        var number = 0;

        foreach (var element in root.Descendants())
        {
            if (IsInsideDefs(element))
                continue;

            if (element.Name.LocalName == "use")
            {
                var href = (string?)element.Attribute("href")
                    ?? element.Attributes()
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
                    metadata[$"shape-{++number}"] = ReadMetadata(
                        source,
                        element);
                }

                continue;
            }

            if (!IsGeometryElement(element))
                continue;

            metadata[$"shape-{++number}"] = ReadMetadata(
                element,
                element);
        }

        return new GeometricScene(
            scene.Shapes
                .Select(shape =>
                {
                    var baseId = BaseShapeId(shape.Id);

                    if (!metadata.TryGetValue(baseId, out var source))
                        return shape;

                    return shape with
                    {
                        SourceClass = source.SourceClass,
                        StrokeDashArray = source.StrokeDashArray
                    };
                })
                .ToArray());
    }

    private static SourceMetadata ReadMetadata(
        XElement source,
        XElement instanceElement)
    {
        var sourceClass = (string?)source.Attribute("class")
            ?? (string?)instanceElement.Attribute("class");
        var dash = ResolveProperty(
            source,
            instanceElement,
            "stroke-dasharray");

        if (string.IsNullOrWhiteSpace(dash)
            || dash.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            dash = null;
        }

        return new SourceMetadata(
            sourceClass,
            dash?.Trim());
    }

    private static string? ResolveProperty(
        XElement source,
        XElement instanceElement,
        string propertyName)
    {
        var sourceValue = FindInheritedProperty(
            source,
            propertyName);

        if (sourceValue is not null)
            return sourceValue;

        if (!ReferenceEquals(source, instanceElement))
        {
            return FindInheritedProperty(
                instanceElement,
                propertyName);
        }

        return null;
    }

    private static string? FindInheritedProperty(
        XElement element,
        string propertyName)
    {
        for (XElement? current = element;
             current is not null;
             current = current.Parent)
        {
            var style = (string?)current.Attribute("style");

            if (!string.IsNullOrWhiteSpace(style))
            {
                foreach (var declaration in style.Split(
                             ';',
                             StringSplitOptions.RemoveEmptyEntries))
                {
                    var pair = declaration.Split(
                        ':',
                        2,
                        StringSplitOptions.TrimEntries);

                    if (pair.Length == 2
                        && pair[0].Equals(
                            propertyName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return pair[1];
                    }
                }
            }

            var attribute = (string?)current.Attribute(propertyName);

            if (!string.IsNullOrWhiteSpace(attribute))
                return attribute.Trim();
        }

        return null;
    }

    private static string BaseShapeId(string id)
    {
        var separator = id.IndexOf('.', StringComparison.Ordinal);
        return separator < 0 ? id : id[..separator];
    }

    private static IEnumerable<XElement> GeometryElements(
        XElement target)
    {
        if (IsGeometryElement(target))
            yield return target;

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

    private sealed record SourceMetadata(
        string? SourceClass,
        string? StrokeDashArray);
}
