namespace SvgMusic.Scene;

/// <summary>
/// Adds classification-only whole-shape alternatives for SVG paths that were split
/// into several independent geometric components. The whole alternative is appended
/// after primitive extraction, so it can be classified and owned without ever being
/// reinterpreted as a stroke/ellipse/hairpin/etc.
/// </summary>
public sealed class CompoundClassificationHypothesisBuilder
{
    private const double MaxWidthInSpacings = 5.0;
    private const double MaxHeightInSpacings = 4.0;

    private readonly ShapeDescriptorMatcher _descriptorMatcher = new();

    public (GeometricScene Geometry, NotationScene Notation) Augment(
        GeometricScene geometry,
        NotationScene notation,
        ScoreLayout layout,
        IReadOnlySet<string>? excludedSourceShapeIds = null)
    {
        var spacing = Math.Max(
            0.001,
            GlyphRasterizer.ResolveSourceInterline(layout));
        var existingShapeIds = geometry.Shapes
            .Select(shape => shape.Id)
            .ToHashSet(StringComparer.Ordinal);
        var addedShapes = new List<GeometricShape>();
        var addedPrototypes = new List<ShapePrototype>();
        var addedInstances = new List<ShapeInstance>();

        var groups = geometry.Shapes
            .Select(shape => new
            {
                Shape = shape,
                BaseId = TryGetSplitBaseId(shape.Id)
            })
            .Where(item => item.BaseId is not null)
            .GroupBy(item => item.BaseId!, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal);

        foreach (var group in groups)
        {
            var parts = group
                .Select(item => item.Shape)
                .OrderBy(shape => shape.Id, StringComparer.Ordinal)
                .ToArray();

            if (parts.Any(part =>
                    excludedSourceShapeIds?.Contains(part.Id) == true))
            {
                continue;
            }

            if (parts.Length < 2)
            {
                continue;
            }

            var wholeId = $"{group.Key}.whole";
            if (existingShapeIds.Contains(wholeId))
            {
                continue;
            }

            var contours = parts
                .SelectMany(shape => shape.EffectiveContours)
                .ToArray();
            var points = contours
                .SelectMany(contour => contour.Points)
                .ToArray();

            if (points.Length == 0)
            {
                continue;
            }

            var bounds = BoundsD.FromPoints(points);
            if (bounds.Width <= 0
                || bounds.Height <= 0
                || bounds.Width > spacing * MaxWidthInSpacings
                || bounds.Height > spacing * MaxHeightInSpacings)
            {
                continue;
            }

            var first = parts[0];
            var whole = new GeometricShape(
                wholeId,
                first.SourceKind,
                points,
                bounds,
                first.SourceId,
                first.SourceIndex,
                false,
                parts.Max(shape => shape.StrokeWidth),
                contours,
                parts.Any(shape => shape.HasFill),
                parts.Any(shape => shape.HasStroke),
                CommonValue(parts.Select(shape => shape.SourceClass)),
                CommonValue(parts.Select(shape => shape.StrokeDashArray)));

            var prototypeId = $"compound-prototype-{group.Key}";
            var prototype = new ShapePrototype(
                prototypeId,
                wholeId,
                _descriptorMatcher.Describe(whole));
            var instance = new ShapeInstance(
                wholeId,
                prototypeId,
                bounds.MinX,
                bounds.MinY,
                bounds.Width,
                bounds.Height,
                first.SourceKind,
                first.SourceIndex,
                null,
                parts.Select(shape => shape.Id).ToArray());

            addedShapes.Add(whole);
            addedPrototypes.Add(prototype);
            addedInstances.Add(instance);
            existingShapeIds.Add(wholeId);
        }

        if (addedShapes.Count == 0)
        {
            return (geometry, notation);
        }

        return (
            new GeometricScene([.. geometry.Shapes, .. addedShapes]),
            notation with
            {
                Prototypes = [.. notation.Prototypes, .. addedPrototypes],
                Instances = [.. notation.Instances, .. addedInstances]
            });
    }

    private static string? TryGetSplitBaseId(string shapeId)
    {
        var separator = shapeId.LastIndexOf('.');
        if (separator <= 0
            || separator == shapeId.Length - 1
            || !int.TryParse(shapeId[(separator + 1)..], out _))
        {
            return null;
        }

        return shapeId[..separator];
    }

    private static string? CommonValue(IEnumerable<string?> values)
    {
        var materialized = values.Distinct(StringComparer.Ordinal).ToArray();
        return materialized.Length == 1 ? materialized[0] : null;
    }
}
