using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SvgMusic.Canonical;

if (args.Length < 2)
{
    PrintUsage();
    return 2;
}

var inputDirectory = Path.GetFullPath(args[0]);
var outputDirectory = Path.GetFullPath(args[1]);
var baseName = ReadOption(args, "--base");
var referencePath = ReadOption(args, "--reference");
var modelPath = ReadOption(args, "--model");
var configuration = ReadOption(args, "--configuration") ?? "Release";
var noBuild = HasFlag(args, "--no-build");
var failOnDiff = HasFlag(args, "--fail-on-diff");
var noDiagnosticArchive = HasFlag(
    args,
    "--no-diagnostic-archive");

if (!Directory.Exists(inputDirectory))
{
    Console.Error.WriteLine($"Input directory not found: {inputDirectory}");
    return 2;
}

var pageSet = DiscoverPages(inputDirectory, baseName);
if (pageSet.Pages.Count == 0)
{
    Console.Error.WriteLine(
        $"No SVG pages matching <name>-N.svg were found in {inputDirectory}");
    return 2;
}

Directory.CreateDirectory(outputDirectory);

var repoRoot = FindRepositoryRoot();
var semanticProject = Path.Combine(
    repoRoot,
    "SemanticInterpreter",
    "SemanticInterpreter.csproj");

Console.WriteLine($"Score       : {pageSet.BaseName}");
Console.WriteLine($"Pages       : {pageSet.Pages.Count}");
Console.WriteLine($"Input       : {inputDirectory}");
Console.WriteLine($"Output      : {outputDirectory}");
Console.WriteLine($"Repository  : {repoRoot}");

if (!noBuild)
{
    Console.WriteLine();
    Console.WriteLine("Building SemanticInterpreter once...");
    var buildExit = await RunProcessAsync(
        "dotnet",
        [
            "build",
            semanticProject,
            "--configuration",
            configuration
        ],
        repoRoot);

    if (buildExit != 0)
    {
        return buildExit;
    }
}

var pageCanonicals = new List<CanonicalNotation>();
TimeSignature? activeTimeSignature = null;
KeySignature? activeKeySignature = null;
var pagesDirectory = Path.Combine(
    outputDirectory,
    "pages");
Directory.CreateDirectory(pagesDirectory);

