using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class StressTreeRunner
{
    private static readonly Regex PageRegex = new(
        @"^(?<base>.+)-(?<page>\d+)\.svg$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool ShouldRun(string[] args)
    {
        if (args.Length == 0 || !Directory.Exists(args[0]))
        {
            return false;
        }

        return HasFlag(args, "--tree")
            || args.Length == 1
            || args[1].StartsWith("--", StringComparison.Ordinal);
    }

    public static async Task<int> RunAsync(string[] args)
    {
        var root = Path.GetFullPath(args[0]);
        var configuration = ReadOption(args, "--configuration") ?? "Release";
        var modelPath = ReadOption(args, "--model");
        var diffLevel = (ReadOption(args, "--diff-level") ?? "error").ToLowerInvariant();
        var noBuild = HasFlag(args, "--no-build");
        var failOnDiff = HasFlag(args, "--fail-on-diff");
        var noDiagnosticArchive = HasFlag(args, "--no-diagnostic-archive");
        var debugSemantic = HasFlag(args, "--debug-semantic");
        var keepExistingOut = HasFlag(args, "--keep-existing-out");

        if (ReadOption(args, "--reference") is not null
            || ReadOption(args, "--base") is not null)
        {
            Console.Error.WriteLine(
                "Tree mode discovers the reference and SVG page set automatically; "
                + "--reference and --base are not supported.");
            return 2;
        }

        if (SeverityRank(diffLevel) < 0)
        {
            Console.Error.WriteLine(
                $"Invalid --diff-level '{diffLevel}'. Expected warning, error, or critical.");
            return 2;
        }

        var patients = DiscoverPatients(root);
        if (patients.Count == 0)
        {
            Console.Error.WriteLine(
                "No patient folders found. Expected exactly one *.musicxml "
                + "and one SVG page set (<name>-N.svg) in the same directory.");
            return 2;
        }

        var repoRoot = FindRepositoryRoot();
        var semanticProject = Path.Combine(
            repoRoot,
            "SemanticInterpreter",
            "SemanticInterpreter.csproj");

        Console.WriteLine($"Stress root : {root}");
        Console.WriteLine($"Patients    : {patients.Count}");
        Console.WriteLine($"Diff level  : {diffLevel}+");
        Console.WriteLine();

        if (!noBuild)
        {
            var buildLog = Path.Combine(root, "stress-build.log");
            Console.WriteLine("Building SemanticInterpreter once...");

            var buildExit = await RunCapturedAsync(
                "dotnet",
                new[]
                {
                    "build",
                    semanticProject,
                    "--configuration",
                    configuration
                },
                repoRoot,
                buildLog);

            if (buildExit != 0)
            {
                Console.Error.WriteLine(
                    $"Build failed with exit code {buildExit}. See {buildLog}");
                PrintLogTail(buildLog);
                return buildExit;
            }

            Console.WriteLine("Build OK.");
            Console.WriteLine();
        }

        var results = new List<StressPatientResult>();
        var batchAssembly = Assembly.GetExecutingAssembly().Location;

        for (var index = 0; index < patients.Count; index++)
        {
            var patient = patients[index];
            var display = Path.GetRelativePath(root, patient.Directory)
                .Replace('\\', '/');
            var outDirectory = Path.Combine(patient.Directory, "out");

            if (!keepExistingOut && Directory.Exists(outDirectory))
            {
                Directory.Delete(outDirectory, recursive: true);
            }

            Directory.CreateDirectory(outDirectory);

            var logPath = Path.Combine(outDirectory, "batch.log");
            Console.Write(
                $"[{index + 1}/{patients.Count}] {display} "
                + $"({patient.Pages.Count} page(s)) ... ");

            var childArgs = new List<string>
            {
                batchAssembly,
                patient.Directory,
                outDirectory,
                "--base",
                patient.BaseName,
                "--reference",
                patient.ReferencePath,
                "--configuration",
                configuration,
                "--no-build",
                "--diff-level",
                diffLevel
            };

            if (!string.IsNullOrWhiteSpace(modelPath))
            {
                childArgs.Add("--model");
                childArgs.Add(Path.GetFullPath(modelPath));
            }

            if (debugSemantic)
            {
                childArgs.Add("--debug-semantic");
            }

            if (noDiagnosticArchive)
            {
                childArgs.Add("--no-diagnostic-archive");
            }

            var exitCode = await RunCapturedAsync(
                "dotnet",
                childArgs,
                repoRoot,
                logPath);

            var selectedDiffPath = Path.Combine(
                outDirectory,
                $"{patient.BaseName}.diff.json");
            var fullDiffPath = Path.Combine(
                outDirectory,
                $"{patient.BaseName}.diff.all.json");

            var selected = File.Exists(selectedDiffPath)
                ? ReadDiffSnapshot(selectedDiffPath)
                : DiffSnapshot.Empty;
            var full = File.Exists(fullDiffPath)
                ? ReadDiffSnapshot(fullDiffPath)
                : selected;

            string status;
            string? error = null;

            if (exitCode != 0)
            {
                status = "FAILED";
                error = ReadLogTail(logPath, 20);
            }
            else if (selected.Issues == 0)
            {
                status = "clean";
            }
            else
            {
                status = "differences";
            }

            results.Add(new StressPatientResult(
                display,
                patient.BaseName,
                Path.GetFileName(patient.ReferencePath),
                patient.Pages.Count,
                status,
                exitCode,
                selected.Issues,
                selected.RootCauses,
                selected.CategoryCounts,
                selected.CodeCounts,
                full.SeverityCounts,
                error));

            if (status == "FAILED")
            {
                Console.WriteLine($"FAILED (exit {exitCode})");
                PrintLogTail(logPath);
            }
            else
            {
                Console.WriteLine(
                    selected.Issues == 0
                        ? "clean"
                        : $"{selected.Issues} problem(s), {selected.RootCauses} root cause(s)");
            }
        }

        var markdownPath = Path.Combine(root, "stress-summary.md");
        var jsonPath = Path.Combine(root, "stress-summary.json");

        await File.WriteAllTextAsync(
            markdownPath,
            BuildMarkdown(root, diffLevel, results),
            new UTF8Encoding(false));

        await File.WriteAllTextAsync(
            jsonPath,
            BuildJson(root, diffLevel, results),
            new UTF8Encoding(false));

        var failed = results.Count(result => result.Status == "FAILED");
        var selectedProblems = results.Sum(result => result.Problems);
        var clean = results.Count(result => result.Status == "clean");

        Console.WriteLine();
        Console.WriteLine("Stress run complete:");
        Console.WriteLine($"  patients : {results.Count}");
        Console.WriteLine($"  clean    : {clean}");
        Console.WriteLine($"  failed   : {failed}");
        Console.WriteLine($"  problems : {selectedProblems} ({diffLevel}+)");
        Console.WriteLine($"  synopsis : {markdownPath}");
        Console.WriteLine($"  json     : {jsonPath}");

        if (failed > 0)
        {
            return 3;
        }

        return failOnDiff && selectedProblems > 0 ? 1 : 0;
    }

    private static IReadOnlyList<StressPatient> DiscoverPatients(string root)
    {
        var directories = new List<string> { root };
        directories.AddRange(
            Directory.EnumerateDirectories(
                root,
                "*",
                SearchOption.AllDirectories));

        var result = new List<StressPatient>();

        foreach (var directory in directories)
        {
            if (IsInsideOutDirectory(root, directory))
            {
                continue;
            }

            var references = Directory
                .EnumerateFiles(
                    directory,
                    "*.musicxml",
                    SearchOption.TopDirectoryOnly)
                .ToArray();

            if (references.Length != 1)
            {
                continue;
            }

            var pageGroups = Directory
                .EnumerateFiles(
                    directory,
                    "*.svg",
                    SearchOption.TopDirectoryOnly)
                .Select(path => new
                {
                    Path = path,
                    Match = PageRegex.Match(Path.GetFileName(path))
                })
                .Where(item => item.Match.Success)
                .Select(item => new StressSvgPage(
                    item.Match.Groups["base"].Value,
                    int.Parse(item.Match.Groups["page"].Value),
                    item.Path))
                .GroupBy(
                    page => page.BaseName,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (pageGroups.Length != 1)
            {
                continue;
            }

            var pages = pageGroups[0]
                .OrderBy(page => page.Number)
                .ToArray();

            if (pages
                .GroupBy(page => page.Number)
                .Any(group => group.Count() > 1))
            {
                continue;
            }

            result.Add(new StressPatient(
                directory,
                references[0],
                pageGroups[0].Key,
                pages));
        }

        return result
            .OrderBy(
                patient => Path.GetRelativePath(root, patient.Directory),
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsInsideOutDirectory(
        string root,
        string directory)
    {
        var relative = Path.GetRelativePath(root, directory);

        if (relative == ".")
        {
            return false;
        }

        return relative
            .Split(
                new[]
                {
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar
                },
                StringSplitOptions.RemoveEmptyEntries)
            .Any(segment =>
                segment.Equals(
                    "out",
                    StringComparison.OrdinalIgnoreCase));
    }

    private static DiffSnapshot ReadDiffSnapshot(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        var severityCounts = new Dictionary<string, int>(
            StringComparer.OrdinalIgnoreCase);
        var categoryCounts = new Dictionary<string, int>(
            StringComparer.OrdinalIgnoreCase);
        var codeCounts = new Dictionary<string, int>(
            StringComparer.OrdinalIgnoreCase);

        var issues = 0;

        if (root.TryGetProperty("issues", out var issueArray)
            && issueArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var issue in issueArray.EnumerateArray())
            {
                issues++;
                Increment(severityCounts, GetString(issue, "severity") ?? "Unknown");
                Increment(categoryCounts, GetString(issue, "category") ?? "Unknown");
                Increment(codeCounts, GetString(issue, "code") ?? "unknown");
            }
        }

        var roots = root.TryGetProperty("rootCauses", out var rootCauseArray)
            && rootCauseArray.ValueKind == JsonValueKind.Array
                ? rootCauseArray.GetArrayLength()
                : 0;

        return new DiffSnapshot(
            issues,
            roots,
            severityCounts,
            categoryCounts,
            codeCounts);
    }

    private static string? GetString(
        JsonElement element,
        string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static void Increment(
        IDictionary<string, int> counts,
        string key)
    {
        counts[key] = counts.TryGetValue(key, out var current)
            ? current + 1
            : 1;
    }

    private static string BuildMarkdown(
        string root,
        string diffLevel,
        IReadOnlyList<StressPatientResult> results)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# SVG music stress synopsis");
        sb.AppendLine();
        sb.AppendLine($"Generated: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"Root: {root}");
        sb.AppendLine($"Patients: {results.Count}");
        sb.AppendLine($"Problem threshold: {diffLevel}+");
        sb.AppendLine();
        sb.AppendLine(
            "Problems and Root causes use the configured diff threshold. "
            + "Severity columns come from the full diff.");
        sb.AppendLine();

        sb.AppendLine("## Patients");
        sb.AppendLine();
        sb.AppendLine(
            "| Patient | Pages | Status | Problems | Root causes | Critical | Error | Warning |");
        sb.AppendLine(
            "|---|---:|---|---:|---:|---:|---:|---:|");

        foreach (var result in results)
        {
            var critical = Count(result.SeverityCounts, "Critical");
            var error = Count(result.SeverityCounts, "Error");
            var warning = Count(result.SeverityCounts, "Warning");
            var display = EscapeMarkdown(result.RelativePath);
            var diffRelative = (
                    result.RelativePath
                    + "/out/"
                    + result.BaseName
                    + ".diff.md")
                .Replace('\\', '/')
                .Replace(" ", "%20");

            var patientCell = result.ExitCode == 0
                ? $"[{display}]({diffRelative})"
                : display;

            sb.Append("| ")
                .Append(patientCell)
                .Append(" | ")
                .Append(result.Pages)
                .Append(" | ")
                .Append(result.Status)
                .Append(" | ")
                .Append(result.Problems)
                .Append(" | ")
                .Append(result.RootCauses)
                .Append(" | ")
                .Append(critical)
                .Append(" | ")
                .Append(error)
                .Append(" | ")
                .Append(warning)
                .AppendLine(" |");
        }

        AppendAggregateTable(
            sb,
            "Problems by category",
            "Category",
            results.SelectMany(result =>
                result.CategoryCounts.Select(pair => new AggregateItem(
                    result.RelativePath,
                    pair.Key,
                    pair.Value))));

        AppendAggregateTable(
            sb,
            "Problems by code",
            "Code",
            results.SelectMany(result =>
                result.CodeCounts.Select(pair => new AggregateItem(
                    result.RelativePath,
                    pair.Key,
                    pair.Value))));

        var failures = results
            .Where(result => result.ExitCode != 0)
            .ToArray();

        if (failures.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Failed patients");
            sb.AppendLine();

            foreach (var failure in failures)
            {
                sb.Append("### ")
                    .AppendLine(EscapeMarkdown(failure.RelativePath));
                sb.AppendLine();
                sb.AppendLine("See out/batch.log in the patient directory.");

                if (!string.IsNullOrWhiteSpace(failure.Error))
                {
                    sb.AppendLine();
                    sb.AppendLine("    " + failure.Error.Replace(
                        Environment.NewLine,
                        Environment.NewLine + "    "));
                }
            }
        }

        return sb.ToString();
    }

    private static void AppendAggregateTable(
        StringBuilder sb,
        string title,
        string firstColumn,
        IEnumerable<AggregateItem> items)
    {
        var rows = items
            .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Key = group.Key,
                Details = group.Sum(item => item.Count),
                Patients = group
                    .Select(item => item.Patient)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count()
            })
            .OrderByDescending(item => item.Details)
            .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        sb.AppendLine();
        sb.Append("## ").AppendLine(title);
        sb.AppendLine();

        if (rows.Length == 0)
        {
            sb.AppendLine("No problems at the selected diff level.");
            return;
        }

        sb.Append("| ")
            .Append(firstColumn)
            .AppendLine(" | Details | Affected patients |");
        sb.AppendLine("|---|---:|---:|");

        foreach (var row in rows)
        {
            sb.Append("| ")
                .Append(EscapeMarkdown(row.Key))
                .Append(" | ")
                .Append(row.Details)
                .Append(" | ")
                .Append(row.Patients)
                .AppendLine(" |");
        }
    }

    private static string BuildJson(
        string root,
        string diffLevel,
        IReadOnlyList<StressPatientResult> results)
    {
        var payload = new
        {
            generatedUtc = DateTimeOffset.UtcNow,
            root,
            diffLevel,
            totals = new
            {
                patients = results.Count,
                clean = results.Count(result => result.Status == "clean"),
                withDifferences = results.Count(result => result.Status == "differences"),
                failed = results.Count(result => result.Status == "FAILED"),
                problems = results.Sum(result => result.Problems),
                rootCauses = results.Sum(result => result.RootCauses),
                critical = results.Sum(result => Count(result.SeverityCounts, "Critical")),
                error = results.Sum(result => Count(result.SeverityCounts, "Error")),
                warning = results.Sum(result => Count(result.SeverityCounts, "Warning"))
            },
            patients = results
        };

        return JsonSerializer.Serialize(
            payload,
            new JsonSerializerOptions
            {
                WriteIndented = true
            });
    }

    private static int Count(
        IReadOnlyDictionary<string, int> counts,
        string key) =>
        counts.TryGetValue(key, out var value) ? value : 0;

    private static string EscapeMarkdown(string value) =>
        value.Replace("|", "\\|");

    private static int SeverityRank(string value) =>
        value.ToLowerInvariant() switch
        {
            "warning" => 0,
            "error" => 1,
            "critical" => 2,
            _ => -1
        };

    private static async Task<int> RunCapturedAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        string logPath)
    {
        Directory.CreateDirectory(
            Path.GetDirectoryName(logPath) ?? workingDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                $"Could not start {fileName}.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        var log = new StringBuilder();
        log.AppendLine("=== STDOUT ===");
        log.Append(stdout);
        if (!stdout.EndsWith(Environment.NewLine, StringComparison.Ordinal))
        {
            log.AppendLine();
        }
        log.AppendLine("=== STDERR ===");
        log.Append(stderr);

        await File.WriteAllTextAsync(
            logPath,
            log.ToString(),
            new UTF8Encoding(false));

        return process.ExitCode;
    }

    private static void PrintLogTail(string path)
    {
        var tail = ReadLogTail(path, 12);
        if (!string.IsNullOrWhiteSpace(tail))
        {
            Console.Error.WriteLine(tail);
        }
    }

    private static string ReadLogTail(
        string path,
        int lineCount)
    {
        if (!File.Exists(path))
        {
            return string.Empty;
        }

        return string.Join(
            Environment.NewLine,
            File.ReadLines(path).TakeLast(lineCount));
    }

    private static string FindRepositoryRoot()
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

    private static string? ReadOption(
        IReadOnlyList<string> arguments,
        string name)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (string.Equals(
                    arguments[index],
                    name,
                    StringComparison.OrdinalIgnoreCase))
            {
                return arguments[index + 1];
            }
        }

        return null;
    }

    private static bool HasFlag(
        IReadOnlyList<string> arguments,
        string name) =>
        arguments.Any(argument =>
            string.Equals(
                argument,
                name,
                StringComparison.OrdinalIgnoreCase));
}

internal sealed record StressSvgPage(
    string BaseName,
    int Number,
    string Path);

internal sealed record StressPatient(
    string Directory,
    string ReferencePath,
    string BaseName,
    IReadOnlyList<StressSvgPage> Pages);

internal sealed record StressPatientResult(
    string RelativePath,
    string BaseName,
    string ReferenceFile,
    int Pages,
    string Status,
    int ExitCode,
    int Problems,
    int RootCauses,
    IReadOnlyDictionary<string, int> CategoryCounts,
    IReadOnlyDictionary<string, int> CodeCounts,
    IReadOnlyDictionary<string, int> SeverityCounts,
    string? Error);

internal sealed record AggregateItem(
    string Patient,
    string Key,
    int Count);

internal sealed record DiffSnapshot(
    int Issues,
    int RootCauses,
    IReadOnlyDictionary<string, int> SeverityCounts,
    IReadOnlyDictionary<string, int> CategoryCounts,
    IReadOnlyDictionary<string, int> CodeCounts)
{
    public static DiffSnapshot Empty { get; } = new(
        0,
        0,
        new Dictionary<string, int>(),
        new Dictionary<string, int>(),
        new Dictionary<string, int>());
}
