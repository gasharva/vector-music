namespace SvgMusic.Semantics;

public sealed class KeySignaturePass : ISemanticPass
{
    private const double MinimumClassificationConfidence = 0.70;
    private const double MaximumStepError = 0.90;

    private static readonly IReadOnlyDictionary<string, double[]> TreblePatterns =
        new Dictionary<string, double[]>(StringComparer.Ordinal)
        {
            ["sharp"] = [0, 3, -1, 2, 5, 1, 4],
            ["flat"] = [4, 1, 5, 2, 6, 3, 7]
        };

    private static readonly IReadOnlyDictionary<string, double[]> BassPatterns =
        new Dictionary<string, double[]>(StringComparer.Ordinal)
        {
            ["sharp"] = [2, 5, 1, 4, 0, 3, 6],
            ["flat"] = [6, 3, 7, 4, 8, 5, 9]
        };

    public string Name => nameof(KeySignaturePass);

    public void Run(
        SemanticDocument document,
        SemanticFacts facts)
    {
        var firstMeasure = document.Measures
            .OrderBy(measure => measure.Number)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                "KeySignaturePass requires at least one measure.");

        var time = facts
            .OfType<TimeSignatureFact>()
            .SingleOrDefault(fact => fact.MeasureNumber == firstMeasure.Number)
            ?? throw new InvalidDataException(
                "KeySignaturePass requires TimeSignaturePass to run first.");

        var upper = ReadStaffKey(
            firstMeasure,
            firstMeasure.Upper,
            time,
            facts);
        var lower = ReadStaffKey(
            firstMeasure,
            firstMeasure.Lower,
            time,
            facts);

        if (upper.Fifths != lower.Fifths)
        {
            throw new InvalidDataException(
                $"Key signature disagreement between staves in measure "
                + $"{firstMeasure.Number}: upper fifths={upper.Fifths}, "
                + $"lower fifths={lower.Fifths}.");
        }

