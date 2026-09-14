using System.Globalization;
using System.Xml.Linq;

namespace SvgMusic.Canonical;

/// <summary>
/// Compatibility wrapper for consumers such as MuseScore that do not handle a
/// structurally valid but completely empty MusicXML score well.
///
/// CanonicalNotation is intentionally allowed to contain empty measures while
/// semantic recognition is still incomplete. MusicXML 4.0 also permits empty
/// music-data, but MuseScore expects actual musical content when importing a
/// score. For an empty measure we therefore serialize one full-measure rest per
/// staff. These rests are serialization scaffolding only; they are not added to
/// CanonicalNotation.
/// </summary>
public sealed class MuseScoreCompatibleMusicXmlWriter
{
    public XDocument Write(CanonicalNotation score)
    {
        var document = new MusicXmlWriter().Write(score);

        EnsureMusicXmlDoctype(document);
        FillEmptyMeasures(document);

        return document;
    }

    public void Write(
        CanonicalNotation score,
        string fileName)
    {
        Write(score).Save(fileName);
    }

    private static void EnsureMusicXmlDoctype(XDocument document)
    {
        if (document.DocumentType is not null)
        {
            return;
        }

        var root = document.Root
            ?? throw new InvalidDataException("MusicXML document has no root element.");

        root.AddBeforeSelf(
            new XDocumentType(
                "score-partwise",
                "-//Recordare//DTD MusicXML 4.0 Partwise//EN",
                "http://www.musicxml.org/dtds/partwise.dtd",
                null));
    }

    private static void FillEmptyMeasures(XDocument document)
    {
        var root = document.Root
            ?? throw new InvalidDataException("MusicXML document has no root element.");

        foreach (var part in root.Elements("part"))
        {
            var state = new MeasureState();

            foreach (var measure in part.Elements("measure"))
            {
                UpdateState(measure, state);

                if (measure.Elements("note").Any())
                {
                    continue;
                }

                AddFullMeasureRests(
                    measure,
                    state);
            }
        }
    }

    private static void UpdateState(
        XElement measure,
        MeasureState state)
    {
        var attributes = measure.Element("attributes");

        if (attributes is null)
        {
            return;
        }

        state.Divisions = ReadPositiveInt(
            attributes.Element("divisions"),
            state.Divisions,
            "divisions");

        state.Staves = ReadPositiveInt(
            attributes.Element("staves"),
            state.Staves,
            "staves");

        var time = attributes.Element("time");

        if (time is null)
        {
            return;
        }

        state.Beats = ReadPositiveInt(
            time.Element("beats"),
            state.Beats,
            "beats");

        state.BeatType = ReadPositiveInt(
            time.Element("beat-type"),
            state.BeatType,
            "beat-type");
    }

    private static int ReadPositiveInt(
        XElement? element,
        int currentValue,
        string name)
    {
        if (element is null)
        {
            return currentValue;
        }

        if (!int.TryParse(
                element.Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var value)
            || value <= 0)
        {
            throw new InvalidDataException(
                $"Invalid MusicXML {name} value '{element.Value}'.");
        }

        return value;
    }

    private static void AddFullMeasureRests(
        XElement measure,
        MeasureState state)
    {
        var numerator = (long)state.Beats * 4 * state.Divisions;

        if (numerator % state.BeatType != 0)
        {
            throw new InvalidDataException(
                $"Cannot express full-measure rest for {state.Beats}/{state.BeatType} "
                + $"with divisions={state.Divisions} as an integer MusicXML duration.");
        }

        var duration = numerator / state.BeatType;

        if (duration <= 0)
        {
            throw new InvalidDataException(
                "Full-measure rest duration must be positive.");
        }

        var generated = new List<XElement>();

        for (var staff = 1; staff <= state.Staves; staff++)
        {
            if (staff > 1)
            {
                generated.Add(
                    new XElement(
                        "backup",
                        new XElement("duration", duration)));
            }

            var note = new XElement(
                "note",
                new XElement(
                    "rest",
                    new XAttribute("measure", "yes")),
                new XElement("duration", duration),
                new XElement("voice", VoiceForStaff(staff)));

            if (state.Staves > 1)
            {
                note.Add(new XElement("staff", staff));
            }

            generated.Add(note);
        }

        var rightBarline = measure.Elements("barline")
            .FirstOrDefault(barline =>
                string.Equals(
                    (string?)barline.Attribute("location"),
                    "right",
                    StringComparison.Ordinal));

        if (rightBarline is null)
        {
            measure.Add(generated);
        }
        else
        {
            rightBarline.AddBeforeSelf(generated);
        }
    }

    private static int VoiceForStaff(int staff)
    {
        // MuseScore itself commonly exports piano voices as 1..4 on staff 1,
        // 5..8 on staff 2, and so on. Keeping the same convention makes the
        // temporary serialization scaffolding unsurprising when inspected.
        return (staff - 1) * 4 + 1;
    }

    private sealed class MeasureState
    {
        public int Divisions { get; set; } = 1;
        public int Beats { get; set; } = 4;
        public int BeatType { get; set; } = 4;
        public int Staves { get; set; } = 1;
    }
}
