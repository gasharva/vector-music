using SvgMusic.Canonical;

namespace SvgMusic.Semantics;

public sealed class CanonicalNotationBuilder
{
    public CanonicalNotation Build(
        SemanticDocument document,
        SemanticFacts facts,
        string? title,
        string? composer)
    {
        var clefFacts = facts
            .OfType<ClefFact>()
            .GroupBy(fact => fact.MeasureNumber)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray());
        var timeFacts = facts
            .OfType<TimeSignatureFact>()
            .ToDictionary(
                fact => fact.MeasureNumber);
        var keyFacts = facts
            .OfType<KeySignatureFact>()
            .ToDictionary(
                fact => fact.MeasureNumber);

        var currentClefs = new Dictionary<int, Clef>();
        var measures = new List<Measure>();

        foreach (var measure in document.Measures)
        {
            var clefChanges = new List<Clef>();
            var inspectPrintedClefs = measure.Number == 1 || measure.BreakBefore;

            if (inspectPrintedClefs
                && clefFacts.TryGetValue(measure.Number, out var measureClefs))
            {
                foreach (var staffNumber in new[] { 1, 2 })
                {
                    var selected = SelectSystemStartClef(
                        measure,
                        staffNumber,
                        measureClefs);

                    if (selected is null)
                    {
                        continue;
                    }

                    var clef = new Clef(
                        staffNumber,
                        selected.Sign,
                        selected.Line);

                    if (!currentClefs.TryGetValue(staffNumber, out var current)
                        || current.Sign != clef.Sign
                        || current.Line != clef.Line
                        || current.OctaveChange != clef.OctaveChange)
                    {
                        clefChanges.Add(clef);
                        currentClefs[staffNumber] = clef;

                        facts.AddTrace(
                            $"CanonicalBuilder: m{measure.Number} staff {staffNumber} "
                            + $"clef={clef.Sign}{clef.Line} from {selected.ShapeId}");
                    }
                }
            }

            TimeSignature? time = null;
            KeySignature? key = null;

            if (timeFacts.TryGetValue(measure.Number, out var timeFact))
            {
                time = new TimeSignature(
                    timeFact.Beats,
                    timeFact.BeatType);

                facts.AddTrace(
                    $"CanonicalBuilder: m{measure.Number} "
                    + $"time={timeFact.Beats}/{timeFact.BeatType}");
            }

            if (keyFacts.TryGetValue(measure.Number, out var keyFact))
            {
                key = new KeySignature(keyFact.Fifths);

                facts.AddTrace(
                    $"CanonicalBuilder: m{measure.Number} "
                    + $"key fifths={keyFact.Fifths}");
            }

            MeasureAttributes? attributes = null;

            if (measure.Number == 1
                || clefChanges.Count > 0
                || time is not null
                || key is not null)
            {
                attributes = new MeasureAttributes(
                    Time: time,
                    Key: key,
                    Staves: measure.Number == 1 ? 2 : null,
                    Clefs: clefChanges.Count > 0
                        ? clefChanges
                        : null);
            }

            measures.Add(new Measure(
                measure.Number,
                [],
                attributes,
                Layout: measure.BreakBefore
                    ? new LayoutHint("system")
                    : null));
        }

        return new CanonicalNotation(
            "CanonicalNotation",
            "0.3",
            new Metadata(title, composer),
            [new Part("P1", "Piano", measures)],
            new Relations(
                [],
                [],
                [],
                [],
                [],
                [],
                [],
                []));
    }

    private static ClefFact? SelectSystemStartClef(
        MeasureScene measure,
        int staffNumber,
        IReadOnlyList<ClefFact> facts)
    {
        var width = measure.XEnd - measure.XStart;
        var systemStartLimit = measure.XStart + width * 0.35;

        return facts
            .Where(fact =>
                fact.Staff == staffNumber
                && fact.X <= systemStartLimit)
            .OrderBy(fact => fact.X)
            .ThenByDescending(fact => fact.Confidence)
            .FirstOrDefault();
    }
}
