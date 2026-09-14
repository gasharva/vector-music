using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace SvgMusic.Canonical;

public sealed class CompressedMusicXmlWriter
{
    private const string Mimetype = "application/vnd.recordare.musicxml";
    private const string RootMediaType = "application/vnd.recordare.musicxml+xml";

    public void Write(
        CanonicalNotation score,
        string fileName,
        string scoreEntryName = "score.musicxml")
    {
        if (string.IsNullOrWhiteSpace(scoreEntryName))
        {
            throw new ArgumentException(
                "A score entry name is required.",
                nameof(scoreEntryName));
        }

        using var file = File.Create(fileName);
        using var archive = new ZipArchive(
            file,
            ZipArchiveMode.Create,
            leaveOpen: false);

        WriteMimetypeEntry(archive);
        WriteContainerEntry(archive, scoreEntryName);
        WriteScoreEntry(archive, score, scoreEntryName);
    }

    private static void WriteMimetypeEntry(ZipArchive archive)
    {
        var entry = archive.CreateEntry(
            "mimetype",
            CompressionLevel.NoCompression);

        using var stream = entry.Open();
        using var writer = new StreamWriter(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            leaveOpen: false);

        writer.Write(Mimetype);
    }

    private static void WriteContainerEntry(
        ZipArchive archive,
        string scoreEntryName)
    {
        var document = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement(
                XName.Get("container", "urn:oasis:names:tc:opendocument:xmlns:container"),
                new XAttribute("version", "1.0"),
                new XElement(
                    XName.Get("rootfiles", "urn:oasis:names:tc:opendocument:xmlns:container"),
                    new XElement(
                        XName.Get("rootfile", "urn:oasis:names:tc:opendocument:xmlns:container"),
                        new XAttribute("full-path", scoreEntryName),
                        new XAttribute("media-type", RootMediaType)))));

        var entry = archive.CreateEntry(
            "META-INF/container.xml",
            CompressionLevel.Optimal);

        using var stream = entry.Open();
        document.Save(stream);
    }

    private static void WriteScoreEntry(
        ZipArchive archive,
        CanonicalNotation score,
        string scoreEntryName)
    {
        var entry = archive.CreateEntry(
            scoreEntryName,
            CompressionLevel.Optimal);

        using var stream = entry.Open();
        new MusicXmlWriter()
            .Write(score)
            .Save(stream);
    }
}
