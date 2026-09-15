from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    p = Path(path)
    text = p.read_text(encoding="utf-8")
    if old not in text:
        raise SystemExit(f"Patch anchor not found in {path}:\n{old}")
    p.write_text(text.replace(old, new, 1), encoding="utf-8")


# 1) Augmentation dots are rhythmic properties of a shared-stem chord. Geometry can
# miss one printed dot (especially on ledger-line heads); propagate the strongest
# dot count among same-fill noteheads attached to the same stem.
replace_once(
    "SemanticInterpreter/DurationPass.cs",
    '''            var matchingDots = dots
                .Where(dot =>
                    dot.MeasureNumber == notehead.MeasureNumber
                    && dot.TargetNoteheadId == notehead.ShapeId)
                .ToArray();
            var dotCount = matchingDots.Sum(dot => dot.Count);
            var dottedDuration = ApplyDots(baseDuration, dotCount);
''',
    '''            var directDots = dots
                .Where(dot =>
                    dot.MeasureNumber == notehead.MeasureNumber
                    && dot.TargetNoteheadId == notehead.ShapeId)
                .ToArray();
            var directDotCount = directDots.Sum(dot => dot.Count);
            var dotCount = directDotCount;
            var matchingDots = directDots.ToList();

            if (stem is not null)
            {
                var siblingIds = noteheads
                    .Where(candidate =>
                        candidate.MeasureNumber == notehead.MeasureNumber
                        && candidate.FillKind.Equals(
                            notehead.FillKind,
                            StringComparison.OrdinalIgnoreCase)
                        && stem.AttachedNoteheadIds.Contains(
                            candidate.ShapeId,
                            StringComparer.Ordinal))
                    .Select(candidate => candidate.ShapeId)
                    .ToHashSet(StringComparer.Ordinal);
                var strongestPeerDots = dots
                    .Where(dot =>
                        dot.MeasureNumber == notehead.MeasureNumber
                        && siblingIds.Contains(dot.TargetNoteheadId))
                    .GroupBy(dot => dot.TargetNoteheadId, StringComparer.Ordinal)
                    .Select(group => new
                    {
                        Count = group.Sum(dot => dot.Count),
                        Facts = group.ToArray()
                    })
                    .OrderByDescending(group => group.Count)
                    .ThenBy(group => group.Facts[0].TargetNoteheadId, StringComparer.Ordinal)
                    .FirstOrDefault();

                if (strongestPeerDots is not null
                    && strongestPeerDots.Count > dotCount)
                {
                    dotCount = strongestPeerDots.Count;
                    matchingDots.AddRange(strongestPeerDots.Facts);
                }
            }

            var inheritedChordDots = dotCount > directDotCount;
            var dottedDuration = ApplyDots(baseDuration, dotCount);
''')

replace_once(
    "SemanticInterpreter/DurationPass.cs",
    '''            var dotText = dotCount == 0
                ? "no augmentation dots"
                : $"dots={dotCount}";
''',
    '''            var dotText = dotCount == 0
                ? "no augmentation dots"
                : inheritedChordDots
                    ? $"dots={dotCount} propagated from same-fill notehead(s) on shared stem {stem!.StemShapeId}"
                    : $"dots={dotCount}";
''')

# 2) A tie on a ledger note can geometrically live in the neighbouring staff's
# ownership band. Tie matching uses same pitch + same staff + same voice, so allow
# its wider endpoint search to cross the ownership band. Ordinary slurs remain
# constrained to the observed staff.
replace_once(
    "SemanticInterpreter/SlurPass.cs",
    '''        var wideStartCandidates = EndpointCandidates(
            left,
            observation,
            anchors,
            spacing,
            MaximumTieEndpointDistanceInSpacings);
        var wideEndCandidates = EndpointCandidates(
            right,
            observation,
            anchors,
            spacing,
            MaximumTieEndpointDistanceInSpacings);

        var bestTie = FindBestPair(
            wideStartCandidates,
            wideEndCandidates,
            requireSamePitch: true);

        var slurStartCandidates = wideStartCandidates
            .Where(candidate => candidate.DistanceInSpacings <= MaximumSlurEndpointDistanceInSpacings)
            .ToArray();
        var slurEndCandidates = wideEndCandidates
            .Where(candidate => candidate.DistanceInSpacings <= MaximumSlurEndpointDistanceInSpacings)
            .ToArray();
''',
    '''        var wideStartCandidates = EndpointCandidates(
            left,
            observation,
            anchors,
            spacing,
            MaximumTieEndpointDistanceInSpacings,
            constrainToObservedStaff: false);
        var wideEndCandidates = EndpointCandidates(
            right,
            observation,
            anchors,
            spacing,
            MaximumTieEndpointDistanceInSpacings,
            constrainToObservedStaff: false);

        var bestTie = FindBestPair(
            wideStartCandidates,
            wideEndCandidates,
            requireSamePitch: true);

        var slurStartCandidates = EndpointCandidates(
            left,
            observation,
            anchors,
            spacing,
            MaximumSlurEndpointDistanceInSpacings,
            constrainToObservedStaff: true);
        var slurEndCandidates = EndpointCandidates(
            right,
            observation,
            anchors,
            spacing,
            MaximumSlurEndpointDistanceInSpacings,
            constrainToObservedStaff: true);
''')

