using SvgMusic.Scene;

namespace SvgMusic.Semantics;

public enum SemanticTextRole
{
    Title,
    Subtitle,
    Composer,
    Instruction,
    Tempo,
    MeasureNumber,
    Fingering,
    Unknown
}

public sealed record TextFact(
    string ObservationId,
    SemanticTextRole Role,
    string Text,
    BoundsD Bounds,
    double AverageGlyphHeight,
    double OcrConfidence,
    int? MeasureNumber,
    int? Staff,
    string? At,
    string? AnchorShapeId,
    string? Placement,
    double Confidence,
    string Reason,
    IReadOnlyList<string> SourceShapeIds)
    : SemanticFact(
        "TextPass",
        Reason,
        SourceShapeIds);

public sealed class TextPass : ISemanticPass
{
    private const double HeaderClearanceInSpacings = 5.0;
    private const double HeaderHeightGroupingTolerance = 0.08;
    private const double MeasureNumberMaxGapInSpacings = 3.0;
    private const double MeasureNumberMaxDxInSpacings = 2.0;
    private const double TempoMaxGapInSpacings = 5.0;
    private const double InstructionMaxGapInSpacings = 4.0;
    private const double FingeringMaxDxInSpacings = 1.25;
    private const double FingeringMaxDyInSpacings = 2.5;
    private const double AnyMusicOverlap = 0.0;

    private readonly TextRecognitionAnalysisResult _analysis;
    private readonly GeometricScene _geometry;
    private readonly ScoreLayout _layout;
    private readonly LogicalOwnershipScene _ownership;

    public TextPass(
        TextRecognitionAnalysisResult analysis,
        GeometricScene geometry,
        ScoreLayout layout,
        LogicalOwnershipScene ownership)
    {
        _analysis = analysis;
        _geometry = geometry;
        _layout = layout;
        _ownership = ownership;
    }

    public string Name => nameof(TextPass);

    public void Run(
        SemanticDocument document,
        SemanticFacts facts)
    {
        var shapesById = _geometry.Shapes
            .GroupBy(shape => shape.Id, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.Ordinal);
        var ownedShapeIds = _ownership.Assignments
            .Select(item => item.ShapeId)
            .ToHashSet(StringComparer.Ordinal);
        var musicShapeFamilies = facts.Items
            .SelectMany(item => item.SourceShapeIds)
            .Select(BaseShapeFamily)
            .ToHashSet(StringComparer.Ordinal);
        var spacing = Math.Max(
            0.001,
            GlyphRasterizer.ResolveSourceInterline(_layout));

        var candidates = _analysis.Recognized
            .Where(item => item.Recognition is not null)
            .Select(item => new Candidate(
                item,
                item.Recognition!,
                AverageGlyphHeight(item.SourceShapeIds, shapesById)))
            .OrderBy(item => item.Observation.Bounds.MinY)
            .ThenBy(item => item.Observation.Bounds.MinX)
            .ToArray();

        var headerCandidates = candidates
            .Where(candidate => IsHeaderCandidate(
                candidate,
                ownedShapeIds,
                spacing))
            .ToArray();
        var headerRoles = AssignHeaderRoles(headerCandidates);

        var emitted = new HashSet<string>(StringComparer.Ordinal);

        foreach (var candidate in headerCandidates)
        {
            var role = headerRoles[candidate.Observation.Id];
            AddFact(
                facts,
                candidate,
                role,
                null,
                null,
                null,
                null,
                null,
                candidate.Recognition.Confidence,
                $"unowned header text above first system; avgGlyphHeight={candidate.AverageGlyphHeight:F2}");
            emitted.Add(candidate.Observation.Id);
        }

        var noteheads = facts.OfType<NoteheadFact>().ToArray();
        var stems = facts.OfType<StemAttachmentFact>().ToArray();
        var onsets = facts.OfType<OnsetFact>().ToArray();

        foreach (var candidate in candidates.Where(item =>
                     !emitted.Contains(item.Observation.Id)))
        {
            var text = candidate.Recognition.Text.Trim();

            if (TryResolveFingering(
                    candidate,
                    text,
                    noteheads,
                    stems,
                    spacing,
                    out var fingeringAnchor))
            {
                AddFact(
                    facts,
                    candidate,
                    SemanticTextRole.Fingering,
                    fingeringAnchor.MeasureNumber,
                    fingeringAnchor.Staff,
                    null,
                    fingeringAnchor.ShapeId,
                    "above",
                    candidate.Recognition.Confidence,
                    $"single digit 1..5 above note/stem {fingeringAnchor.ShapeId}");
                continue;
            }

            var overlap = MusicOverlap(candidate, musicShapeFamilies);
            if (overlap > AnyMusicOverlap
                || IntersectsStaffCore(candidate.Observation.Bounds, spacing))
            {
                AddFact(
                    facts,
                    candidate,
                    SemanticTextRole.Unknown,
                    null,
                    null,
                    null,
                    null,
                    null,
                    candidate.Recognition.Confidence * (1.0 - overlap),
                    $"rejected as text: source glyph already belongs to accepted music (overlap={overlap:P0}) or candidate lies in staff core");
                continue;
            }

            if (TryResolveMeasureNumber(
                    candidate,
                    document,
                    spacing,
                    out var measureContext))
            {
                AddFact(
                    facts,
                    candidate,
                    SemanticTextRole.MeasureNumber,
                    measureContext.MeasureNumber,
                    1,
                    "0",
                    null,
                    "above",
                    candidate.Recognition.Confidence,
                    $"positioned above measure start m{measureContext.MeasureNumber}");
                continue;
            }

            if (TryResolveTempo(
                    candidate,
                    document,
                    spacing,
                    onsets,
                    out var tempoContext))
            {
                AddFact(
                    facts,
                    candidate,
                    SemanticTextRole.Tempo,
                    tempoContext.MeasureNumber,
                    tempoContext.Staff,
                    tempoContext.At,
                    null,
                    "above",
                    candidate.Recognition.Confidence,
                    $"multi-glyph text above first measure of system; m{tempoContext.MeasureNumber}@{tempoContext.At}");
                continue;
            }

            if (TryResolveInstruction(
                    candidate,
                    document,
                    spacing,
                    onsets,
                    out var instructionContext))
            {
                AddFact(
                    facts,
                    candidate,
                    SemanticTextRole.Instruction,
                    instructionContext.MeasureNumber,
                    instructionContext.Staff,
                    instructionContext.At,
                    null,
                    instructionContext.Placement,
                    candidate.Recognition.Confidence,
                    $"text outside staff core near m{instructionContext.MeasureNumber}/s{instructionContext.Staff}@{instructionContext.At}");
                continue;
            }

            AddFact(
                facts,
                candidate,
                SemanticTextRole.Unknown,
                null,
                null,
                null,
                null,
                null,
                candidate.Recognition.Confidence,
                "recognized by OCR but no semantic text role matched");
        }
    }

