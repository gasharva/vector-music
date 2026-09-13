namespace SvgMusic.Scene;

/// <summary>
/// Adds a final ownership pass for notation outside a piano system: above the
/// upper staff and below the lower staff.
///
/// The primary pass works measure by measure. For each side it finds the
/// furthest already-owned musical element which extends outside the staff and
/// uses it as the outer edge of a rectangular ownership band. Staff lines and
/// barlines are layout geometry and are never allowed to become outer anchors.
///
/// A single bounded closure pass handles a useful edge case: a generation-4
/// element owned by one measure may physically extend into an adjacent measure.
/// If that adjacent measure has no primary band on the same side, the crossing
/// element may seed one band there. Elements assigned by this closure never seed
/// another pass, so generation 4 cannot keep expanding across the page.
/// </summary>
public sealed class FourthGenerationOuterBandAssigner
{
    public (NotationScene Scene, LogicalOwnershipScene Ownership) AssignAndApply(
        GeometricScene geometry,
        NotationScene notation,
        ScoreLayout layout,
        LogicalOwnershipScene existingOwnership)
    {
        var elements = BuildElements(
            geometry,
            notation,
            layout);

        var assignments = existingOwnership.Assignments
            .ToDictionary(
                assignment => assignment.ShapeId,
                assignment => assignment.Ownership,
                StringComparer.Ordinal);

        var layoutAnchorExclusions = BuildLayoutAnchorExclusions(layout);
        var snapshot = assignments.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal);

        var primaryBands = BuildOuterBands(
            elements,
            layout,
            snapshot,
            layoutAnchorExclusions);

        AssignFourthGeneration(
            elements,
            primaryBands,
            assignments);

