from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    p = Path(path)
    text = p.read_text()
    if text.count(old) != 1:
        raise RuntimeError(f"{path}: expected one occurrence, found {text.count(old)}\n--- needle ---\n{old}")
    p.write_text(text.replace(old, new, 1))


# 1. VoicePass: collision-displaced noteheads can sit on opposite sides of the
# same rhythmic x-position. Stem axes are a stronger simultaneity signal than
# notehead centres in that engraving pattern.
path = "SemanticInterpreter/VoicePass.cs"
replace_once(path,
'''                stem?.Direction,
                chord.Confidence,
                chord.SourceShapeIds));''',
'''                stem?.Direction,
                stem is null ? null : (stem.StartX + stem.EndX) / 2.0,
                chord.Confidence,
                chord.SourceShapeIds));''')
replace_once(path,
'''                stem?.Direction,
                Math.Min(
                    notehead.Confidence,''',
'''                stem?.Direction,
                stem is null ? null : (stem.StartX + stem.EndX) / 2.0,
                Math.Min(
                    notehead.Confidence,''')
replace_once(path,
'''                null,
                rest.Confidence,
                rest.SourceShapeIds));''',
'''                null,
                null,
                rest.Confidence,
                rest.SourceShapeIds));''')
replace_once(path,
'''                if (Math.Abs(pitched[i].AnchorX - pitched[j].AnchorX)
                    <= spacing * OppositeStemAlignmentInSpacings)
                {
                    return true;
                }''',
'''                var noteheadAligned = Math.Abs(pitched[i].AnchorX - pitched[j].AnchorX)
                    <= spacing * OppositeStemAlignmentInSpacings;
                var stemAxesAligned = pitched[i].StemAnchorX is not null
                    && pitched[j].StemAnchorX is not null
                    && Math.Abs(pitched[i].StemAnchorX!.Value - pitched[j].StemAnchorX!.Value)
                        <= spacing * OppositeStemAlignmentInSpacings;

                if (noteheadAligned || stemAxesAligned)
                {
                    return true;
                }''')
replace_once(path,
'''        double BottomY,
        StemDirection? StemDirection,
        double SourceConfidence,''',
'''        double BottomY,
        StemDirection? StemDirection,
        double? StemAnchorX,
        double SourceConfidence,''')

# 2. SlurPass: equal pitch alone is insufficient for a tie. Inside one measure,
# the second event must start exactly when the first one ends. Otherwise a long
# same-pitch phrase arc (Mimino m5) is a slur.
path = "SemanticInterpreter/SlurPass.cs"
replace_once(path,
'''using SvgMusic.Scene;
''',
'''using SvgMusic.Canonical;
using SvgMusic.Scene;
''')
replace_once(path,
'''        var voiceByTarget = voices
            .GroupBy(voice => new VoiceKey(voice.TargetKind, voice.TargetId))
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(voice => voice.Confidence).First());

        var anchors = noteheads''',
'''        var voiceByTarget = voices
            .GroupBy(voice => new VoiceKey(voice.TargetKind, voice.TargetId))
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(voice => voice.Confidence).First());
        var onsetByTarget = facts
            .OfType<OnsetFact>()
            .GroupBy(onset => new VoiceKey(onset.TargetKind, onset.TargetId))
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(onset => onset.Confidence).First());
        var durationByNotehead = facts
            .OfType<DurationFact>()
            .GroupBy(duration => duration.NoteheadId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(duration => duration.Confidence).First(),
                StringComparer.Ordinal);

        var anchors = noteheads''')
replace_once(path,
'''                return new EndpointAnchor(
                    notehead,
                    pitchByNotehead[notehead.ShapeId],
                    stem,
                    targetKind,
                    targetId,
                    localVoice);''',
'''                onsetByTarget.TryGetValue(
                    new VoiceKey(targetKind, targetId),
                    out var onset);
                durationByNotehead.TryGetValue(
                    notehead.ShapeId,
                    out var duration);

                return new EndpointAnchor(
                    notehead,
                    pitchByNotehead[notehead.ShapeId],
                    stem,
                    targetKind,
                    targetId,
                    localVoice,
                    onset,
                    duration);''')
