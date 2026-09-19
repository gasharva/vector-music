namespace SvgMusic.Scene;

public sealed record ScoreLayoutDiagnostics(
    int StrokeCount,
    int HorizontalStrokeCount,
    int LogicalHorizontalCount,
    int VerticalStrokeCount,
    int StaffCount,
    int SystemCount,
    int BoundaryCount,
    int MeasureCount,
    IReadOnlyList<string> LongHorizontalCandidates,
    IReadOnlyList<string> StaffDescriptions,
    IReadOnlyList<string> VerticalCandidates);

public sealed class ScoreLayoutAnalyzer
{
    public ScoreLayoutDiagnostics? LastDiagnostics { get; private set; }
    public ScoreLayout Analyze(NotationScene notation)
    {
        var horizontal = notation.Strokes
            .Select(NormalizeStroke)
            .Where(IsHorizontal)
            .ToList();
        var logicalHorizontal = MergeCollinearHorizontalSegments(horizontal);

        var vertical = notation.Strokes
            .Select(NormalizeStroke)
            .Where(IsVertical)
            .ToList();

        var staffs = DetectStaffs(logicalHorizontal, vertical);
        var systems = BuildSystemsAndPairs(staffs, vertical, horizontal);

        var maximumLength = logicalHorizontal.Count == 0
            ? 0
            : logicalHorizontal.Max(stroke => stroke.Length);
        var longThreshold = maximumLength * 0.45;
        var longCandidates = logicalHorizontal
            .Where(stroke => stroke.Length >= longThreshold)
            .OrderBy(stroke => stroke.CenterY)
            .Select(stroke =>
                $"{stroke.Stroke.ShapeId}: y={stroke.CenterY:F3} "
                + $"x={stroke.XStart:F3}..{stroke.XEnd:F3} "
                + $"len={stroke.Length:F3}")
            .ToArray();

        var staffDescriptions = staffs
            .Select(staff =>
                $"{staff.Id}: x={staff.Bounds.MinX:F3}..{staff.Bounds.MaxX:F3} "
                + $"y={staff.Bounds.MinY:F3}..{staff.Bounds.MaxY:F3} "
                + $"spacing={staff.AverageLineSpacing:F3}")
            .ToArray();

        var verticalCandidates = vertical
            .Where(stroke =>
                staffs.Count == 0
                || staffs.Any(staff =>
                    stroke.CenterX >= staff.Bounds.MinX - 2 * staff.AverageLineSpacing
                    && stroke.CenterX <= staff.Bounds.MaxX + 2 * staff.AverageLineSpacing
                    && stroke.YEnd >= staff.Bounds.MinY - 2 * staff.AverageLineSpacing
                    && stroke.YStart <= staff.Bounds.MaxY + 2 * staff.AverageLineSpacing))
            .OrderBy(stroke => stroke.CenterX)
            .ThenBy(stroke => stroke.YStart)
            .Select(stroke =>
                $"{stroke.Stroke.ShapeId}: x={stroke.CenterX:F3} "
                + $"y={stroke.YStart:F3}..{stroke.YEnd:F3} "
                + $"len={stroke.Length:F3} w={stroke.Stroke.Width:F3}")
            .Take(250)
            .ToArray();

        LastDiagnostics = new ScoreLayoutDiagnostics(
            notation.Strokes.Count,
            horizontal.Count,
            logicalHorizontal.Count,
            vertical.Count,
            staffs.Count,
            systems.Count,
            systems
                .SelectMany(system => system.StaffPairs)
                .Sum(pair => pair.Boundaries.Count),
            systems
                .SelectMany(system => system.StaffPairs)
                .Sum(pair => pair.Measures.Count),
            longCandidates,
            staffDescriptions,
            verticalCandidates);

        return new ScoreLayout(
            systems,
            staffs);
    }