replace_once(
    "SemanticInterpreter/SlurPass.cs",
    '''    private static IReadOnlyList<EndpointChoice> EndpointCandidates(
        PointD endpoint,
        CurveObservation observation,
        IReadOnlyList<EndpointAnchor> anchors,
        double spacing,
        double maximumDistanceInSpacings)
    {
        return anchors
            .Where(anchor => observation.Measures.Contains(anchor.Notehead.MeasureNumber))
            .Where(anchor => observation.Staffs.Contains(anchor.Notehead.Staff))
''',
    '''    private static IReadOnlyList<EndpointChoice> EndpointCandidates(
        PointD endpoint,
        CurveObservation observation,
        IReadOnlyList<EndpointAnchor> anchors,
        double spacing,
        double maximumDistanceInSpacings,
        bool constrainToObservedStaff)
    {
        return anchors
            .Where(anchor => observation.Measures.Contains(anchor.Notehead.MeasureNumber))
            .Where(anchor =>
                !constrainToObservedStaff
                || observation.Staffs.Contains(anchor.Notehead.Staff))
''')

replace_once(
    "SemanticInterpreter/TieCandidateMatcher.cs",
    '''        return anchors
            .Where(anchor => observation.Measures.Contains(anchor.Notehead.MeasureNumber))
            .Where(anchor => observation.Staffs.Contains(anchor.Notehead.Staff))
            .Select(anchor => new EndpointChoice(
''',
    '''        return anchors
            .Where(anchor => observation.Measures.Contains(anchor.Notehead.MeasureNumber))
            .Select(anchor => new EndpointChoice(
''')

# 3) Voice continuity around a note/rest split. In m16 the leading Ab half-note has
# a down stem, but the same Ab returns after an eighth rest in voice 1 while a second
# voice starts underneath the rest. Stem direction alone must not steal the lead-in
# note into voice 2.
replace_once(
    "SemanticInterpreter/VoicePass.cs",
    '''        var chords = facts.OfType<ChordFact>().ToArray();
        var rests = facts.OfType<RestFact>().ToArray();
''',
    '''        var chords = facts.OfType<ChordFact>().ToArray();
        var rests = facts.OfType<RestFact>().ToArray();
        var pitches = facts.OfType<PitchFact>().ToArray();
''')

replace_once(
    "SemanticInterpreter/VoicePass.cs",
    '''        var events = BuildEvents(
            noteheads,
            stems,
            chords,
            rests);
''',
    '''        var events = BuildEvents(
            noteheads,
            stems,
            chords,
            rests,
            pitches);
''')

replace_once(
    "SemanticInterpreter/VoicePass.cs",
    '''    private static IReadOnlyList<VoiceEvent> BuildEvents(
        IReadOnlyList<NoteheadFact> noteheads,
        IReadOnlyList<StemAttachmentFact> stems,
        IReadOnlyList<ChordFact> chords,
        IReadOnlyList<RestFact> rests)
    {
        var noteheadsByKey = noteheads.ToDictionary(
''',
    '''    private static IReadOnlyList<VoiceEvent> BuildEvents(
        IReadOnlyList<NoteheadFact> noteheads,
        IReadOnlyList<StemAttachmentFact> stems,
        IReadOnlyList<ChordFact> chords,
        IReadOnlyList<RestFact> rests,
        IReadOnlyList<PitchFact> pitches)
    {
        var pitchByNotehead = pitches
            .GroupBy(pitch => pitch.NoteheadId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(pitch => pitch.Confidence).First(),
                StringComparer.Ordinal);
        var noteheadsByKey = noteheads.ToDictionary(
''')

