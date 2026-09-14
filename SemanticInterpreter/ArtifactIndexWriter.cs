using System.Net;
using System.Text;

namespace SvgMusic.Semantics;

public sealed class ArtifactIndexWriter
{
    public void Write(
        string outputDirectory,
        string inputSvgPath,
        string? title,
        string canonicalFileName,
        string musicXmlFileName,
        string compressedMusicXmlFileName,
        string factsFileName,
        string summaryFileName,
        string runLogFileName)
    {
        Directory.CreateDirectory(outputDirectory);

        var sourceSvgFileName = CopySourceFile(
            inputSvgPath,
            outputDirectory,
            "source.svg");

        var sourceMusicXmlPath = Path.ChangeExtension(
            inputSvgPath,
            ".musicxml");
        var sourceMusicXmlFileName = File.Exists(sourceMusicXmlPath)
            ? CopySourceFile(
                sourceMusicXmlPath,
                outputDirectory,
                "source.musicxml")
            : null;

        var commit = Environment.GetEnvironmentVariable("GITHUB_SHA");
        var displayTitle = string.IsNullOrWhiteSpace(title)
            ? "Semantic interpretation artifacts"
            : title;

        var artifacts = new List<ArtifactLink>
        {
            new(
                compressedMusicXmlFileName,
                "Compressed MusicXML (.mxl)",
                "Best choice for MuseScore; the browser should download it instead of displaying XML.",
                true),
            new(
                musicXmlFileName,
                "MusicXML (.musicxml)",
                "Generated MusicXML before compression.",
                true),
            new(
                canonicalFileName,
                "CanonicalNotation JSON",
                "Canonical v0.3 produced by the semantic passes.",
                true),
            new(
                factsFileName,
                "Semantic facts",
                "Facts and evidence emitted by individual semantic passes.",
                true),
            new(
                summaryFileName,
                "Run summary",
                "Counts and output paths from the semantic run.",
                true),
            new(
                runLogFileName,
                "Run log",
                "Console trace from the semantic pipeline.",
                true)
        };

        AddOptionalArtifact(
            artifacts,
            outputDirectory,
            "semantic.noteheads.svg",
            "Detected noteheads (SVG)",
            "Semantic NoteheadPass overlay: accepted filled noteheads are green, hollow noteheads blue, small-dot size cluster orange, off-grid ellipses gray.",
            false);

        AddOptionalArtifact(
            artifacts,
            outputDirectory,
            "semantic.noteheads.txt",
            "Notehead size ranking and decisions",
            "All ellipse candidates ranked by normalized size with small-dot split, staff-grid alignment and final NoteheadPass decision.",
            true);

        AddOptionalArtifact(
            artifacts,
            outputDirectory,
            "semantic.accidentals.svg",
            "Detected local accidentals (SVG)",
            "AccidentalPass overlay on top of notehead diagnostics: accepted accidentals are orange, explicit target noteheads have a solid orange ring, inherited targets a dashed orange ring.",
            false);

        AddOptionalArtifact(
            artifacts,
            outputDirectory,
            "semantic.accidentals.txt",
            "Accidental targets and propagation",
            "Classifier candidates, logical anchor matching, explicit notehead target and all noteheads affected until the next accidental on the same staff-step or measure end.",
            true);

        AddOptionalArtifact(
            artifacts,
            outputDirectory,
            "semantic.stems.svg",
            "Detected stem attachments (SVG)",
            "StemAttachmentPass overlay: accepted stems are magenta, cross-staff stems purple, attached noteheads are ringed, unmatched vertical candidates are faint gray dashed lines.",
            false);

        AddOptionalArtifact(
            artifacts,
            outputDirectory,
            "semantic.stems.txt",
            "Stem attachment decisions",
            "Per-stem geometry, attached noteheads, inferred stem direction, cross-staff status and unmatched vertical stroke candidates.",
            true);

        AddOptionalArtifact(
            artifacts,
            outputDirectory,
            "parser.ownership.svg",
            "Ownership coloring (SVG)",
            "Final staff+measure ownership coloring after G1-G4. Best visual oracle for semantic ownership.",
            false);

        AddOptionalArtifact(
            artifacts,
            outputDirectory,
            "parser.classified-symbols.svg",
            "Classified symbols (SVG)",
            "Audiveris symbol classifications overlaid on the original SVG.",
            false);

        AddOptionalArtifact(
            artifacts,
            outputDirectory,
            "parser.strokes.svg",
            "Primitive strokes (SVG)",
            "Straight strokes extracted by the SVG parser.",
            false);

        AddOptionalArtifact(
            artifacts,
            outputDirectory,
            "parser.arcs.svg",
            "Primitive arcs (SVG)",
            "Curved strokes and slur-like primitives extracted by the SVG parser.",
            false);

        AddOptionalArtifact(
            artifacts,
            outputDirectory,
            "parser.ellipses.svg",
            "Ellipse-like primitives (SVG)",
            "Filled and hollow ellipse-like primitives, including notehead candidates.",
            false);

        AddOptionalArtifact(
            artifacts,
            outputDirectory,
            "parser.contours.svg",
            "Clustered contours (SVG)",
            "Remaining contour instances with prototype labels.",
            false);

        AddOptionalArtifact(
            artifacts,
            outputDirectory,
            "parser.ownership.render-diagnostics.txt",
            "Ownership renderer diagnostics",
            "Per-shape ownership/rendering diagnostics used to distinguish analyzer misses from visualization problems.",
            true);

        artifacts.Add(new ArtifactLink(
            sourceSvgFileName,
            "Source SVG",
            "Exact SVG input used for this run.",
            false));

        if (sourceMusicXmlFileName is not null)
        {
            artifacts.Add(new ArtifactLink(
                sourceMusicXmlFileName,
                "Reference source MusicXML",
                "MuseScore source used as a visual and semantic oracle for this fixture.",
                true));
        }

        WriteHtml(
            Path.Combine(outputDirectory, "index.html"),
            displayTitle,
            commit,
            artifacts);

        WriteMarkdown(
            Path.Combine(outputDirectory, "README.md"),
            displayTitle,
            commit,
            artifacts);
    }

