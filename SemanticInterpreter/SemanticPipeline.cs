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

    public ClassifiedSymbolAnalysisResult? LastClassifiedSymbolAnalysis =>
        _passes
            .OfType<ClassifiedSymbolPass>()
            .SingleOrDefault()
            ?.LastAnalysis;

    public SemanticPipeline(IEnumerable<ISemanticPass> passes)
    {
        var materialized = passes.ToList();
        var deferredTextPasses = materialized
            .Where(pass => pass is TextPass)
            .ToArray();
        materialized.RemoveAll(pass => pass is TextPass);


        // Grace heads are not a parallel note-recognition path. NoteheadPass first
        // accepts every notehead; this pass only tags a reduced-size subcluster so
        // pitch, stems, beams and accidentals keep using the normal facts.
        if (materialized.Any(pass => pass is NoteheadPass)
            && materialized.All(pass => pass is not GraceNotePass))
        {
            var noteheadIndex = materialized.FindLastIndex(pass => pass is NoteheadPass);
            materialized.Insert(noteheadIndex + 1, new GraceNotePass());
        }

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

        // A rest physically drawn between the piano staves can be geometrically
        // closer to the wrong staff. Repair that ambiguity after durations/chords
        // exist but before voice inference consumes the RestFact staff.
        if (materialized.Any(pass => pass is VoicePass)
            && materialized.All(pass => pass is not InterstaffRestPass))
        {
            var voiceIndex = materialized.FindIndex(pass => pass is VoicePass);
            materialized.Insert(
                voiceIndex,
                new InterstaffRestPass());
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

        // When stem-based voices still produce an overfull measure, repartition
        // events into the minimum feasible rhythmic lanes and recompute onsets.
        if (materialized.Any(pass => pass is VoiceContinuityPass)
            && materialized.All(pass => pass is not RhythmicLanePass))
        {
            var continuityIndex = materialized.FindLastIndex(pass => pass is VoiceContinuityPass);
            materialized.Insert(
                continuityIndex + 1,
                new RhythmicLanePass());
        }

        // Piano engraving aligns rhythmic columns between the two staves. A proven
        // internal onset on one staff can therefore recover an implicit leading gap
        // in a voice on the other staff.
        if (materialized.Any(pass => pass is RhythmicLanePass)
            && materialized.All(pass => pass is not CrossStaffOnsetRefinementPass))
        {
            var laneIndex = materialized.FindLastIndex(pass => pass is RhythmicLanePass);
            materialized.Insert(
                laneIndex + 1,
                new CrossStaffOnsetRefinementPass());
        }

        // A single secondary sustained event can have no exact cross-voice x anchor.
        // After all voice refinements are stable, use the barline as an additional
        // timing constraint when geometry clearly supports an end-of-measure fit.
        if (materialized.Any(pass => pass is OnsetPass)
            && materialized.All(pass => pass is not MeasureEndOnsetRefinementPass))
        {
            var crossStaffIndex = materialized.FindLastIndex(pass => pass is CrossStaffOnsetRefinementPass);
            var laneIndex = materialized.FindLastIndex(pass => pass is RhythmicLanePass);
            var continuityIndex = materialized.FindLastIndex(pass => pass is VoiceContinuityPass);
            var insertionIndex = crossStaffIndex >= 0
                ? crossStaffIndex + 1
                : laneIndex >= 0
                    ? laneIndex + 1
                    : continuityIndex >= 0
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

        // Vertical zigzags are already proven geometrically. ArpeggioPass only
        // decides which already-built chord event(s) at one onset they span.
        if (materialized.Any(pass => pass is OnsetPass)
            && materialized.Any(pass => pass is ChordPass)
            && materialized.All(pass => pass is not ArpeggioPass))
        {
            var hairpinIndex = materialized.FindLastIndex(pass => pass is HairpinPass);
            var insertionIndex = hairpinIndex >= 0
                ? hairpinIndex + 1
                : materialized.FindLastIndex(pass => pass is OnsetPass) + 1;
            materialized.Insert(
                insertionIndex,
                new ArpeggioPass());
        }

        // Curves split by a system break are still present as raw CurvedStroke
        // fragments, but neither half has two real note endpoints. Reconstruct a
        // logical complete curve first; SlurPass/TiePass can then classify it with
        // exactly the same pitch/voice semantics as every ordinary curve.
        if (materialized.Any(pass => pass is NoteheadPass)
            && materialized.All(pass => pass is not CrossSystemCurvePass))
        {
            var existingSlurIndex = materialized.FindIndex(pass => pass is SlurPass);
            var existingTieIndex = materialized.FindIndex(pass => pass is TiePass);
            var insertionIndex = existingSlurIndex >= 0
                ? existingSlurIndex
                : existingTieIndex >= 0
                    ? existingTieIndex
                    : materialized.Count;

            materialized.Insert(
                insertionIndex,
                new CrossSystemCurvePass());
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

        // If SlurPass was supplied explicitly, keep the cross-system reconstruction
        // immediately before it even when the caller's pass order was unusual.
        var crossSystemCurveIndex = materialized.FindIndex(
            pass => pass is CrossSystemCurvePass);
        var slurPassIndex = materialized.FindIndex(
            pass => pass is SlurPass);
        if (crossSystemCurveIndex >= 0
            && slurPassIndex >= 0
            && crossSystemCurveIndex > slurPassIndex)
        {
            var pass = materialized[crossSystemCurveIndex];
            materialized.RemoveAt(crossSystemCurveIndex);
            slurPassIndex = materialized.FindIndex(
                item => item is SlurPass);
            materialized.Insert(
                slurPassIndex,
                pass);
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

        // Text semantics deliberately runs after all music passes, including the
        // residual classified-symbol pass. It may inspect music evidence but never
        // removes or rewrites already accepted musical facts.
        materialized.AddRange(deferredTextPasses);

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

            if (pass is ClassifiedSymbolPass classified
                && classified.LastAnalysis is not null)
            {
                foreach (var decision in classified.LastAnalysis.Decisions)
                {
                    var line =
                        $"ClassifiedSymbol decision: shape={decision.ShapeId}; "
                        + $"label={decision.Label}; accepted={decision.Accepted}; "
                        + $"decision={decision.Decision}; m={decision.MeasureNumber}; "
                        + $"staff={decision.Staff}; at={decision.At ?? "-"}; "
                        + $"confidence={decision.Confidence:P1}; reason={decision.Reason}";
                    facts.AddTrace(line);
                    Console.WriteLine(line);
                }
            }
        }

        return facts;
    }
}
