namespace SvgMusic.Scene;

/// <summary>
/// Central tuning values for geometry, layout and classification.
/// Keep representation-independent heuristics here instead of scattering
/// numeric thresholds throughout the pipeline.
/// </summary>
public sealed record SvgMusicSettings
{
    public static SvgMusicSettings Default { get; } = new();

    public PrototypeClassifierSettings PrototypeClassifier { get; init; } = new();

    public TimeSignatureSettings TimeSignature { get; init; } = new();
}

public sealed record TimeSignatureSettings
{
    /// <summary>
    /// A printed time-signature glyph may extend beyond the outer staff lines,
    /// but its centre should remain close to the staff vertical band. This rejects
    /// text glyphs above/below the staff that happen to classify as TIME_* or
    /// COMMON_TIME/CUT_TIME.
    /// </summary>
    public double VerticalCenterToleranceInSpacings { get; init; } = 0.75;

    /// <summary>
    /// Weight of vertical centring in time-signature hypothesis ranking.
    /// </summary>
    public double VerticalCenterScoreWeight { get; init; } = 0.20;
}

public sealed record PrototypeClassifierSettings
{
    /// <summary>
    /// A generic classified glyph must fit within this fraction of at least one
    /// local measure's width. Text is handled by OCR and long structural marks
    /// by dedicated geometry passes.
    /// </summary>
    public double MaxMeasureWidthFraction { get; init; } = 0.25;

    /// <summary>
    /// A generic classified glyph must fit within this fraction of its staff
    /// pair's full vertical measure height. Treble clefs can occupy slightly
    /// more than half of a grand-staff pair height.
    /// </summary>
    public double MaxMeasureHeightFraction { get; init; } = 0.60;
}