    private static void AddFact(
        SemanticFacts facts,
        Candidate candidate,
        SemanticTextRole role,
        int? measureNumber,
        int? staff,
        string? at,
        string? anchorShapeId,
        string? placement,
        double confidence,
        string reason)
    {
        facts.Add(new TextFact(
            candidate.Observation.Id,
            role,
            candidate.Recognition.Text.Trim(),
            candidate.Observation.Bounds,
            candidate.AverageGlyphHeight,
            candidate.Recognition.Confidence,
            measureNumber,
            staff,
            at,
            anchorShapeId,
            placement,
            Math.Clamp(confidence, 0, 1),
            reason,
            candidate.Observation.SourceShapeIds));
    }

    private bool IsHeaderCandidate(
        Candidate candidate,
        IReadOnlySet<string> ownedShapeIds,
        double spacing)
    {
        if (_layout.Systems.Count == 0)
        {
            return false;
        }

        if (candidate.Observation.SourceShapeIds.Any(ownedShapeIds.Contains))
        {
            return false;
        }

        var firstSystemTop = _layout.Systems.Min(system => system.Bounds.MinY);
        return candidate.Observation.Bounds.MaxY
            < firstSystemTop - spacing * HeaderClearanceInSpacings;
    }

    private static IReadOnlyDictionary<string, SemanticTextRole> AssignHeaderRoles(
        IReadOnlyList<Candidate> candidates)
    {
        var result = new Dictionary<string, SemanticTextRole>(
            StringComparer.Ordinal);

        if (candidates.Count == 0)
        {
            return result;
        }

        var groups = new List<List<Candidate>>();

        foreach (var candidate in candidates
                     .OrderBy(item => item.AverageGlyphHeight)
                     .ThenBy(item => item.Observation.Bounds.MinY)
                     .ThenBy(item => item.Observation.Bounds.MinX))
        {
            if (groups.Count == 0)
            {
                groups.Add([candidate]);
                continue;
            }

            var current = groups[^1];
            var average = Math.Max(
                0.001,
                current.Average(item => item.AverageGlyphHeight));
            var relativeDelta = Math.Abs(candidate.AverageGlyphHeight - average)
                / average;

            if (relativeDelta <= HeaderHeightGroupingTolerance)
            {
                current.Add(candidate);
            }
            else
            {
                groups.Add([candidate]);
            }
        }

        if (groups.Count == 1)
        {
            foreach (var candidate in groups[0])
            {
                result[candidate.Observation.Id] = SemanticTextRole.Title;
            }

            return result;
        }

        foreach (var candidate in groups[0])
        {
            result[candidate.Observation.Id] = SemanticTextRole.Composer;
        }

        foreach (var candidate in groups[^1])
        {
            result[candidate.Observation.Id] = SemanticTextRole.Title;
        }

        foreach (var group in groups.Skip(1).SkipLast(1))
        {
            foreach (var candidate in group)
            {
                result[candidate.Observation.Id] = SemanticTextRole.Subtitle;
            }
        }

        return result;
    }

