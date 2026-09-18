using System.Globalization;
using System.Xml.Linq;

namespace SvgMusic.Canonical;

public sealed class MusicXmlCanonicalizer
{
    private readonly List<BeamMarker> _beamMarkers = [];
    private readonly List<TieMarker> _tieMarkers = [];
    private readonly List<SlurMarker> _slurMarkers = [];
    private readonly List<TupletMarker> _tupletMarkers = [];
    private readonly List<ArpeggioMarker> _arpeggioMarkers = [];
    private readonly List<DirectionMarker> _directionMarkers = [];

    private int _divisions = 1;

    public CanonicalNotation Read(string fileName)
    {
        Reset();
        var doc = XDocument.Load(fileName, LoadOptions.PreserveWhitespace);
        var root = doc.Root ?? throw new InvalidDataException("Missing score-partwise root.");

        var title = root.Element("work")?.Element("work-title")?.Value.Trim();
        var composer = root.Element("identification")?.Elements("creator")
            .FirstOrDefault(x => (string?)x.Attribute("type") == "composer")?.Value.Trim();
        var subtitle = root.Elements("credit")
            .FirstOrDefault(credit =>
                string.Equals(
                    credit.Element("credit-type")?.Value.Trim(),
                    "subtitle",
                    StringComparison.OrdinalIgnoreCase))
            ?.Elements("credit-words")
            .FirstOrDefault()
            ?.Value
            .Trim();

        var partNames = root.Element("part-list")?.Elements("score-part")
            .ToDictionary(
                x => (string?)x.Attribute("id") ?? "",
                x => x.Element("part-name")?.Value.Trim() ?? "")
            ?? new Dictionary<string, string>();

        var parts = new List<Part>();
        foreach (var partXml in root.Elements("part"))
        {
            var partId = (string?)partXml.Attribute("id") ?? "P1";
            var measures = partXml.Elements("measure")
                .Select(m => ReadMeasure(partId, m))
                .ToList();

            parts.Add(new Part(
                partId,
                partNames.GetValueOrDefault(partId, partId),
                measures));
        }

        return new CanonicalNotation(
            "CanonicalNotation",
            "0.4",
            new Metadata(title, composer, subtitle),
            parts,
            BuildRelations());
    }