    private static void AddOptionalArtifact(
        ICollection<ArtifactLink> artifacts,
        string outputDirectory,
        string fileName,
        string label,
        string description,
        bool download)
    {
        if (!File.Exists(Path.Combine(outputDirectory, fileName)))
        {
            return;
        }

        artifacts.Add(new ArtifactLink(
            fileName,
            label,
            description,
            download));
    }

    private static string CopySourceFile(
        string sourcePath,
        string outputDirectory,
        string targetFileName)
    {
        var targetPath = Path.Combine(
            outputDirectory,
            targetFileName);

        File.Copy(
            sourcePath,
            targetPath,
            overwrite: true);

        return targetFileName;
    }

    private static void WriteHtml(
        string fileName,
        string title,
        string? commit,
        IReadOnlyList<ArtifactLink> artifacts)
    {
        var html = new StringBuilder();
        html.AppendLine("<!doctype html>");
        html.AppendLine("<html lang=\"en\">");
        html.AppendLine("<head>");
        html.AppendLine("  <meta charset=\"utf-8\">");
        html.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        html.AppendLine($"  <title>{Encode(title)} - artifacts</title>");
        html.AppendLine("  <style>");
        html.AppendLine("    body { font-family: system-ui, sans-serif; max-width: 900px; margin: 40px auto; padding: 0 20px; line-height: 1.45; }");
        html.AppendLine("    h1 { margin-bottom: 0.25rem; }");
        html.AppendLine("    .meta { color: #666; margin-bottom: 2rem; }");
        html.AppendLine("    .artifact { border: 1px solid #ddd; border-radius: 10px; padding: 14px 16px; margin: 12px 0; }");
        html.AppendLine("    .artifact.primary { border-width: 2px; }");
        html.AppendLine("    .artifact a { font-size: 1.05rem; font-weight: 650; }");
        html.AppendLine("    .artifact p { margin: 0.4rem 0 0; color: #555; }");
        html.AppendLine("    code { overflow-wrap: anywhere; }");
        html.AppendLine("  </style>");
        html.AppendLine("</head>");
        html.AppendLine("<body>");
        html.AppendLine($"  <h1>{Encode(title)}</h1>");
        html.AppendLine("  <div class=\"meta\">Semantic interpreter run artifacts");

        if (!string.IsNullOrWhiteSpace(commit))
        {
            html.AppendLine($"    <br>commit <code>{Encode(commit)}</code>");
        }

        html.AppendLine("  </div>");

        for (var index = 0; index < artifacts.Count; index++)
        {
            var artifact = artifacts[index];
            var className = index == 0
                ? "artifact primary"
                : "artifact";
            var download = artifact.Download
                ? " download"
                : string.Empty;

            html.AppendLine($"  <div class=\"{className}\">");
            html.AppendLine($"    <a href=\"{Encode(artifact.FileName)}\"{download}>{Encode(artifact.Label)}</a>");
            html.AppendLine($"    <p>{Encode(artifact.Description)}</p>");
            html.AppendLine("  </div>");
        }

        html.AppendLine("</body>");
        html.AppendLine("</html>");

        File.WriteAllText(
            fileName,
            html.ToString(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void WriteMarkdown(
        string fileName,
        string title,
        string? commit,
        IReadOnlyList<ArtifactLink> artifacts)
    {
        var markdown = new StringBuilder();
        markdown.AppendLine($"# {title} - artifacts");
        markdown.AppendLine();

        if (!string.IsNullOrWhiteSpace(commit))
        {
            markdown.AppendLine($"Commit: `{commit}`");
            markdown.AppendLine();
        }

        foreach (var artifact in artifacts)
        {
            markdown.AppendLine($"- [{artifact.Label}]({artifact.FileName}) — {artifact.Description}");
        }

        File.WriteAllText(
            fileName,
            markdown.ToString(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string Encode(string value)
    {
        return WebUtility.HtmlEncode(value);
    }

    private sealed record ArtifactLink(
        string FileName,
        string Label,
        string Description,
        bool Download);
}