foreach (var page in pageSet.Pages)
{
    var inheritedTimeForPage = activeTimeSignature;
    var inheritedKeyForPage = activeKeySignature;
    var pageOutput = Path.Combine(
        pagesDirectory,
        $"{page.Number:D3}");
    Directory.CreateDirectory(pageOutput);

    Console.WriteLine();
    Console.WriteLine(
        $"=== Page {page.Number:D3}: {Path.GetFileName(page.Path)} ===");

    var processArgs = new List<string>
    {
        "run",
        "--project",
        semanticProject,
        "--configuration",
        configuration,
        "--no-build",
        "--",
        page.Path,
        pageOutput
    };

    if (!string.IsNullOrWhiteSpace(modelPath))
    {
        processArgs.Add("--model");
        processArgs.Add(Path.GetFullPath(modelPath));
    }

    if (activeTimeSignature is not null)
    {
        processArgs.Add("--initial-time");
        processArgs.Add(
            $"{activeTimeSignature.Beats}/{activeTimeSignature.BeatType}");
        Console.WriteLine(
            $"Inherited time: {activeTimeSignature.Beats}/{activeTimeSignature.BeatType}");
    }

    if (activeKeySignature is not null)
    {
        processArgs.Add("--initial-key");
        processArgs.Add(
            activeKeySignature.Fifths.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        Console.WriteLine(
            $"Inherited key : fifths={activeKeySignature.Fifths}");
    }

    var exitCode = await RunProcessAsync(
        "dotnet",
        processArgs,
        repoRoot);

    if (exitCode != 0)
    {
        Console.Error.WriteLine(
            $"Page {page.Number:D3} failed with exit code {exitCode}.");
        return exitCode;
    }

    var canonicalPath = Path.Combine(
        pageOutput,
        "kancheli.semantic.canonical.json");

    if (!File.Exists(canonicalPath))
    {
        Console.Error.WriteLine(
            $"Page canonical output not found: {canonicalPath}");
        return 3;
    }

    var pageCanonical = CanonicalJson.Read(
        canonicalPath);
    pageCanonicals.Add(pageCanonical);

    if (inheritedTimeForPage is not null
        || inheritedKeyForPage is not null)
    {
        WriteStandaloneContinuationPreview(
            pageCanonical,
            inheritedTimeForPage,
            inheritedKeyForPage,
            pageOutput);
    }

    activeTimeSignature = ResolveFinalTimeSignature(
        pageCanonical,
        activeTimeSignature);
    activeKeySignature = ResolveFinalKeySignature(
        pageCanonical,
        activeKeySignature);
}

Console.WriteLine();
Console.WriteLine("Merging page canonicals...");
var merged = new CanonicalScoreMerger().Merge(
    pageCanonicals);

var canonicalOutput = Path.Combine(
    outputDirectory,
    $"{pageSet.BaseName}.canonical.json");
var musicXmlOutput = Path.Combine(
    outputDirectory,
    $"{pageSet.BaseName}.musicxml");
var mxlOutput = Path.Combine(
    outputDirectory,
    $"{pageSet.BaseName}.mxl");

await File.WriteAllTextAsync(
    canonicalOutput,
    CanonicalJson.Serialize(merged));
new MuseScoreCompatibleMusicXmlWriter().Write(
    merged,
    musicXmlOutput);
new CompressedMusicXmlWriter().Write(
    merged,
    mxlOutput);

Console.WriteLine($"Canonical   : {canonicalOutput}");
Console.WriteLine($"MusicXML    : {musicXmlOutput}");
Console.WriteLine($"MXL         : {mxlOutput}");

CanonicalDiffReport? diff = null;

if (!string.IsNullOrWhiteSpace(referencePath))
{
    var referenceFullPath = Path.GetFullPath(
        referencePath);
    Console.WriteLine();
    Console.WriteLine(
        $"Canonicalizing reference: {referenceFullPath}");

    var reference = ReadCanonical(
        referenceFullPath);
    var referenceCanonicalPath = Path.Combine(
        outputDirectory,
        $"{pageSet.BaseName}.reference.canonical.json");

    await File.WriteAllTextAsync(
        referenceCanonicalPath,
        CanonicalJson.Serialize(reference));

    diff = new CanonicalComparer().Compare(
        reference,
        merged);
    diff = DecorateWithPageOrigins(
        diff,
        pageCanonicals);

    var diffPrefix = Path.Combine(
        outputDirectory,
        $"{pageSet.BaseName}.diff");
    var diffMarkdown = diffPrefix + ".md";
    var diffJson = diffPrefix + ".json";

    await File.WriteAllTextAsync(
        diffMarkdown,
        diff.ToMarkdown());
    await File.WriteAllTextAsync(
        diffJson,
        diff.ToJson());

    Console.WriteLine(
        diff.IsEqual
            ? "Semantic diff: clean."
            : $"Semantic diff: {diff.Issues.Count} issue(s).");

    foreach (var group in diff.RootCauses
                 .GroupBy(root => root.Category)
                 .OrderBy(group => group.Key))
    {
        Console.WriteLine(
            $"  {group.Key,-12} roots={group.Count(),3} "
            + $"details={group.Sum(root => root.Issues.Count),4}");
    }

    Console.WriteLine($"Diff MD     : {diffMarkdown}");
    Console.WriteLine($"Diff JSON   : {diffJson}");
}

if (!noDiagnosticArchive)
{
    var archivePath = Path.Combine(
        outputDirectory,
        $"{pageSet.BaseName}.diagnostics.zip");

    await CreateDiagnosticArchiveAsync(
        archivePath,
        outputDirectory,
        pageSet,
        referencePath,
        repoRoot);

    Console.WriteLine($"Diagnostics : {archivePath}");
}

return failOnDiff
    && diff is { IsEqual: false }
        ? 1
        : 0;

static void WriteStandaloneContinuationPreview(
    CanonicalNotation page,
    TimeSignature? inheritedTime,
    KeySignature? inheritedKey,
    string pageOutput)
{
    if (page.Parts.Count == 0
        || page.Parts[0].Measures.Count == 0)
    {
        return;
    }

    var parts = page.Parts
        .Select((part, partIndex) =>
        {
            if (partIndex != 0
                || part.Measures.Count == 0)
            {
                return part;
            }

            var measures = part.Measures.ToList();
            var firstIndex = measures
                .Select((measure, index) => new
                {
                    measure.Number,
                    index
                })
                .OrderBy(item => item.Number)
                .First()
                .index;
            var first = measures[firstIndex];
            var attributes = first.Attributes
                ?? new MeasureAttributes();

            measures[firstIndex] = first with
            {
                Attributes = attributes with
                {
                    Time = attributes.Time
                        ?? inheritedTime,
                    Key = attributes.Key
                        ?? inheritedKey
                }
            };

            return part with
            {
                Measures = measures
            };
        })
        .ToList();

    var preview = page with
    {
        Parts = parts
    };

    new MuseScoreCompatibleMusicXmlWriter().Write(
        preview,
        Path.Combine(
            pageOutput,
            "kancheli.semantic.musicxml"));
    new CompressedMusicXmlWriter().Write(
        preview,
        Path.Combine(
            pageOutput,
            "kancheli.semantic.mxl"));
}

static CanonicalDiffReport DecorateWithPageOrigins(
    CanonicalDiffReport report,
    IReadOnlyList<CanonicalNotation> pages)
{
    var origins = new Dictionary<
        int,
        (int Page, int LocalMeasure)>();
    var offset = 0;

    for (var pageIndex = 0;
         pageIndex < pages.Count;
         pageIndex++)
    {
        var page = pages[pageIndex];
        var numbers = page.Parts
            .SelectMany(part => part.Measures)
            .Select(measure => measure.Number)
            .Distinct()
            .OrderBy(number => number)
            .ToArray();

        foreach (var localMeasure in numbers)
        {
            origins[offset + localMeasure] =
                (pageIndex + 1, localMeasure);
        }

        offset += numbers.Length;
    }

    return report with
    {
        Issues = report.Issues
            .Select(issue =>
            {
                if (issue.Measure is null
                    || !origins.TryGetValue(
                        issue.Measure.Value,
                        out var origin))
                {
                    return issue;
                }

                return issue with
                {
                    Page = origin.Page,
                    LocalMeasure = origin.LocalMeasure
                };
            })
            .ToArray()
    };
}

static async Task CreateDiagnosticArchiveAsync(
    string archivePath,
    string outputDirectory,
    PageSet pageSet,
    string? referencePath,
    string repoRoot)
{
    if (File.Exists(archivePath))
    {
        File.Delete(archivePath);
    }

    var outputFiles = Directory
        .EnumerateFiles(
            outputDirectory,
            "*",
            SearchOption.AllDirectories)
        .Where(path => !Path.GetFullPath(path).Equals(
            Path.GetFullPath(archivePath),
            StringComparison.OrdinalIgnoreCase))
        .ToArray();

    using var archive = ZipFile.Open(
        archivePath,
        ZipArchiveMode.Create);

    foreach (var path in outputFiles)
    {
        var relative = Path.GetRelativePath(
            outputDirectory,
            path);
        archive.CreateEntryFromFile(
            path,
            "out/" + relative.Replace(
                '\\',
                '/'),
            CompressionLevel.Optimal);
    }

    foreach (var page in pageSet.Pages)
    {
        archive.CreateEntryFromFile(
            page.Path,
            "input/svg/" + Path.GetFileName(page.Path),
            CompressionLevel.Optimal);
    }

    string? referenceFullPath = null;

    if (!string.IsNullOrWhiteSpace(referencePath))
    {
        referenceFullPath = Path.GetFullPath(
            referencePath);

        if (File.Exists(referenceFullPath))
        {
            archive.CreateEntryFromFile(
                referenceFullPath,
                "input/reference/"
                + Path.GetFileName(referenceFullPath),
                CompressionLevel.Optimal);
        }
    }

    var manifest = new
    {
        score = pageSet.BaseName,
        createdUtc = DateTimeOffset.UtcNow,
        gitHead = await TryReadGitHeadAsync(repoRoot),
        commandLine = Environment.CommandLine,
        pages = pageSet.Pages.Select(page => new
        {
            page = page.Number,
            file = Path.GetFileName(page.Path)
        }),
        reference = referenceFullPath is null
            ? null
            : Path.GetFileName(referenceFullPath)
    };

    var entry = archive.CreateEntry(
        "diagnostics-manifest.json",
        CompressionLevel.Optimal);

    await using var stream = entry.Open();
    await using var writer = new StreamWriter(
        stream,
        new UTF8Encoding(false));

    await writer.WriteAsync(
        JsonSerializer.Serialize(
            manifest,
            new JsonSerializerOptions
            {
                WriteIndented = true
            }));
}

static async Task<string?> TryReadGitHeadAsync(
    string repoRoot)
{
    try
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("rev-parse");
        startInfo.ArgumentList.Add("HEAD");

        using var process = Process.Start(startInfo);

        if (process is null)
        {
            return null;
        }

        var value = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        return process.ExitCode == 0
            ? value.Trim()
            : null;
    }
    catch
    {
        return null;
    }
}