replace_once(path,
'''        if (bestTie is not null
            && (bestSlur is null''',
'''        if (bestTie is not null
            && IsTieAdjacent(bestTie.Start.Anchor, bestTie.End.Anchor)
            && (bestSlur is null''')
replace_once(path,
'''        if (IsSamePitchVoice(bestSlur.Start.Anchor, bestSlur.End.Anchor))
        {
            return TieLike(curve, left, right, bestSlur);
        }''',
'''        if (IsSamePitchVoice(bestSlur.Start.Anchor, bestSlur.End.Anchor)
            && IsTieAdjacent(bestSlur.Start.Anchor, bestSlur.End.Anchor))
        {
            return TieLike(curve, left, right, bestSlur);
        }''')
replace_once(path,
'''    private static SlurDecision TieLike(
''',
'''    private static bool IsTieAdjacent(
        EndpointAnchor first,
        EndpointAnchor second)
    {
        if (!IsSamePitchVoice(first, second))
        {
            return false;
        }

        // Keep the established cross-measure tie behaviour until absolute score
        // timing is available. The ambiguity we can resolve exactly here is the
        // same-measure case: a tie must connect consecutive rhythmic events.
        if (first.Notehead.MeasureNumber != second.Notehead.MeasureNumber)
        {
            return true;
        }

        // Synthetic/unit callers may not have run DurationPass/OnsetPass yet.
        // Preserve the previous geometry-only behaviour in that incomplete state.
        if (first.Onset is null
            || second.Onset is null
            || first.Duration is null)
        {
            return true;
        }

        var expectedNextOnset = Fraction.Parse(first.Onset.At)
            + Fraction.Parse(first.Duration.EffectiveDuration);
        var actualNextOnset = Fraction.Parse(second.Onset.At);

        return expectedNextOnset == actualNextOnset;
    }

    private static SlurDecision TieLike(
''')
replace_once(path,
'''        VoiceTargetKind TargetKind,
        string TargetId,
        int LocalVoice);''',
'''        VoiceTargetKind TargetKind,
        string TargetId,
        int LocalVoice,
        OnsetFact? Onset,
        DurationFact? Duration);''')

# 3. Parser ownership: staff-local rest glyphs in the interstaff gap should not
# inherit a nearby stem from the wrong staff. When one staff is clearly nearer,
# classification + staff geometry wins over proximity propagation.
path = "SvgScene/LogicalOwnership.cs"
replace_once(path,
'''    private const double LedgerHalfHeightInSpacings = 0.25;
''',
'''    private const double LedgerHalfHeightInSpacings = 0.25;
    private const double InterstaffRestAmbiguityMarginInSpacings = 0.35;
    private const double MinimumRestClassificationConfidence = 0.75;
''')
replace_once(path,
'''        AssignBetweenStaffBridges(elements, layout, assignments);

        var scene = ApplyOwnership(notation, assignments);''',
'''        AssignBetweenStaffBridges(elements, layout, assignments);
        CorrectInterstaffRestOwnership(
            geometry,
            notation,
            layout,
            assignments);

        var scene = ApplyOwnership(notation, assignments);''')
