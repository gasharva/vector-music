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

public sealed record MetronomeMarkFact(
    string ObservationId,
    int MeasureNumber,
    int Staff,
    string At,
    string BeatUnit,
    decimal Bpm,
    string? InstructionText,
    string BeatGlyphShapeId,
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
    private readonly NotationScene? _notation;

    public TextPass(
        TextRecognitionAnalysisResult analysis,
        GeometricScene geometry,
        ScoreLayout layout,
        LogicalOwnershipScene ownership,
        NotationScene? notation = null)
    {
        _analysis = analysis;
        _geometry = geometry;
        _layout = layout;
        _ownership = ownership;
        _notation = notation;
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

                foreach (var diagnostic in DescribeClassifiedBeatCandidates(
                             candidate.Observation.Bounds,
                             spacing))
                {
                    facts.AddTrace(diagnostic);
                }

                if (TryBuildMetronomeMark(
                        candidate,
                        tempoContext,
                        spacing,
                        out var metronomeMark))
                {
                    facts.Add(metronomeMark);
                    facts.AddTrace(
                        $"MetronomeMark: m{metronomeMark.MeasureNumber}@{metronomeMark.At}; "
                        + $"beat={metronomeMark.BeatUnit}; bpm={metronomeMark.Bpm}; "
                        + $"glyph={metronomeMark.BeatGlyphShapeId}; "
                        + $"text={metronomeMark.InstructionText ?? "-"}");
                }
                else
                {
                    facts.AddTrace(
                        $"MetronomeMark: no beat glyph resolved for OCR '{candidate.Recognition.Text.Trim()}' "
                        + $"at x={candidate.Observation.Bounds.MinX:F2}..{candidate.Observation.Bounds.MaxX:F2}");
                }

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

        // Header semantics are positional, not typographic. Font/glyph height is
        // strongly affected by lowercase/uppercase content and style, while the
        // conventional score header order is stable: title, optional subtitle,
        // composer. Keep OCR content out of this decision.
        var ordered = candidates
            .OrderBy(candidate => candidate.Observation.Bounds.CenterY)
            .ThenBy(candidate => candidate.Observation.Bounds.MinX)
            .ToArray();

        if (ordered.Length == 1)
        {
            result[ordered[0].Observation.Id] = SemanticTextRole.Title;
            return result;
        }

        result[ordered[0].Observation.Id] = SemanticTextRole.Title;
        result[ordered[^1].Observation.Id] = SemanticTextRole.Composer;

        foreach (var candidate in ordered.Skip(1).SkipLast(1))
        {
            result[candidate.Observation.Id] = SemanticTextRole.Subtitle;
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

    private bool TryBuildMetronomeMark(
        Candidate candidate,
        TextContext context,
        double spacing,
        out MetronomeMarkFact fact)
    {
        fact = null!;

        var match = System.Text.RegularExpressions.Regex.Match(
            candidate.Recognition.Text.Trim(),
            @"^(?<prefix>.*?)(?:\(\s*)?=\s*(?<bpm>\d+(?:[\.,]\d+)?)\s*\)?\s*$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        if (!match.Success)
        {
            return false;
        }

        var bpmText = match.Groups["bpm"].Value.Replace(',', '.');
        if (!decimal.TryParse(
                bpmText,
                System.Globalization.NumberStyles.AllowDecimalPoint,
                System.Globalization.CultureInfo.InvariantCulture,
                out var bpm)
            || bpm <= 0)
        {
            return false;
        }

        var sourceFamilies = candidate.Observation.SourceShapeIds
            .Select(BaseShapeFamily)
            .ToHashSet(StringComparer.Ordinal);
        var observation = candidate.Observation.Bounds;

        if (TryResolveClassifiedBeatGlyph(
                observation,
                spacing,
                out var classifiedBeat))
        {
            var _instruction = match.Groups["prefix"].Value
                .Trim()
                .TrimEnd('(')
                .Trim();
            if (_instruction.Length == 0)
            {
                _instruction = null;
            }

            var _sourceIds = candidate.Observation.SourceShapeIds
                .Append(classifiedBeat.ShapeId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var _confidence = Math.Clamp(
                Math.Min(
                    candidate.Recognition.Confidence,
                    classifiedBeat.Confidence),
                0,
                1);
            var _reason =
                $"metronome mark parsed from OCR BPM and geometry classifier; "
                + $"glyph={classifiedBeat.ShapeId}; label={classifiedBeat.Label}; "
                + $"beat={classifiedBeat.BeatUnit}; bpm={bpm}";

            fact = new MetronomeMarkFact(
                candidate.Observation.Id,
                context.MeasureNumber,
                context.Staff,
                context.At,
                classifiedBeat.BeatUnit,
                bpm,
                _instruction,
                classifiedBeat.ShapeId,
                _confidence,
                _reason,
                _sourceIds);
            return true;
        }

        // OCR deliberately ignores the musical beat symbol. Find the missing
        // geometry inside the recognized horizontal run without consulting SVG ids,
        // CSS classes or producer metadata. Group split contours back by their
        // geometric source family because a compound note glyph may have been split
        // into independent primitives earlier in the scene pipeline.
        var beatGlyph = _geometry.Shapes
            .GroupBy(
                shape => BaseShapeFamily(shape.Id),
                StringComparer.Ordinal)
            .Where(group => !sourceFamilies.Contains(group.Key))
            .Select(group =>
            {
                var shapes = group.ToArray();
                var bounds = new BoundsD(
                    shapes.Min(shape => shape.Bounds.MinX),
                    shapes.Min(shape => shape.Bounds.MinY),
                    shapes.Max(shape => shape.Bounds.MaxX),
                    shapes.Max(shape => shape.Bounds.MaxY));
                var contourCount = shapes.Sum(shape =>
                    shape.EffectiveContours.Count);
                var hasNestedContours = shapes.Any(shape =>
                    shape.EffectiveContours.Count > 1);
                var hasFill = shapes.Any(shape => shape.HasFill);

                return new
                {
                    Family = group.Key,
                    Shapes = shapes,
                    Bounds = bounds,
                    ContourCount = contourCount,
                    HasNestedContours = hasNestedContours,
                    HasFill = hasFill
                };
            })
            .Where(group =>
                group.Bounds.CenterX >= observation.MinX
                && group.Bounds.CenterX <= observation.MaxX
                && group.Bounds.MaxY >= observation.MinY - spacing * 1.75
                && group.Bounds.MinY <= observation.MaxY + spacing * 1.75)
            .Where(group =>
                group.Bounds.Height >= spacing * 1.20
                && group.Bounds.Height <= spacing * 4.20
                && group.Bounds.Width >= spacing * 0.20
                && group.Bounds.Width <= spacing * 3.20)
            .Where(group =>
                group.Bounds.Width / Math.Max(group.Bounds.Height, 0.001)
                    is >= 0.12 and <= 1.15)
            .OrderByDescending(group => group.HasFill)
            .ThenBy(group => Math.Abs(
                group.Bounds.CenterY - observation.CenterY) / spacing)
            .ThenBy(group => Math.Abs(
                group.Bounds.Height / spacing - 2.6))
            .ThenBy(group => group.Bounds.Width)
            .FirstOrDefault();

        if (beatGlyph is null)
        {
            return false;
        }

        var heightInSpacings = beatGlyph.Bounds.Height / spacing;
        var aspect = beatGlyph.Bounds.Width
            / Math.Max(beatGlyph.Bounds.Height, 0.001);

        // The adapter extracts only visual evidence from the compound glyph.
        // Musical duration semantics themselves are shared with ordinary notes.
        var hasStem = heightInSpacings >= 1.35;
        var isHollow = beatGlyph.HasNestedContours;

        var subdivisionLevel = 0;
        if (!isHollow && hasStem)
        {
            subdivisionLevel = aspect switch
            {
                <= 0.52 => 0,
                <= 0.82 => 1,
                _ => 2
            };
        }

        var written = WrittenDurationClassifier.Classify(
            isHollow,
            hasStem,
            subdivisionLevel);

        var instruction = match.Groups["prefix"].Value
            .Trim()
            .TrimEnd('(')
            .Trim();
        if (instruction.Length == 0)
        {
            instruction = null;
        }

        var sourceIds = candidate.Observation.SourceShapeIds
            .Concat(beatGlyph.Shapes.Select(shape => shape.Id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var confidence = Math.Clamp(
            candidate.Recognition.Confidence * 0.95,
            0,
            1);
        var reason =
            $"metronome mark parsed from OCR BPM and nearby note-like geometry; "
            + $"glyph={beatGlyph.Family}; contours={beatGlyph.ContourCount}; "
            + $"aspect={aspect:F2}; height={heightInSpacings:F2}sp; "
            + $"beat={written.NoteType}; bpm={bpm}";

        fact = new MetronomeMarkFact(
            candidate.Observation.Id,
            context.MeasureNumber,
            context.Staff,
            context.At,
            written.NoteType,
            bpm,
            instruction,
            beatGlyph.Family,
            confidence,
            reason,
            sourceIds);
        return true;
    }

    private IEnumerable<string> DescribeClassifiedBeatCandidates(
        BoundsD observation,
        double spacing)
    {
        if (_notation is null)
        {
            yield return "Metronome classifier candidates: notation scene unavailable";
            yield break;
        }

        var candidates = _notation.Instances
            .Where(instance => instance.Classification is not null)
            .Select(instance =>
            {
                var classification = instance.Classification!;
                var label = NormalizeClassifierLabel(classification.Label);
                var bounds = new BoundsD(
                    instance.X,
                    instance.Y,
                    instance.X + instance.Width,
                    instance.Y + instance.Height);

                return new
                {
                    instance.ShapeId,
                    Label = classification.Label,
                    NormalizedLabel = label,
                    classification.Confidence,
                    Bounds = bounds,
                    BeatUnit = BeatUnitFromClassifierLabel(label)
                };
            })
            .Where(item =>
                item.Bounds.CenterX >= observation.MinX - spacing * 0.5
                && item.Bounds.CenterX <= observation.MaxX + spacing * 0.5
                && item.Bounds.MaxY >= observation.MinY - spacing * 2.0
                && item.Bounds.MinY <= observation.MaxY + spacing * 2.0)
            .Where(item => item.BeatUnit is not null)
            .OrderByDescending(item => item.Confidence)
            .ThenBy(item => item.Bounds.CenterX)
            .ToArray();

        if (candidates.Length == 0)
        {
            yield return
                $"Metronome classifier candidates: none near OCR run "
                + $"x={observation.MinX:F2}..{observation.MaxX:F2}";
            yield break;
        }

        foreach (var item in candidates)
        {
            yield return
                $"Metronome classifier candidate: shape={item.ShapeId}; "
                + $"label={item.Label}; beat={item.BeatUnit}; "
                + $"confidence={item.Confidence:P1}; "
                + $"bounds={item.Bounds.MinX:F2},{item.Bounds.MinY:F2}.."
                + $"{item.Bounds.MaxX:F2},{item.Bounds.MaxY:F2}";
        }
    }

    private bool TryResolveClassifiedBeatGlyph(
        BoundsD observation,
        double spacing,
        out ClassifiedBeat beat)
    {
        beat = default;

        if (_notation is null)
        {
            return false;
        }

        var beatCandidates = _notation.Instances
            .Where(instance => instance.Classification is not null)
            .Select(instance =>
            {
                var classification = instance.Classification!;
                var normalized = NormalizeClassifierLabel(classification.Label);
                var beatUnit = BeatUnitFromClassifierLabel(normalized);
                var bounds = new BoundsD(
                    instance.X,
                    instance.Y,
                    instance.X + instance.Width,
                    instance.Y + instance.Height);

                return new
                {
                    Instance = instance,
                    Classification = classification,
                    BeatUnit = beatUnit,
                    Bounds = bounds
                };
            })
            .Where(item => item.BeatUnit is not null)
            .Where(item =>
                item.Bounds.CenterX >= observation.MinX - spacing * 0.5
                && item.Bounds.CenterX <= observation.MaxX + spacing * 0.5
                && item.Bounds.MaxY >= observation.MinY - spacing * 2.0
                && item.Bounds.MinY <= observation.MaxY + spacing * 2.0)
            .ToArray();

        var candidate = beatCandidates
            .Where(item => item.Classification.Confidence >= 0.80)
            .OrderBy(item => Math.Abs(
                item.Bounds.CenterY - observation.CenterY))
            .ThenBy(item => Math.Abs(
                item.Bounds.CenterX - observation.CenterX))
            .ThenByDescending(item => item.Classification.Confidence)
            .FirstOrDefault();

        if (candidate is null)
        {
            return false;
        }

        beat = new ClassifiedBeat(
            candidate.Instance.ShapeId,
            candidate.Classification.Label,
            candidate.BeatUnit!,
            candidate.Classification.Confidence);
        return true;
    }

    private static string? BeatUnitFromClassifierLabel(string label)
    {
        if (label.Contains("EIGHTH", StringComparison.Ordinal))
        {
            return "eighth";
        }

        if (label.Contains("SIXTEENTH", StringComparison.Ordinal)
            || label.Contains("16TH", StringComparison.Ordinal))
        {
            return "16th";
        }

        if (label.Contains("QUARTER", StringComparison.Ordinal))
        {
            return "quarter";
        }

        if (label.Contains("HALF", StringComparison.Ordinal))
        {
            return "half";
        }

        if (label.Contains("WHOLE", StringComparison.Ordinal))
        {
            return "whole";
        }

        return null;
    }

    private static string NormalizeClassifierLabel(string label) =>
        new(
            label
                .Trim()
                .ToUpperInvariant()
                .Select(character =>
                    char.IsLetterOrDigit(character)
                        ? character
                        : '_')
                .ToArray());

    private readonly record struct ClassifiedBeat(
        string ShapeId,
        string Label,
        string BeatUnit,
        double Confidence);

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