static PageSet DiscoverPages(
    string directory,
    string? requestedBase)
{
    var regex = new Regex(
        @"^(?<base>.+)-(?<page>\d+)\.svg$",
        RegexOptions.IgnoreCase
        | RegexOptions.CultureInvariant);

    var matches = Directory
        .EnumerateFiles(
            directory,
            "*.svg",
            SearchOption.TopDirectoryOnly)
        .Select(path => new
        {
            Path = path,
            Match = regex.Match(
                Path.GetFileName(path))
        })
        .Where(item => item.Match.Success)
        .Select(item => new SvgPage(
            item.Match.Groups["base"].Value,
            int.Parse(
                item.Match.Groups["page"].Value),
            item.Path))
        .GroupBy(
            page => page.BaseName,
            StringComparer.OrdinalIgnoreCase)
        .ToArray();

    if (!string.IsNullOrWhiteSpace(requestedBase))
    {
        var selected = matches.FirstOrDefault(group =>
            string.Equals(
                group.Key,
                requestedBase,
                StringComparison.OrdinalIgnoreCase));

        if (selected is null)
        {
            throw new InvalidDataException(
                $"No SVG page set named '{requestedBase}' was found.");
        }

        return ValidatePageSet(
            selected.Key,
            selected);
    }

    if (matches.Length == 0)
    {
        return new PageSet(
            string.Empty,
            []);
    }

    if (matches.Length > 1)
    {
        throw new InvalidDataException(
            "More than one <name>-N.svg score was found. "
            + "Use --base <name>. Found: "
            + string.Join(
                ", ",
                matches.Select(group => group.Key)));
    }

    return ValidatePageSet(
        matches[0].Key,
        matches[0]);
}