    private static List<NormalizedStroke> MergeCollinearHorizontalSegments(
        IReadOnlyList<NormalizedStroke> horizontal)
    {
        if (horizontal.Count == 0)
        {
            return [];
        }

        var positiveWidths = horizontal
            .Select(stroke => stroke.Stroke.Width)
            .Where(width => width > 0)
            .OrderBy(width => width)
            .ToArray();
        var medianWidth = positiveWidths.Length == 0
            ? 0.5
            : positiveWidths[positiveWidths.Length / 2];
        var yTolerance = Math.Max(0.25, medianWidth * 1.5);
        var gapTolerance = Math.Max(0.75, medianWidth * 3.0);

        var yGroups = new List<List<NormalizedStroke>>();

        foreach (var stroke in horizontal.OrderBy(stroke => stroke.CenterY))
        {
            var group = yGroups.LastOrDefault();
            if (group is null
                || Math.Abs(stroke.CenterY - group.Average(item => item.CenterY))
                    > yTolerance)
            {
                yGroups.Add([stroke]);
            }
            else
            {
                group.Add(stroke);
            }
        }

        var result = new List<NormalizedStroke>();
        var syntheticIndex = 0;

        foreach (var yGroup in yGroups)
        {
            var ordered = yGroup
                .OrderBy(stroke => stroke.XStart)
                .ThenBy(stroke => stroke.XEnd)
                .ToArray();

            var run = new List<NormalizedStroke>();

            void Flush()
            {
                if (run.Count == 0)
                {
                    return;
                }

                if (run.Count == 1)
                {
                    result.Add(run[0]);
                    run.Clear();
                    return;
                }

                var xStart = run.Min(item => item.XStart);
                var xEnd = run.Max(item => item.XEnd);
                var centerY = run.Average(item => item.CenterY);
                var width = run
                    .Select(item => item.Stroke.Width)
                    .Where(value => value > 0)
                    .DefaultIfEmpty(medianWidth)
                    .Average();
                var stroke = new Stroke(
                    $"logical-horizontal-{++syntheticIndex}",
                    new PointD(xStart, centerY),
                    new PointD(xEnd, centerY),
                    width,
                    "logical-horizontal-run",
                    null);

                result.Add(NormalizeStroke(stroke));
                run.Clear();
            }

            foreach (var stroke in ordered)
            {
                if (run.Count == 0)
                {
                    run.Add(stroke);
                    continue;
                }

                var currentEnd = run.Max(item => item.XEnd);
                if (stroke.XStart <= currentEnd + gapTolerance)
                {
                    run.Add(stroke);
                    continue;
                }

                Flush();
                run.Add(stroke);
            }

            Flush();
        }

        return result;
    }

    private static List<StaffLayout> DetectStaffs(
        IReadOnlyList<NormalizedStroke> horizontal,
        IReadOnlyList<NormalizedStroke> vertical)
    {
        if (horizontal.Count < 5)
        {
            return [];
        }

        var maximumLength = horizontal.Max(stroke => stroke.Length);
        var longThreshold = maximumLength * 0.45;

        var candidates = horizontal
            .Where(stroke => stroke.Length >= longThreshold)
            .OrderBy(stroke => stroke.CenterY)
            .ToList();

        var result = new List<StaffLayout>();
        var staffNumber = 0;

        for (var index = 0; index < candidates.Count;)
        {
            var fullGroup = candidates
                .Skip(index)
                .Take(5)
                .ToArray();

            if (fullGroup.Length == 5
                && LooksLikeStaff(fullGroup))
            {
                staffNumber++;
                result.Add(CreateStaff(
                    $"staff-{staffNumber}",
                    string.Empty,
                    fullGroup,
                    []));

                index += 5;
                continue;
            }

            var fourLineGroup = candidates
                .Skip(index)
                .Take(4)
                .ToArray();

            if (fourLineGroup.Length == 4
                && TryRecoverMissingEdgeStaffLine(
                    fourLineGroup,
                    vertical,
                    staffNumber + 1,
                    out var recoveredGroup))
            {
                staffNumber++;
                result.Add(CreateStaff(
                    $"staff-{staffNumber}",
                    string.Empty,
                    recoveredGroup,
                    []));

                index += 4;
                continue;
            }

            index++;
        }

        return result;
    }