replace_once(path,
'''    private static void AssignBetweenStaffBridges(
''',
'''    private static void CorrectInterstaffRestOwnership(
        GeometricScene geometry,
        NotationScene notation,
        ScoreLayout layout,
        IDictionary<string, LogicalOwnership> assignments)
    {
        var shapesById = geometry.Shapes.ToDictionary(
            shape => shape.Id,
            StringComparer.Ordinal);
        var staffsById = layout.Staffs.ToDictionary(
            staff => staff.Id,
            StringComparer.Ordinal);

        foreach (var instance in notation.Instances)
        {
            if (!IsStaffLocalRest(instance.Classification)
                || !shapesById.TryGetValue(instance.ShapeId, out var shape))
            {
                continue;
            }

            foreach (var system in layout.Systems)
            {
                var corrected = false;

                foreach (var pair in system.StaffPairs)
                {
                    var upper = staffsById[pair.UpperStaffId];
                    var lower = staffsById[pair.LowerStaffId];
                    var centerY = shape.Bounds.CenterY;

                    if (centerY <= upper.Bounds.MaxY
                        || centerY >= lower.Bounds.MinY)
                    {
                        continue;
                    }

                    var measure = pair.Measures.FirstOrDefault(candidate =>
                        shape.Bounds.CenterX >= candidate.XStart
                        && shape.Bounds.CenterX <= candidate.XEnd);
                    if (measure is null)
                    {
                        continue;
                    }

                    var upperDistance = centerY - upper.Bounds.MaxY;
                    var lowerDistance = lower.Bounds.MinY - centerY;
                    var spacing = Math.Max(
                        0.001,
                        Math.Min(
                            upper.AverageLineSpacing,
                            lower.AverageLineSpacing));

                    if (Math.Abs(upperDistance - lowerDistance)
                        <= spacing * InterstaffRestAmbiguityMarginInSpacings)
                    {
                        continue;
                    }

                    var nearestStaff = lowerDistance < upperDistance
                        ? lower
                        : upper;
                    var nearestDistance = Math.Min(
                        upperDistance,
                        lowerDistance);
                    var coordinate = new LogicalCoordinate(
                        nearestStaff.Id,
                        measure.Id);

                    if (assignments.TryGetValue(instance.ShapeId, out var existing)
                        && existing.Start == coordinate
                        && existing.End == coordinate)
                    {
                        corrected = true;
                        break;
                    }

                    assignments[instance.ShapeId] = new LogicalOwnership(
                        coordinate,
                        coordinate,
                        2,
                        null,
                        nearestDistance,
                        "InterstaffRestNearestStaff");

                    corrected = true;
                    break;
                }

                if (corrected)
                {
                    break;
                }
            }
        }
    }

    private static bool IsStaffLocalRest(SymbolClassification? classification)
    {
        if (classification is null
            || classification.Confidence < MinimumRestClassificationConfidence)
        {
            return false;
        }

        return classification.Label is
            "HW_REST_set"
            or "WHOLE_REST"
            or "HALF_REST"
            or "QUARTER_REST"
            or "EIGHTH_REST"
            or "ONE_16TH_REST"
            or "ONE_32ND_REST"
            or "ONE_64TH_REST"
            or "ONE_128TH_REST"
            or "BREVE_REST"
            or "LONG_REST";
    }

    private static void AssignBetweenStaffBridges(
''')

# Regression tests.
path = "SemanticInterpreter.Tests/VoicePassTests.cs"
replace_once(path,
'''    [Fact]
    public void LowerStaff_UsesSameLocalConvention_UpOneDownTwo()
''',
'''    [Fact]
    public void CollisionDisplacedOppositeNoteheads_UseAlignedStemAxesForPolyphony()
    {
        var facts = new SemanticFacts();
        facts.Add(Notehead("upper", 90, 92, staff: 1));
        facts.Add(Notehead("lower", 110, 108, staff: 1));
        facts.Add(Stem("stem-up", "upper", StemDirection.Up, 100, staff: 1));
        facts.Add(Stem("stem-down", "lower", StemDirection.Down, 100, staff: 1));

        var pass = new VoicePass();
        pass.Run(Document(), facts);

        Assert.Equal(1, Voice(facts, VoiceTargetKind.Notehead, "upper").LocalVoice);
        Assert.Equal(2, Voice(facts, VoiceTargetKind.Notehead, "lower").LocalVoice);
        Assert.Contains((1, 1), pass.LastAnalysis!.PolyphonicStaffs);
    }

    [Fact]
    public void LowerStaff_UsesSameLocalConvention_UpOneDownTwo()
''')

