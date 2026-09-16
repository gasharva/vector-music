from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    p = Path(path)
    text = p.read_text()
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected one occurrence, found {count}\n--- needle ---\n{old}")
    p.write_text(text.replace(old, new, 1))


# PedalPass: semantic pedal geometry is stronger than generic parser ownership.
path = "SemanticInterpreter/PedalPass.cs"
replace_once(path,
'''            if (source.Ownership is null)
            {
                decisions.Add(Reject(
                    source.Id,
                    "unmapped-ownership",
                    $"{source.Id}: bracket has no logical ownership"));
                continue;
            }

            var staffId = source.Ownership.Start.StaffId;
            var startContext = ResolveContextAtX(
                contexts,
                staffId,
                source.Start.X,
                source.Ownership.Start,
                preferLaterAtBoundary: true);
            var endContext = ResolveContextAtX(
                contexts,
                staffId,
                source.End.X,
                source.Ownership.End,
                preferLaterAtBoundary: false);
''',
'''            var baselineY = (source.Start.Y + source.End.Y) / 2.0;
            var startContext = ResolvePedalContextAtX(
                contexts,
                source.Start.X,
                baselineY,
                preferLaterAtBoundary: true);
            var endContext = startContext is null
                ? null
                : ResolveContextAtX(
                    contexts,
                    startContext.Coordinate.StaffId,
                    source.End.X,
                    startContext.Coordinate,
                    preferLaterAtBoundary: false);
''')
replace_once(path,
'''    private static CoordinateContext? ResolveContextAtX(
''',
'''    private static CoordinateContext? ResolvePedalContextAtX(
        IReadOnlyList<CoordinateContext> contexts,
        double x,
        double baselineY,
        bool preferLaterAtBoundary)
    {
        var candidates = contexts
            .Where(context => context.StaffNumber == 2)
            .Where(context =>
                x >= context.XStart - CoordinateEpsilon
                && x <= context.XEnd + CoordinateEpsilon)
            .Select(context => new
            {
                Context = context,
                Gap = baselineY - context.StaffBounds.MaxY,
                Tolerance = Math.Max(context.LineSpacing, 0.001)
                    * OutsideStaffToleranceInSpacings
            })
            // A pedal line is semantically below the lower stave. This deliberately
            // ignores a generic owner inherited from nearby geometry in the next
            // system: the closest lower stave above the line is the only plausible one.
            .Where(candidate => candidate.Gap >= -candidate.Tolerance)
            .OrderBy(candidate =>
                candidate.Gap / Math.Max(candidate.Context.LineSpacing, 0.001))
            .ThenBy(candidate => preferLaterAtBoundary
                ? -candidate.Context.XStart
                : candidate.Context.XEnd)
            .ThenBy(candidate => candidate.Context.MeasureNumber)
            .Select(candidate => candidate.Context)
            .FirstOrDefault();

        return candidates;
    }

    private static CoordinateContext? ResolveContextAtX(
''')
replace_once(path,
'''            .Where(label =>
                label.Ownership.Start == context.Coordinate
                || label.Ownership.End == context.Coordinate)
            .Select(label =>
''',
'''            // PEDAL_MARK ownership can fail for exactly the same reason as the
            // bracket: both live in the inter-system whitespace. Once the bracket
            // has selected the lower stave geometrically, pair the glyph by its own
            // x/y relation to the bracket rather than inherited ownership.
            .Select(label =>
''')