replace_once(
    "SemanticInterpreter/VoicePass.cs",
    '''                stem?.Direction,
                chord.Confidence,
                chord.SourceShapeIds));
''',
    '''                stem?.Direction,
                chord.Confidence,
                chord.SourceShapeIds,
                SinglePitchKey(homeHeads, pitchByNotehead)));
''')

replace_once(
    "SemanticInterpreter/VoicePass.cs",
    '''                stem is null
                    ? notehead.SourceShapeIds
                    : notehead.SourceShapeIds
                        .Concat(new[] { stem.StemShapeId })
                        .Distinct(StringComparer.Ordinal)
                        .ToArray()));
''',
    '''                stem is null
                    ? notehead.SourceShapeIds
                    : notehead.SourceShapeIds
                        .Concat(new[] { stem.StemShapeId })
                        .Distinct(StringComparer.Ordinal)
                        .ToArray(),
                pitchByNotehead.TryGetValue(notehead.ShapeId, out var pitch)
                    ? pitch.Pitch
                    : null));
''')

replace_once(
    "SemanticInterpreter/VoicePass.cs",
    '''                null,
                rest.Confidence,
                rest.SourceShapeIds));
''',
    '''                null,
                rest.Confidence,
                rest.SourceShapeIds,
                null));
''')

replace_once(
    "SemanticInterpreter/VoicePass.cs",
    '''        return result;
    }

    private static int ChooseHomeStaff(
''',
    '''        return result;
    }

    private static string? SinglePitchKey(
        IReadOnlyList<NoteheadFact> noteheads,
        IReadOnlyDictionary<string, PitchFact> pitchByNotehead)
    {
        var pitchKeys = noteheads
            .Where(notehead => pitchByNotehead.ContainsKey(notehead.ShapeId))
            .Select(notehead => pitchByNotehead[notehead.ShapeId].Pitch)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return pitchKeys.Length == 1
            ? pitchKeys[0]
            : null;
    }

    private static int ChooseHomeStaff(
''')

replace_once(
    "SemanticInterpreter/VoicePass.cs",
    '''        foreach (var rest in rests.Where(rest => !result.ContainsKey(Key(rest))))
        {
''',
    '''        foreach (var rest in rests.Where(rest => !result.ContainsKey(Key(rest))))
        {
''')

replace_once(
    "SemanticInterpreter/VoicePass.cs",
    '''            result[Key(rest)] = CreateVoiceFact(
                rest,
                1,
                true,
                0.42,
                "polyphonic staff but rest is not vertically separated enough to identify a voice; defaulting to local voice 1");
        }

        return events
''',
    '''            result[Key(rest)] = CreateVoiceFact(
                rest,
                1,
                true,
                0.42,
                "polyphonic staff but rest is not vertically separated enough to identify a voice; defaulting to local voice 1");
        }

        RefineRestBridgedPitchContinuity(
            events,
            spacing,
            result);

        return events
''')

