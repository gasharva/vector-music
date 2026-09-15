namespace SvgMusic.Semantics;

public sealed class PitchPass : ISemanticPass
{
    private static readonly string[] NaturalSteps =
    [
        "C",
        "D",
        "E",
        "F",
        "G",
        "A",
        "B"
    ];

    private static readonly string[] SharpOrder =
    [
        "F",
        "C",
        "G",
        "D",
        "A",
        "E",
        "B"
    ];

    private static readonly string[] FlatOrder =
    [
        "B",
        "E",
        "A",
        "D",
        "G",
        "C",
        "F"
    ];

    public string Name => nameof(PitchPass);

    public void Run(
        SemanticDocument document,
        SemanticFacts facts)
    {
        var noteheads = facts
            .OfType<NoteheadFact>()
            .OrderBy(notehead => notehead.MeasureNumber)
            .ThenBy(notehead => notehead.Staff)
            .ThenBy(notehead => notehead.CenterX)
            .ThenBy(notehead => notehead.CenterY)
            .ToArray();

        if (noteheads.Length == 0)
        {
            throw new InvalidDataException(
                "PitchPass requires NoteheadPass to run first.");
        }

        var clefs = facts
            .OfType<ClefFact>()
            .ToArray();
        var keys = facts
            .OfType<KeySignatureFact>()
            .ToArray();
        var accidentals = facts
            .OfType<AccidentalFact>()
            .ToArray();

        if (clefs.Length == 0)
        {
            throw new InvalidDataException(
                "PitchPass requires ClefPass to run first.");
        }

        if (keys.Length == 0)
        {
            throw new InvalidDataException(
                "PitchPass requires KeySignaturePass to run first.");
        }

        var explicitAccidentals = 0;
        var inheritedAccidentals = 0;
        var keyOnly = 0;

        foreach (var notehead in noteheads)
        {
            var clef = ResolveActiveClef(
                notehead,
                clefs);
            var key = ResolveActiveKey(
                notehead,
                keys);
            var accidental = ResolveActiveAccidental(
                notehead,
                accidentals);

            var diatonicPitch = ResolveDiatonicPitch(
                notehead.StaffStep,
                clef);
            var keyAlter = ResolveKeyAlter(
                diatonicPitch.Step,
                key.Fifths);

            var alter = keyAlter;
            var activeAccidentalShapeId = (string?)null;
            var activeAccidentalKind = (AccidentalKind?)null;
            var isAccidentalExplicit = false;

            if (accidental is not null)
            {
                alter = AlterFor(accidental.Kind);
                activeAccidentalShapeId = accidental.ShapeId;
                activeAccidentalKind = accidental.Kind;
                isAccidentalExplicit = accidental.ExplicitTargetNoteheadId
                    .Equals(
                        notehead.ShapeId,
                        StringComparison.Ordinal);

                if (isAccidentalExplicit)
                {
                    explicitAccidentals++;
                }
                else
                {
                    inheritedAccidentals++;
                }
            }
            else
            {
                keyOnly++;
            }

            var pitch = FormatPitch(
                diatonicPitch.Step,
                alter,
                diatonicPitch.Octave);
            var confidence = Math.Min(
                notehead.Confidence,
                clef.Confidence);

            if (accidental is not null)
            {
                confidence = Math.Min(
                    confidence,
                    accidental.Confidence);
            }

            var sourceShapeIds = new List<string>
            {
                notehead.ShapeId,
                clef.ShapeId
            };

            sourceShapeIds.AddRange(key.SourceShapeIds);

            if (accidental is not null)
            {
                sourceShapeIds.Add(accidental.ShapeId);
            }

            var accidentalReason = accidental is null
                ? $"no local accidental; key fifths={key.Fifths} gives alter={keyAlter}"
                : $"local {accidental.Kind} {accidental.ShapeId} "
                    + $"{(isAccidentalExplicit ? "explicitly" : "implicitly")} "
                    + $"overrides key alter {keyAlter} with alter={alter}";

            facts.Add(new PitchFact(
                notehead.MeasureNumber,
                notehead.Staff,
                notehead.ShapeId,
                diatonicPitch.Step,
                diatonicPitch.Octave,
                alter,
                pitch,
                notehead.StaffStep,
                clef.Sign,
                clef.Line,
                clef.ShapeId,
                key.Fifths,
                activeAccidentalShapeId,
                activeAccidentalKind,
                isAccidentalExplicit,
                confidence,
                $"staff-step={notehead.StaffStep} under active {clef.Sign}{clef.Line} "
                    + $"({clef.ShapeId}) gives {diatonicPitch.Step}{diatonicPitch.Octave}; "
                    + $"{accidentalReason}; final={pitch}",
                sourceShapeIds
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()));
        }

        facts.AddTrace(
            $"PitchPass: added {noteheads.Length} pitch fact(s); "
            + $"local-explicit={explicitAccidentals}; "
            + $"local-inherited={inheritedAccidentals}; "
            + $"key-only={keyOnly}");
    }

