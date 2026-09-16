namespace SvgMusic.Semantics;

/// <summary>
/// Refines local voice assignments after the first onset reconstruction.
///
/// Stem direction is strong evidence at a simultaneous split, but it is not a
/// permanent voice identity. A common piano engraving pattern is:
///
///   sustained note -> rest + second voice -> same pitch continues
///
/// where the sustained lead-in may have a down stem even though it belongs to the
/// upper/local voice 1. When a rest is horizontally aligned with another voice and
/// the same pitch appears immediately before/after that split, continuity wins over
/// the isolated stem direction. Onsets are then recomputed from the corrected voices.
/// </summary>
public sealed class VoiceContinuityPass : ISemanticPass
{
    private const double SplitAlignmentInSpacings = 0.78;

    public string Name => nameof(VoiceContinuityPass);

    public void Run(
        SemanticDocument document,
        SemanticFacts facts)
    {
        var voices = facts.OfType<VoiceFact>().ToArray();
        if (voices.Length == 0)
        {
            facts.AddTrace("VoiceContinuityPass: no VoiceFact input");
            return;
        }

        var pitches = facts.OfType<PitchFact>()
            .GroupBy(pitch => new NoteKey(
                pitch.MeasureNumber,
                pitch.NoteheadId))
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(pitch => pitch.Confidence).First());
        var corrections = new List<(VoiceFact Original, VoiceFact Replacement)>();

        foreach (var measure in document.Measures)
        {
            foreach (var staffNumber in new[] { 1, 2 })
            {
                var staff = staffNumber == 1
                    ? measure.Upper
                    : measure.Lower;
                var spacing = Math.Max(staff.LineSpacing, 0.001);
                var group = voices
                    .Where(voice =>
                        voice.MeasureNumber == measure.Number
                        && voice.Staff == staffNumber)
                    .OrderBy(voice => voice.AnchorX)
                    .ThenBy(voice => voice.TargetId, StringComparer.Ordinal)
                    .ToArray();

                if (group.Select(voice => voice.LocalVoice).Distinct().Count() < 2)
                {
                    continue;
                }

                var rests = group
                    .Where(voice => voice.TargetKind == VoiceTargetKind.Rest)
                    .ToArray();
                var pitched = group
                    .Where(voice => voice.TargetKind != VoiceTargetKind.Rest)
                    .ToArray();

                foreach (var rest in rests)
                {
                    var splitPartner = pitched
                        .Where(voice => voice.LocalVoice != rest.LocalVoice)
                        .Where(voice => Math.Abs(voice.AnchorX - rest.AnchorX)
                            <= spacing * SplitAlignmentInSpacings)
                        .OrderBy(voice => Math.Abs(voice.AnchorX - rest.AnchorX))
                        .ThenBy(voice => voice.TargetId, StringComparer.Ordinal)
                        .FirstOrDefault();
                    if (splitPartner is null)
                    {
                        continue;
                    }

                    var following = pitched
                        .Where(voice =>
                            voice.TargetKind == VoiceTargetKind.Notehead
                            && voice.LocalVoice == rest.LocalVoice
                            && voice.AnchorX > rest.AnchorX + 1e-6)
                        .Select(voice => new
                        {
                            Voice = voice,
                            Pitch = FindPitch(pitches, voice)
                        })
                        .Where(item => item.Pitch is not null)
                        .OrderBy(item => item.Voice.AnchorX)
                        .ThenBy(item => item.Voice.TargetId, StringComparer.Ordinal)
                        .FirstOrDefault();
                    if (following?.Pitch is null)
                    {
                        continue;
                    }

                    var preceding = pitched
                        .Where(voice =>
                            voice.TargetKind == VoiceTargetKind.Notehead
                            && voice.LocalVoice != rest.LocalVoice
                            && voice.AnchorX < rest.AnchorX - 1e-6)
                        .Select(voice => new
                        {
                            Voice = voice,
                            Pitch = FindPitch(pitches, voice)
                        })
                        .Where(item => item.Pitch is not null
                            && string.Equals(
                                item.Pitch.Pitch,
                                following.Pitch.Pitch,
                                StringComparison.Ordinal))
                        .OrderByDescending(item => item.Voice.AnchorX)
                        .ThenBy(item => item.Voice.TargetId, StringComparer.Ordinal)
                        .FirstOrDefault();
                    if (preceding is null)
                    {
                        continue;
                    }

                    var interveningInRestVoice = pitched.Any(voice =>
                        voice.LocalVoice == rest.LocalVoice
                        && voice.AnchorX > preceding.Voice.AnchorX + 1e-6
                        && voice.AnchorX < rest.AnchorX - 1e-6);
                    if (interveningInRestVoice)
                    {
                        continue;
                    }

                    var replacement = preceding.Voice with
                    {
                        LocalVoice = rest.LocalVoice,
                        Confidence = Math.Min(preceding.Voice.Confidence, 0.93),
                        Reason = preceding.Voice.Reason
                            + $"; voice continuity: {following.Pitch.Pitch} continues through "
                            + $"rest {rest.TargetId}; lead-in reassigned to local voice {rest.LocalVoice}"
                    };
                    corrections.Add((preceding.Voice, replacement));
                }
            }
        }

        foreach (var correction in corrections
                     .GroupBy(item => new VoiceKey(
                         item.Original.MeasureNumber,
                         item.Original.Staff,
                         item.Original.TargetKind,
                         item.Original.TargetId))
                     .Select(group => group.First()))
        {
            facts.Replace(
                correction.Original,
                correction.Replacement);
        }

        if (corrections.Count > 0)
        {
            var removed = facts.RemoveWhere<OnsetFact>(_ => true);
            new OnsetPass().Run(document, facts);
            facts.AddTrace(
                $"VoiceContinuityPass: corrected={corrections.Count}; "
                + $"old-onsets-removed={removed}; onsets-recomputed=True");
        }
        else
        {
            facts.AddTrace("VoiceContinuityPass: corrected=0; onsets-recomputed=False");
        }
    }

    private static PitchFact? FindPitch(
        IReadOnlyDictionary<NoteKey, PitchFact> pitches,
        VoiceFact voice)
    {
        return pitches.TryGetValue(
                new NoteKey(
                    voice.MeasureNumber,
                    voice.TargetId),
                out var pitch)
            ? pitch
            : null;
    }

    private readonly record struct NoteKey(
        int MeasureNumber,
        string NoteheadId);

    private readonly record struct VoiceKey(
        int MeasureNumber,
        int Staff,
        VoiceTargetKind TargetKind,
        string TargetId);
}
