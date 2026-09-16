using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SvgMusic.Canonical;

public sealed class MusicXmlWriter
{
    private static readonly Regex PitchRegex = new(
        @"^([A-G])(bb|##|b|#)?(-?\d+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private int _divisions;
    private int _currentMeasureNumber;
    private Relations _relations = null!;

    public XDocument Write(CanonicalNotation score)
    {
        _relations = score.Relations;
        _divisions = CalculateDivisions(score);

        var root = new XElement("score-partwise", new XAttribute("version", "4.0"));

        if (!string.IsNullOrWhiteSpace(score.Metadata.Title))
            root.Add(new XElement("work", new XElement("work-title", score.Metadata.Title)));

        if (!string.IsNullOrWhiteSpace(score.Metadata.Composer))
            root.Add(new XElement("identification",
                new XElement("creator",
                    new XAttribute("type", "composer"),
                    score.Metadata.Composer)));

        var partList = new XElement("part-list");
        foreach (var part in score.Parts)
        {
            partList.Add(new XElement("score-part",
                new XAttribute("id", part.Id),
                new XElement("part-name", string.IsNullOrWhiteSpace(part.Name) ? part.Id : part.Name)));
        }
        root.Add(partList);

        foreach (var part in score.Parts)
        {
            var px = new XElement("part", new XAttribute("id", part.Id));
            for (var i = 0; i < part.Measures.Count; i++)
                px.Add(WriteMeasure(part.Measures[i], i == 0));
            root.Add(px);
        }

        return new XDocument(new XDeclaration("1.0", "UTF-8", null), root);
    }

    public void Write(CanonicalNotation score, string fileName) =>
        Write(score).Save(fileName);

    private XElement WriteMeasure(Measure measure, bool firstMeasure)
    {
        _currentMeasureNumber = measure.Number;
        var mx = new XElement("measure", new XAttribute("number", measure.Number));

        if (measure.Layout is not null)
        {
            var print = new XElement("print");
            if (measure.Layout.BreakBefore == "page") print.SetAttributeValue("new-page", "yes");
            else if (measure.Layout.BreakBefore == "system") print.SetAttributeValue("new-system", "yes");
            mx.Add(print);
        }

        if (firstMeasure || measure.Attributes is not null)
            mx.Add(WriteAttributes(measure.Attributes, firstMeasure));

        var directions = BuildDirections(measure)
            .OrderBy(x => x.At)
            .ThenBy(x => x.Order)
            .ToList();
        var directionIndex = 0;

        var noteEvents = measure.Events
            .Where(e => e.Type is "chord" or "rest")
            .ToList();

        var streams = noteEvents
            .GroupBy(e => e.Voice ?? 1)
            .OrderBy(g => g.Key)
            .ToList();

        long previousStreamCursor = 0;
        for (var streamIndex = 0; streamIndex < streams.Count; streamIndex++)
        {
            if (streamIndex > 0 && previousStreamCursor > 0)
                mx.Add(new XElement("backup", new XElement("duration", previousStreamCursor)));

            long cursor = 0;
            foreach (var ev in streams[streamIndex]
                         .OrderBy(e => Fraction.Parse(e.At).Numerator / (double)Fraction.Parse(e.At).Denominator)
                         .ThenBy(EventStaff)
                         .ThenBy(e => e.Id, StringComparer.Ordinal))
            {
                var at = Units(ev.At);

                if (streamIndex == 0)
                {
                    while (directionIndex < directions.Count && directions[directionIndex].At <= at)
                    {
                        mx.Add(WithOffset(directions[directionIndex].Element, directions[directionIndex].At - cursor));
                        directionIndex++;
                    }
                }

                if (at > cursor)
                {
                    mx.Add(new XElement("forward", new XElement("duration", at - cursor)));
                    cursor = at;
                }

                if (at < cursor)
                {
                    mx.Add(new XElement("backup", new XElement("duration", cursor - at)));
                    cursor = at;
                }

                foreach (var note in WriteEvent(ev))
                    mx.Add(note);

                cursor = at + Units(ev.Duration ?? "0");
            }

            if (streamIndex == 0)
            {
                while (directionIndex < directions.Count)
                {
                    mx.Add(WithOffset(directions[directionIndex].Element, directions[directionIndex].At - cursor));
                    directionIndex++;
                }
            }

            previousStreamCursor = cursor;
        }

        if (streams.Count == 0)
        {
            foreach (var d in directions)
                mx.Add(WithOffset(d.Element, d.At));
        }

        if (measure.LeftBarline is not null || measure.LeftRepeat is not null)
            mx.Add(WriteBarline("left", measure.LeftBarline, measure.LeftRepeat));
        if (measure.RightBarline is not null || measure.RightRepeat is not null)
            mx.Add(WriteBarline("right", measure.RightBarline, measure.RightRepeat));

        return mx;
    }

    private XElement WriteAttributes(MeasureAttributes? a, bool firstMeasure)
    {
        var ax = new XElement("attributes");
        if (firstMeasure)
            ax.Add(new XElement("divisions", _divisions));

        if (a?.Key is not null)
        {
            var key = new XElement("key", new XElement("fifths", a.Key.Fifths));
            if (!string.IsNullOrWhiteSpace(a.Key.Mode)) key.Add(new XElement("mode", a.Key.Mode));
            ax.Add(key);
        }

        if (a?.Time is not null)
            ax.Add(new XElement("time",
                new XElement("beats", a.Time.Beats),
                new XElement("beat-type", a.Time.BeatType)));

        if (a?.Staves is not null)
            ax.Add(new XElement("staves", a.Staves));

        if (a?.Clefs is not null)
        {
            foreach (var clef in a.Clefs)
            {
                var cx = new XElement("clef");
                if (a.Staves is > 1 || clef.Staff != 1)
                    cx.SetAttributeValue("number", clef.Staff);
                cx.Add(new XElement("sign", clef.Sign));
                cx.Add(new XElement("line", clef.Line));
                if (clef.OctaveChange is not null)
                    cx.Add(new XElement("clef-octave-change", clef.OctaveChange));
                ax.Add(cx);
            }
        }

        return ax;
    }

    private IEnumerable<XElement> WriteEvent(CanonicalEvent ev)
    {
        var durationUnits = Units(ev.Duration ?? "0");
        if (ev.Type == "rest")
        {
            yield return WriteNoteElement(ev, null, durationUnits, false, 0);
            yield break;
        }

        var notes = ev.Notes ?? [];
        for (var i = 0; i < notes.Count; i++)
            yield return WriteNoteElement(ev, notes[i], durationUnits, i > 0, i);
    }

    private XElement WriteNoteElement(
        CanonicalEvent ev,
        CanonicalNote? note,
        long durationUnits,
        bool chordContinuation,
        int noteIndex)
    {
        var nx = new XElement("note");
        if (chordContinuation) nx.Add(new XElement("chord"));

        if (note is null)
        {
            nx.Add(new XElement("rest"));
        }
        else
        {
            var (step, alter, octave) = ParsePitch(note.Pitch);
            octave += OctaveShiftPitchOffset(
                ev.At,
                note.Staff ?? ev.Staff ?? 1);
            var pitch = new XElement("pitch", new XElement("step", step));
            if (alter != 0) pitch.Add(new XElement("alter", alter));
            pitch.Add(new XElement("octave", octave));
            nx.Add(pitch);
        }

        nx.Add(new XElement("duration", durationUnits));

        if (note is not null)
        {
            foreach (var tie in TieMarkers(ev.Id, note.Pitch, start: false))
                nx.Add(new XElement("tie", new XAttribute("type", "stop")));
            foreach (var tie in TieMarkers(ev.Id, note.Pitch, start: true))
                nx.Add(new XElement("tie", new XAttribute("type", "start")));
        }

        if (ev.Voice is not null)
            nx.Add(new XElement("voice", ev.Voice));

        if (!string.IsNullOrWhiteSpace(ev.Notation?.NoteType))
            nx.Add(new XElement("type", ev.Notation.NoteType));

        for (var i = 0; i < (ev.Notation?.Dots ?? 0); i++)
            nx.Add(new XElement("dot"));

        if (note?.Accidental is not null)
        {
            var acc = new XElement("accidental", note.Accidental.Type);
            SetYesNo(acc, "cautionary", note.Accidental.Cautionary);
            SetYesNo(acc, "editorial", note.Accidental.Editorial);
            SetYesNo(acc, "parentheses", note.Accidental.Parentheses);
            SetYesNo(acc, "bracket", note.Accidental.Bracket);
            nx.Add(acc);
        }

        var tuplet = TupletForEvent(ev.Id);
        if (tuplet is not null)
            nx.Add(new XElement("time-modification",
                new XElement("actual-notes", tuplet.Relation.Actual),
                new XElement("normal-notes", tuplet.Relation.Normal)));

        if (!string.IsNullOrWhiteSpace(ev.Notation?.Stem))
            nx.Add(new XElement("stem", ev.Notation.Stem));

        if (!string.IsNullOrWhiteSpace(ev.Notation?.Notehead))
            nx.Add(new XElement("notehead", ev.Notation.Notehead));

        nx.Add(new XElement("staff", note?.Staff ?? ev.Staff ?? 1));

        if (noteIndex == 0)
        {
            foreach (var beam in BeamMarkers(ev.Id))
                nx.Add(new XElement("beam", new XAttribute("number", beam.Level), beam.Value));
        }

        var notations = new XElement("notations");

        if (note is not null)
        {
            foreach (var tie in TieMarkers(ev.Id, note.Pitch, start: false))
            {
                var tx = new XElement("tied", new XAttribute("type", "stop"));
                if (!string.IsNullOrWhiteSpace(tie.Placement)) tx.SetAttributeValue("placement", tie.Placement);
                notations.Add(tx);
            }
            foreach (var tie in TieMarkers(ev.Id, note.Pitch, start: true))
            {
                var tx = new XElement("tied", new XAttribute("type", "start"));
                if (!string.IsNullOrWhiteSpace(tie.Placement)) tx.SetAttributeValue("placement", tie.Placement);
                notations.Add(tx);
            }
        }

        if (noteIndex == 0)
        {
            foreach (var slur in SlurMarkers(ev.Id))
            {
                var sx = new XElement("slur",
                    new XAttribute("type", slur.Type),
                    new XAttribute("number", slur.Number));
                if (!string.IsNullOrWhiteSpace(slur.Placement)) sx.SetAttributeValue("placement", slur.Placement);
                notations.Add(sx);
            }

            if (tuplet is not null)
            {
                if (tuplet.IsFirst)
                {
                    var tx = new XElement("tuplet",
                        new XAttribute("type", "start"),
                        new XAttribute("number", tuplet.Number));
                    SetYesNo(tx, "bracket", tuplet.Relation.Bracket);
                    notations.Add(tx);
                }
                if (tuplet.IsLast)
                    notations.Add(new XElement("tuplet",
                        new XAttribute("type", "stop"),
                        new XAttribute("number", tuplet.Number)));
            }

            AddMarkGroup(notations, "ornaments", ev.Notation?.Ornaments);
            AddMarkGroup(notations, "articulations", ev.Notation?.Articulations);

            foreach (var fermata in ev.Notation?.Fermatas ?? [])
                notations.Add(NotationMarkElement(fermata));
        }

        if (note?.Technical is { Count: > 0 })
        {
            var tx = new XElement("technical");
            foreach (var mark in note.Technical)
            {
                var mx = new XElement(mark.Type);
                if (!string.IsNullOrWhiteSpace(mark.Value)) mx.Value = mark.Value;
                if (!string.IsNullOrWhiteSpace(mark.Placement)) mx.SetAttributeValue("placement", mark.Placement);
                tx.Add(mx);
            }
            notations.Add(tx);
        }

        foreach (var arp in ArpeggioMarkers(ev.Id))
        {
            var ax = new XElement("arpeggiate", new XAttribute("number", arp.Number));
            if (!string.IsNullOrWhiteSpace(arp.Direction)) ax.SetAttributeValue("direction", arp.Direction);
            notations.Add(ax);
        }

        if (notations.HasElements)
            nx.Add(notations);

        return nx;
    }

    private static void AddMarkGroup(XElement notations, string groupName, List<NotationMark>? marks)
    {
        if (marks is not { Count: > 0 }) return;
        var group = new XElement(groupName);
        foreach (var mark in marks)
            group.Add(NotationMarkElement(mark));
        notations.Add(group);
    }

    private static XElement NotationMarkElement(NotationMark mark)
    {
        var x = new XElement(mark.Type);
        if (!string.IsNullOrWhiteSpace(mark.Subtype)) x.SetAttributeValue("type", mark.Subtype);
        if (!string.IsNullOrWhiteSpace(mark.Placement)) x.SetAttributeValue("placement", mark.Placement);
        return x;
    }

    private IEnumerable<(long At, int Order, XElement Element)> BuildDirections(Measure measure)
    {
        var order = 0;
        foreach (var ev in measure.Events.Where(e => e.Type is "text" or "dynamic" or "tempo" or "navigation"))
        {
            if (ev.Type == "navigation")
            {
                yield return (Units(ev.At), order++, NavigationDirection(ev));
                continue;
            }

            XElement? type = ev.Type switch
            {
                "text" => new XElement("words", ev.Text ?? ""),
                "dynamic" => new XElement("dynamics", new XElement(ev.Value ?? "mf")),
                "tempo" => new XElement("metronome",
                    new XElement("beat-unit", ev.BeatUnit ?? "quarter"),
                    new XElement("per-minute", (ev.Bpm ?? 120m).ToString(CultureInfo.InvariantCulture))),
                _ => null
            };

            if (type is null) continue;
            yield return (Units(ev.At), order++, Direction(ev.Staff ?? 1, ev.Placement, type));
        }

        foreach (var span in SpansStartingAt(measure.Number))
            yield return (Units(span.From.At), order++, Direction(span.From.Staff, span.Placement, SpanStart(span)));

        foreach (var span in SpansEndingAt(measure.Number))
            yield return (Units(span.To.At), order++, Direction(span.To.Staff, span.Placement, SpanStop(span)));
    }

    private static XElement NavigationDirection(CanonicalEvent ev)
    {
        var kind = ev.Value is "segno" ? "segno" : "coda";
        var dx = Direction(ev.Staff ?? 1, ev.Placement, new XElement(kind));
        if (!string.IsNullOrWhiteSpace(ev.Target))
            dx.Add(new XElement("sound", new XAttribute(kind, ev.Target)));
        return dx;
    }

    private static XElement Direction(int staff, string? placement, XElement content)
    {
        var dx = new XElement("direction");
        if (!string.IsNullOrWhiteSpace(placement)) dx.SetAttributeValue("placement", placement);
        dx.Add(new XElement("direction-type", content));
        dx.Add(new XElement("staff", staff));
        return dx;
    }

    private static XElement WithOffset(XElement direction, long offset)
    {
        var copy = new XElement(direction);
        if (offset != 0)
        {
            var staff = copy.Element("staff");
            if (staff is not null) staff.AddBeforeSelf(new XElement("offset", offset));
            else copy.Add(new XElement("offset", offset));
        }
        return copy;
    }

    private XElement SpanStart(SpanRelation span)
    {
        return span.Kind switch
        {
            "hairpin" => new XElement("wedge",
                new XAttribute("type", span.Type ?? "crescendo"),
                new XAttribute("number", SpanNumber(span))),
            "pedal" => Pedal(
                span.StartMark == true ? "resume" : "start",
                span,
                span.StartMark == true),
            "octaveShift" => new XElement("octave-shift",
                new XAttribute("type", span.Direction ?? "up"),
                new XAttribute("number", SpanNumber(span)),
                new XAttribute("size", span.Size ?? 8)),
            _ => throw new InvalidDataException($"Unknown span kind: {span.Kind}")
        };
    }

    private XElement SpanStop(SpanRelation span)
    {
        return span.Kind switch
        {
            "hairpin" => new XElement("wedge",
                new XAttribute("type", "stop"),
                new XAttribute("number", SpanNumber(span))),
            "pedal" => Pedal("stop", span, false),
            "octaveShift" => new XElement("octave-shift",
                new XAttribute("type", "stop"),
                new XAttribute("number", SpanNumber(span)),
                new XAttribute("size", span.Size ?? 8)),
            _ => throw new InvalidDataException($"Unknown span kind: {span.Kind}")
        };
    }

    private static XElement Pedal(
        string type,
        SpanRelation span,
        bool sign)
    {
        var px = new XElement("pedal", new XAttribute("type", type));
        px.SetAttributeValue("number", SpanNumber(span));
        SetYesNo(px, "line", span.Line);
        SetYesNo(px, "sign", sign);
        return px;
    }

    private IEnumerable<SpanRelation> SpansStartingAt(int measure) =>
        _relations.Hairpins.Concat(_relations.Pedals).Concat(_relations.OctaveShifts)
            .Where(x => x.From.Measure == measure);

    private IEnumerable<SpanRelation> SpansEndingAt(int measure) =>
        _relations.Hairpins.Concat(_relations.Pedals).Concat(_relations.OctaveShifts)
            .Where(x => x.To.Measure == measure);

    private IEnumerable<(int Level, string Value)> BeamMarkers(string eventId)
    {
        foreach (var beam in _relations.Beams.Where(b => b.Events.Contains(eventId)))
        {
            if (beam.Hook is not null)
            {
                yield return (beam.Level, beam.Hook + " hook");
                continue;
            }

            var index = beam.Events.IndexOf(eventId);
            var value = index == 0 ? "begin" : index == beam.Events.Count - 1 ? "end" : "continue";
            yield return (beam.Level, value);
        }
    }

    private IEnumerable<TieRelation> TieMarkers(string eventId, string pitch, bool start) =>
        _relations.Ties.Where(t => start
            ? t.From.Event == eventId && t.From.Note == pitch
            : t.To.Event == eventId && t.To.Note == pitch);

    private IEnumerable<(string Type, int Number, string? Placement)> SlurMarkers(string eventId)
    {
        for (var i = 0; i < _relations.Slurs.Count; i++)
        {
            var slur = _relations.Slurs[i];
            if (slur.From == eventId) yield return ("start", i + 1, slur.Placement);
            if (slur.To == eventId) yield return ("stop", i + 1, slur.Placement);
        }
    }

    private TupletEventInfo? TupletForEvent(string eventId)
    {
        for (var i = 0; i < _relations.Tuplets.Count; i++)
        {
            var t = _relations.Tuplets[i];
            var pos = t.Events.IndexOf(eventId);
            if (pos >= 0)
                return new TupletEventInfo(t, i + 1, pos == 0, pos == t.Events.Count - 1);
        }
        return null;
    }

    private IEnumerable<(int Number, string? Direction)> ArpeggioMarkers(string eventId)
    {
        for (var i = 0; i < _relations.Arpeggios.Count; i++)
        {
            var a = _relations.Arpeggios[i];
            if (a.Events.Contains(eventId)) yield return (i + 1, a.Direction);
        }
    }

    private int OctaveShiftPitchOffset(
        string at,
        int staff)
    {
        var position = Fraction.Parse(at);
        var offset = 0;

        foreach (var span in _relations.OctaveShifts)
        {
            if (span.From.Staff != staff || span.To.Staff != staff)
            {
                continue;
            }

            if (ComparePosition(
                    _currentMeasureNumber,
                    position,
                    span.From) < 0
                || ComparePosition(
                    _currentMeasureNumber,
                    position,
                    span.To) >= 0)
            {
                continue;
            }

            var octaves = Math.Max(
                1,
                ((span.Size ?? 8) - 1) / 7);

            offset += span.Direction?.ToLowerInvariant() switch
            {
                "down" => octaves,
                "up" => -octaves,
                _ => 0
            };
        }

        return offset;
    }

    private static int ComparePosition(
        int measure,
        Fraction at,
        TimeAnchor anchor)
    {
        var measureComparison = measure.CompareTo(anchor.Measure);
        if (measureComparison != 0)
        {
            return measureComparison;
        }

        var other = Fraction.Parse(anchor.At);
        return (at.Numerator * other.Denominator)
            .CompareTo(other.Numerator * at.Denominator);
    }

    private long Units(string fractionText)
    {
        var f = Fraction.Parse(fractionText).Reduce();
        var numerator = f.Numerator * _divisions * 4L;
        if (numerator % f.Denominator != 0)
            throw new InvalidDataException($"Fraction {fractionText} cannot be represented with divisions={_divisions}.");
        return numerator / f.Denominator;
    }

    private static int CalculateDivisions(CanonicalNotation score)
    {
        long lcm = 1;
        IEnumerable<string> fractions = score.Parts
            .SelectMany(p => p.Measures)
            .SelectMany(m => m.Events)
            .SelectMany(e => new[] { e.At, e.Duration }.Where(x => !string.IsNullOrWhiteSpace(x))!)
            .Concat(score.Relations.Hairpins.SelectMany(x => new[] { x.From.At, x.To.At }))
            .Concat(score.Relations.Pedals.SelectMany(x => new[] { x.From.At, x.To.At }))
            .Concat(score.Relations.OctaveShifts.SelectMany(x => new[] { x.From.At, x.To.At }));

        foreach (var text in fractions)
            lcm = Lcm(lcm, Fraction.Parse(text!).Reduce().Denominator);

        var gcd = Gcd(lcm, 4);
        var divisions = lcm / gcd;
        if (divisions > int.MaxValue)
            throw new InvalidDataException("Canonical durations require too many MusicXML divisions.");
        return Math.Max(1, (int)divisions);
    }

    private static (string Step, int Alter, int Octave) ParsePitch(string pitch)
    {
        var m = PitchRegex.Match(pitch);
        if (!m.Success) throw new InvalidDataException($"Unsupported pitch: {pitch}");
        var alter = m.Groups[2].Value switch
        {
            "bb" => -2,
            "b" => -1,
            "" => 0,
            "#" => 1,
            "##" => 2,
            _ => 0
        };
        return (m.Groups[1].Value, alter,
            int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));
    }

