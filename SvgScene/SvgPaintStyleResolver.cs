using System.Globalization;
using System.Xml.Linq;

namespace SvgMusic.Scene;

public sealed record SvgPaintStyle(
    bool HasFill,
    bool HasStroke,
    double StrokeWidth);

/// <summary>
/// Resolves the small subset of SVG paint semantics that affects geometric
/// interpretation. In particular, an open path with a visible fill is filled
/// as though each subpath had an implicit closing segment.
/// </summary>
public sealed class SvgPaintStyleResolver
{
    public SvgPaintStyle Resolve(
        XElement source,
        XElement instanceElement)
    {
        var fill = ResolveProperty(
            source,
            instanceElement,
            "fill",
            "black");

        var stroke = ResolveProperty(
            source,
            instanceElement,
            "stroke",
            "none");

        var fillOpacity = ResolveNumberProperty(
            source,
            instanceElement,
            "fill-opacity",
            1.0);

        var strokeOpacity = ResolveNumberProperty(
            source,
            instanceElement,
            "stroke-opacity",
            1.0);

        var strokeWidth = ResolveNumberProperty(
            source,
            instanceElement,
            "stroke-width",
            1.0);

        var hasFill = !IsNone(fill)
            && fillOpacity > 0;

        var hasStroke = !IsNone(stroke)
            && strokeOpacity > 0
            && strokeWidth > 0;

        return new SvgPaintStyle(
            hasFill,
            hasStroke,
            hasStroke ? strokeWidth : 0);
    }

    private static string ResolveProperty(
        XElement source,
        XElement instanceElement,
        string propertyName,
        string defaultValue)
    {
        var sourceValue = FindInheritedProperty(
            source,
            propertyName);

        if (sourceValue is not null)
            return sourceValue;

        if (!ReferenceEquals(source, instanceElement))
        {
            var instanceValue = FindInheritedProperty(
                instanceElement,
                propertyName);

            if (instanceValue is not null)
                return instanceValue;
        }

        return defaultValue;
    }

    private static double ResolveNumberProperty(
        XElement source,
        XElement instanceElement,
        string propertyName,
        double defaultValue)
    {
        var text = ResolveProperty(
            source,
            instanceElement,
            propertyName,
            defaultValue.ToString(CultureInfo.InvariantCulture));

        return double.TryParse(
            text,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var value)
            ? Math.Max(0, value)
            : defaultValue;
    }

    private static string? FindInheritedProperty(
        XElement element,
        string propertyName)
    {
        for (XElement? current = element;
             current is not null;
             current = current.Parent)
        {
            var inlineStyleValue = FindStyleDeclaration(
                current,
                propertyName);

            if (inlineStyleValue is not null)
                return inlineStyleValue;

            var presentationAttribute =
                (string?)current.Attribute(propertyName);

            if (!string.IsNullOrWhiteSpace(presentationAttribute))
                return presentationAttribute.Trim();
        }

        return null;
    }

    private static string? FindStyleDeclaration(
        XElement element,
        string propertyName)
    {
        var style = (string?)element.Attribute("style");

        if (string.IsNullOrWhiteSpace(style))
            return null;

        foreach (var declaration in style.Split(
                     ';',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = declaration.Split(
                ':',
                2,
                StringSplitOptions.TrimEntries);

            if (pair.Length != 2)
                continue;

            if (pair[0].Equals(
                    propertyName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return pair[1];
            }
        }

        return null;
    }

    private static bool IsNone(string value) =>
        value.Trim().Equals(
            "none",
            StringComparison.OrdinalIgnoreCase);
}