static PageSet ValidatePageSet(
    string baseName,
    IEnumerable<SvgPage> pages)
{
    var ordered = pages
        .OrderBy(page => page.Number)
        .ToArray();

    var duplicates = ordered
        .GroupBy(page => page.Number)
        .Where(group => group.Count() > 1)
        .Select(group => group.Key)
        .ToArray();

    if (duplicates.Length > 0)
    {
        throw new InvalidDataException(
            "Duplicate SVG page numbers: "
            + string.Join(", ", duplicates));
    }

    return new PageSet(
        baseName,
        ordered);
}

static KeySignature? ResolveFinalKeySignature(
    CanonicalNotation page,
    KeySignature? inherited)
{
    var current = inherited;
    var measures = page.Parts
        .FirstOrDefault()
        ?.Measures
        ?? [];

    foreach (var measure in measures
                 .OrderBy(measure => measure.Number))
    {
        if (measure.Attributes?.Key is { } key)
        {
            current = key;
        }
    }

    return current;
}

static TimeSignature? ResolveFinalTimeSignature(
    CanonicalNotation page,
    TimeSignature? inherited)
{
    var current = inherited;
    var measures = page.Parts
        .FirstOrDefault()
        ?.Measures
        ?? [];

    foreach (var measure in measures
                 .OrderBy(measure => measure.Number))
    {
        if (measure.Attributes?.Time is { } time)
        {
            current = time;
        }
    }

    return current;
}

static CanonicalNotation ReadCanonical(
    string path)
{
    return Path.GetExtension(path).Equals(
            ".json",
            StringComparison.OrdinalIgnoreCase)
        ? CanonicalJson.Read(path)
        : new MusicXmlCanonicalizer().Read(path);
}

static string FindRepositoryRoot()
{
    foreach (var start in new[]
             {
                 Directory.GetCurrentDirectory(),
                 AppContext.BaseDirectory
             })
    {
        var directory = new DirectoryInfo(start);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(
                    directory.FullName,
                    "SemanticInterpreter",
                    "SemanticInterpreter.csproj")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }
    }

    throw new DirectoryNotFoundException(
        "Could not locate repository root containing SemanticInterpreter.");
}

static async Task<int> RunProcessAsync(
    string fileName,
    IEnumerable<string> arguments,
    string workingDirectory)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = fileName,
        WorkingDirectory = workingDirectory,
        UseShellExecute = false
    };

    foreach (var argument in arguments)
    {
        startInfo.ArgumentList.Add(argument);
    }

    using var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException(
            $"Could not start {fileName}.");

    await process.WaitForExitAsync();
    return process.ExitCode;
}

static string? ReadOption(
    IReadOnlyList<string> arguments,
    string name)
{
    for (var index = 0;
         index < arguments.Count - 1;
         index++)
    {
        if (string.Equals(
                arguments[index],
                name,
                StringComparison.Ordinal))
        {
            return arguments[index + 1];
        }
    }

    return null;
}

static bool HasFlag(
    IReadOnlyList<string> arguments,
    string name) =>
    arguments.Any(argument =>
        string.Equals(
            argument,
            name,
            StringComparison.Ordinal));

static void PrintUsage()
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine(
        "  SvgScoreBatch <svg-folder> <output-folder> "
        + "[--base <name>] [--reference <musicxml|json>] "
        + "[--model <classifier.zip>] [--configuration Release] "
        + "[--no-build] [--fail-on-diff] [--no-diagnostic-archive]");
    Console.Error.WriteLine();
    Console.Error.WriteLine(
        "SVG pages must be named <name>-N.svg, for example "
        + "prelude-1.svg, prelude-2.svg.");
}

sealed record SvgPage(
    string BaseName,
    int Number,
    string Path);

sealed record PageSet(
    string BaseName,
    IReadOnlyList<SvgPage> Pages);