    private static int EventStaff(CanonicalEvent ev) =>
        ev.Staff ?? ev.Notes?.FirstOrDefault()?.Staff ?? 1;

    private static XElement WriteBarline(string location, string? value, RepeatMark? repeat)
    {
        var x = new XElement("barline", new XAttribute("location", location));
        if (value is not null)
        {
            x.Add(new XElement("bar-style", value switch
            {
                "final" => "light-heavy",
                "double" => "light-light",
                "normal" => "regular",
                "reverse-final" => "heavy-light",
                _ => value
            }));
        }

        if (repeat is not null)
        {
            var rx = new XElement("repeat", new XAttribute("direction", repeat.Direction));
            if (repeat.Times is not null) rx.SetAttributeValue("times", repeat.Times.Value);
            x.Add(rx);
        }

        return x;
    }

    private static void SetYesNo(XElement x, string name, bool? value)
    {
        if (value is not null) x.SetAttributeValue(name, value.Value ? "yes" : "no");
    }

    private static int SpanNumber(SpanRelation span)
    {
        var digits = new string(span.Id.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) && n > 0 ? n : 1;
    }

    private static long Gcd(long a, long b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return Math.Abs(a);
    }

    private static long Lcm(long a, long b) => checked(a / Gcd(a, b) * b);

    private sealed record TupletEventInfo(
        TupletRelation Relation,
        int Number,
        bool IsFirst,
        bool IsLast);
}
