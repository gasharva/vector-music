namespace SvgMusic.Scene;

public sealed record CompositeGeometryCandidates(
    IReadOnlyList<HairpinPrimitive> Hairpins,
    IReadOnlyList<BracketSpannerPrimitive> Brackets,
    IReadOnlyList<VerticalZigZagPrimitive> VerticalZigZags);

/// <summary>
/// Captures composite-geometry hypotheses from the original split geometry
/// before generic primitive extraction can simplify their structure.
///
/// Detection is deliberately non-destructive: candidates do not consume source
/// shapes. Layout gets the same geometry and resolves structural claims first.
/// </summary>
public sealed class CompositeCandidateDetector
{
    private readonly IHairpinExtractor _hairpinExtractor;
    private readonly BracketSpannerExtractor _bracketExtractor;
    private readonly VerticalZigZagRunExtractor _zigZagRunExtractor;
    private readonly VerticalZigZagExtractor _zigZagExtractor;

    public CompositeCandidateDetector(
        IHairpinExtractor? hairpinExtractor = null,
        BracketSpannerExtractor? bracketExtractor = null,
        VerticalZigZagRunExtractor? zigZagRunExtractor = null,
        VerticalZigZagExtractor? zigZagExtractor = null)
    {
        _hairpinExtractor = hairpinExtractor ?? new HairpinExtractor();
        _bracketExtractor = bracketExtractor ?? new BracketSpannerExtractor();
        _zigZagRunExtractor = zigZagRunExtractor ?? new VerticalZigZagRunExtractor();
        _zigZagExtractor = zigZagExtractor ?? new VerticalZigZagExtractor();
    }

    public CompositeGeometryCandidates Detect(GeometricScene scene)
    {
        var hairpins = new List<HairpinPrimitive>();
        var zigZags = new List<VerticalZigZagPrimitive>();

        var runExtraction = _zigZagRunExtractor.Extract(scene);
        zigZags.AddRange(runExtraction.ZigZags);

        var runShapeIds = runExtraction.ZigZags
            .SelectMany(item => item.SourceShapeIds)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var shape in scene.Shapes)
        {
            if (_hairpinExtractor.TryCreateHairpin(shape, out var hairpin))
            {
                hairpins.Add(hairpin);
            }

            // A repeated-tile run already provides the stronger hypothesis for
            // its source shapes. Do not emit duplicate single-shape zigzags.
            if (!runShapeIds.Contains(shape.Id)
                && _zigZagExtractor.TryCreateVerticalZigZag(
                    shape,
                    out var zigZag))
            {
                zigZags.Add(zigZag);
            }
        }

        return new CompositeGeometryCandidates(
            hairpins,
            _bracketExtractor.Extract(scene).Brackets,
            zigZags);
    }
}

public sealed record CompositeCandidateDecision(
    string Kind,
    string Id,
    bool Accepted,
    string Reason,
    IReadOnlyList<string> SourceShapeIds);

/// <summary>
/// Resolves composite hypotheses only after score layout has claimed structural
/// geometry. Staff lines, ledgers and measure boundaries always win over a
/// competing composite interpretation.
/// </summary>
public sealed class CompositeCandidateResolver
{
    public IReadOnlyList<CompositeCandidateDecision> LastDecisions { get; private set; }
        = Array.Empty<CompositeCandidateDecision>();

    public IReadOnlySet<string> LastAcceptedSourceShapeIds { get; private set; }
        = new HashSet<string>(StringComparer.Ordinal);

