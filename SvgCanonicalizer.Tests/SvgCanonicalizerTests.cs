using System.Xml.Linq;
using SkiaSharp;
using Svg.Skia;
using SvgMusic.Canonicalization;
using Xunit;

namespace SvgCanonicalizer.Tests;

public sealed class SvgCanonicalizerTests
{
    [Fact]
    public void UseAndProducerMetadata_DisappearAfterVisualRoundTrip()
    {
        var source = """
            <svg xmlns="http://www.w3.org/2000/svg"
                 xmlns:xlink="http://www.w3.org/1999/xlink"
                 viewBox="0 0 100 100">
              <defs>
                <path id="glyph"
                      class="producer-hint"
                      d="M 0 0 L 10 0 L 10 10 Z"
                      fill="black" />
              </defs>
              <g transform="translate(20 30)">
                <use href="#glyph"
                     x="5"
                     y="7"
                     data-index="42" />
              </g>
            </svg>
            """;

        using var fixture = new TempSvgFixture(source);

        new SvgCanonicalizerService().Canonicalize(
            fixture.InputPath,
            fixture.OutputPath);

        var document = XDocument.Load(fixture.OutputPath);
        Assert.NotNull(document.Root);

        var descendants = document.Root!.Descendants().ToArray();

        Assert.DoesNotContain(
            descendants,
            element => element.Name.LocalName == "use");

        Assert.DoesNotContain(
            descendants
                .SelectMany(element => element.Attributes()),
            attribute =>
                attribute.Name.LocalName == "class"
                || attribute.Name.LocalName.StartsWith(
                    "data-",
                    StringComparison.OrdinalIgnoreCase));

        Assert.Contains(
            descendants,
            element => element.Name.LocalName == "path");
    }

    [Fact]
    public void RoundTrip_PreservesRenderedPixels()
    {
        var source = """
            <svg xmlns="http://www.w3.org/2000/svg"
                 viewBox="0 0 40 40">
              <path d="
                  M 2 2 L 38 2 L 38 38 L 2 38 Z
                  M 10 10 L 10 30 L 30 30 L 30 10 Z"
                    fill="black"
                    fill-rule="evenodd" />
              <path d="M 4 20 L 36 20"
                    fill="none"
                    stroke="black"
                    stroke-width="2" />
            </svg>
            """;

        using var fixture = new TempSvgFixture(source);

        new SvgCanonicalizerService().Canonicalize(
            fixture.InputPath,
            fixture.OutputPath);

        var before = Render(fixture.InputPath, 160, 160);
        var after = Render(fixture.OutputPath, 160, 160);

        Assert.Equal(before.Length, after.Length);

        var different = before
            .Zip(after)
            .Count(pair => pair.First != pair.Second);

        Assert.True(
            different <= before.Length * 0.002,
            $"Rendered pixel difference was {different} of {before.Length} bytes.");
    }

    private static byte[] Render(
        string path,
        int width,
        int height)
    {
        using var svg = new SKSvg();
        var picture = svg.Load(path)
            ?? throw new InvalidDataException($"Could not render {path}");

        using var bitmap = new SKBitmap(
            width,
            height,
            SKColorType.Bgra8888,
            SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);

        canvas.Clear(SKColors.White);

        var bounds = picture.CullRect;
        var scale = Math.Min(
            width / bounds.Width,
            height / bounds.Height);

        canvas.Scale(scale);
        canvas.Translate(-bounds.Left, -bounds.Top);
        canvas.DrawPicture(picture);
        canvas.Flush();

        return bitmap.Bytes;
    }

    private sealed class TempSvgFixture : IDisposable
    {
        private readonly string _directory;

        public TempSvgFixture(string source)
        {
            _directory = Path.Combine(
                Path.GetTempPath(),
                "svg-canonicalizer-tests",
                Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(_directory);

            InputPath = Path.Combine(_directory, "input.svg");
            OutputPath = Path.Combine(_directory, "output.svg");

            File.WriteAllText(InputPath, source);
        }

        public string InputPath { get; }

        public string OutputPath { get; }

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