    private static double AverageGlyphHeight(
        IReadOnlyList<string> shapeIds,
        IReadOnlyDictionary<string, GeometricShape> shapesById)
    {
        var heights = shapeIds
            .Where(shapesById.ContainsKey)
            .Select(shapeId => shapesById[shapeId].Bounds.Height)
            .Where(height => height > 0)
            .ToArray();

        return heights.Length == 0
            ? 0
            : heights.Average();
    }

    private static double MusicOverlap(
        Candidate candidate,
        IReadOnlySet<string> musicShapeFamilies)
    {
        var families = candidate.Observation.SourceShapeIds
            .Select(BaseShapeFamily)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (families.Length == 0)
        {
            return 0;
        }

        return families.Count(musicShapeFamilies.Contains)
            / (double)families.Length;
    }

    private static string BaseShapeFamily(string shapeId)
    {
        const string wholeSuffix = ".whole";
        if (shapeId.EndsWith(wholeSuffix, StringComparison.Ordinal))
        {
            return shapeId[..^wholeSuffix.Length];
        }

        var separator = shapeId.LastIndexOf('.');
        if (separator > 0
            && separator < shapeId.Length - 1
            && int.TryParse(shapeId[(separator + 1)..], out _))
        {
            return shapeId[..separator];
        }

        return shapeId;
    }

    private bool IntersectsStaffCore(
        BoundsD bounds,
        double spacing)
    {
        return _layout.Staffs.Any(staff =>
        {
            var expanded = new BoundsD(
                staff.Bounds.MinX,
                staff.Bounds.MinY - spacing * 0.25,
                staff.Bounds.MaxX,
                staff.Bounds.MaxY + spacing * 0.25);

            return BoundsIntersect(bounds, expanded);
        });
    }

    private static bool TryResolveFingering(
        Candidate candidate,
        string text,
        IReadOnlyList<NoteheadFact> noteheads,
        IReadOnlyList<StemAttachmentFact> stems,
        double spacing,
        out NoteheadFact anchor)
    {
        anchor = null!;

        if (text.Length != 1
            || text[0] < '1'
            || text[0] > '5')
        {
            return false;
        }

        var options = new List<(NoteheadFact Note, double Score)>();

        foreach (var note in noteheads)
        {
            var dx = Math.Abs(candidate.Observation.Bounds.CenterX - note.CenterX);
            var verticalGap = note.CenterY - candidate.Observation.Bounds.MaxY;

            if (dx <= spacing * FingeringMaxDxInSpacings
                && verticalGap >= -spacing * 0.25
                && verticalGap <= spacing * FingeringMaxDyInSpacings)
            {
                options.Add((
                    note,
                    dx / spacing + Math.Max(0, verticalGap) / spacing));
            }
        }

        var noteheadsById = noteheads.ToDictionary(
            item => item.ShapeId,
            StringComparer.Ordinal);

        foreach (var stem in stems)
        {
            var topY = Math.Min(stem.StartY, stem.EndY);
            var topX = stem.StartY <= stem.EndY
                ? stem.StartX
                : stem.EndX;
            var dx = Math.Abs(candidate.Observation.Bounds.CenterX - topX);
            var dy = Math.Abs(candidate.Observation.Bounds.MaxY - topY);

            if (dx > spacing * FingeringMaxDxInSpacings
                || dy > spacing * FingeringMaxDyInSpacings)
            {
                continue;
            }

            foreach (var noteheadId in stem.AttachedNoteheadIds)
            {
                if (noteheadsById.TryGetValue(noteheadId, out var note))
                {
                    options.Add((
                        note,
                        dx / spacing + dy / spacing));
                }
            }
        }

        var selected = options
            .OrderBy(item => item.Score)
            .ThenBy(item => item.Note.ShapeId, StringComparer.Ordinal)
            .FirstOrDefault();

        if (selected.Note is null)
        {
            return false;
        }

        anchor = selected.Note;
        return true;
    }

