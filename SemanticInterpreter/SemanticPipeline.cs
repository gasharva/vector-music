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

        // Some overlapping voices have no simultaneous x-position to trigger the
        // primary VoicePass. Rhythmic overfill plus opposite stems can still prove
        // that a provisional single voice must be split before onset reconstruction.
        if (materialized.Any(pass => pass is VoicePass)
            && materialized.All(pass => pass is not MeasureFitVoicePass))
        {
            var voiceIndex = materialized.FindLastIndex(pass => pass is VoicePass);
            materialized.Insert(
                voiceIndex + 1,
                new MeasureFitVoicePass());
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

        // A single secondary sustained event can have no exact cross-voice x anchor.
        // After all voice refinements are stable, use the barline as an additional
        // timing constraint when geometry clearly supports an end-of-measure fit.
        if (materialized.Any(pass => pass is OnsetPass)
            && materialized.All(pass => pass is not MeasureEndOnsetRefinementPass))
        {
            var continuityIndex = materialized.FindLastIndex(pass => pass is VoiceContinuityPass);
            var insertionIndex = continuityIndex >= 0
                ? continuityIndex + 1
                : materialized.FindLastIndex(pass => pass is OnsetPass) + 1;
            materialized.Insert(
                insertionIndex,
                new MeasureEndOnsetRefinementPass());
        }

        // Ottava semantics needs two already independent signals: a classified OTTAVA
        // glyph and a generic bracket spanner. Run it only after rhythmic onsets have
        // stabilized so geometric span endpoints can be projected onto musical time.
        if (materialized.Any(pass => pass is OnsetPass)
            && materialized.All(pass => pass is not OttavaPass))
        {
            var refinementIndex = materialized.FindLastIndex(pass => pass is MeasureEndOnsetRefinementPass);
            var insertionIndex = refinementIndex >= 0
                ? refinementIndex + 1
                : materialized.FindLastIndex(pass => pass is OnsetPass) + 1;
            materialized.Insert(
                insertionIndex,
                new OttavaPass());
        }

        // Pedal semantics mirrors ottava: a classified PEDAL_MARK plus a generic
        // solid bracket becomes a timed span only after rhythmic onsets are stable.
        if (materialized.Any(pass => pass is OnsetPass)
            && materialized.All(pass => pass is not PedalPass))
        {
            var ottavaIndex = materialized.FindLastIndex(pass => pass is OttavaPass);
            var refinementIndex = materialized.FindLastIndex(pass => pass is MeasureEndOnsetRefinementPass);
            var insertionIndex = ottavaIndex >= 0
                ? ottavaIndex + 1
                : refinementIndex >= 0
                    ? refinementIndex + 1
                    : materialized.FindLastIndex(pass => pass is OnsetPass) + 1;
            materialized.Insert(
                insertionIndex,
                new PedalPass());
        }

        // Hairpins are already proven geometrically by HairpinExtractor. Once onsets
        // are stable, only their horizontal endpoints need projection onto musical time.
        if (materialized.Any(pass => pass is OnsetPass)
            && materialized.All(pass => pass is not HairpinPass))
        {
            var pedalIndex = materialized.FindLastIndex(pass => pass is PedalPass);
            var ottavaIndex = materialized.FindLastIndex(pass => pass is OttavaPass);
            var refinementIndex = materialized.FindLastIndex(pass => pass is MeasureEndOnsetRefinementPass);
            var insertionIndex = pedalIndex >= 0
                ? pedalIndex + 1
                : ottavaIndex >= 0
                    ? ottavaIndex + 1
                    : refinementIndex >= 0
                        ? refinementIndex + 1
                        : materialized.FindLastIndex(pass => pass is OnsetPass) + 1;
            materialized.Insert(
                insertionIndex,
                new HairpinPass());
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

        // Curves crossing a barline can be logically owned by only one of its two
        // measures, and ledger curves can also sit in the neighbouring staff band.
        // Recovery therefore uses global page geometry after the normal classifiers.
        if (materialized.Any(pass => pass is TiePass)
            && materialized.All(pass => pass is not TieSpatialRecoveryPass))
        {
            var tieIndex = materialized.FindLastIndex(pass => pass is TiePass);
            materialized.Insert(
                tieIndex + 1,
                new TieSpatialRecoveryPass());
        }

        // Simple classifier leftovers run last, after every specialized pass has had
        // a chance to claim its source shapes through SemanticFact.SourceShapeIds.
        if (materialized.Any(pass => pass is NoteheadPass)
            && materialized.All(pass => pass is not ClassifiedSymbolPass))
        {
            materialized.Add(new ClassifiedSymbolPass());
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