    private Measure ReadMeasure(string partId, XElement measureXml)
    {
        var measureNo = IntAttr(measureXml, "number", 0);
        long cursor = 0;
        var events = new List<CanonicalEvent>();
        CanonicalEvent? lastChordEvent = null;
        var eventNo = 0;
        var directionNo = 0;
        MeasureAttributes? attributes = null;
        string? leftBarline = null;
        string? rightBarline = null;
        RepeatMark? leftRepeat = null;
        RepeatMark? rightRepeat = null;
        LayoutHint? layout = null;

        foreach (var node in measureXml.Elements())
        {
            switch (node.Name.LocalName)
            {
                case "attributes":
                    attributes = MergeAttributes(attributes, ReadAttributes(node, cursor));
                    break;

                case "backup":
                    cursor -= Long(node, "duration");
                    break;

                case "forward":
                    cursor += Long(node, "duration");
                    break;

                case "note":
                {
                    var durationUnits = Long(node, "duration");
                    var staff = Int(node, "staff", 1);
                    var voice = Int(node, "voice", 1);
                    var isChordContinuation = node.Element("chord") is not null;
                    var isGrace = node.Element("grace") is not null;

                    CanonicalEvent ev;
                    if (isChordContinuation && lastChordEvent is { Type: "chord", Notes: not null })
                    {
                        var note = ReadPitch(node, staff);
                        if (note is not null) lastChordEvent.Notes.Add(note);

                        var extraNotation = ReadNotation(node);
                        if (extraNotation is not null)
                        {
                            lastChordEvent = lastChordEvent with
                            {
                                Notation = MergeNotation(lastChordEvent.Notation, extraNotation)
                            };
                            events[^1] = lastChordEvent;
                        }

                        ev = lastChordEvent;
                    }
                    else
                    {
                        eventNo++;
                        var id = $"m{measureNo}.e{eventNo}";
                        var at = Duration(cursor).ToString();
                        var duration = Duration(durationUnits).ToString();
                        var notation = ReadNotation(node);

                        if (node.Element("rest") is not null)
                        {
                            ev = new CanonicalEvent
                            {
                                Id = id, Type = "rest", At = at,
                                Duration = duration, Staff = staff, Voice = voice,
                                Notation = notation
                            };
                        }
                        else
                        {
                            ev = new CanonicalEvent
                            {
                                Id = id, Type = "chord", At = at,
                                Duration = isGrace ? null : duration,
                                Grace = isGrace ? true : null,
                                Voice = voice,
                                Notes = [ReadPitch(node, staff)!], Notation = notation
                            };
                        }

                        events.Add(ev);
                        lastChordEvent = ev;
                        cursor += durationUnits;
                    }

                    CollectNoteRelations(partId, measureNo, ev.Id, staff, voice, node);
                    break;
                }

                case "direction":
                    directionNo++;
                    ReadDirection(partId, measureNo, directionNo, cursor, node, events);
                    break;

                case "barline":
                {
                    var location = (string?)node.Attribute("location") ?? "right";
                    var style = NormalizeBarline(node.Element("bar-style")?.Value.Trim());
                    var repeat = ReadRepeat(node.Element("repeat"));
                    if (location == "left")
                    {
                        leftBarline = style;
                        leftRepeat = repeat;
                    }
                    else
                    {
                        rightBarline = style;
                        rightRepeat = repeat;
                    }
                    break;
                }

                case "print":
                    if ((string?)node.Attribute("new-page") == "yes") layout = new LayoutHint("page");
                    else if ((string?)node.Attribute("new-system") == "yes") layout = new LayoutHint("system");
                    break;
            }
        }

        return new Measure(
            measureNo, events, attributes, leftBarline, rightBarline, layout,
            leftRepeat, rightRepeat);
    }

    private MeasureAttributes ReadAttributes(XElement xml, long cursor)
    {
        var divisions = xml.Element("divisions");
        if (divisions is not null) _divisions = int.Parse(divisions.Value, CultureInfo.InvariantCulture);

        TimeSignature? time = null;
        if (xml.Element("time") is { } tx)
            time = new TimeSignature(Int(tx, "beats"), Int(tx, "beat-type"));

        KeySignature? key = null;
        if (xml.Element("key") is { } kx)
            key = new KeySignature(Int(kx, "fifths"), kx.Element("mode")?.Value.Trim());

        int? staves = xml.Element("staves") is null ? null : Int(xml, "staves");

        var clefs = xml.Elements("clef").Select(c => new Clef(
            IntAttr(c, "number", 1),
            c.Element("sign")?.Value.Trim() ?? "G",
            Int(c, "line"),
            c.Element("clef-octave-change") is null ? null : Int(c, "clef-octave-change")))
            .ToList();

        return new MeasureAttributes(
            time,
            key,
            staves,
            clefs.Count == 0 ? null : clefs,
            cursor == 0 ? null : Duration(cursor).ToString());
    }

    private static MeasureAttributes MergeAttributes(MeasureAttributes? a, MeasureAttributes b) => a is null
        ? b
        : new MeasureAttributes(
            b.Time ?? a.Time,
            b.Key ?? a.Key,
            b.Staves ?? a.Staves,
            b.Clefs ?? a.Clefs,
            b.At ?? a.At);