    private static bool TryRecoverMissingEdgeStaffLine(
        IReadOnlyList<NormalizedStroke> fourLines,
        IReadOnlyList<NormalizedStroke> vertical,
        int staffNumber,
        out IReadOnlyList<NormalizedStroke> recovered)
    {
        recovered = Array.Empty<NormalizedStroke>();

        var gaps = Enumerable.Range(0, 3)
            .Select(index =>
                fourLines[index + 1].CenterY
                - fourLines[index].CenterY)
            .ToArray();

        if (gaps.Any(gap => gap <= 0))
        {
            return false;
        }

        var spacing = gaps.Average();
        var maxDeviation = gaps
            .Max(gap => Math.Abs(gap - spacing));
        if (maxDeviation > spacing * 0.22)
        {
            return false;
        }

        var commonXStart = fourLines.Max(line => line.XStart);
        var commonXEnd = fourLines.Min(line => line.XEnd);
        var averageLength = fourLines.Average(line => line.Length);
        if (commonXEnd - commonXStart < averageLength * 0.72)
        {
            return false;
        }

        var shortest = fourLines.Min(line => line.Length);
        var longest = fourLines.Max(line => line.Length);
        if (shortest < longest * 0.70)
        {
            return false;
        }

        var missingTopY = fourLines[0].CenterY - spacing;
        var missingBottomY = fourLines[^1].CenterY + spacing;
        var endpointTolerance = spacing * 0.22;
        var minimumVerticalLength = spacing * 3.5;

        int EndpointEvidence(double targetY) =>
            vertical.Count(stroke =>
                stroke.Length >= minimumVerticalLength
                && stroke.CenterX >= commonXStart - spacing
                && stroke.CenterX <= commonXEnd + spacing
                && (Math.Abs(stroke.YStart - targetY) <= endpointTolerance
                    || Math.Abs(stroke.YEnd - targetY) <= endpointTolerance));

        var topEvidence = EndpointEvidence(missingTopY);
        var bottomEvidence = EndpointEvidence(missingBottomY);

        if (topEvidence == bottomEvidence
            || Math.Max(topEvidence, bottomEvidence) == 0)
        {
            return false;
        }

        var missingY = topEvidence > bottomEvidence
            ? missingTopY
            : missingBottomY;
        var xStart = fourLines.Min(line => line.XStart);
        var xEnd = fourLines.Max(line => line.XEnd);
        var width = fourLines
            .Select(line => line.Stroke.Width)
            .Where(value => value > 0)
            .DefaultIfEmpty(1.0)
            .Average();
        var inferredStroke = new Stroke(
            $"inferred-staff-{staffNumber}-edge",
            new PointD(xStart, missingY),
            new PointD(xEnd, missingY),
            width,
            "inferred-from-geometry",
            null);
        var inferred = NormalizeStroke(inferredStroke);

        recovered = (topEvidence > bottomEvidence
                ? new[] { inferred }.Concat(fourLines)
                : fourLines.Concat([inferred]))
            .ToArray();

        return LooksLikeStaff(recovered);
    }

    private static bool LooksLikeStaff(IReadOnlyList<NormalizedStroke> lines)
    {
        var gaps = new double[4];

        for (var index = 0; index < gaps.Length; index++)
        {
            gaps[index] = lines[index + 1].CenterY - lines[index].CenterY;
        }

        if (gaps.Any(gap => gap <= 0))
        {
            return false;
        }

        var averageSpacing = gaps.Average();
        var maximumSpacingDeviation = gaps
            .Max(gap => Math.Abs(gap - averageSpacing));

        if (maximumSpacingDeviation > averageSpacing * 0.22)
        {
            return false;
        }

        var commonXStart = lines.Max(line => line.XStart);
        var commonXEnd = lines.Min(line => line.XEnd);
        var averageLength = lines.Average(line => line.Length);

        if (commonXEnd - commonXStart < averageLength * 0.72)
        {
            return false;
        }

        var shortest = lines.Min(line => line.Length);
        var longest = lines.Max(line => line.Length);

        return shortest >= longest * 0.75;
    }