    public NotationScene Resolve(
        NotationScene notation,
        CompositeGeometryCandidates candidates,
        ScoreLayout layout)
    {
        var structuralIds = StructuralShapeIds(layout);
        var acceptedIds = new HashSet<string>(StringComparer.Ordinal);
        var decisions = new List<CompositeCandidateDecision>();

        var choices = new List<CandidateChoice>();

        choices.AddRange(candidates.Hairpins.Select(item =>
            new CandidateChoice(
                "hairpin",
                item.ShapeId,
                item.Confidence,
                [item.ShapeId],
                item,
                null,
                null)));

        choices.AddRange(candidates.Brackets.Select(item =>
            new CandidateChoice(
                "bracket",
                item.Id,
                item.Confidence,
                item.SourceShapeIds,
                null,
                item,
                null)));

        choices.AddRange(candidates.VerticalZigZags.Select(item =>
            new CandidateChoice(
                "zigzag",
                item.ShapeId,
                item.Confidence,
                item.SourceShapeIds.Count > 0
                    ? item.SourceShapeIds
                    : [item.ShapeId],
                null,
                null,
                item)));

        var acceptedHairpins = new List<HairpinPrimitive>();
        var acceptedBrackets = new List<BracketSpannerPrimitive>();
        var acceptedZigZags = new List<VerticalZigZagPrimitive>();

        foreach (var choice in choices
                     .OrderByDescending(item => item.Confidence)
                     .ThenBy(item => item.Kind, StringComparer.Ordinal)
                     .ThenBy(item => item.Id, StringComparer.Ordinal))
        {
            var structuralConflict = choice.SourceShapeIds
                .Where(structuralIds.Contains)
                .ToArray();

            if (structuralConflict.Length > 0)
            {
                decisions.Add(new CompositeCandidateDecision(
                    choice.Kind,
                    choice.Id,
                    false,
                    "source geometry claimed by score layout: "
                    + string.Join(",", structuralConflict),
                    choice.SourceShapeIds));
                continue;
            }

            var compositeConflict = choice.SourceShapeIds
                .Where(acceptedIds.Contains)
                .ToArray();

            if (compositeConflict.Length > 0)
            {
                decisions.Add(new CompositeCandidateDecision(
                    choice.Kind,
                    choice.Id,
                    false,
                    "source geometry already claimed by a stronger composite: "
                    + string.Join(",", compositeConflict),
                    choice.SourceShapeIds));
                continue;
            }

            foreach (var id in choice.SourceShapeIds)
            {
                acceptedIds.Add(id);
            }

            if (choice.Hairpin is not null)
                acceptedHairpins.Add(choice.Hairpin);
            if (choice.Bracket is not null)
                acceptedBrackets.Add(choice.Bracket);
            if (choice.ZigZag is not null)
                acceptedZigZags.Add(choice.ZigZag);

            decisions.Add(new CompositeCandidateDecision(
                choice.Kind,
                choice.Id,
                true,
                "accepted after structural layout claims",
                choice.SourceShapeIds));
        }

        var survivingInstances = notation.Instances
            .Where(instance => !acceptedIds.Contains(instance.ShapeId))
            .ToArray();
        var survivingPrototypeIds = survivingInstances
            .Select(instance => instance.PrototypeId)
            .ToHashSet(StringComparer.Ordinal);

        LastDecisions = decisions;
        LastAcceptedSourceShapeIds = new HashSet<string>(
            acceptedIds,
            StringComparer.Ordinal);

        return notation with
        {
            Strokes = notation.Strokes
                .Where(stroke => !acceptedIds.Contains(stroke.ShapeId))
                .ToArray(),
            CurvedStrokes = notation.CurvedStrokes
                .Where(curve => !acceptedIds.Contains(curve.ShapeId))
                .ToArray(),
            Ellipses = notation.Ellipses
                .Where(ellipse => !acceptedIds.Contains(ellipse.ShapeId))
                .ToArray(),
            Instances = survivingInstances,
            Prototypes = notation.Prototypes
                .Where(prototype => survivingPrototypeIds.Contains(prototype.Id))
                .ToArray(),
            Hairpins = acceptedHairpins,
            BracketSpanners = acceptedBrackets,
            VerticalZigZags = acceptedZigZags
        };
    }

    public static IReadOnlySet<string> StructuralShapeIds(ScoreLayout layout)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var staff in layout.Staffs)
        {
            foreach (var line in staff.Lines)
                ids.Add(line.StrokeId);

            foreach (var level in staff.LedgerLevels)
            {
                foreach (var segment in level.Segments)
                    ids.Add(segment.StrokeId);
            }
        }

        foreach (var boundary in layout.Systems
                     .SelectMany(system => system.StaffPairs)
                     .SelectMany(pair => pair.Boundaries))
        {
            foreach (var id in boundary.StrokeIds)
                ids.Add(id);
        }

        return ids;
    }

    private sealed record CandidateChoice(
        string Kind,
        string Id,
        double Confidence,
        IReadOnlyList<string> SourceShapeIds,
        HairpinPrimitive? Hairpin,
        BracketSpannerPrimitive? Bracket,
        VerticalZigZagPrimitive? ZigZag);
}