    private CanonicalNote? ReadPitch(XElement note, int staff)
    {
        var p = note.Element("pitch");
        if (p is null) return null;

        var step = p.Element("step")?.Value.Trim() ?? "C";
        var alter = Int(p, "alter", 0);
        var octave = Int(p, "octave");
        var pitch = $"{step}{AlterText(alter)}{octave}";

        Accidental? accidental = null;
        if (note.Element("accidental") is { } ax)
        {
            accidental = new Accidental(
                ax.Value.Trim(),
                YesNoAttr(ax, "cautionary"),
                YesNoAttr(ax, "editorial"),
                YesNoAttr(ax, "parentheses"),
                YesNoAttr(ax, "bracket"));
        }

        var technical = note.Element("notations")?.Element("technical")?.Elements()
            .Select(ReadTechnicalMark)
            .ToList();

        return new CanonicalNote(
            pitch,
            staff,
            accidental,
            technical is { Count: > 0 } ? technical : null);
    }

    private static EventNotation? ReadNotation(XElement note)
    {
        var type = note.Element("type")?.Value.Trim();
        var dots = note.Elements("dot").Count();
        var stem = note.Element("stem")?.Value.Trim();
        var notehead = note.Element("notehead")?.Value.Trim();
        var notations = note.Element("notations");

        var articulations = ReadMarks(notations?.Element("articulations")?.Elements());
        var ornaments = ReadMarks(notations?.Element("ornaments")?.Elements());
        var fermatas = ReadMarks(notations?.Elements("fermata"));

        if (type is null && dots == 0 && stem is null && notehead is null &&
            articulations is null && ornaments is null && fermatas is null)
            return null;

        return new EventNotation(
            type,
            dots == 0 ? null : dots,
            stem,
            notehead,
            articulations,
            ornaments,
            fermatas);
    }

    private static EventNotation MergeNotation(EventNotation? a, EventNotation b)
    {
        if (a is null) return b;
        return new EventNotation(
            a.NoteType ?? b.NoteType,
            a.Dots ?? b.Dots,
            a.Stem ?? b.Stem,
            a.Notehead ?? b.Notehead,
            MergeMarks(a.Articulations, b.Articulations),
            MergeMarks(a.Ornaments, b.Ornaments),
            MergeMarks(a.Fermatas, b.Fermatas));
    }

    private static List<NotationMark>? MergeMarks(List<NotationMark>? a, List<NotationMark>? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return a.Concat(b).Distinct().ToList();
    }

    private static List<NotationMark>? ReadMarks(IEnumerable<XElement>? elements)
    {
        if (elements is null) return null;
        var result = elements.Select(ReadNotationMark).ToList();
        return result.Count == 0 ? null : result;
    }

    private static NotationMark ReadNotationMark(XElement x) => new(
        x.Name.LocalName,
        (string?)x.Attribute("type"),
        (string?)x.Attribute("placement"));

    private static TechnicalMark ReadTechnicalMark(XElement x) => new(
        x.Name.LocalName,
        string.IsNullOrWhiteSpace(x.Value) ? null : x.Value.Trim(),
        (string?)x.Attribute("placement"));

    private void CollectNoteRelations(string part, int measure, string eventId, int staff, int voice, XElement note)
    {
        foreach (var b in note.Elements("beam"))
            _beamMarkers.Add(new BeamMarker(part, eventId, staff, voice,
                IntAttr(b, "number", 1), b.Value.Trim()));

        var pitch = ReadPitch(note, staff)?.Pitch;
        foreach (var t in note.Element("notations")?.Elements("tied") ?? [])
            _tieMarkers.Add(new TieMarker(part, eventId, pitch, staff, voice,
                IntAttr(t, "number", 1), (string?)t.Attribute("type") ?? "",
                (string?)t.Attribute("placement")));

        foreach (var s in note.Element("notations")?.Elements("slur") ?? [])
            _slurMarkers.Add(new SlurMarker(part, eventId,
                IntAttr(s, "number", 1), (string?)s.Attribute("type") ?? "",
                (string?)s.Attribute("placement")));

        var tm = note.Element("time-modification");
        var tuplet = note.Element("notations")?.Element("tuplet");
        if (tm is not null || tuplet is not null)
        {
            _tupletMarkers.Add(new TupletMarker(
                part, eventId, staff, voice,
                tuplet is null ? 1 : IntAttr(tuplet, "number", 1),
                tuplet is null ? null : (string?)tuplet.Attribute("type"),
                tm is null ? null : Int(tm, "actual-notes"),
                tm is null ? null : Int(tm, "normal-notes"),
                tuplet is null ? null : YesNoAttr(tuplet, "bracket")));
        }

        if (note.Element("notations")?.Element("arpeggiate") is { } arp)
            _arpeggioMarkers.Add(new ArpeggioMarker(part, measure, eventId,
                IntAttr(arp, "number", 1), (string?)arp.Attribute("direction")));
    }

