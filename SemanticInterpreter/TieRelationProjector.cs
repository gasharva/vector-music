using SvgMusic.Canonical;

namespace SvgMusic.Semantics;

internal static class TieRelationProjector
{
    public static List<TieRelation> Build(
        SemanticFacts facts,
        IReadOnlyDictionary<string, string> noteheadToEventId)
    {
        var result = new List<TieRelation>();

        foreach (var tie in facts
                     .OfType<TieFact>()
                     .OrderBy(tie => tie.StartMeasureNumber)
                     .ThenBy(tie => tie.CurveShapeId, StringComparer.Ordinal))
        {
            if (!noteheadToEventId.TryGetValue(tie.FromNoteheadId, out var fromEvent)
                || !noteheadToEventId.TryGetValue(tie.ToNoteheadId, out var toEvent)
                || string.Equals(fromEvent, toEvent, StringComparison.Ordinal))
            {
                continue;
            }

            result.Add(new TieRelation(
                $"tie-{tie.CurveShapeId}",
                new NoteAnchor(fromEvent, tie.Pitch),
                new NoteAnchor(toEvent, tie.Pitch),
                tie.Placement));
        }

        return result;
    }
}
