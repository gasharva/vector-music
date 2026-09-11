using SvgMusic.Canonical;

if (args.Length is < 2 or > 3)
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  CanonicalNotation.Cli to-json <input.musicxml> [output.json]");
    Console.Error.WriteLine("  CanonicalNotation.Cli to-musicxml <input.canonical.json> [output.musicxml]");
    return 2;
}

var command = args[0];
var input = Path.GetFullPath(args[1]);

switch (command)
{
    case "to-json":
    {
        var output = args.Length == 3
            ? Path.GetFullPath(args[2])
            : Path.ChangeExtension(input, ".canonical.json");

        var score = new MusicXmlCanonicalizer().Read(input);
        await File.WriteAllTextAsync(output, CanonicalJson.Serialize(score));
        Console.WriteLine(output);
        return 0;
    }

    case "to-musicxml":
    {
        var output = args.Length == 3
            ? Path.GetFullPath(args[2])
            : Path.ChangeExtension(input, ".musicxml");

        var score = CanonicalJson.Read(input);
        new MusicXmlWriter().Write(score, output);
        Console.WriteLine(output);
        return 0;
    }

    default:
        Console.Error.WriteLine($"Unknown command: {command}");
        return 2;
}