    private void ReadDirection(
        string part, int measure, int directionNo, long cursor,
        XElement direction, List<CanonicalEvent> events)
    {
        var staff = Int(direction, "staff", 1);
        var placement = (string?)direction.Attribute("placement");
        var offset = Long(direction, "offset", 0);
        var at = Duration(cursor + offset).ToString();
        var sound = direction.Element("sound");

        foreach (var type in direction.Elements("direction-type").Elements())
        {
            var id = $"m{measure}.d{directionNo}";
            switch (type.Name.LocalName)
            {
                case "words":
                    events.Add(new CanonicalEvent
                    {
                        Id = id, Type = "text", At = at, Staff = staff,
                        Text = type.Value.Trim(), Placement = placement
                    });
                    break;

                case "dynamics":
                    if (type.Elements().FirstOrDefault() is { } dyn)
                        events.Add(new CanonicalEvent
                        {
                            Id = id, Type = "dynamic", At = at, Staff = staff,
                            Value = dyn.Name.LocalName, Placement = placement
                        });
                    break;

                case "metronome":
                    decimal? bpm = null;
                    if (decimal.TryParse(type.Element("per-minute")?.Value,
                            NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)) bpm = parsed;
                    events.Add(new CanonicalEvent
                    {
                        Id = id, Type = "tempo", At = at, Staff = staff,
                        BeatUnit = type.Element("beat-unit")?.Value.Trim(),
                        Bpm = bpm, Placement = placement
                    });
                    break;

                case "coda":
                case "segno":
                    events.Add(new CanonicalEvent
                    {
                        Id = id,
                        Type = "navigation",
                        At = at,
                        Staff = staff,
                        Value = type.Name.LocalName,
                        Target = (string?)sound?.Attribute(type.Name.LocalName),
                        Placement = placement
                    });
                    break;

                case "wedge":
                case "pedal":
                case "octave-shift":
                    _directionMarkers.Add(new DirectionMarker(
                        type.Name.LocalName, part, measure, at, staff, placement,
                        type.Attributes().ToDictionary(a => a.Name.LocalName, a => a.Value)));
                    break;
            }
        }
    }

    private Relations BuildRelations() => new(
        BuildBeams(), BuildTies(), BuildSlurs(), BuildTuplets(), BuildArpeggios(),
        BuildSpans("wedge"), BuildSpans("pedal"), BuildSpans("octave-shift"));

    private List<BeamRelation> BuildBeams()
    {
        var result = new List<BeamRelation>();
        var open = new Dictionary<(string Part, int Voice, int Level), List<string>>();
        var id = 0;
        foreach (var m in _beamMarkers)
        {
            var key = (m.Part, m.Voice, m.Level);
            switch (m.Value)
            {
                case "begin": open[key] = [m.Event]; break;
                case "continue": AddUnique(open.GetValueOrDefault(key), m.Event); break;
                case "end":
                    var events = open.GetValueOrDefault(key) ?? [];
                    AddUnique(events, m.Event);
                    open.Remove(key);
                    result.Add(new BeamRelation($"beam-{++id}", m.Level, events));
                    break;
                case "forward hook":
                case "backward hook":
                    result.Add(new BeamRelation($"beam-{++id}", m.Level, [m.Event],
                        m.Value.StartsWith("backward") ? "backward" : "forward"));
                    break;
            }
        }
        return result;
    }