    private static StaffLayout CreateStaff(
        string id,
        string systemId,
        IReadOnlyList<NormalizedStroke> lines,
        IReadOnlyList<LedgerLevelLayout> ledgerLevels)
    {
        var spacing = Enumerable.Range(0, 4)
            .Select(index => lines[index + 1].CenterY - lines[index].CenterY)
            .Average();

        var staffLines = lines
            .Select((line, index) => new StaffLineLayout(
                index,
                line.CenterY,
                line.XStart,
                line.XEnd,
                line.Stroke.ShapeId))
            .ToArray();

        var bounds = new BoundsD(
            lines.Min(line => line.XStart),
            lines.First().CenterY,
            lines.Max(line => line.XEnd),
            lines.Last().CenterY);

        return new StaffLayout(
            id,
            systemId,
            bounds,
            staffLines,
            spacing,
            ledgerLevels);
    }

    private static List<ScoreSystem> BuildSystemsAndPairs(
        List<StaffLayout> staffs,
        IReadOnlyList<NormalizedStroke> vertical,
        IReadOnlyList<NormalizedStroke> horizontal)
    {
        var systems = new List<ScoreSystem>();

        for (var index = 0; index + 1 < staffs.Count; index += 2)
        {
            var upper = staffs[index];
            var lower = staffs[index + 1];

            if (!CanPair(upper, lower))
            {
                continue;
            }

            var systemId = $"system-{systems.Count + 1}";
            var pairId = $"staff-pair-{systems.Count + 1}";

            var upperWithLedgers = upper with
            {
                SystemId = systemId,
                LedgerLevels = DetectLedgerLevels(upper, horizontal)
            };

            var lowerWithLedgers = lower with
            {
                SystemId = systemId,
                LedgerLevels = DetectLedgerLevels(lower, horizontal)
            };

            staffs[index] = upperWithLedgers;
            staffs[index + 1] = lowerWithLedgers;

            var boundaries = DetectMeasureBoundaries(
                upperWithLedgers,
                lowerWithLedgers,
                vertical);

            var measures = BuildMeasures(pairId, boundaries);
            var bounds = new BoundsD(
                Math.Min(upper.Bounds.MinX, lower.Bounds.MinX),
                upper.Bounds.MinY,
                Math.Max(upper.Bounds.MaxX, lower.Bounds.MaxX),
                lower.Bounds.MaxY);

            var pair = new StaffPairLayout(
                pairId,
                upper.Id,
                lower.Id,
                bounds,
                measures,
                boundaries);

            systems.Add(new ScoreSystem(
                systemId,
                bounds,
                [pair]));
        }

        return systems;
    }

    private static bool CanPair(StaffLayout upper, StaffLayout lower)
    {
        if (lower.Bounds.MinY <= upper.Bounds.MaxY)
        {
            return false;
        }

        var overlap = HorizontalOverlap(
            upper.Bounds.MinX,
            upper.Bounds.MaxX,
            lower.Bounds.MinX,
            lower.Bounds.MaxX);

        var shorterWidth = Math.Min(
            upper.Bounds.Width,
            lower.Bounds.Width);

        if (overlap < shorterWidth * 0.75)
        {
            return false;
        }

        var spacingRatio = upper.AverageLineSpacing / lower.AverageLineSpacing;
        return spacingRatio is >= 0.8 and <= 1.25;
    }