    private static ClefFact ResolveActiveClef(
        NoteheadFact notehead,
        IReadOnlyList<ClefFact> clefs)
    {
        var clef = clefs
            .Where(candidate =>
                candidate.Staff == notehead.Staff
                && (
                    candidate.MeasureNumber < notehead.MeasureNumber
                    || (
                        candidate.MeasureNumber == notehead.MeasureNumber
                        && candidate.X <= notehead.CenterX)))
            .OrderByDescending(candidate => candidate.MeasureNumber)
            .ThenByDescending(candidate => candidate.X)
            .FirstOrDefault();

        return clef
            ?? throw new InvalidDataException(
                $"Cannot resolve active clef for notehead {notehead.ShapeId}: "
                + $"m{notehead.MeasureNumber} staff={notehead.Staff} "
                + $"x={notehead.CenterX:F2}.");
    }

    private static KeySignatureFact ResolveActiveKey(
        NoteheadFact notehead,
        IReadOnlyList<KeySignatureFact> keys)
    {
        var key = keys
            .Where(candidate => candidate.MeasureNumber <= notehead.MeasureNumber)
            .OrderByDescending(candidate => candidate.MeasureNumber)
            .FirstOrDefault();

        return key
            ?? throw new InvalidDataException(
                $"Cannot resolve active key signature for notehead "
                + $"{notehead.ShapeId} in measure {notehead.MeasureNumber}.");
    }

    private static AccidentalFact? ResolveActiveAccidental(
        NoteheadFact notehead,
        IReadOnlyList<AccidentalFact> accidentals)
    {
        var matches = accidentals
            .Where(accidental =>
                accidental.MeasureNumber == notehead.MeasureNumber
                && accidental.Staff == notehead.Staff
                && accidental.AffectedNoteheadIds.Contains(
                    notehead.ShapeId,
                    StringComparer.Ordinal))
            .ToArray();

        if (matches.Length > 1)
        {
            throw new InvalidDataException(
                $"PitchPass found {matches.Length} active accidentals for notehead "
                + $"{notehead.ShapeId}: "
                + string.Join(
                    ", ",
                    matches.Select(match =>
                        $"{match.Kind}/{match.ShapeId}")));
        }

        return matches.SingleOrDefault();
    }

    private static DiatonicPitch ResolveDiatonicPitch(
        int staffStep,
        ClefFact clef)
    {
        var reference = clef.Sign switch
        {
            "G" => new DiatonicPitch("G", 4),
            "F" => new DiatonicPitch("F", 3),
            _ => throw new InvalidDataException(
                $"PitchPass does not support clef {clef.Sign}{clef.Line}.")
        };

        if (clef.Line < 1 || clef.Line > 5)
        {
            throw new InvalidDataException(
                $"Invalid clef line {clef.Line} for {clef.Sign} clef.");
        }

        var clefStaffStep = (5 - clef.Line) * 2;
        var diatonicOffset = clefStaffStep - staffStep;
        var referenceIndex = ToDiatonicIndex(reference);
        var noteIndex = referenceIndex + diatonicOffset;

        return FromDiatonicIndex(noteIndex);
    }

    private static int ResolveKeyAlter(
        string step,
        int fifths)
    {
        if (fifths < -7 || fifths > 7)
        {
            throw new InvalidDataException(
                $"Unsupported key signature fifths={fifths}; expected -7..7.");
        }

        if (fifths > 0)
        {
            return SharpOrder
                .Take(fifths)
                .Contains(step, StringComparer.Ordinal)
                ? 1
                : 0;
        }

        if (fifths < 0)
        {
            return FlatOrder
                .Take(-fifths)
                .Contains(step, StringComparer.Ordinal)
                ? -1
                : 0;
        }

        return 0;
    }

    private static int AlterFor(AccidentalKind kind)
    {
        return kind switch
        {
            AccidentalKind.Flat => -1,
            AccidentalKind.Sharp => 1,
            AccidentalKind.Natural => 0,
            AccidentalKind.DoubleFlat => -2,
            AccidentalKind.DoubleSharp => 2,
            _ => throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "Unsupported accidental kind.")
        };
    }

    private static string FormatPitch(
        string step,
        int alter,
        int octave)
    {
        var accidental = alter switch
        {
            -2 => "bb",
            -1 => "b",
            0 => string.Empty,
            1 => "#",
            2 => "##",
            _ => throw new InvalidDataException(
                $"Cannot format pitch with alter={alter}.")
        };

        return $"{step}{accidental}{octave}";
    }

    private static int ToDiatonicIndex(DiatonicPitch pitch)
    {
        var stepIndex = Array.IndexOf(
            NaturalSteps,
            pitch.Step);

        if (stepIndex < 0)
        {
            throw new InvalidDataException(
                $"Unknown diatonic step {pitch.Step}.");
        }

        return pitch.Octave * 7 + stepIndex;
    }

    private static DiatonicPitch FromDiatonicIndex(int index)
    {
        var octave = (int)Math.Floor(index / 7.0);
        var stepIndex = index - octave * 7;

        return new DiatonicPitch(
            NaturalSteps[stepIndex],
            octave);
    }

    private sealed record DiatonicPitch(
        string Step,
        int Octave);
}