    private List<TieRelation> BuildTies()
    {
        var result = new List<TieRelation>();
        var open = new Dictionary<(string Part, int Voice, string? Pitch, int Number), TieMarker>();
        var id = 0;
        foreach (var m in _tieMarkers)
        {
            var key = (m.Part, m.Voice, m.Pitch, m.Number);
            if (m.Type == "start") open[key] = m;
            else if (m.Type == "stop" && open.Remove(key, out var start) && m.Pitch is not null)
                result.Add(new TieRelation($"tie-{++id}",
                    new NoteAnchor(start.Event, start.Pitch!),
                    new NoteAnchor(m.Event, m.Pitch),
                    start.Placement ?? m.Placement));
        }
        return result;
    }

    private List<SlurRelation> BuildSlurs()
    {
        var result = new List<SlurRelation>();
        var open = new Dictionary<(string Part, int Number), SlurMarker>();
        var id = 0;
        foreach (var m in _slurMarkers)
        {
            var key = (m.Part, m.Number);
            if (m.Type == "start") open[key] = m;
            else if (m.Type == "stop" && open.Remove(key, out var start))
                result.Add(new SlurRelation($"slur-{++id}", start.Event, m.Event,
                    start.Placement ?? m.Placement));
        }
        return result;
    }

    private List<TupletRelation> BuildTuplets()
    {
        var result = new List<TupletRelation>();
        var open = new Dictionary<(string Part, int Voice, int Number), int>();
        var id = 0;

        for (var i = 0; i < _tupletMarkers.Count; i++)
        {
            var m = _tupletMarkers[i];
            var key = (m.Part, m.Voice, m.Number);
            if (m.Type == "start") open[key] = i;
            else if (m.Type == "stop" && open.Remove(key, out var startIndex))
            {
                var segment = _tupletMarkers.Skip(startIndex).Take(i - startIndex + 1)
                    .Where(x => x.Part == m.Part && x.Voice == m.Voice)
                    .ToList();
                var events = segment.Select(x => x.Event).Distinct().ToList();
                var actual = segment.Select(x => x.Actual).FirstOrDefault(x => x.HasValue) ?? 0;
                var normal = segment.Select(x => x.Normal).FirstOrDefault(x => x.HasValue) ?? 0;
                var bracket = _tupletMarkers[startIndex].Bracket;
                result.Add(new TupletRelation($"tuplet-{++id}", events, actual, normal, bracket));
            }
        }
        return result;
    }

    private List<ArpeggioRelation> BuildArpeggios()
    {
        var id = 0;
        return _arpeggioMarkers
            .GroupBy(x => (x.Part, x.Measure, x.Number))
            .Select(g => new ArpeggioRelation(
                $"arpeggio-{++id}",
                g.Select(x => x.Event).Distinct().ToList(),
                g.Select(x => x.Direction).FirstOrDefault(x => x is not null)))
            .ToList();
    }