replace_once(
    "SemanticInterpreter/VoicePass.cs",
    '''    private static VoiceFact CreateVoiceFact(
''',
    '''    private static void RefineRestBridgedPitchContinuity(
        IReadOnlyList<VoiceEvent> events,
        double spacing,
        IDictionary<TargetKey, VoiceFact> result)
    {
        var pitched = events
            .Where(item => item.TargetKind != VoiceTargetKind.Rest)
            .ToArray();
        var rests = events
            .Where(item => item.TargetKind == VoiceTargetKind.Rest)
            .OrderBy(item => item.AnchorX)
            .ToArray();
        var alignment = spacing * NoteRestAlignmentInSpacings;
        var margin = spacing * VerticalOutsideMarginInSpacings;

        foreach (var rest in rests)
        {
            if (!result.TryGetValue(Key(rest), out var restVoice))
            {
                continue;
            }

            var splitPartner = pitched
                .Where(note => result.TryGetValue(Key(note), out var noteVoice)
                    && noteVoice.LocalVoice != restVoice.LocalVoice)
                .Where(note => Math.Abs(note.AnchorX - rest.AnchorX) <= alignment)
                .Where(note => rest.AnchorY < note.TopY - margin
                    || rest.AnchorY > note.BottomY + margin)
                .OrderBy(note => Math.Abs(note.AnchorX - rest.AnchorX))
                .ThenBy(note => Math.Abs(note.AnchorY - rest.AnchorY))
                .FirstOrDefault();
            if (splitPartner is null)
            {
                continue;
            }

            var following = pitched
                .Where(note => note.AnchorX > rest.AnchorX + 1e-6)
                .Where(note => note.PitchKey is not null)
                .Where(note => result.TryGetValue(Key(note), out var noteVoice)
                    && noteVoice.LocalVoice == restVoice.LocalVoice)
                .OrderBy(note => note.AnchorX)
                .ThenBy(note => Math.Abs(note.AnchorY - rest.AnchorY))
                .FirstOrDefault();
            if (following?.PitchKey is null)
            {
                continue;
            }

            var preceding = pitched
                .Where(note => note.AnchorX < rest.AnchorX - 1e-6)
                .Where(note => string.Equals(
                    note.PitchKey,
                    following.PitchKey,
                    StringComparison.Ordinal))
                .OrderByDescending(note => note.AnchorX)
                .ThenBy(note => Math.Abs(note.AnchorY - following.AnchorY))
                .FirstOrDefault();
            if (preceding is null
                || !result.TryGetValue(Key(preceding), out var precedingVoice)
                || precedingVoice.LocalVoice == restVoice.LocalVoice)
            {
                continue;
            }

            var interveningSameVoice = pitched.Any(note =>
                note != preceding
                && note.AnchorX > preceding.AnchorX + 1e-6
                && note.AnchorX < rest.AnchorX - 1e-6
                && result.TryGetValue(Key(note), out var noteVoice)
                && noteVoice.LocalVoice == restVoice.LocalVoice);
            if (interveningSameVoice)
            {
                continue;
            }

            result[Key(preceding)] = CreateVoiceFact(
                preceding,
                restVoice.LocalVoice,
                true,
                0.93,
                $"same-pitch phrase continues through rest {rest.TargetId} into {following.TargetId}; "
                + $"the isolated lead-in belongs to local voice {restVoice.LocalVoice} despite stem direction");
        }
    }

    private static VoiceFact CreateVoiceFact(
''')

replace_once(
    "SemanticInterpreter/VoicePass.cs",
    '''        double SourceConfidence,
        IReadOnlyList<string> SourceShapeIds)
''',
    '''        double SourceConfidence,
        IReadOnlyList<string> SourceShapeIds,
        string? PitchKey)
''')