        var sourceShapeIds = upper.SourceShapeIds
            .Concat(lower.SourceShapeIds)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        facts.Add(new KeySignatureFact(
            firstMeasure.Number,
            upper.Fifths,
            upper.Kind,
            Math.Abs(upper.Fifths),
            Math.Min(upper.MinX, lower.MinX),
            Math.Max(upper.MaxX, lower.MaxX),
            upper.Fifths == 0
                ? "No key-signature symbols exist between clef and time signature on either staff; fifths=0."
                : $"Both staves independently match the canonical {upper.Kind} key-signature "
                    + $"position sequence with {Math.Abs(upper.Fifths)} symbol(s); fifths={upper.Fifths}.",
            sourceShapeIds));
    }

    private static StaffKey ReadStaffKey(
        MeasureScene measure,
        StaffMeasureScene staff,
        TimeSignatureFact time,
        SemanticFacts facts)
    {
        var clef = facts
            .OfType<ClefFact>()
            .Where(fact =>
                fact.MeasureNumber == measure.Number
                && fact.Staff == staff.StaffNumber)
            .OrderBy(fact => fact.X)
            .FirstOrDefault()
            ?? throw new InvalidDataException(
                $"Cannot read key signature in measure {measure.Number}, "
                + $"staff {staff.StaffNumber}: no clef fact exists.");

        var clefElement = staff.Elements
            .FirstOrDefault(element => element.ShapeId == clef.ShapeId)
            ?? throw new InvalidDataException(
                $"Clef source {clef.ShapeId} is absent from semantic staff scene.");

        var zoneLeft = clefElement.Bounds.MaxX;
        var zoneRight = time.MinX;

        if (zoneRight <= zoneLeft)
        {
            throw new InvalidDataException(
                $"Invalid key-signature search zone in measure {measure.Number}, "
                + $"staff {staff.StaffNumber}: clefRight={zoneLeft:F2}, "
                + $"timeLeft={zoneRight:F2}.");
        }

        var candidates = staff.Elements
            .OfType<ShapeElement>()
            .Where(shape =>
                shape.CenterX > zoneLeft
                && shape.CenterX < zoneRight
                && IsPlausibleKeyGlyph(shape, staff.LineSpacing))
            .OrderBy(shape => shape.CenterX)
            .ThenBy(shape => shape.CenterY)
            .ToArray();

        if (candidates.Length == 0)
        {
            return new StaffKey(
                0,
                "none",
                zoneLeft,
                zoneLeft,
                []);
        }

        if (candidates.Length > 7)
        {
            throw new InvalidDataException(
                $"Suspicious key signature in measure {measure.Number}, "
                + $"staff {staff.StaffNumber}: found {candidates.Length} glyphs "
                + $"between clef and time signature; maximum is 7.");
        }

        var classifiedKinds = candidates
            .Select(GetClassifiedAccidentalKind)
            .Where(kind => kind is not null)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (classifiedKinds.Contains("invalid", StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                $"Unsupported accidental type inside initial key signature in measure "
                + $"{measure.Number}, staff {staff.StaffNumber}.");
        }

        var musicalKinds = classifiedKinds
            .Where(kind => kind is "flat" or "sharp")
            .ToArray();

        if (musicalKinds.Length > 1)
        {
            throw new InvalidDataException(
                $"Mixed flats and sharps inside key signature in measure "
                + $"{measure.Number}, staff {staff.StaffNumber}.");
        }

        var geometricKind = InferKindFromPositions(
            candidates,
            staff,
            clef.Sign);

        if (geometricKind is null)
        {
            throw new InvalidDataException(
                $"Key-signature glyphs in measure {measure.Number}, "
                + $"staff {staff.StaffNumber} do not match the canonical "
                + $"flat or sharp vertical sequence.");
        }

        if (musicalKinds.Length == 1
            && musicalKinds[0] != geometricKind)
        {
            throw new InvalidDataException(
                $"Key-signature classifier/geometry disagreement in measure "
                + $"{measure.Number}, staff {staff.StaffNumber}: classifier says "
                + $"{musicalKinds[0]}, positions say {geometricKind}.");
        }

        ValidateClassifiedGlyphs(
            candidates,
            geometricKind,
            measure.Number,
            staff.StaffNumber);

        var count = candidates.Length;
        var fifths = geometricKind == "sharp"
            ? count
            : -count;

        return new StaffKey(
            fifths,
            geometricKind,
            candidates.Min(candidate => candidate.Bounds.MinX),
            candidates.Max(candidate => candidate.Bounds.MaxX),
            candidates.Select(candidate => candidate.ShapeId).ToArray());
    }

    private static bool IsPlausibleKeyGlyph(
        ShapeElement shape,
        double lineSpacing)
    {
        if (lineSpacing <= 0)
        {
            return false;
        }

        var classification = shape.Classification;
        var classifiedAsAccidental = classification is not null
            && classification.Confidence >= MinimumClassificationConfidence
            && classification.Label is "FLAT" or "SHARP" or "NATURAL"
                or "DOUBLE_FLAT" or "DOUBLE_SHARP";

        if (classifiedAsAccidental)
        {
            return true;
        }

        return shape.Bounds.Width <= lineSpacing * 1.8
            && shape.Bounds.Height >= lineSpacing * 1.0
            && shape.Bounds.Height <= lineSpacing * 3.8;
    }

    private static string? GetClassifiedAccidentalKind(ShapeElement shape)
    {
        var classification = shape.Classification;

        if (classification is null
            || classification.Confidence < MinimumClassificationConfidence)
        {
            return null;
        }

        return classification.Label switch
        {
            "FLAT" => "flat",
            "SHARP" => "sharp",
            "NATURAL" => "invalid",
            "DOUBLE_FLAT" => "invalid",
            "DOUBLE_SHARP" => "invalid",
            _ => null
        };
    }

    private static string? InferKindFromPositions(
        IReadOnlyList<ShapeElement> candidates,
        StaffMeasureScene staff,
        string clefSign)
    {
        var patterns = clefSign switch
        {
            "G" => TreblePatterns,
            "F" => BassPatterns,
            _ => throw new InvalidDataException(
                $"KeySignaturePass currently supports G and F clefs only, got {clefSign}.")
        };

        var matches = new List<(string Kind, double Error)>();

        foreach (var pair in patterns)
        {
            var pattern = pair.Value;
            var maxError = 0.0;
            var sumError = 0.0;
            var halfSpacing = staff.LineSpacing / 2.0;

            for (var index = 0; index < candidates.Count; index++)
            {
                var expectedY = staff.StaffBounds.MinY
                    + pattern[index] * halfSpacing;
                var stepError = Math.Abs(
                    candidates[index].CenterY - expectedY) / halfSpacing;

                maxError = Math.Max(maxError, stepError);
                sumError += stepError;
            }

            if (maxError <= MaximumStepError)
            {
                matches.Add((pair.Key, sumError / candidates.Count));
            }
        }

        return matches
            .OrderBy(match => match.Error)
            .ThenBy(match => match.Kind, StringComparer.Ordinal)
            .Select(match => match.Kind)
            .FirstOrDefault();
    }

    private static void ValidateClassifiedGlyphs(
        IReadOnlyList<ShapeElement> candidates,
        string expectedKind,
        int measureNumber,
        int staffNumber)
    {
        foreach (var candidate in candidates)
        {
            var kind = GetClassifiedAccidentalKind(candidate);

            if (kind is null)
            {
                continue;
            }

            if (kind == "invalid")
            {
                throw new InvalidDataException(
                    $"Unexpected {candidate.Classification!.Label} in initial key "
                    + $"signature, measure {measureNumber}, staff {staffNumber}.");
            }

            if (kind != expectedKind)
            {
                throw new InvalidDataException(
                    $"Mixed or misplaced key accidental {candidate.ShapeId}: "
                    + $"expected {expectedKind}, classifier says {kind}.");
            }
        }
    }

    private sealed record StaffKey(
        int Fifths,
        string Kind,
        double MinX,
        double MaxX,
        IReadOnlyList<string> SourceShapeIds);
}