# ClassifiedSymbolPass: compose split MP from siblings of one original SVG shape.
path = "SemanticInterpreter/ClassifiedSymbolPass.cs"
replace_once(path,
'''        var noteheads = facts.OfType<NoteheadFact>().ToArray();
        var onsets = facts.OfType<OnsetFact>().ToArray();
        var decisions = new List<ClassifiedSymbolDecision>();
''',
'''        var noteheads = facts.OfType<NoteheadFact>().ToArray();
        var onsets = facts.OfType<OnsetFact>().ToArray();
        var shapeGroups = document.Measures
            .SelectMany(measure => new[] { measure.Upper, measure.Lower })
            .SelectMany(staff => staff.Elements.OfType<ShapeElement>())
            .GroupBy(shape => shape.ShapeId, StringComparer.Ordinal)
            .Select(group => group.First())
            .GroupBy(shape => BaseShapeId(shape.ShapeId), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(shape => shape.CenterX).ToArray(),
                StringComparer.Ordinal);
        var decisions = new List<ClassifiedSymbolDecision>();
''')
replace_once(path,
'''                    var classification = element.Classification;
                    var label = NormalizeLabel(classification.Label);
                    if (!TryMap(label, out var mapping))
                    {
                        continue;
                    }

                    if (classification.Confidence < MinimumConfidence)
''',
'''                    var classification = element.Classification;
                    var label = NormalizeLabel(classification.Label);
                    var effectiveConfidence = classification.Confidence;
                    IReadOnlyList<string> sourceShapeIds = [element.ShapeId];

                    if (!TryMap(label, out var mapping)
                        && !TryComposeSplitDynamic(
                            element,
                            label,
                            staff.LineSpacing,
                            shapeGroups,
                            out label,
                            out mapping,
                            out effectiveConfidence,
                            out sourceShapeIds))
                    {
                        continue;
                    }

                    if (effectiveConfidence < MinimumConfidence)
''')
replace_once(path,
'''                            classification.Confidence,
                            $"{label} confidence {classification.Confidence:P0} is below {MinimumConfidence:P0}"));
''',
'''                            effectiveConfidence,
                            $"{label} confidence {effectiveConfidence:P0} is below {MinimumConfidence:P0}"));
''')
replace_once(path,
'''                    if (consumed.Contains(element.ShapeId))
                    {
''',
'''                    if (sourceShapeIds.Any(consumed.Contains))
                    {
''')
replace_once(path,
'''                            classification.Confidence,
                            $"{element.ShapeId} is already referenced by an earlier semantic fact"));
''',
'''                            effectiveConfidence,
                            $"{string.Join(",", sourceShapeIds)} includes geometry already referenced by an earlier semantic fact"));
''')
replace_once(path,
'''                            classification.Confidence,
                            onsets,
                            facts,
                            decisions);
''',
'''                            effectiveConfidence,
                            sourceShapeIds,
                            onsets,
                            facts,
                            decisions);
''')
replace_once(path,
'''        double classificationConfidence,
        IReadOnlyList<OnsetFact> onsets,
''',
'''        double classificationConfidence,
        IReadOnlyList<string> sourceShapeIds,
        IReadOnlyList<OnsetFact> onsets,
''')
replace_once(path,
'''            reason,
            [element.ShapeId]));
        decisions.Add(new ClassifiedSymbolDecision(
''',
'''            reason,
            sourceShapeIds));
        decisions.Add(new ClassifiedSymbolDecision(
''')
replace_once(path,
'''    private static bool TryMap(
''',
'''    private static bool TryComposeSplitDynamic(
        ShapeElement element,
        string primaryLabel,
        double lineSpacing,
        IReadOnlyDictionary<string, ShapeElement[]> shapeGroups,
        out string effectiveLabel,
        out SymbolMapping mapping,
        out double effectiveConfidence,
        out IReadOnlyList<string> sourceShapeIds)
    {
        effectiveLabel = primaryLabel;
        mapping = default;
        effectiveConfidence = element.Classification?.Confidence ?? 0;
        sourceShapeIds = [element.ShapeId];

        // The Audiveris model has no reliable standalone M-dynamic component for
        // split glyphs. MuseScore-style compound paths can therefore become an
        // unrecognised left 'm' plus a very confident right DYNAMICS_P. Provenance
        // from CompoundShapeSplitter gives both fragments the same shape-N prefix.
        if (primaryLabel != "DYNAMICS_P"
            || element.Classification is null
            || element.Classification.Confidence < MinimumConfidence
            || !shapeGroups.TryGetValue(BaseShapeId(element.ShapeId), out var siblings)
            || siblings.Length != 2)
        {
            return false;
        }

        var spacing = Math.Max(lineSpacing, 0.001);
        var left = siblings
            .Where(candidate => candidate.ShapeId != element.ShapeId)
            .Where(candidate => candidate.CenterX < element.CenterX)
            .Where(candidate =>
                element.CenterX - candidate.CenterX <= spacing * 2.0
                && Math.Abs(element.CenterY - candidate.CenterY) <= spacing * 1.0)
            .OrderBy(candidate => element.CenterX - candidate.CenterX)
            .FirstOrDefault();

        if (left?.Classification is null
            || left.Classification.Confidence > 0.30)
        {
            return false;
        }

        var verticalOverlap = Math.Max(
            0,
            Math.Min(left.Bounds.MaxY, element.Bounds.MaxY)
                - Math.Max(left.Bounds.MinY, element.Bounds.MinY));
        var minimumHeight = Math.Max(
            0.001,
            Math.Min(left.Bounds.Height, element.Bounds.Height));
        var broadEnough = left.Bounds.Width >= element.Bounds.Width * 0.55;

        if (verticalOverlap / minimumHeight < 0.55 || !broadEnough)
        {
            return false;
        }

        effectiveLabel = "DYNAMICS_MP";
        mapping = new SymbolMapping(SymbolFamily.Dynamic, "mp");
        // Keep this conservative: provenance+geometry are strong evidence, but the
        // classifier only proved the P component. Still high enough for the residual
        // pass while remaining below a direct whole-glyph classification.
        effectiveConfidence = Math.Min(element.Classification.Confidence, 0.92);
        sourceShapeIds = [left.ShapeId, element.ShapeId];
        return true;
    }

    private static string BaseShapeId(string shapeId)
    {
        var separator = shapeId.IndexOf('.');
        return separator < 0 ? shapeId : shapeId[..separator];
    }

    private static bool TryMap(
''')

