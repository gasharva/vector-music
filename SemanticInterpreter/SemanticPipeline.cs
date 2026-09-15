namespace SvgMusic.Semantics;

public interface ISemanticPass
{
    string Name { get; }

    void Run(
        SemanticDocument document,
        SemanticFacts facts);
}

public sealed class SemanticPipeline
{
    private readonly IReadOnlyList<ISemanticPass> _passes;

    public SemanticPipeline(IEnumerable<ISemanticPass> passes)
    {
        var materialized = passes.ToList();

        // Rest facts must exist before DotAttachmentPass: augmentation dots can
        // belong to rests as well as noteheads. If callers do not position RestPass
        // explicitly, insert it immediately before dots when possible.
        if (materialized.Any(pass => pass is NoteheadPass)
            && materialized.All(pass => pass is not RestPass))
        {
            var dotIndex = materialized.FindIndex(pass => pass is DotAttachmentPass);
            if (dotIndex >= 0)
            {
                materialized.Insert(dotIndex, new RestPass());
            }
            else
            {
                materialized.Add(new RestPass());
            }
        }

        if (materialized.Any(pass => pass is NoteheadPass)
            && materialized.All(pass => pass is not DurationPass))
        {
            materialized.Add(new DurationPass());
        }

        // ChordPass knows which visually separate heads are one rhythmic event. Use
        // that fact to repair missed per-head augmentation dots before voices/onsets.
        if (materialized.Any(pass => pass is ChordPass)
            && materialized.All(pass => pass is not ChordDurationNormalizationPass))
        {
            var chordIndex = materialized.FindLastIndex(pass => pass is ChordPass);
            materialized.Insert(
                chordIndex + 1,
                new ChordDurationNormalizationPass());
        }

        // Voice inference needs both pitched/chord facts and rests.
        if (materialized.Any(pass => pass is NoteheadPass)
            && materialized.All(pass => pass is not VoicePass))
        {
            materialized.Add(new VoicePass());
        }

        // Onsets consume duration, chord, rest and voice assignments and recover
        // exact positions inside the measure.
        if (materialized.Any(pass => pass is NoteheadPass)
            && materialized.All(pass => pass is not OnsetPass))
        {
            materialized.Add(new OnsetPass());
        }

        // Stem direction is local engraving evidence, not a permanent voice id.
        // Refine rest-bridged pitch continuity and recompute onsets when necessary.
        if (materialized.Any(pass => pass is OnsetPass)
            && materialized.All(pass => pass is not VoiceContinuityPass))
        {
            var onsetIndex = materialized.FindLastIndex(pass => pass is OnsetPass);
            materialized.Insert(
                onsetIndex + 1,
                new VoiceContinuityPass());
        }

        // Curves are already extracted geometrically. SlurPass classifies their
        // pitched endpoints first and deliberately reserves same-pitch arches.
        if (materialized.Any(pass => pass is NoteheadPass)
            && materialized.All(pass => pass is not SlurPass))
        {
            var tieIndex = materialized.FindIndex(pass => pass is TiePass);
            if (tieIndex >= 0)
            {
                materialized.Insert(tieIndex, new SlurPass());
            }
            else
            {
                materialized.Add(new SlurPass());
            }
        }

        // TiePass consumes the same-pitch arc hypotheses after SlurPass has claimed
        // genuine slurs, so one raw curve cannot become both relations.
        if (materialized.Any(pass => pass is NoteheadPass)
            && materialized.All(pass => pass is not TiePass))
        {
            materialized.Add(new TiePass());
        }

        // Ledger-note tie curves can be owned by the neighbouring staff even though
        // both note endpoints belong to the same musical staff. Recover only curves
        // left unclaimed by the normal slur/tie classifiers.
        if (materialized.Any(pass => pass is TiePass)
            && materialized.All(pass => pass is not TieOwnershipRecoveryPass))
        {
            var tieIndex = materialized.FindLastIndex(pass => pass is TiePass);
            materialized.Insert(
                tieIndex + 1,
                new TieOwnershipRecoveryPass());
        }

        _passes = materialized;
    }

    public SemanticFacts Run(SemanticDocument document)
    {
        var facts = new SemanticFacts();

        foreach (var pass in _passes)
        {
            var before = facts.Items.Count;
            pass.Run(document, facts);
            var added = facts.Items.Count - before;

            facts.AddTrace(
                $"{pass.Name}: added {added} fact(s); total={facts.Items.Count}");
        }

        return facts;
    }
}
