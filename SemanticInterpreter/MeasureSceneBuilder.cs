using SvgMusic.Scene;

namespace SvgMusic.Semantics;

public sealed class MeasureSceneBuilder
{
    public SemanticDocument Build(
        GeometricScene geometry,
        NotationScene notation,
        ScoreLayout layout)
    {
        var elements = BuildElements(geometry, notation);
        var staffsById = layout.Staffs.ToDictionary(
            staff => staff.Id,
            StringComparer.Ordinal);
        var measures = new List<MeasureScene>();
        var measureNumber = 1;

        foreach (var system in layout.Systems)
        {
            if (system.StaffPairs.Count != 1)
            {
                throw new InvalidOperationException(
                    $"SemanticInterpreter PoC expects one piano staff pair per system, "
                    + $"but {system.Id} contains {system.StaffPairs.Count} pairs.");
            }

            var pair = system.StaffPairs[0];
            var upperStaff = staffsById[pair.UpperStaffId];
            var lowerStaff = staffsById[pair.LowerStaffId];

            foreach (var measure in pair.Measures)
            {
                var upperCoordinate = new LogicalCoordinate(
                    pair.UpperStaffId,
                    measure.Id);
                var lowerCoordinate = new LogicalCoordinate(
                    pair.LowerStaffId,
                    measure.Id);

                var upperElements = elements
                    .Where(element => OwnershipTouches(
                        element.Ownership,
                        upperCoordinate))
                    .OrderBy(element => element.Bounds.MinX)
                    .ThenBy(element => element.Bounds.MinY)
                    .ToArray();

                var lowerElements = elements
                    .Where(element => OwnershipTouches(
                        element.Ownership,
                        lowerCoordinate))
                    .OrderBy(element => element.Bounds.MinX)
                    .ThenBy(element => element.Bounds.MinY)
                    .ToArray();

                measures.Add(new MeasureScene(
                    measureNumber,
                    system.Id,
                    pair.Id,
                    measure.Id,
                    measure.XStart,
                    measure.XEnd,
                    measureNumber > 1 && measure == pair.Measures[0],
                    new StaffMeasureScene(
                        1,
                        pair.UpperStaffId,
                        upperStaff.Bounds,
                        upperStaff.AverageLineSpacing,
                        upperElements,
                        upperStaff.LedgerLevels),
                    new StaffMeasureScene(
                        2,
                        pair.LowerStaffId,
                        lowerStaff.Bounds,
                        lowerStaff.AverageLineSpacing,
                        lowerElements,
                        lowerStaff.LedgerLevels)));

                measureNumber++;
            }
        }

        return new SemanticDocument(measures);
    }

    private static IReadOnlyList<SemanticElement> BuildElements(
        GeometricScene geometry,
        NotationScene notation)
    {
        var result = new List<SemanticElement>();
        var shapesById = geometry.Shapes.ToDictionary(
            shape => shape.Id,
            StringComparer.Ordinal);

        foreach (var stroke in notation.Strokes)
        {
            if (stroke.Ownership is null)
            {
                continue;
            }

            var half = Math.Max(stroke.Width / 2.0, 0.01);
            var bounds = new BoundsD(
                Math.Min(stroke.Start.X, stroke.End.X) - half,
                Math.Min(stroke.Start.Y, stroke.End.Y) - half,
                Math.Max(stroke.Start.X, stroke.End.X) + half,
                Math.Max(stroke.Start.Y, stroke.End.Y) + half);

            result.Add(new StrokeElement
            {
                ShapeId = stroke.ShapeId,
                Bounds = bounds,
                Ownership = stroke.Ownership,
                Source = stroke
            });
        }

        foreach (var curve in notation.CurvedStrokes)
        {
            if (curve.Ownership is null || curve.Centerline.Count == 0)
            {
                continue;
            }

            var half = curve.WidthProfile.Count == 0
                ? 0.01
                : Math.Max(0.01, curve.WidthProfile.Max() / 2.0);
            var raw = BoundsD.FromPoints(curve.Centerline);
            var bounds = new BoundsD(
                raw.MinX - half,
                raw.MinY - half,
                raw.MaxX + half,
                raw.MaxY + half);

            result.Add(new CurveElement
            {
                ShapeId = curve.ShapeId,
                Bounds = bounds,
                Ownership = curve.Ownership,
                Source = curve
            });
        }

        foreach (var ellipse in notation.Ellipses)
        {
            if (ellipse.Ownership is null)
            {
                continue;
            }

            var radius = ellipse.MajorRadius;
            var bounds = new BoundsD(
                ellipse.Center.X - radius,
                ellipse.Center.Y - radius,
                ellipse.Center.X + radius,
                ellipse.Center.Y + radius);

            result.Add(new EllipseElement
            {
                ShapeId = ellipse.ShapeId,
                Bounds = bounds,
                Ownership = ellipse.Ownership,
                Source = ellipse
            });
        }

        foreach (var instance in notation.Instances)
        {
            if (instance.Ownership is null
                || !shapesById.TryGetValue(instance.ShapeId, out var shape))
            {
                continue;
            }

            result.Add(new ShapeElement
            {
                ShapeId = instance.ShapeId,
                Bounds = shape.Bounds,
                Ownership = instance.Ownership,
                Source = instance
            });
        }

        foreach (var bracket in notation.BracketSpanners)
        {
            if (bracket.Ownership is null)
            {
                continue;
            }

            result.Add(new BracketSpannerElement
            {
                ShapeId = bracket.Id,
                Bounds = BracketBounds(bracket),
                Ownership = bracket.Ownership,
                Source = bracket
            });
        }

        return result;
    }

    private static BoundsD BracketBounds(BracketSpannerPrimitive bracket)
    {
        var points = new List<PointD>
        {
            bracket.Start,
            bracket.End
        };

        if (bracket.LeftHookEnd is { } leftHook)
        {
            points.Add(leftHook);
        }

        if (bracket.RightHookEnd is { } rightHook)
        {
            points.Add(rightHook);
        }

        var raw = BoundsD.FromPoints(points);
        var half = Math.Max(bracket.StrokeWidth / 2.0, 0.01);
        return new BoundsD(
            raw.MinX - half,
            raw.MinY - half,
            raw.MaxX + half,
            raw.MaxY + half);
    }

    private static bool OwnershipTouches(
        LogicalOwnership ownership,
        LogicalCoordinate coordinate)
    {
        return ownership.Start == coordinate
            || ownership.End == coordinate;
    }
}
