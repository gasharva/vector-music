namespace SvgMusic.Canonical;

public sealed class CanonicalScoreMerger
{
    public CanonicalNotation Merge(
        IReadOnlyList<CanonicalNotation> pages)
    {
        if (pages.Count == 0)
        {
            throw new ArgumentException(
                "At least one canonical page is required.",
                nameof(pages));
        }

        var first = pages[0];
        var partIds = first.Parts
            .Select(part => part.Id)
            .ToArray();

        foreach (var page in pages.Skip(1))
        {
            var ids = page.Parts
                .Select(part => part.Id)
                .ToArray();

            if (!partIds.SequenceEqual(ids, StringComparer.Ordinal))
            {
                throw new InvalidDataException(
                    "All pages must contain the same parts in the same order.");
            }
        }

        var mergedParts = partIds
            .Select((partId, partIndex) => MergePart(
                pages,
                partId,
                partIndex))
            .ToList();

        var relationPages = new List<PageRelationContext>();
        var measureOffset = 0;

        for (var pageIndex = 0; pageIndex < pages.Count; pageIndex++)
        {
            var page = pages[pageIndex];
            var pageMeasureCount = page.Parts
                .Select(part => part.Measures.Count)
                .DefaultIfEmpty(0)
                .Max();

            relationPages.Add(new PageRelationContext(
                pageIndex + 1,
                measureOffset,
                page));

            measureOffset += pageMeasureCount;
        }

        return new CanonicalNotation(
            "CanonicalNotation",
            "0.4",
            MergeMetadata(pages),
            mergedParts,
            MergeRelations(relationPages));
    }

    private static Part MergePart(
        IReadOnlyList<CanonicalNotation> pages,
        string partId,
        int partIndex)
    {
        var measures = new List<Measure>();
        var measureOffset = 0;

        for (var pageIndex = 0; pageIndex < pages.Count; pageIndex++)
        {
            var page = pages[pageIndex];
            var part = page.Parts[partIndex];
            var pagePrefix = $"p{pageIndex + 1:D3}";
            var eventMap = part.Measures
                .SelectMany(measure => measure.Events)
                .ToDictionary(
                    ev => ev.Id,
                    ev => PrefixEventId(
                        pagePrefix,
                        ev.Id),
                    StringComparer.Ordinal);

            foreach (var measure in part.Measures
                         .OrderBy(item => item.Number))
            {
                var number = measureOffset + measure.Number;
                var events = measure.Events
                    .Select(ev => ev with
                    {
                        Id = eventMap[ev.Id]
                    })
                    .ToList();

                measures.Add(measure with
                {
                    Number = number,
                    Events = events,
                    Layout = pageIndex > 0
                        && measure.Number == part.Measures.Min(item => item.Number)
                            ? new LayoutHint("page")
                            : measure.Layout
                });
            }

            measureOffset += part.Measures.Count;
        }

        return new Part(
            partId,
            pages
                .Select(page => page.Parts[partIndex].Name)
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
                ?? partId,
            measures);
    }

    private static Metadata MergeMetadata(
        IReadOnlyList<CanonicalNotation> pages)
    {
        static string? First(
            IEnumerable<string?> values) =>
            values.FirstOrDefault(value =>
                !string.IsNullOrWhiteSpace(value));

        return new Metadata(
            First(pages.Select(page => page.Metadata.Title)),
            First(pages.Select(page => page.Metadata.Composer)),
            First(pages.Select(page => page.Metadata.Subtitle)));
    }

    private static Relations MergeRelations(
        IReadOnlyList<PageRelationContext> pages)
    {
        var beams = new List<BeamRelation>();
        var ties = new List<TieRelation>();
        var slurs = new List<SlurRelation>();
        var tuplets = new List<TupletRelation>();
        var arpeggios = new List<ArpeggioRelation>();
        var hairpins = new List<SpanRelation>();
        var pedals = new List<SpanRelation>();
        var octaveShifts = new List<SpanRelation>();

        foreach (var context in pages)
        {
            var prefix = $"p{context.PageNumber:D3}";
            string Event(string id) => PrefixEventId(prefix, id);
            string Relation(string kind, string id) =>
                $"{prefix}:{kind}:{id}";

            beams.AddRange(context.Page.Relations.Beams.Select(relation =>
                relation with
                {
                    Id = Relation("beam", relation.Id),
                    Events = relation.Events.Select(Event).ToList()
                }));

            ties.AddRange(context.Page.Relations.Ties.Select(relation =>
                relation with
                {
                    Id = Relation("tie", relation.Id),
                    From = relation.From with
                    {
                        Event = Event(relation.From.Event)
                    },
                    To = relation.To with
                    {
                        Event = Event(relation.To.Event)
                    }
                }));

            slurs.AddRange(context.Page.Relations.Slurs.Select(relation =>
                relation with
                {
                    Id = Relation("slur", relation.Id),
                    From = Event(relation.From),
                    To = Event(relation.To)
                }));

            tuplets.AddRange(context.Page.Relations.Tuplets.Select(relation =>
                relation with
                {
                    Id = Relation("tuplet", relation.Id),
                    Events = relation.Events.Select(Event).ToList()
                }));

            arpeggios.AddRange(context.Page.Relations.Arpeggios.Select(relation =>
                relation with
                {
                    Id = Relation("arpeggio", relation.Id),
                    Events = relation.Events.Select(Event).ToList()
                }));

            hairpins.AddRange(context.Page.Relations.Hairpins.Select(relation =>
                ShiftSpan(context, "hairpin", relation)));
            pedals.AddRange(context.Page.Relations.Pedals.Select(relation =>
                ShiftSpan(context, "pedal", relation)));
            octaveShifts.AddRange(context.Page.Relations.OctaveShifts.Select(relation =>
                ShiftSpan(context, "ottava", relation)));
        }

        return new Relations(
            beams,
            ties,
            slurs,
            tuplets,
            arpeggios,
            hairpins,
            pedals,
            octaveShifts);
    }

    private static SpanRelation ShiftSpan(
        PageRelationContext context,
        string kind,
        SpanRelation relation)
    {
        return relation with
        {
            Id = $"p{context.PageNumber:D3}:{kind}:{relation.Id}",
            From = relation.From with
            {
                Measure = relation.From.Measure + context.MeasureOffset
            },
            To = relation.To with
            {
                Measure = relation.To.Measure + context.MeasureOffset
            }
        };
    }

    private static string PrefixEventId(
        string pagePrefix,
        string eventId) =>
        $"{pagePrefix}:{eventId}";

    private sealed record PageRelationContext(
        int PageNumber,
        int MeasureOffset,
        CanonicalNotation Page);
}
