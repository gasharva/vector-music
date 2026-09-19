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