        var firstWaveShapeIds = assignments
            .Where(pair =>
                !snapshot.ContainsKey(pair.Key)
                && pair.Value.Generation == 4)
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.Ordinal);

        var closureSnapshot = assignments.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal);

        var closureBands = BuildClosureBands(
            elements,
            layout,
            closureSnapshot,
            firstWaveShapeIds,
            primaryBands,
            layoutAnchorExclusions);

        AssignFourthGeneration(
            elements,
            closureBands,
            assignments);

        var scene = ApplyOwnership(
            notation,
            assignments);

        var kinds = BuildKinds(notation);
        var ownershipScene = new LogicalOwnershipScene(
            assignments
                .Select(pair => new LogicalOwnershipAssignment(
                    pair.Key,
                    kinds.GetValueOrDefault(pair.Key, "Unknown"),
                    pair.Value))
                .OrderBy(assignment => ParseShapeNumber(assignment.ShapeId))
                .ThenBy(assignment => assignment.ShapeId, StringComparer.Ordinal)
                .ToArray());

        return (scene, ownershipScene);
    }

    private static IReadOnlyList<OuterBand> BuildOuterBands(
        IReadOnlyList<OwnershipElement> elements,
        ScoreLayout layout,
        IReadOnlyDictionary<string, LogicalOwnership> assignments,
        IReadOnlySet<string> layoutAnchorExclusions)
    {
        var result = new List<OuterBand>();
        var staffsById = layout.Staffs.ToDictionary(
            staff => staff.Id,
            StringComparer.Ordinal);

        foreach (var system in layout.Systems)
        {
            foreach (var pair in system.StaffPairs)
            {
                var upperStaff = staffsById[pair.UpperStaffId];
                var lowerStaff = staffsById[pair.LowerStaffId];

                foreach (var measure in pair.Measures)
                {
                    var upperCoordinate = new LogicalCoordinate(
                        upperStaff.Id,
                        measure.Id);
                    var lowerCoordinate = new LogicalCoordinate(
                        lowerStaff.Id,
                        measure.Id);

                    var upperAnchor = elements
                        .Where(element =>
                            !layoutAnchorExclusions.Contains(element.ShapeId)
                            && assignments.TryGetValue(element.ShapeId, out var ownership)
                            && OwnershipTouches(ownership, upperCoordinate)
                            && HorizontallyIntersects(element.Bounds, measure)
                            && element.Bounds.MinY < upperStaff.Bounds.MinY)
                        .OrderBy(element => element.Bounds.MinY)
                        .ThenBy(element => element.Bounds.MinX)
                        .FirstOrDefault();

                    if (upperAnchor is not null)
                    {
                        result.Add(new OuterBand(
                            upperCoordinate,
                            upperAnchor.ShapeId,
                            new BoundsD(
                                measure.XStart,
                                upperAnchor.Bounds.MinY,
                                measure.XEnd,
                                upperStaff.Bounds.MinY),
                            OuterBandSide.Above,
                            "OuterBandAbove"));
                    }

                    var lowerAnchor = elements
                        .Where(element =>
                            !layoutAnchorExclusions.Contains(element.ShapeId)
                            && assignments.TryGetValue(element.ShapeId, out var ownership)
                            && OwnershipTouches(ownership, lowerCoordinate)
                            && HorizontallyIntersects(element.Bounds, measure)
                            && element.Bounds.MaxY > lowerStaff.Bounds.MaxY)
                        .OrderByDescending(element => element.Bounds.MaxY)
                        .ThenBy(element => element.Bounds.MinX)
                        .FirstOrDefault();

                    if (lowerAnchor is not null)
                    {
                        result.Add(new OuterBand(
                            lowerCoordinate,
                            lowerAnchor.ShapeId,
                            new BoundsD(
                                measure.XStart,
                                lowerStaff.Bounds.MaxY,
                                measure.XEnd,
                                lowerAnchor.Bounds.MaxY),
                            OuterBandSide.Below,
                            "OuterBandBelow"));
                    }
                }
            }
        }

        return result;
    }

    private static IReadOnlyList<OuterBand> BuildClosureBands(
        IReadOnlyList<OwnershipElement> elements,
        ScoreLayout layout,
        IReadOnlyDictionary<string, LogicalOwnership> assignments,
        IReadOnlySet<string> firstWaveShapeIds,
        IReadOnlyList<OuterBand> primaryBands,
        IReadOnlySet<string> layoutAnchorExclusions)
    {
        if (firstWaveShapeIds.Count == 0)
        {
            return Array.Empty<OuterBand>();
        }

        var result = new List<OuterBand>();
        var staffsById = layout.Staffs.ToDictionary(
            staff => staff.Id,
            StringComparer.Ordinal);
        var primaryBandKeys = primaryBands
            .Select(band => new OuterBandKey(
                band.Coordinate,
                band.Side))
            .ToHashSet();

        foreach (var system in layout.Systems)
        {
            foreach (var pair in system.StaffPairs)
            {
                var upperStaff = staffsById[pair.UpperStaffId];
                var lowerStaff = staffsById[pair.LowerStaffId];

                for (var measureIndex = 0; measureIndex < pair.Measures.Count; measureIndex++)
                {
                    var measure = pair.Measures[measureIndex];
                    var neighborMeasureIds = GetNeighborMeasureIds(
                        pair.Measures,
                        measureIndex);

                    if (neighborMeasureIds.Count == 0)
                    {
                        continue;
                    }

                    var upperCoordinate = new LogicalCoordinate(
                        upperStaff.Id,
                        measure.Id);
                    var upperKey = new OuterBandKey(
                        upperCoordinate,
                        OuterBandSide.Above);

                    if (!primaryBandKeys.Contains(upperKey))
                    {
                        var upperAnchor = FindClosureAnchor(
                            elements,
                            assignments,
                            firstWaveShapeIds,
                            layoutAnchorExclusions,
                            upperStaff.Id,
                            upperCoordinate,
                            neighborMeasureIds,
                            measure,
                            element => element.Bounds.MinY < upperStaff.Bounds.MinY,
                            elementsOnSide => elementsOnSide
                                .OrderBy(element => element.Bounds.MinY)
                                .ThenBy(element => element.Bounds.MinX));

                        if (upperAnchor is not null)
                        {
                            result.Add(new OuterBand(
                                upperCoordinate,
                                upperAnchor.ShapeId,
                                new BoundsD(
                                    measure.XStart,
                                    upperAnchor.Bounds.MinY,
                                    measure.XEnd,
                                    upperStaff.Bounds.MinY),
                                OuterBandSide.Above,
                                "OuterBandAboveClosure"));
                        }
                    }

                    var lowerCoordinate = new LogicalCoordinate(
                        lowerStaff.Id,
                        measure.Id);
                    var lowerKey = new OuterBandKey(
                        lowerCoordinate,
                        OuterBandSide.Below);

                    if (!primaryBandKeys.Contains(lowerKey))
                    {
                        var lowerAnchor = FindClosureAnchor(
                            elements,
                            assignments,
                            firstWaveShapeIds,
                            layoutAnchorExclusions,
                            lowerStaff.Id,
                            lowerCoordinate,
                            neighborMeasureIds,
                            measure,
                            element => element.Bounds.MaxY > lowerStaff.Bounds.MaxY,
                            elementsOnSide => elementsOnSide
                                .OrderByDescending(element => element.Bounds.MaxY)
                                .ThenBy(element => element.Bounds.MinX));

                        if (lowerAnchor is not null)
                        {
                            result.Add(new OuterBand(
                                lowerCoordinate,
                                lowerAnchor.ShapeId,
                                new BoundsD(
                                    measure.XStart,
                                    lowerStaff.Bounds.MaxY,
                                    measure.XEnd,
                                    lowerAnchor.Bounds.MaxY),
                                OuterBandSide.Below,
                                "OuterBandBelowClosure"));
                        }
                    }
                }
            }
        }

        return result;
    }

    private static OwnershipElement? FindClosureAnchor(
        IReadOnlyList<OwnershipElement> elements,
        IReadOnlyDictionary<string, LogicalOwnership> assignments,
        IReadOnlySet<string> firstWaveShapeIds,
        IReadOnlySet<string> layoutAnchorExclusions,
        string staffId,
        LogicalCoordinate currentCoordinate,
        IReadOnlySet<string> neighborMeasureIds,
        MeasureLayout measure,
        Func<OwnershipElement, bool> isOnOuterSide,
        Func<IEnumerable<OwnershipElement>, IOrderedEnumerable<OwnershipElement>> order)
    {
        var candidates = elements
            .Where(element =>
                firstWaveShapeIds.Contains(element.ShapeId)
                && !layoutAnchorExclusions.Contains(element.ShapeId)
                && assignments.TryGetValue(element.ShapeId, out var ownership)
                && !OwnershipTouches(ownership, currentCoordinate)
                && OwnershipTouchesNeighbor(
                    ownership,
                    staffId,
                    neighborMeasureIds)
                && HorizontallyIntersects(element.Bounds, measure)
                && isOnOuterSide(element));

        return order(candidates).FirstOrDefault();
    }

    private static void AssignFourthGeneration(
        IReadOnlyList<OwnershipElement> elements,
        IReadOnlyList<OuterBand> bands,
        IDictionary<string, LogicalOwnership> assignments)
    {
        foreach (var element in elements)
        {
            if (assignments.ContainsKey(element.ShapeId))
            {
                continue;
            }

            var hits = bands
                .Where(band => BoundsIntersect(element.Bounds, band.Bounds))
                .ToArray();

            if (hits.Length == 0)
            {
                continue;
            }

            var startBand = hits
                .OrderBy(band => DistanceToBounds(element.StartPoint, band.Bounds))
                .ThenBy(band => band.Bounds.MinX)
                .ThenBy(band => band.Bounds.MinY)
                .First();

            var endBand = hits
                .OrderBy(band => DistanceToBounds(element.EndPoint, band.Bounds))
                .ThenByDescending(band => band.Bounds.MaxX)
                .ThenByDescending(band => band.Bounds.MaxY)
                .First();

            var reason = startBand.Reason == endBand.Reason
                ? startBand.Reason
                : "OuterBandSpan";

            assignments[element.ShapeId] = new LogicalOwnership(
                startBand.Coordinate,
                endBand.Coordinate,
                4,
                startBand.AnchorShapeId,
                0,
                reason);
        }
    }

    private static HashSet<string> BuildLayoutAnchorExclusions(
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

        foreach (var boundary in layout.Systems
                     .SelectMany(system => system.StaffPairs)
                     .SelectMany(pair => pair.Boundaries))
        {
            foreach (var strokeId in boundary.StrokeIds)
            {
                result.Add(strokeId);
            }
        }

        return result;
    }

    private static HashSet<string> GetNeighborMeasureIds(
        IReadOnlyList<MeasureLayout> measures,
        int measureIndex)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);

        if (measureIndex > 0)
        {
            result.Add(measures[measureIndex - 1].Id);
        }

        if (measureIndex + 1 < measures.Count)
        {
            result.Add(measures[measureIndex + 1].Id);
        }

        return result;
    }

    private static bool OwnershipTouches(
        LogicalOwnership ownership,
        LogicalCoordinate coordinate)
    {
        return ownership.Start == coordinate
            || ownership.End == coordinate;
    }

    private static bool OwnershipTouchesNeighbor(
        LogicalOwnership ownership,
        string staffId,
        IReadOnlySet<string> neighborMeasureIds)
    {
        return CoordinateTouchesNeighbor(
                ownership.Start,
                staffId,
                neighborMeasureIds)
            || CoordinateTouchesNeighbor(
                ownership.End,
                staffId,
                neighborMeasureIds);
    }

    private static bool CoordinateTouchesNeighbor(
        LogicalCoordinate coordinate,
        string staffId,
        IReadOnlySet<string> neighborMeasureIds)
    {
        return string.Equals(
                coordinate.StaffId,
                staffId,
                StringComparison.Ordinal)
            && neighborMeasureIds.Contains(coordinate.MeasureId);
    }

    private static bool HorizontallyIntersects(
        BoundsD bounds,
        MeasureLayout measure)
    {
        return bounds.MaxX >= measure.XStart
            && bounds.MinX <= measure.XEnd;
    }

    private static IReadOnlyDictionary<string, string> BuildKinds(
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

    private static NotationScene ApplyOwnership(
        NotationScene notation,
        IReadOnlyDictionary<string, LogicalOwnership> assignments)
    {
        return notation with
        {
            Instances = notation.Instances
                .Select(instance => instance with
                {
                    Ownership = assignments.GetValueOrDefault(instance.ShapeId)
                })
                .ToArray(),
            Strokes = notation.Strokes
                .Select(stroke => stroke with
                {
                    Ownership = assignments.GetValueOrDefault(stroke.ShapeId)
                })
                .ToArray(),
            CurvedStrokes = notation.CurvedStrokes
                .Select(curve => curve with
                {
                    Ownership = assignments.GetValueOrDefault(curve.ShapeId)
                })
                .ToArray(),
            Ellipses = notation.Ellipses
                .Select(ellipse => ellipse with
                {
                    Ownership = assignments.GetValueOrDefault(ellipse.ShapeId)
                })
                .ToArray()
        };
    }

    private static IReadOnlyList<OwnershipElement> BuildElements(
        GeometricScene geometry,
        NotationScene notation,
        ScoreLayout layout)
    {
        var shapesById = geometry.Shapes.ToDictionary(
            shape => shape.Id,
            StringComparer.Ordinal);
        var result = new List<OwnershipElement>();

        var maxMeasureWidth = layout.Systems
            .SelectMany(system => system.StaffPairs)
            .SelectMany(pair => pair.Measures)
            .Select(measure => measure.XEnd - measure.XStart)
            .DefaultIfEmpty(double.PositiveInfinity)
            .Max();

        var maxMeasureHeight = layout.Staffs
            .Select(staff => staff.Bounds.Height + 2.0 * staff.AverageLineSpacing)
            .DefaultIfEmpty(double.PositiveInfinity)
            .Max();

        bool IsOversized(BoundsD bounds)
        {
            return bounds.Width > maxMeasureWidth
                && bounds.Height > maxMeasureHeight;
        }

        foreach (var stroke in notation.Strokes)
        {
            var half = Math.Max(stroke.Width / 2.0, 0.01);
            var bounds = new BoundsD(
                Math.Min(stroke.Start.X, stroke.End.X) - half,
                Math.Min(stroke.Start.Y, stroke.End.Y) - half,
                Math.Max(stroke.Start.X, stroke.End.X) + half,
                Math.Max(stroke.Start.Y, stroke.End.Y) + half);

            if (!IsOversized(bounds))
            {
                result.Add(new OwnershipElement(
                    stroke.ShapeId,
                    bounds,
                    stroke.Start,
                    stroke.End));
            }
        }

        foreach (var curve in notation.CurvedStrokes)
        {
            if (curve.Centerline.Count == 0)
            {
                continue;
            }

            var maxHalfWidth = curve.WidthProfile.Count == 0
                ? 0.01
                : Math.Max(0.01, curve.WidthProfile.Max() / 2.0);
            var raw = BoundsD.FromPoints(curve.Centerline);
            var bounds = new BoundsD(
                raw.MinX - maxHalfWidth,
                raw.MinY - maxHalfWidth,
                raw.MaxX + maxHalfWidth,
                raw.MaxY + maxHalfWidth);

            if (!IsOversized(bounds))
            {
                result.Add(new OwnershipElement(
                    curve.ShapeId,
                    bounds,
                    curve.Centerline[0],
                    curve.Centerline[^1]));
            }
        }

        foreach (var ellipse in notation.Ellipses)
        {
            var radius = ellipse.MajorRadius;
            var bounds = new BoundsD(
                ellipse.Center.X - radius,
                ellipse.Center.Y - radius,
                ellipse.Center.X + radius,
                ellipse.Center.Y + radius);

            if (!IsOversized(bounds))
            {
                result.Add(new OwnershipElement(
                    ellipse.ShapeId,
                    bounds,
                    ellipse.Center,
                    ellipse.Center));
            }
        }

        foreach (var instance in notation.Instances)
        {
            if (!shapesById.TryGetValue(instance.ShapeId, out var shape)
                || IsOversized(shape.Bounds))
            {
                continue;
            }

            result.Add(new OwnershipElement(
                instance.ShapeId,
                shape.Bounds,
                new PointD(shape.Bounds.MinX, shape.Bounds.CenterY),
                new PointD(shape.Bounds.MaxX, shape.Bounds.CenterY)));
        }

        return result;
    }

    private static bool BoundsIntersect(BoundsD left, BoundsD right)
    {
        return left.MaxX >= right.MinX
            && right.MaxX >= left.MinX
            && left.MaxY >= right.MinY
            && right.MaxY >= left.MinY;
    }

    private static double DistanceToBounds(
        PointD point,
        BoundsD bounds)
    {
        var dx = Math.Max(
            Math.Max(bounds.MinX - point.X, 0),
            point.X - bounds.MaxX);
        var dy = Math.Max(
            Math.Max(bounds.MinY - point.Y, 0),
            point.Y - bounds.MaxY);

        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static int ParseShapeNumber(string shapeId)
    {
        if (!shapeId.StartsWith("shape-", StringComparison.Ordinal))
        {
            return int.MaxValue;
        }

        var end = shapeId.IndexOf('.', StringComparison.Ordinal);
        var text = end >= 0
            ? shapeId[6..end]
            : shapeId[6..];

        return int.TryParse(text, out var number)
            ? number
            : int.MaxValue;
    }

    private enum OuterBandSide
    {
        Above,
        Below
    }

    private sealed record OwnershipElement(
        string ShapeId,
        BoundsD Bounds,
        PointD StartPoint,
        PointD EndPoint);

    private sealed record OuterBand(
        LogicalCoordinate Coordinate,
        string AnchorShapeId,
        BoundsD Bounds,
        OuterBandSide Side,
        string Reason);

    private sealed record OuterBandKey(
        LogicalCoordinate Coordinate,
        OuterBandSide Side);
}
