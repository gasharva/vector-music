namespace AudiverisGlyphPoc;

public static class ModelCache
{
    public const string DefaultUrl =
        "https://raw.githubusercontent.com/Audiveris/audiveris/master/app/res/basic-classifier.zip";

    public static async Task<string> EnsureAsync(
        string? explicitPath = null,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return Path.GetFullPath(explicitPath);
        }

        var cacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "vector-music",
            "audiveris-glyph-poc");

        Directory.CreateDirectory(cacheDirectory);

        var modelPath = Path.Combine(cacheDirectory, "basic-classifier.zip");

        if (File.Exists(modelPath) && new FileInfo(modelPath).Length > 100_000)
        {
            return modelPath;
        }

        Console.WriteLine("Downloading official Audiveris basic-classifier.zip...");

        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("vector-music-audiveris-glyph-poc/0.1");

        await using var source = await client.GetStreamAsync(DefaultUrl, cancellationToken);
        await using var target = File.Create(modelPath);
        await source.CopyToAsync(target, cancellationToken);

        return modelPath;
    }
}