# Golden regression tests for the three concrete bugs.
Path("SemanticInterpreter.Tests/ScoreRegressionTests.cs").write_text(r'''using SvgMusic.Scene;
using SvgMusic.Semantics;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class ScoreRegressionTests
{
    [Fact]
    public void SharedStemChord_PropagatesDetectedAugmentationDotToAllHeads()
    {
        var facts = new SemanticFacts();
        facts.Add(Notehead("top", 100, 90, "hollow", staff: 2));
        facts.Add(Notehead("bottom", 100, 110, "hollow", staff: 2));
        facts.Add(new StemAttachmentFact(
            1, "stem", StemDirection.Down, ["top", "bottom"], [2], false,
            90, 90, 90, 140, 5, 0.1, 0.98, "test", ["stem", "top", "bottom"]));
        facts.Add(new DotAttachmentFact(
            1, 2, "bottom", ["dot"], 1, 0.96, "test", ["dot", "bottom"]));

        new DurationPass().Run(new SemanticDocument([]), facts);

        var durations = facts.OfType<DurationFact>().OrderBy(d => d.NoteheadId).ToArray();
        Assert.Equal(2, durations.Length);
        Assert.All(durations, duration => Assert.Equal(1, duration.Dots));
        Assert.All(durations, duration => Assert.Equal("3/4", duration.EffectiveDuration));
        Assert.Contains(durations, duration => duration.Reason.Contains("propagated", StringComparison.Ordinal));
    }

    [Fact]
    public void TieOnLowerStaffLedgerNotes_CanCrossCurveOwnershipBand()
    {
        const double spacing = 20;
        var curve = Curve("tie", [
            new PointD(100, 205),
            new PointD(150, 190),
            new PointD(200, 205)
        ], spacing);
        var document = Document(curve, spacing);
        var facts = new SemanticFacts();
        facts.Add(Notehead("left", 100, 220, "hollow", staff: 2));
        facts.Add(Notehead("right", 200, 220, "hollow", staff: 2));
        facts.Add(Pitch("left", "F4", staff: 2));
        facts.Add(Pitch("right", "F4", staff: 2));

        var slur = new SlurPass();
        slur.Run(document, facts);
        Assert.Equal("tie-like", Assert.Single(slur.LastAnalysis!.Decisions).Decision);

        new TiePass().Run(document, facts);
        var tie = Assert.Single(facts.OfType<TieFact>());
        Assert.Equal(2, tie.Staff);
        Assert.Equal("F4", tie.Pitch);
        Assert.Equal("left", tie.FromNoteheadId);
        Assert.Equal("right", tie.ToNoteheadId);
    }

    [Fact]
    public void RestBridgesSamePitchVoice_AroundPolyphonicSplit_AndOnsetsStayCorrect()
    {
        var facts = new SemanticFacts();
        facts.Add(Notehead("lead", 40, 90, "hollow", staff: 1));
        facts.Add(Notehead("parallel", 100, 108, "filled", staff: 1));
        facts.Add(Notehead("return", 150, 90, "filled", staff: 1));
        facts.Add(Stem("lead-stem", "lead", StemDirection.Down, 40));
        facts.Add(Stem("parallel-stem", "parallel", StemDirection.Down, 100));
        facts.Add(Stem("return-stem", "return", StemDirection.Up, 150));
        facts.Add(Pitch("lead", "Ab5", staff: 1));
        facts.Add(Pitch("parallel", "F5", staff: 1));
        facts.Add(Pitch("return", "Ab5", staff: 1));
        facts.Add(new RestFact(
            1, 1, "rest", "EIGHTH_REST", 0.99, "eighth", "1/8",
            103, 82, 0.98, "test rest", ["rest"]));
        facts.Add(Duration("lead", "lead-stem", "1/2", "half"));
        facts.Add(Duration("parallel", "parallel-stem", "1/4", "quarter"));
        facts.Add(Duration("return", "return-stem", "1/8", "eighth"));

        var document = Document(null, 10);
        new VoicePass().Run(document, facts);
        new OnsetPass().Run(document, facts);

        Assert.Equal(1, Voice(facts, VoiceTargetKind.Notehead, "lead").LocalVoice);
        Assert.Equal(1, Voice(facts, VoiceTargetKind.Rest, "rest").LocalVoice);
        Assert.Equal(1, Voice(facts, VoiceTargetKind.Notehead, "return").LocalVoice);
        Assert.Equal(2, Voice(facts, VoiceTargetKind.Notehead, "parallel").LocalVoice);

        Assert.Equal("0", Onset(facts, VoiceTargetKind.Notehead, "lead").At);
        Assert.Equal("1/2", Onset(facts, VoiceTargetKind.Rest, "rest").At);
        Assert.Equal("5/8", Onset(facts, VoiceTargetKind.Notehead, "return").At);
        Assert.Equal("1/2", Onset(facts, VoiceTargetKind.Notehead, "parallel").At);
    }

    private static VoiceFact Voice(SemanticFacts facts, VoiceTargetKind kind, string id) =>
        Assert.Single(facts.OfType<VoiceFact>(), v => v.TargetKind == kind && v.TargetId == id);

    private static OnsetFact Onset(SemanticFacts facts, VoiceTargetKind kind, string id) =>
        Assert.Single(facts.OfType<OnsetFact>(), o => o.TargetKind == kind && o.TargetId == id);

    private static NoteheadFact Notehead(string id, double x, double y, string fill, int staff) =>
        new(1, staff, id, x, y, 5, 4, fill, 1, 0, 0, 0.99, "test", [id]);

    private static PitchFact Pitch(string id, string pitch, int staff)
    {
        var step = pitch[0].ToString();
        var octave = int.Parse(pitch[^1].ToString());
        var alter = pitch.Contains('b') ? -1 : pitch.Contains('#') ? 1 : 0;
        return new PitchFact(
            1, staff, id, step, octave, alter, pitch, 0,
            staff == 1 ? "G" : "F", staff == 1 ? 2 : 4, "clef", 0,
            null, null, false, 0.99, "test", [id]);
    }

    private static StemAttachmentFact Stem(string id, string note, StemDirection direction, double x) =>
        new(1, id, direction, [note], [1], false, x, 60, x, 120, 6, 0.1, 0.98, "test", [id, note]);

    private static DurationFact Duration(string note, string stem, string duration, string noteType) =>
        new(1, 1, note, stem, duration, duration, noteType, 0, 0, null, null, 0.98, "test", [note, stem]);

    private static CurveElement Curve(string id, IReadOnlyList<PointD> points, double spacing)
    {
        var ownership = new LogicalOwnership(
            new LogicalCoordinate("staff-1", "measure-1"),
            new LogicalCoordinate("staff-1", "measure-1"),
            1, null, 0, "test");
        var source = new CurvedStroke(
            id, points, Enumerable.Repeat(spacing * 0.08, points.Count).ToArray(),
            0.2, 1.0, null, "path", null, ownership);
        return new CurveElement
        {
            ShapeId = id,
            Bounds = BoundsD.FromPoints(points),
            Ownership = ownership,
            Source = source
        };
    }

    private static SemanticDocument Document(CurveElement? upperCurve, double spacing)
    {
        var upperElements = upperCurve is null
            ? Array.Empty<SemanticElement>()
            : new SemanticElement[] { upperCurve };
        return new SemanticDocument([
            new MeasureScene(
                1, "system-1", "pair-1", "measure-1", 0, 300, false,
                new StaffMeasureScene(1, "staff-1", new BoundsD(0, 80, 300, 120), spacing, upperElements),
                new StaffMeasureScene(2, "staff-2", new BoundsD(0, 200, 300, 240), spacing, Array.Empty<SemanticElement>()))
        ]);
    }
}
''', encoding="utf-8")

