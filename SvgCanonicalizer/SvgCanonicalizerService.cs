using System.Xml.Linq;
using SkiaSharp;
using Svg.Skia;
using SvgMusic.Scene;

namespace SvgMusic.Canonicalization;

public sealed record SvgCanonicalizationResult(
    string InputPath,
    string OutputPath,
    int PathCount,
    BoundsD Bounds);

public sealed class SvgCanonicalizerService
{
    public SvgCanonicalizationResult Canonicalize(
        string inputPath,
        string outputPath)
    {
        var fullInput = Path.GetFullPath(inputPath);
        var fullOutput = Path.GetFullPath(outputPath);

        var directory = Path.GetDirectoryName(fullOutput);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var svg = new SKSvg();
        var picture = svg.Load(fullInput)
            ?? throw new InvalidDataException(
                $"Svg.Skia could not render input SVG: {fullInput}");

        var bounds = picture.CullRect;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new InvalidDataException(
                $"Input SVG has empty visible bounds: {fullInput}");
        }

        using (var stream = File.Create(fullOutput))
        using (var canvas = SKSvgCanvas.Create(bounds, stream))
        {
            canvas.DrawPicture(picture);
            canvas.Flush();
        }

        RemoveProducerMetadata(fullOutput);

        var document = XDocument.Load(fullOutput);
        var pathCount = document
            .Descendants()
            .Count(element => element.Name.LocalName == "path");

        return new SvgCanonicalizationResult(
            fullInput,
            fullOutput,
            pathCount,
            new BoundsD(
                bounds.Left,
                bounds.Top,
                bounds.Right,
                bounds.Bottom));
    }

    private static void RemoveProducerMetadata(string path)
    {
        var document = XDocument.Load(path);
        var root = document.Root
            ?? throw new InvalidDataException(
                "Generated SVG root element is missing.");

        foreach (var element in root.DescendantsAndSelf())
        {
            element.Attribute("class")?.Remove();

            foreach (var attribute in element
                         .Attributes()
                         .Where(attribute =>
                             attribute.Name.LocalName.StartsWith(
                                 "data-",
                                 StringComparison.OrdinalIgnoreCase))
                         .ToArray())
            {
                attribute.Remove();
            }
        }

        document.Save(path);
    }
}
