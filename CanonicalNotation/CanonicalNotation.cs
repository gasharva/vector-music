using System.Text.Json.Serialization;

namespace SvgMusic.Canonical;

public sealed record CanonicalNotation(
    string Format,
    string Version,
    Metadata Metadata,
    List<Part> Parts,
    Relations Relations);

public sealed record Metadata(string? Title = null, string? Composer = null);

public sealed record Part(string Id, string Name, List<Measure> Measures);

public sealed record Measure(
    int Number,
    List<CanonicalEvent> Events,
    MeasureAttributes? Attributes = null,
    string? LeftBarline = null,
    string? RightBarline = null,
    LayoutHint? Layout = null,
    RepeatMark? LeftRepeat = null,
    RepeatMark? RightRepeat = null);

public sealed record MeasureAttributes(
    TimeSignature? Time = null,
    KeySignature? Key = null,
    int? Staves = null,
    List<Clef>? Clefs = null,
    string? At = null);

public sealed record TimeSignature(int Beats, int BeatType);
public sealed record KeySignature(int Fifths, string? Mode = null);
public sealed record Clef(int Staff, string Sign, int Line, int? OctaveChange = null);
public sealed record LayoutHint(string BreakBefore);
public sealed record RepeatMark(string Direction, int? Times = null);

/// <summary>
/// One JSON event shape on purpose: test fixtures remain easy to read and diff.
/// Fields irrelevant to a particular Type stay null and are omitted from JSON.
///
/// For chord events Staff is intentionally null: each notehead carries its own Staff,
/// which allows one chord to span multiple staves. Staff remains event-level for rests
/// and directions because those objects are attached to one staff as a whole.
/// </summary>
public sealed record CanonicalEvent
{
    public required string Id { get; init; }
    public required string Type { get; init; } // chord, rest, text, dynamic, tempo, navigation
    public required string At { get; init; }
    public int? Staff { get; init; }
    public int? Voice { get; init; }
    public string? Duration { get; init; }
    public bool? Grace { get; init; }
    public List<CanonicalNote>? Notes { get; init; }
    public EventNotation? Notation { get; init; }
    public string? Text { get; init; }
    public string? Value { get; init; }
    public string? Target { get; init; }
    public string? Placement { get; init; }
    public string? BeatUnit { get; init; }
    public decimal? Bpm { get; init; }
}

/// <summary>
/// A notehead belongs to a staff independently from the chord event. Staff is nullable
/// only for reading v0.1 JSON; newly canonicalized data writes it explicitly.
/// Technical marks belong to the individual notehead rather than the whole chord.
/// </summary>
public sealed record CanonicalNote(
    string Pitch,
    int? Staff = null,
    Accidental? Accidental = null,
    List<TechnicalMark>? Technical = null);

public sealed record Accidental(
    string Type,
    bool? Cautionary = null,
    bool? Editorial = null,
    bool? Parentheses = null,
    bool? Bracket = null);

/// <summary>
/// Generic local notation mark. Type is the MusicXML semantic name (for example
/// tenuto, strong-accent, trill-mark). Subtype preserves a mark-specific type attribute
/// when one exists; Placement is kept only when explicitly present.
/// </summary>
public sealed record NotationMark(
    string Type,
    string? Subtype = null,
    string? Placement = null);

/// <summary>
/// A notehead-local technical mark such as fingering or string number.
/// </summary>
public sealed record TechnicalMark(
    string Type,
    string? Value = null,
    string? Placement = null);

public sealed record EventNotation(
    string? NoteType = null,
    int? Dots = null,
    string? Stem = null,
    string? Notehead = null,
    List<NotationMark>? Articulations = null,
    List<NotationMark>? Ornaments = null,
    List<NotationMark>? Fermatas = null);

public sealed record Relations(
    List<BeamRelation> Beams,
    List<TieRelation> Ties,
    List<SlurRelation> Slurs,
    List<TupletRelation> Tuplets,
    List<ArpeggioRelation> Arpeggios,
    List<SpanRelation> Hairpins,
    List<SpanRelation> Pedals,
    List<SpanRelation> OctaveShifts);

public sealed record BeamRelation(
    string Id,
    int Level,
    List<string> Events,
    string? Hook = null);

public sealed record NoteAnchor(string Event, string Note);

public sealed record TieRelation(
    string Id,
    NoteAnchor From,
    NoteAnchor To,
    string? Placement = null);

public sealed record SlurRelation(
    string Id,
    string From,
    string To,
    string? Placement = null);

public sealed record TupletRelation(
    string Id,
    List<string> Events,
    int Actual,
    int Normal,
    bool? Bracket = null);

public sealed record ArpeggioRelation(
    string Id,
    List<string> Events,
    string? Direction = null);

public sealed record TimeAnchor(int Measure, string At, int Staff);

/// <summary>
/// Generic time span for wedge/pedal/ottava. Only fields meaningful for Kind are serialized.
/// </summary>
public sealed record SpanRelation
{
    public required string Id { get; init; }
    public required string Kind { get; init; } // hairpin, pedal, octaveShift
    public required TimeAnchor From { get; init; }
    public required TimeAnchor To { get; init; }
    public string? Type { get; init; }          // crescendo / diminuendo
    public string? Direction { get; init; }     // up / down
    public int? Size { get; init; }
    public bool? Line { get; init; }
    public bool? StartMark { get; init; }
    public string? Placement { get; init; }
}