    private bool TryResolveMeasureNumber(
        Candidate candidate,
        SemanticDocument document,
        double spacing,
        out TextContext context)
    {
        context = default;

        foreach (var system in _layout.Systems)
        {
            foreach (var pair in system.StaffPairs)
            {
                var upper = _layout.Staffs.First(staff =>
                    staff.Id == pair.UpperStaffId);
                var verticalGap = upper.Bounds.MinY
                    - candidate.Observation.Bounds.MaxY;

                if (verticalGap < 0
                    || verticalGap > spacing * MeasureNumberMaxGapInSpacings)
                {
                    continue;
                }

                foreach (var measure in pair.Measures)
                {
                    var dx = Math.Abs(
                        candidate.Observation.Bounds.CenterX - measure.XStart);

                    if (dx > spacing * MeasureNumberMaxDxInSpacings)
                    {
                        continue;
                    }

                    var semantic = document.Measures.FirstOrDefault(item =>
                        item.PairId == pair.Id
                        && item.LayoutMeasureId == measure.Id);

                    if (semantic is null)
                    {
                        continue;
                    }

                    context = new TextContext(
                        semantic.Number,
                        1,
                        "0",
                        "above");
                    return true;
                }
            }
        }

        return false;
    }

    private bool TryResolveTempo(
        Candidate candidate,
        SemanticDocument document,
        double spacing,
        IReadOnlyList<OnsetFact> onsets,
        out TextContext context)
    {
        context = default;

        if (candidate.Observation.SourceShapeIds.Count < 2)
        {
            return false;
        }

        foreach (var system in _layout.Systems)
        {
            foreach (var pair in system.StaffPairs)
            {
                if (pair.Measures.Count == 0)
                {
                    continue;
                }

                var upper = _layout.Staffs.First(staff =>
                    staff.Id == pair.UpperStaffId);
                var verticalGap = upper.Bounds.MinY
                    - candidate.Observation.Bounds.MaxY;

                if (verticalGap < 0
                    || verticalGap > spacing * TempoMaxGapInSpacings)
                {
                    continue;
                }

                var measure = pair.Measures[0];

                if (candidate.Observation.Bounds.CenterX < measure.XStart
                    || candidate.Observation.Bounds.CenterX > measure.XEnd)
                {
                    continue;
                }

                var semantic = document.Measures.FirstOrDefault(item =>
                    item.PairId == pair.Id
                    && item.LayoutMeasureId == measure.Id);

                if (semantic is null)
                {
                    continue;
                }

                context = new TextContext(
                    semantic.Number,
                    1,
                    "0",
                    "above");
                return true;
            }
        }

        return false;
    }

    private bool TryResolveInstruction(
        Candidate candidate,
        SemanticDocument document,
        double spacing,
        IReadOnlyList<OnsetFact> onsets,
        out TextContext context)
    {
        context = default;
        TextContext? best = null;
        var bestDistance = double.PositiveInfinity;

        foreach (var semantic in document.Measures)
        {
            foreach (var staff in new[] { semantic.Upper, semantic.Lower })
            {
                if (candidate.Observation.Bounds.CenterX < semantic.XStart
                    || candidate.Observation.Bounds.CenterX > semantic.XEnd)
                {
                    continue;
                }

                double distance;
                string placement;

                if (candidate.Observation.Bounds.MaxY <= staff.StaffBounds.MinY)
                {
                    distance = staff.StaffBounds.MinY
                        - candidate.Observation.Bounds.MaxY;
                    placement = "above";
                }
                else if (candidate.Observation.Bounds.MinY >= staff.StaffBounds.MaxY)
                {
                    distance = candidate.Observation.Bounds.MinY
                        - staff.StaffBounds.MaxY;
                    placement = "below";
                }
                else
                {
                    continue;
                }

                if (distance > spacing * InstructionMaxGapInSpacings
                    || distance >= bestDistance)
                {
                    continue;
                }

                bestDistance = distance;
                best = new TextContext(
                    semantic.Number,
                    staff.StaffNumber,
                    ResolveAt(
                        semantic.Number,
                        staff.StaffNumber,
                        candidate.Observation.Bounds.CenterX,
                        onsets),
                    placement);
            }
        }

        if (best is null)
        {
            return false;
        }

        context = best.Value;
        return true;
    }

    private static string ResolveAt(
        int measureNumber,
        int staff,
        double x,
        IReadOnlyList<OnsetFact> onsets)
    {
        return onsets
            .Where(item =>
                item.MeasureNumber == measureNumber
                && item.Staff == staff)
            .OrderBy(item => Math.Abs(item.AnchorX - x))
            .ThenByDescending(item => item.Confidence)
            .Select(item => item.At)
            .FirstOrDefault()
            ?? "0";
    }

    private static bool BoundsIntersect(
        BoundsD first,
        BoundsD second) =>
        first.MaxX >= second.MinX
        && second.MaxX >= first.MinX
        && first.MaxY >= second.MinY
        && second.MaxY >= first.MinY;

    private sealed record Candidate(
        TextRecognitionObservation Observation,
        TextRecognition Recognition,
        double AverageGlyphHeight);

    private readonly record struct TextContext(
        int MeasureNumber,
        int Staff,
        string At,
        string Placement);
}