path = "SemanticInterpreter.Tests/SlurPassTests.cs"
replace_once(path,
'''    [Fact]
    public void VerticalParenthesisLikeCurve_IsRejectedBeforeEndpointMatching()
''',
'''    [Fact]
    public void SamePitchArc_WithInterveningRhythmicEvent_IsSlurNotTie()
    {
        const double spacing = 20;
        var document = Document(
            Curve(
                "phrase-arc",
                [
                    new PointD(100, 160),
                    new PointD(170, 125),
                    new PointD(240, 160)
                ],
                spacing));
        var facts = new SemanticFacts();
        facts.Add(Notehead("left", 100, 160, 2, spacing));
        facts.Add(Notehead("middle", 170, 150, 3, spacing));
        facts.Add(Notehead("right", 240, 160, 2, spacing));
        facts.Add(Pitch("left", "D4"));
        facts.Add(Pitch("middle", "E4"));
        facts.Add(Pitch("right", "D4"));
        facts.Add(Duration("left", "1/4"));
        facts.Add(Duration("middle", "1/4"));
        facts.Add(Duration("right", "1/4"));
        facts.Add(Onset("left", "0", 100));
        facts.Add(Onset("middle", "1/4", 170));
        facts.Add(Onset("right", "1/2", 240));

        var pass = new SlurPass();
        pass.Run(document, facts);

        var slur = Assert.Single(facts.OfType<SlurFact>());
        Assert.Equal("left", slur.FromNoteheadId);
        Assert.Equal("right", slur.ToNoteheadId);
        Assert.Equal("slur", Assert.Single(pass.LastAnalysis!.Decisions).Decision);
    }

    [Fact]
    public void VerticalParenthesisLikeCurve_IsRejectedBeforeEndpointMatching()
''')
replace_once(path,
'''    private static StemAttachmentFact Stem(
''',
'''    private static DurationFact Duration(
        string noteheadId,
        string duration)
    {
        return new DurationFact(
            1,
            1,
            noteheadId,
            null,
            duration,
            duration,
            "quarter",
            0,
            0,
            null,
            null,
            0.99,
            "test duration",
            [noteheadId]);
    }

    private static OnsetFact Onset(
        string noteheadId,
        string at,
        double x)
    {
        return new OnsetFact(
            1,
            1,
            VoiceTargetKind.Notehead,
            noteheadId,
            1,
            at,
            x,
            0.99,
            "test onset",
            [noteheadId]);
    }

    private static StemAttachmentFact Stem(
''')

path = "SemanticInterpreter.Tests/LogicalOwnershipPrimitiveTests.cs"
replace_once(path,
'''    [Fact]
    public void BracketSpanner_AcrossMeasureBoundary_PreservesLogicalSpan()
''',
'''    [Fact]
    public void InterstaffRest_CloserToLowerStaff_DoesNotInheritUpperStemOwnership()
    {
        var restBounds = new BoundsD(48, 178, 52, 188);
        var geometry = new GeometricScene(
        [
            new GeometricShape(
                "rest",
                "path",
                [new PointD(48, 178), new PointD(52, 188)],
                restBounds)
        ]);
        var upperStem = new Stroke(
            "upper-stem",
            new PointD(50, 130),
            new PointD(50, 176),
            1,
            "path",
            null);
        var rest = new ShapeInstance(
            "rest",
            "rest-prototype",
            restBounds.CenterX,
            restBounds.CenterY,
            restBounds.Width,
            restBounds.Height,
            "path",
            null,
            new SymbolClassification(
                "QUARTER_REST",
                0.97,
                20,
                []));
        var notation = EmptyNotation() with
        {
            Strokes = [upperStem],
            Instances = [rest]
        };

        var result = new LogicalOwnershipAnalyzer().AnalyzeAndApply(
            geometry,
            notation,
            Layout());

        var ownership = Assert.Single(result.Scene.Instances).Ownership;
        Assert.NotNull(ownership);
        Assert.Equal(new LogicalCoordinate("staff-lower", "m1"), ownership.Start);
        Assert.Equal(ownership.Start, ownership.End);
        Assert.Equal("InterstaffRestNearestStaff", ownership.Reason);
    }

    [Fact]
    public void BracketSpanner_AcrossMeasureBoundary_PreservesLogicalSpan()
''')

print("Mimino regression patch applied")