# Pedal regression: wrong generic owner in next system is ignored semantically.
path = "SemanticInterpreter.Tests/PedalPassTests.cs"
replace_once(path,
'''    [Fact]
    public void PedalFact_ProjectsToCanonicalRelation_AndMusicXmlDirections()
''',
'''    [Fact]
    public void PedalBelowFirstSystem_IgnoresWrongOwnershipInSystemBelow()
    {
        var wrongOwnership = new LogicalOwnership(
            new LogicalCoordinate("lower-2", "m2"),
            new LogicalCoordinate("lower-2", "m2"),
            2,
            "foreign-stem",
            10,
            "Proximity");
        var misplacedElements = new SemanticElement[]
        {
            Label(wrongOwnership),
            Bracket(wrongOwnership, endX: 98, dashed: false)
        };
        var document = TwoSystemDocument(misplacedElements);
        var facts = TimingFacts(1, "note-1", 5, "3/4");

        new PedalPass().Run(document, facts);

        var pedal = Assert.Single(facts.OfType<PedalFact>());
        Assert.Equal(1, pedal.StartMeasureNumber);
        Assert.Equal(1, pedal.EndMeasureNumber);
        Assert.Equal(2, pedal.Staff);
        Assert.Equal("0", pedal.StartAt);
        Assert.Equal("3/4", pedal.EndAt);
    }

    [Fact]
    public void PedalFact_ProjectsToCanonicalRelation_AndMusicXmlDirections()
''')
replace_once(path,
'''    private static MeasureScene Measure(
''',
'''    private static SemanticDocument TwoSystemDocument(
        IReadOnlyList<SemanticElement> wronglyOwnedElements) =>
        new([
            new MeasureScene(
                1,
                "system-1",
                "pair-1",
                "m1",
                0,
                100,
                false,
                new StaffMeasureScene(
                    1,
                    "upper-1",
                    new BoundsD(0, 100, 100, 140),
                    10,
                    []),
                new StaffMeasureScene(
                    2,
                    "lower-1",
                    new BoundsD(0, 200, 100, 240),
                    10,
                    [])),
            new MeasureScene(
                2,
                "system-2",
                "pair-2",
                "m2",
                0,
                100,
                true,
                new StaffMeasureScene(
                    1,
                    "upper-2",
                    new BoundsD(0, 400, 100, 440),
                    10,
                    []),
                new StaffMeasureScene(
                    2,
                    "lower-2",
                    new BoundsD(0, 500, 100, 540),
                    10,
                    wronglyOwnedElements))
        ]);

    private static MeasureScene Measure(
''')

# Composite mp regression and helper bounds.
path = "SemanticInterpreter.Tests/ClassifiedSymbolPassTests.cs"
replace_once(path,
'''    [Fact]
    public void ShapeConsumedByEarlierPass_IsNotReinterpreted()
''',
'''    [Fact]
    public void SplitMp_UsesLowConfidenceLeftSiblingFromSameSourceShape()
    {
        var mFragment = Symbol(
            "shape-20.1",
            "TREMOLO_3",
            0.12,
            x: 40,
            y: 160,
            width: 12);
        var pFragment = Symbol(
            "shape-20.2",
            "DYNAMICS_P",
            0.99,
            x: 54,
            y: 160,
            width: 10);
        var document = Document([mFragment, pFragment]);
        var facts = new SemanticFacts();
        AddOnset(facts, "note", x: 52, at: "1/4");

        new ClassifiedSymbolPass().Run(document, facts);

        var dynamic = Assert.Single(facts.OfType<DynamicDirectionFact>());
        Assert.Equal("mp", dynamic.Value);
        Assert.Equal("DYNAMICS_MP", dynamic.ClassificationLabel);
        Assert.Equal("1/4", dynamic.At);
        Assert.Contains("shape-20.1", dynamic.SourceShapeIds);
        Assert.Contains("shape-20.2", dynamic.SourceShapeIds);
    }

    [Fact]
    public void ShapeConsumedByEarlierPass_IsNotReinterpreted()
''')
replace_once(path,
'''        double x,
        double y)
    {
''',
'''        double x,
        double y,
        double width = 10)
    {
''')
replace_once(path,
'''            x - 5,
            y - 5,
            10,
            10,
''',
'''            x - width / 2.0,
            y - 5,
            width,
            10,
''')
replace_once(path,
'''            Bounds = new BoundsD(x - 5, y - 5, x + 5, y + 5),
''',
'''            Bounds = new BoundsD(x - width / 2.0, y - 5, x + width / 2.0, y + 5),
''')

print("Final Mimino patch applied")