    private static IReadOnlyList<LedgerLevelLayout> DetectLedgerLevels(
        StaffLayout staff,
        IReadOnlyList<NormalizedStroke> horizontal)
    {
        var staffStrokeIds = staff.Lines
            .Select(line => line.StrokeId)
            .ToHashSet(StringComparer.Ordinal);

        var maximumLedgerLength = staff.AverageLineSpacing * 4.5;
        var minimumLedgerLength = staff.AverageLineSpacing * 0.55;
        var yTolerance = staff.AverageLineSpacing * 0.28;

        var staffLineTolerance = staff.AverageLineSpacing * 0.18;
        var candidates = horizontal
            .Where(stroke => !staffStrokeIds.Contains(stroke.Stroke.ShapeId))
            .Where(stroke => !staff.Lines.Any(line =>
                Math.Abs(stroke.CenterY - line.Y) <= staffLineTolerance
                && HorizontalOverlap(
                    stroke.XStart,
                    stroke.XEnd,
                    line.XStart,
                    line.XEnd) > 0))
            .Where(stroke => stroke.Length >= minimumLedgerLength)
            .Where(stroke => stroke.Length <= maximumLedgerLength)
            .Where(stroke => HorizontalOverlap(
                stroke.XStart,
                stroke.XEnd,
                staff.Bounds.MinX,
                staff.Bounds.MaxX) > 0)
            .ToList();

        var result = new List<LedgerLevelLayout>();

        AddLedgerDirection(
            result,
            "above",
            staff.Bounds.MinY,
            -staff.AverageLineSpacing,
            candidates,
            yTolerance);

        AddLedgerDirection(
            result,
            "below",
            staff.Bounds.MaxY,
            staff.AverageLineSpacing,
            candidates,
            yTolerance);

        return result;
    }

    private static void AddLedgerDirection(
        List<LedgerLevelLayout> result,
        string direction,
        double staffEdgeY,
        double signedSpacing,
        IReadOnlyList<NormalizedStroke> candidates,
        double yTolerance)
    {
        IReadOnlyList<LedgerSegmentLayout>? previousLevel = null;

        for (var step = 1; step <= 12; step++)
        {
            var targetY = staffEdgeY + signedSpacing * step;

            var levelCandidates = candidates
                .Where(stroke => Math.Abs(stroke.CenterY - targetY) <= yTolerance)
                .Select(stroke => new LedgerSegmentLayout(
                    stroke.XStart,
                    stroke.XEnd,
                    stroke.Stroke.ShapeId))
                .ToList();

            if (step > 1)
            {
                levelCandidates = levelCandidates
                    .Where(candidate => previousLevel!.Any(previous =>
                        LedgerSegmentsOverlap(candidate, previous)))
                    .ToList();
            }

            if (levelCandidates.Count == 0)
            {
                break;
            }

            result.Add(new LedgerLevelLayout(
                direction,
                step,
                levelCandidates.Average(segment =>
                {
                    var source = candidates.First(stroke =>
                        stroke.Stroke.ShapeId == segment.StrokeId);
                    return source.CenterY;
                }),
                levelCandidates));

            previousLevel = levelCandidates;
        }
    }

    private static bool LedgerSegmentsOverlap(
        LedgerSegmentLayout current,
        LedgerSegmentLayout previous)
    {
        var currentLength = current.XEnd - current.XStart;
        var previousLength = previous.XEnd - previous.XStart;
        var allowance = Math.Min(currentLength, previousLength) * 0.2;

        return current.XEnd + allowance >= previous.XStart
            && previous.XEnd + allowance >= current.XStart;
    }