# Add sample-specific smoke checks: the current golden sample must retain all three
# lower-staff ties in m11, dotted lower chord in m13, and the m16 two-voice split.
workflow = Path(".github/workflows/semantic-canonical-poc.yml")
text = workflow.read_text(encoding="utf-8")
anchor = '''          assert tie_starts == canonical_tie_count, (tie_starts, canonical_tie_count)\n\n          mxl = Path("artifacts/kancheli.semantic.mxl")\n'''
insert = '''          assert tie_starts == canonical_tie_count, (tie_starts, canonical_tie_count)\n\n          # Golden regressions from visual review of measures 11, 13 and 16.\n          m11 = next(m for m in root.iter("measure") if m.get("number") == "11")\n          m11_lower_tie_starts = sum(\n              1 for note in m11.findall("note")\n              if note.findtext("staff") == "2"\n              and any(t.get("type") == "start" for t in note.findall("tie"))\n          )\n          assert m11_lower_tie_starts == 3, m11_lower_tie_starts\n\n          m13 = next(m for m in root.iter("measure") if m.get("number") == "13")\n          m13_lower = [note for note in m13.findall("note") if note.findtext("staff") == "2"]\n          assert len(m13_lower) == 2, len(m13_lower)\n          assert all(len(note.findall("dot")) == 1 for note in m13_lower)\n\n          import json\n          canonical = json.loads(Path("artifacts/kancheli.semantic.canonical.json").read_text(encoding="utf-8-sig"))\n          m16_events = next(\n              m["events"] for m in canonical["parts"][0]["measures"] if m["number"] == 16\n          )\n          def event(predicate):\n              return next(ev for ev in m16_events if predicate(ev))\n          lead = event(lambda ev: ev.get("duration") == "1/2"\n              and any(note.get("pitch") == "Ab5" for note in ev.get("notes") or []))\n          rest = event(lambda ev: ev.get("type") == "rest" and ev.get("staff") == 1)\n          returned = event(lambda ev: ev.get("duration") == "1/8"\n              and any(note.get("pitch") == "Ab5" for note in ev.get("notes") or []))\n          parallel = event(lambda ev: ev.get("duration") == "1/4"\n              and {note.get("pitch") for note in ev.get("notes") or []} == {"F5", "G5"})\n          assert (lead["voice"], lead["at"]) == (1, "0"), lead\n          assert (rest["voice"], rest["at"]) == (1, "1/2"), rest\n          assert (returned["voice"], returned["at"]) == (1, "5/8"), returned\n          assert (parallel["voice"], parallel["at"]) == (2, "1/2"), parallel\n\n          mxl = Path("artifacts/kancheli.semantic.mxl")\n'''
if anchor not in text:
    raise SystemExit("workflow smoke anchor not found")
workflow.write_text(text.replace(anchor, insert, 1), encoding="utf-8")
