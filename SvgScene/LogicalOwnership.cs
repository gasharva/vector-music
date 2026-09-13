namespace SvgMusic.Scene;

public sealed record LogicalCoordinate(
    string StaffId,
    string MeasureId);

public sealed record LogicalOwnership(
    LogicalCoordinate Start,
    LogicalCoordinate End,
    int Generation,
    string? ParentShapeId,
    double Distance,
    string Reason)
{
    public bool IsSpan => Start != End;
}

public sealed record LogicalOwnershipAssignment(
    string ShapeId,
    string Kind,
    LogicalOwnership Ownership);

public sealed record LogicalOwnershipScene(
    IReadOnlyList<LogicalOwnershipAssignment> Assignments);

public sealed class LogicalOwnershipAnalyzer
{
    private const double LedgerHalfHeightInSpacings = 0.25;

    private static readonly IReadOnlyDictionary<string, double> DistanceThresholds =
        new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["Stroke"] = 0.75,
            ["EllipseLike"] = 0.90,
            ["ShapeInstance"] = 1.40,
            ["CurvedStroke"] = 1.80
        };

    public (NotationScene Scene, LogicalOwnershipScene Ownership) AnalyzeAndApply(
        GeometricScene geometry,
        NotationScene notation,
        ScoreLayout layout)
    {
        var elements = BuildElements(geometry, notation);
        var regions = BuildRegions(layout);
        var boundaries = layout.Systems
            .SelectMany(system => system.StaffPairs)
            .SelectMany(pair => pair.Boundaries)
            .ToArray();

        var sourceInterline = GlyphRasterizer.ResolveSourceInterline(layout);
        var assignments = new Dictionary<string, LogicalOwnership>(StringComparer.Ordinal);

        AssignFirstGeneration(elements, regions, assignments);
        PropagateGeneration(2, elements, assignments, boundaries, sourceInterline);
        PropagateGeneration(3, elements, assignments, boundaries, sourceInterline);
        AssignBetweenStaffBridges(elements, layout, assignments);

        var scene = ApplyOwnership(notation, assignments);
        var ownershipScene = new LogicalOwnershipScene(
            elements
                .Where(element => assignments.ContainsKey(element.ShapeId))
                .Select(element => new LogicalOwnershipAssignment(
                    element.ShapeId,
                    element.Kind,
                    assignments[element.ShapeId]))
                .ToArray());

        return (scene, ownershipScene);
    }

    private static void AssignFirstGeneration(
        IReadOnlyList<OwnershipElement> elements,
        IReadOnlyList<OwnershipRegion> regions,
        IDictionary<string, LogicalOwnership> assignments)
    {
        foreach (var element in elements)
        {
            var hits = regions
                .Where(region => IntersectsRegion(element, region))
                .ToArray();

            if (hits.Length == 0)
            {
                continue;
            }

            var startRegion = hits
                .OrderBy(region => DistanceToBounds(element.StartPoint, region.Bounds))
                .ThenBy(region => region.Bounds.MinX)
                .ThenBy(region => region.Bounds.MinY)
                .First();

            var endRegion = hits
                .OrderBy(region => DistanceToBounds(element.EndPoint, region.Bounds))
                .ThenByDescending(region => region.Bounds.MaxX)
                .ThenByDescending(region => region.Bounds.MaxY)
                .First();

            assignments[element.ShapeId] = new LogicalOwnership(
                startRegion.Coordinate,
                endRegion.Coordinate,
                1,
                null,
                0,
                "DirectIntersection");
        }
    }

    private static void PropagateGeneration(
        int generation,
        IReadOnlyList<OwnershipElement> elements,
        IDictionary<string, LogicalOwnership> assignments,
        IReadOnlyList<MeasureBoundary> boundaries,
        double sourceInterline)
    {
        var parents = elements
            .Where(element =>
                assignments.TryGetValue(element.ShapeId, out var ownership)
                && ownership.Generation == generation - 1)
            .ToArray();

        if (parents.Length == 0)
        {
            return;
        }

        foreach (var element in elements)
        {
            if (assignments.ContainsKey(element.ShapeId))
            {
                continue;
            }

            var threshold = sourceInterline * DistanceThresholds[element.Kind];
            OwnershipElement? bestParent = null;
            var bestDistance = double.PositiveInfinity;

            foreach (var parent in parents)
            {
                var distance = GeometryDistance(element, parent);

                if (distance > threshold || distance >= bestDistance)
                {
                    continue;
                }

                if (CrossesBlockingMeasureBoundary(element, parent, boundaries))
                {
                    continue;
                }

                bestParent = parent;
                bestDistance = distance;
            }

            if (bestParent is null)
            {
                continue;
            }

            var parentOwnership = assignments[bestParent.ShapeId];

            assignments[element.ShapeId] = new LogicalOwnership(
                parentOwnership.Start,
                parentOwnership.End,
                generation,
                bestParent.ShapeId,
                bestDistance,
                "Proximity");
        }
    }

    private static void AssignBetweenStaffBridges(
        IReadOnlyList<OwnershipElement> elements,
        ScoreLayout layout,
        IDictionary<string, LogicalOwnership> assignments)
    {
        var staffsById = layout.Staffs.ToDictionary(
            staff => staff.Id,
            StringComparer.Ordinal);

        foreach (var element in elements)
        {
            if (assignments.ContainsKey(element.ShapeId))
            {
                continue;
            }

            foreach (var system in layout.Systems)
            {
                var assigned = false;

                foreach (var pair in system.StaffPairs)
                {
                    var upper = staffsById[pair.UpperStaffId];
                    var lower = staffsById[pair.LowerStaffId];
                    var gapTop = upper.Bounds.MaxY;
                    var gapBottom = lower.Bounds.MinY;

                    if (gapBottom <= gapTop
                        || element.Bounds.MaxY < gapTop
                        || element.Bounds.MinY > gapBottom)
                    {
                        continue;
                    }

                    foreach (var measure in pair.Measures)
                    {
                        if (element.Bounds.MaxX < measure.XStart
                            || element.Bounds.MinX > measure.XEnd)
                        {
                            continue;
                        }

                        assignments[element.ShapeId] = new LogicalOwnership(
                            new LogicalCoordinate(upper.Id, measure.Id),
                            new LogicalCoordinate(lower.Id, measure.Id),
                            1,
                            null,
                            0,
                            "BetweenStaffBridge");

                        assigned = true;
                        break;
                    }

                    if (assigned)
                    {
                        break;
                    }
                }

                if (assigned)
                {
                    break;
                }
            }
        }
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

    private static IReadOnlyList<OwnershipRegion> BuildRegions(ScoreLayout layout)
    {
        var result = new List<OwnershipRegion>();
        var staffsById = layout.Staffs.ToDictionary(
            staff => staff.Id,
            StringComparer.Ordinal);

        foreach (var system in layout.Systems)
        {
            foreach (var pair in system.StaffPairs)
            {
                foreach (var staffId in new[] { pair.UpperStaffId, pair.LowerStaffId })
                {
                    var staff = staffsById[staffId];
                    var spacing = staff.AverageLineSpacing;

                    foreach (var measure in pair.Measures)
                    {
                        var coordinate = new LogicalCoordinate(staff.Id, measure.Id);

                        result.Add(new OwnershipRegion(
                            coordinate,
                            new BoundsD(
                                measure.XStart,
                                staff.Bounds.MinY - spacing,
                                measure.XEnd,
                                staff.Bounds.MaxY + spacing),
                            false));

                        foreach (var level in staff.LedgerLevels)
                        {
                            foreach (var segment in level.Segments)
                            {
                                var xStart = Math.Max(segment.XStart, measure.XStart);
                                var xEnd = Math.Min(segment.XEnd, measure.XEnd);

                                if (xEnd <= xStart)
                                {
                                    continue;
                                }

                                result.Add(new OwnershipRegion(
                                    coordinate,
                                    new BoundsD(
                                        xStart,
                                        level.Y - spacing * LedgerHalfHeightInSpacings,
                                        xEnd,
                                        level.Y + spacing * LedgerHalfHeightInSpacings),
                                    true));
                            }
                        }
                    }
                }
            }
        }

        return result;
    }

    private static IReadOnlyList<OwnershipElement> BuildElements(
        GeometricScene geometry,
        NotationScene notation)
    {
        var shapesById = geometry.Shapes.ToDictionary(
            shape => shape.Id,
            StringComparer.Ordinal);
        var result = new List<OwnershipElement>();

        foreach (var stroke in notation.Strokes)
        {
            var half = Math.Max(stroke.Width / 2.0, 0.01);
            var bounds = new BoundsD(
                Math.Min(stroke.Start.X, stroke.End.X) - half,
                Math.Min(stroke.Start.Y, stroke.End.Y) - half,
                Math.Max(stroke.Start.X, stroke.End.X) + half,
                Math.Max(stroke.Start.Y, stroke.End.Y) + half);

            result.Add(new OwnershipElement(
                stroke.ShapeId,
                "Stroke",
                bounds,
                [stroke.Start, stroke.End],
                stroke.Start,
                stroke.End));
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

            result.Add(new OwnershipElement(
                curve.ShapeId,
                "CurvedStroke",
                bounds,
                curve.Centerline,
                curve.Centerline[0],
                curve.Centerline[^1]));
        }

        foreach (var ellipse in notation.Ellipses)
        {
            var radius = ellipse.MajorRadius;
            var bounds = new BoundsD(
                ellipse.Center.X - radius,
                ellipse.Center.Y - radius,
                ellipse.Center.X + radius,
                ellipse.Center.Y + radius);
            var points = Enumerable.Range(0, 24)
                .Select(index =>
                {
                    var angle = 2.0 * Math.PI * index / 24.0;
                    var cos = Math.Cos(angle);
                    var sin = Math.Sin(angle);
                    var rotatedX = ellipse.MajorRadius * cos * Math.Cos(ellipse.Rotation)
                        - ellipse.MinorRadius * sin * Math.Sin(ellipse.Rotation);
                    var rotatedY = ellipse.MajorRadius * cos * Math.Sin(ellipse.Rotation)
                        + ellipse.MinorRadius * sin * Math.Cos(ellipse.Rotation);

                    return new PointD(
                        ellipse.Center.X + rotatedX,
                        ellipse.Center.Y + rotatedY);
                })
                .Append(new PointD(
                    ellipse.Center.X + ellipse.MajorRadius * Math.Cos(ellipse.Rotation),
                    ellipse.Center.Y + ellipse.MajorRadius * Math.Sin(ellipse.Rotation)))
                .ToArray();

            result.Add(new OwnershipElement(
                ellipse.ShapeId,
                "EllipseLike",
                bounds,
                points,
                ellipse.Center,
                ellipse.Center));
        }

        foreach (var instance in notation.Instances)
        {
            if (!shapesById.TryGetValue(instance.ShapeId, out var shape))
            {
                continue;
            }

            var points = shape.EffectiveContours
                .SelectMany(contour => contour.Points)
                .ToArray();

            if (points.Length == 0)
            {
                continue;
            }

            result.Add(new OwnershipElement(
                instance.ShapeId,
                "ShapeInstance",
                shape.Bounds,
                points,
                new PointD(shape.Bounds.MinX, shape.Bounds.CenterY),
                new PointD(shape.Bounds.MaxX, shape.Bounds.CenterY)));
        }

        return result;
    }

    private static bool IntersectsRegion(
        OwnershipElement element,
        OwnershipRegion region)
    {
        if (!BoundsIntersect(element.Bounds, region.Bounds))
        {
            return false;
        }

        if (element.Points.Any(point => PointInside(point, region.Bounds)))
        {
            return true;
        }

        for (var index = 1; index < element.Points.Count; index++)
        {
            if (SegmentIntersectsBounds(
                    element.Points[index - 1],
                    element.Points[index],
                    region.Bounds))
            {
                return true;
            }
        }

        return region.Bounds.CenterX >= element.Bounds.MinX
            && region.Bounds.CenterX <= element.Bounds.MaxX
            && region.Bounds.CenterY >= element.Bounds.MinY
            && region.Bounds.CenterY <= element.Bounds.MaxY;
    }

    private static double GeometryDistance(
        OwnershipElement left,
        OwnershipElement right)
    {
        if (BoundsIntersect(left.Bounds, right.Bounds))
        {
            return 0;
        }

        var best = double.PositiveInfinity;

        foreach (var point in left.Points)
        {
            best = Math.Min(best, DistanceToPolyline(point, right.Points));
        }

        foreach (var point in right.Points)
        {
            best = Math.Min(best, DistanceToPolyline(point, left.Points));
        }

        return best;
    }

    private static double DistanceToPolyline(
        PointD point,
        IReadOnlyList<PointD> polyline)
    {
        if (polyline.Count == 0)
        {
            return double.PositiveInfinity;
        }

        if (polyline.Count == 1)
        {
            return Distance(point, polyline[0]);
        }

        var best = double.PositiveInfinity;

        for (var index = 1; index < polyline.Count; index++)
        {
            best = Math.Min(
                best,
                DistanceToSegment(point, polyline[index - 1], polyline[index]));
        }

        return best;
    }

    private static bool CrossesBlockingMeasureBoundary(
        OwnershipElement left,
        OwnershipElement right,
        IReadOnlyList<MeasureBoundary> boundaries)
    {
        var start = new PointD(left.Bounds.CenterX, left.Bounds.CenterY);
        var end = new PointD(right.Bounds.CenterX, right.Bounds.CenterY);

        foreach (var boundary in boundaries)
        {
            if ((start.X < boundary.X && end.X < boundary.X)
                || (start.X > boundary.X && end.X > boundary.X)
                || Math.Abs(end.X - start.X) < 1e-12)
            {
                continue;
            }

            var t = (boundary.X - start.X) / (end.X - start.X);

            if (t < 0 || t > 1)
            {
                continue;
            }

            var y = start.Y + t * (end.Y - start.Y);

            if (y >= boundary.UpperY && y <= boundary.LowerY)
            {
                return true;
            }
        }

        return false;
    }

    private static bool SegmentIntersectsBounds(
        PointD start,
        PointD end,
        BoundsD bounds)
    {
        if (PointInside(start, bounds) || PointInside(end, bounds))
        {
            return true;
        }

        return SegmentsIntersect(start, end, new PointD(bounds.MinX, bounds.MinY), new PointD(bounds.MaxX, bounds.MinY))
            || SegmentsIntersect(start, end, new PointD(bounds.MaxX, bounds.MinY), new PointD(bounds.MaxX, bounds.MaxY))
            || SegmentsIntersect(start, end, new PointD(bounds.MaxX, bounds.MaxY), new PointD(bounds.MinX, bounds.MaxY))
            || SegmentsIntersect(start, end, new PointD(bounds.MinX, bounds.MaxY), new PointD(bounds.MinX, bounds.MinY));
    }

    private static bool SegmentsIntersect(
        PointD a,
        PointD b,
        PointD c,
        PointD d)
    {
        static double Cross(PointD p, PointD q, PointD r) =>
            (q.X - p.X) * (r.Y - p.Y) - (q.Y - p.Y) * (r.X - p.X);

        var abC = Cross(a, b, c);
        var abD = Cross(a, b, d);
        var cdA = Cross(c, d, a);
        var cdB = Cross(c, d, b);

        return abC * abD <= 0 && cdA * cdB <= 0;
    }

    private static bool BoundsIntersect(BoundsD left, BoundsD right) =>
        left.MaxX >= right.MinX
        && left.MinX <= right.MaxX
        && left.MaxY >= right.MinY
        && left.MinY <= right.MaxY;

    private static bool PointInside(PointD point, BoundsD bounds) =>
        point.X >= bounds.MinX
        && point.X <= bounds.MaxX
        && point.Y >= bounds.MinY
        && point.Y <= bounds.MaxY;

    private static double DistanceToBounds(PointD point, BoundsD bounds)
    {
        var dx = Math.Max(Math.Max(bounds.MinX - point.X, 0), point.X - bounds.MaxX);
        var dy = Math.Max(Math.Max(bounds.MinY - point.Y, 0), point.Y - bounds.MaxY);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double Distance(PointD left, PointD right)
    {
        var dx = left.X - right.X;
        var dy = left.Y - right.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double DistanceToSegment(
        PointD point,
        PointD start,
        PointD end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var lengthSquared = dx * dx + dy * dy;

        if (lengthSquared <= 1e-12)
        {
            return Distance(point, start);
        }

        var t = ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared;
        t = Math.Clamp(t, 0, 1);
        return Distance(
            point,
            new PointD(start.X + t * dx, start.Y + t * dy));
    }

    private sealed record OwnershipRegion(
        LogicalCoordinate Coordinate,
        BoundsD Bounds,
        bool IsLedger);

    private sealed record OwnershipElement(
        string ShapeId,
        string Kind,
        BoundsD Bounds,
        IReadOnlyList<PointD> Points,
        PointD StartPoint,
        PointD EndPoint);
}