    private static IReadOnlyList<MeasureBoundary> DetectMeasureBoundaries(
        StaffLayout upper,
        StaffLayout lower,
        IReadOnlyList<NormalizedStroke> vertical)
    {
        var spacing = Math.Min(
            upper.AverageLineSpacing,
            lower.AverageLineSpacing);
        var endpointTolerance = spacing * 0.35;
        var xTolerance = spacing * 0.4;
        var clusterTolerance = spacing * 0.8;
        var finalBarlineTolerance = spacing * 1.25;

        var boundaries = new List<MeasureBoundary>();

        // Some engravers emit one continuous barline through both staves.
        foreach (var pairBar in vertical.Where(stroke =>
                     FitsPairBoundary(
                         stroke,
                         upper,
                         lower,
                         endpointTolerance)))
        {
            boundaries.Add(new MeasureBoundary(
                pairBar.CenterX,
                upper.Bounds.MinY,
                lower.Bounds.MaxY,
                [pairBar.Stroke.ShapeId],
                pairBar.Stroke.Width,
                pairBar.Stroke.Width));
        }

        // MuseScore commonly emits a piano barline as an upper segment that runs
        // from the top of the upper staff to the top of the lower staff, plus a
        // lower segment spanning the lower staff. A local upper+lower pair is also
        // accepted for engravers that split both staves independently.
        //
        // Requiring those endpoint phases is important: the old SpansStaff rule
        // accepted ordinary note stems that merely covered most of each staff.
        // When stems happened to line up vertically in both staves, they created
        // phantom measure boundaries.
        var upperBars = vertical
            .Where(stroke => FitsUpperBoundarySegment(
                stroke,
                upper,
                lower,
                endpointTolerance))
            .ToList();
        var lowerBars = vertical
            .Where(stroke => FitsStaffBoundarySegment(
                stroke,
                lower,
                endpointTolerance))
            .ToList();
        var usedLower = new HashSet<string>(StringComparer.Ordinal);

        foreach (var upperBar in upperBars.OrderBy(bar => bar.CenterX))
        {
            var lowerBar = lowerBars
                .Where(bar => !usedLower.Contains(bar.Stroke.ShapeId))
                .OrderBy(bar => Math.Abs(bar.CenterX - upperBar.CenterX))
                .FirstOrDefault();

            if (lowerBar is null
                || Math.Abs(lowerBar.CenterX - upperBar.CenterX) > xTolerance)
            {
                continue;
            }

            usedLower.Add(lowerBar.Stroke.ShapeId);

            boundaries.Add(new MeasureBoundary(
                (upperBar.CenterX + lowerBar.CenterX) / 2.0,
                upper.Bounds.MinY,
                lower.Bounds.MaxY,
                [upperBar.Stroke.ShapeId, lowerBar.Stroke.ShapeId],
                Math.Min(upperBar.Stroke.Width, lowerBar.Stroke.Width),
                Math.Max(upperBar.Stroke.Width, lowerBar.Stroke.Width)));
        }

        return ClusterMeasureBoundaries(
            boundaries,
            clusterTolerance,
            finalBarlineTolerance);
    }

    private static bool FitsUpperBoundarySegment(
        NormalizedStroke stroke,
        StaffLayout upper,
        StaffLayout lower,
        double tolerance)
    {
        return FitsStaffBoundarySegment(stroke, upper, tolerance)
            || (Math.Abs(stroke.YStart - upper.Bounds.MinY) <= tolerance
                && Math.Abs(stroke.YEnd - lower.Bounds.MinY) <= tolerance
                && IsWithinStaffWidth(stroke, upper, tolerance)
                && IsWithinStaffWidth(stroke, lower, tolerance));
    }

    private static bool FitsStaffBoundarySegment(
        NormalizedStroke stroke,
        StaffLayout staff,
        double tolerance)
    {
        return Math.Abs(stroke.YStart - staff.Bounds.MinY) <= tolerance
            && Math.Abs(stroke.YEnd - staff.Bounds.MaxY) <= tolerance
            && IsWithinStaffWidth(stroke, staff, tolerance);
    }

    private static bool FitsPairBoundary(
        NormalizedStroke stroke,
        StaffLayout upper,
        StaffLayout lower,
        double tolerance)
    {
        return Math.Abs(stroke.YStart - upper.Bounds.MinY) <= tolerance
            && Math.Abs(stroke.YEnd - lower.Bounds.MaxY) <= tolerance
            && IsWithinStaffWidth(stroke, upper, tolerance)
            && IsWithinStaffWidth(stroke, lower, tolerance);
    }

    private static bool IsWithinStaffWidth(
        NormalizedStroke stroke,
        StaffLayout staff,
        double tolerance)
    {
        return stroke.CenterX >= staff.Bounds.MinX - tolerance
            && stroke.CenterX <= staff.Bounds.MaxX + tolerance;
    }

