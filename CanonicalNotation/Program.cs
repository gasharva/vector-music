using SvgMusic.Canonical;

if (args.Length < 1)
{
    PrintUsage();
    return 2;
}

var command = args[0];

switch (command)
{
    case "to-json":
    {
        if (args.Length is < 2 or > 3)
        {
            PrintUsage();
            return 2;
        }

        var input = Path.GetFullPath(args[1]);
        var output = args.Length == 3
            ? Path.GetFullPath(args[2])
            : Path.ChangeExtension(input, ".canonical.json");

        var score = new MusicXmlCanonicalizer().Read(input);
        await File.WriteAllTextAsync(
            output,
            CanonicalJson.Serialize(score));
        Console.WriteLine(output);
        return 0;
    }

    case "to-musicxml":
    {
        if (args.Length is < 2 or > 3)
        {
            PrintUsage();
            return 2;
        }

        var input = Path.GetFullPath(args[1]);
        var output = args.Length == 3
            ? Path.GetFullPath(args[2])
            : Path.ChangeExtension(input, ".musicxml");

        var score = CanonicalJson.Read(input);
        new MusicXmlWriter().Write(score, output);
        Console.WriteLine(output);
        return 0;
    }

    case "merge":
    {
        if (args.Length < 4)
        {
            PrintUsage();
            return 2;
        }

        var output = Path.GetFullPath(args[1]);
        var pages = args
            .Skip(2)
            .Select(Path.GetFullPath)
            .Select(CanonicalJson.Read)
            .ToArray();

        var merged = new CanonicalScoreMerger().Merge(pages);
        Directory.CreateDirectory(
            Path.GetDirectoryName(output)
            ?? Directory.GetCurrentDirectory());
        await File.WriteAllTextAsync(
            output,
            CanonicalJson.Serialize(merged));
        Console.WriteLine(output);
        return 0;
    }

    case "diff":
    {
        if (args.Length is < 3 or > 4)
        {
            PrintUsage();
            return 2;
        }

        var expectedPath = Path.GetFullPath(args[1]);
        var actualPath = Path.GetFullPath(args[2]);
        var outputPrefix = args.Length == 4
            ? Path.GetFullPath(args[3])
            : Path.Combine(
                Path.GetDirectoryName(actualPath)
                    ?? Directory.GetCurrentDirectory(),
                Path.GetFileNameWithoutExtension(actualPath)
                    + ".diff");

        var expected = ReadCanonical(expectedPath);
        var actual = ReadCanonical(actualPath);
        var report = new CanonicalComparer().Compare(
            expected,
            actual);

        var outputDirectory = Path.GetDirectoryName(outputPrefix)
            ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(outputDirectory);

        var markdownPath = outputPrefix + ".md";
        var jsonPath = outputPrefix + ".json";

        await File.WriteAllTextAsync(
            markdownPath,
            report.ToMarkdown());
        await File.WriteAllTextAsync(
            jsonPath,
            report.ToJson());

        Console.WriteLine(
            report.IsEqual
                ? "Canonical scores are semantically equal."
                : $"{report.Issues.Count} semantic difference(s).");

        foreach (var group in report.Issues
                     .GroupBy(issue => issue.Category)
                     .OrderBy(group => group.Key))
        {
            Console.WriteLine(
                $"  {group.Key,-12} {group.Count(),4}");
        }

        Console.WriteLine(markdownPath);
        Console.WriteLine(jsonPath);
        return report.IsEqual ? 0 : 1;
    }

    default:
        Console.Error.WriteLine(
            $"Unknown command: {command}");
        PrintUsage();
        return 2;
}

static CanonicalNotation ReadCanonical(string path)
{
    var extension = Path.GetExtension(path);

    return extension.Equals(
            ".json",
            StringComparison.OrdinalIgnoreCase)
        ? CanonicalJson.Read(path)
        : new MusicXmlCanonicalizer().Read(path);
}

static void PrintUsage()
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine(
        "  CanonicalNotation.Cli to-json <input.musicxml> [output.json]");
    Console.Error.WriteLine(
        "  CanonicalNotation.Cli to-musicxml <input.canonical.json> [output.musicxml]");
    Console.Error.WriteLine(
        "  CanonicalNotation.Cli merge <output.canonical.json> <page1.json> <page2.json> [...]");
    Console.Error.WriteLine(
        "  CanonicalNotation.Cli diff <expected.musicxml|json> <actual.musicxml|json> [output-prefix]");
}
