using System.Text.Json;
using System.Text.Json.Serialization;

namespace SvgMusic.Canonical;

public static class CanonicalJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize(CanonicalNotation notation) =>
        JsonSerializer.Serialize(notation, Options);

    public static CanonicalNotation Deserialize(string json) =>
        JsonSerializer.Deserialize<CanonicalNotation>(json, Options)
        ?? throw new InvalidDataException("Invalid CanonicalNotation JSON.");

    public static CanonicalNotation Read(string fileName) =>
        Deserialize(File.ReadAllText(fileName));
}