    private static IReadOnlyList<MeasureBoundary> ClusterMeasureBoundaries(
        IReadOnlyList<MeasureBoundary> boundaries,
        double xTolerance,
        double finalBarlineTolerance)
    {
        if (boundaries.Count == 0)
        {
            return [];
        }

        var ordered = boundaries
            .OrderBy(boundary => boundary.X)
            .ToArray();
        var groups = new List<List<MeasureBoundary>>();
        var current = new List<MeasureBoundary> { ordered[0] };

        foreach (var boundary in ordered.Skip(1))
        {
            var gap = boundary.X - current[^1].X;
            var candidateGroup = current.Append(boundary).ToArray();
            var looksLikeFinalPair =
                gap <= finalBarlineTolerance
                && HasHeavyLightContrast(candidateGroup);

            if (gap <= xTolerance || looksLikeFinalPair)
            {
                current.Add(boundary);
                continue;
            }

            groups.Add(current);
            current = [boundary];
        }

        groups.Add(current);

        return groups
            .Select(group =>
            {
                var isFinal = HasHeavyLightContrast(group);
                var positiveWidths = group
                    .SelectMany(boundary => new[]
                    {
                        boundary.MinStrokeWidth,
                        boundary.MaxStrokeWidth
                    })
                    .Where(width => width > 0)
                    .ToArray();

                return new MeasureBoundary(
                    isFinal
                        ? group.Max(boundary => boundary.X)
                        : group.Average(boundary => boundary.X),
                    group.Min(boundary => boundary.UpperY),
                    group.Max(boundary => boundary.LowerY),
                    group.SelectMany(boundary => boundary.StrokeIds)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(id => id, StringComparer.Ordinal)
                        .ToArray(),
                    positiveWidths.Length == 0
                        ? 0
                        : positiveWidths.Min(),
                    positiveWidths.Length == 0
                        ? 0
                        : positiveWidths.Max(),
                    isFinal);
            })
            .ToArray();
    }

    private static bool HasHeavyLightContrast(
        IReadOnlyList<MeasureBoundary> group)
    {
        var widths = group
            .SelectMany(boundary => new[]
            {
                boundary.MinStrokeWidth,
                boundary.MaxStrokeWidth
            })
            .Where(width => width > 0)
            .ToArray();

        if (group.Count < 2 || widths.Length < 2)
        {
            return false;
        }

        var minimum = widths.Min();
        var maximum = widths.Max();

        return minimum > 0
            && maximum / minimum >= 2.0;
    }

    private static IReadOnlyList<MeasureLayout> BuildMeasures(
        string pairId,
        IReadOnlyList<MeasureBoundary> boundaries)
    {
        var measures = new List<MeasureLayout>();

        for (var index = 0; index + 1 < boundaries.Count; index++)
        {
            measures.Add(new MeasureLayout(
                $"{pairId}-measure-{index + 1}",
                boundaries[index].X,
                boundaries[index + 1].X,
                boundaries[index],
                boundaries[index + 1]));
        }

        return measures;
    }

    private static NormalizedStroke NormalizeStroke(Stroke stroke)
    {
        var start = stroke.Start;
        var end = stroke.End;

        if (start.X > end.X)
        {
            (start, end) = (end, start);
        }

        var dx = end.X - start.X;
        var dy = end.Y - start.Y;

        return new NormalizedStroke(
            stroke,
            start.X,
            end.X,
            Math.Min(start.Y, end.Y),
            Math.Max(start.Y, end.Y),
            (start.X + end.X) / 2.0,
            (start.Y + end.Y) / 2.0,
            Math.Sqrt(dx * dx + dy * dy),
            Math.Abs(dx),
            Math.Abs(dy));
    }

    private static bool IsHorizontal(NormalizedStroke stroke) =>
        stroke.DeltaX >= stroke.DeltaY * 8.0;

    private static bool IsVertical(NormalizedStroke stroke) =>
        stroke.DeltaY >= stroke.DeltaX * 8.0;

    private static double HorizontalOverlap(
        double firstStart,
        double firstEnd,
        double secondStart,
        double secondEnd) =>
        Math.Max(
            0,
            Math.Min(firstEnd, secondEnd) - Math.Max(firstStart, secondStart));

    private sealed record NormalizedStroke(
        Stroke Stroke,
        double XStart,
        double XEnd,
        double YStart,
        double YEnd,
        double CenterX,
        double CenterY,
        double Length,
        double DeltaX,
        double DeltaY);
}