    private List<SpanRelation> BuildSpans(string kind)
    {
        var result = new List<SpanRelation>();
        var open = new Dictionary<(string Part, int Staff, int Number), DirectionMarker>();
        var id = 0;

        foreach (var m in _directionMarkers.Where(x => x.Kind == kind))
        {
            var number = m.Attributes.TryGetValue("number", out var n) ? int.Parse(n) : 1;
            var key = (m.Part, m.Staff, number);
            var type = m.Attributes.GetValueOrDefault("type");
            var isStart = kind switch
            {
                "wedge" => type is "crescendo" or "diminuendo",
                "pedal" => type is "start" or "resume",
                "octave-shift" => type is "up" or "down",
                _ => false
            };

            if (isStart) open[key] = m;
            else if (type == "stop" && open.Remove(key, out var start))
            {
                result.Add(new SpanRelation
                {
                    Id = $"{SpanIdPrefix(kind)}-{++id}",
                    Kind = SpanKind(kind),
                    From = new TimeAnchor(start.Measure, start.At, start.Staff),
                    To = new TimeAnchor(m.Measure, m.At, m.Staff),
                    Type = kind == "wedge" ? start.Attributes.GetValueOrDefault("type") : null,
                    Direction = kind == "octave-shift" ? start.Attributes.GetValueOrDefault("type") : null,
                    Size = kind == "octave-shift" && start.Attributes.TryGetValue("size", out var size) ? int.Parse(size) : null,
                    Line = kind == "pedal" ? YesNo(start.Attributes.GetValueOrDefault("line")) : null,
                    StartMark = kind == "pedal" ? YesNo(start.Attributes.GetValueOrDefault("sign")) : null,
                    Placement = start.Placement
                });
            }
        }
        return result;
    }

    private Fraction Duration(long units) => new(units, _divisions * 4L);

    private void Reset()
    {
        _beamMarkers.Clear(); _tieMarkers.Clear(); _slurMarkers.Clear();
        _tupletMarkers.Clear(); _arpeggioMarkers.Clear(); _directionMarkers.Clear();
        _divisions = 1;
    }

    private static RepeatMark? ReadRepeat(XElement? repeat)
    {
        if (repeat is null) return null;
        var direction = (string?)repeat.Attribute("direction");
        if (string.IsNullOrWhiteSpace(direction)) return null;
        int? times = int.TryParse((string?)repeat.Attribute("times"), out var n) ? n : null;
        return new RepeatMark(direction, times);
    }

    private static string AlterText(int alter) => alter switch
    {
        -2 => "bb", -1 => "b", 0 => "", 1 => "#", 2 => "##", _ => $"({alter:+#;-#;0})"
    };

    private static string? NormalizeBarline(string? s) => s switch
    {
        null => null, "light-heavy" => "final", "light-light" => "double",
        "regular" => "normal", "heavy-light" => "reverse-final", _ => s
    };

    private static int Int(XElement e, string name, int fallback = 0) =>
        int.TryParse(e.Element(name)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    private static long Long(XElement e, string name, long fallback = 0) =>
        long.TryParse(e.Element(name)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    private static int IntAttr(XElement e, string name, int fallback = 0) =>
        int.TryParse((string?)e.Attribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    private static bool? YesNoAttr(XElement e, string name) => YesNo((string?)e.Attribute(name));
    private static bool? YesNo(string? value) => value switch { "yes" => true, "no" => false, _ => null };
    private static void AddUnique(List<string>? list, string value)
    {
        if (list is not null && !list.Contains(value)) list.Add(value);
    }
    private static string SpanIdPrefix(string kind) => kind switch { "wedge" => "hairpin", "pedal" => "pedal", _ => "ottava" };
    private static string SpanKind(string kind) => kind switch { "wedge" => "hairpin", "pedal" => "pedal", _ => "octaveShift" };

    private sealed record BeamMarker(string Part, string Event, int Staff, int Voice, int Level, string Value);
    private sealed record TieMarker(string Part, string Event, string? Pitch, int Staff, int Voice, int Number, string Type, string? Placement);
    private sealed record SlurMarker(string Part, string Event, int Number, string Type, string? Placement);
    private sealed record TupletMarker(string Part, string Event, int Staff, int Voice, int Number, string? Type, int? Actual, int? Normal, bool? Bracket);
    private sealed record ArpeggioMarker(string Part, int Measure, string Event, int Number, string? Direction);
    private sealed record DirectionMarker(string Kind, string Part, int Measure, string At, int Staff, string? Placement, Dictionary<string, string> Attributes);
}
