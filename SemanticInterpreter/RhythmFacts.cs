namespace SvgMusic.Semantics;

public sealed record RestDotAttachmentFact(
    int MeasureNumber,
    int Staff,
    string TargetRestShapeId,
    IReadOnlyList<string> DotShapeIds,
    int Count,
    double Confidence,
    string Reason,
    IReadOnlyList<string> SourceShapeIds)
    : SemanticFact(
        "DotAttachmentPass",
        Reason,
        SourceShapeIds);

public sealed record OnsetFact(
    int MeasureNumber,
    int Staff,
    VoiceTargetKind TargetKind,
    string TargetId,
    int LocalVoice,
    string At,
    double AnchorX,
    double Confidence,
    string Reason,
    IReadOnlyList<string> SourceShapeIds)
    : SemanticFact(
        "OnsetPass",
        Reason,
        SourceShapeIds);

public sealed record OnsetAnalysisResult(
    IReadOnlyList<OnsetFact> Onsets)
{
    public int AnchoredCount => Onsets.Count(onset =>
        onset.Reason.Contains("aligned", StringComparison.OrdinalIgnoreCase));
}
