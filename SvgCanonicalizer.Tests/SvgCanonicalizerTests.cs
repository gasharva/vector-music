using System.Xml.Linq;
using SvgMusic.Canonicalization;
using Xunit;

namespace SvgCanonicalizer.Tests;

public sealed class SvgCanonicalizerTests
{
    [Fact]
    public void UseAndTransforms_AreFlattenedToPlainPaths()
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
        var output = fixture.OutputPath;

        new SvgCanonicalizerService().Canonicalize(
            fixture.InputPath,
            output);

        var document = XDocument.Load(output);
        Assert.NotNull(document.Root);
        var root = document.Root!;
        var descendants = root.Descendants().ToArray();

        Assert.Single(descendants);
        Assert.Equal("path", descendants[0].Name.LocalName);
        Assert.DoesNotContain(
            descendants,
            element => element.Name.LocalName is "defs" or "use" or "g");
        Assert.DoesNotContain(
            descendants.SelectMany(element => element.Attributes()),
            attribute => attribute.Name.LocalName is
                "transform" or "id" or "class"
                || attribute.Name.LocalName.StartsWith(
                    "data-",
                    StringComparison.OrdinalIgnoreCase));

        var pathData = (string?)descendants[0].Attribute("d");
        Assert.False(string.IsNullOrWhiteSpace(pathData));
        Assert.NotEqual("M 0 0 L 10 0 L 10 10 Z", pathData);
    }

    [Fact]
    public void CompoundFill_PreservesContoursWithEvenOddRule()
    {
        var source = """
            <svg xmlns="http://www.w3.org/2000/svg"
                 viewBox="0 0 20 20">
              <path d="
                  M 0 0 L 20 0 L 20 20 L 0 20 Z
                  M 5 5 L 5 15 L 15 15 L 15 5 Z"
                    fill="black" />
            </svg>
            """;

        using var fixture = new TempSvgFixture(source);

        new SvgCanonicalizerService().Canonicalize(
            fixture.InputPath,
            fixture.OutputPath);

        var path = XDocument
            .Load(fixture.OutputPath)
            .Descendants()
            .Single(element => element.Name.LocalName == "path");

        var pathData = (string)path.Attribute("d")!;

        Assert.Equal(
            "evenodd",
            (string?)path.Attribute("fill-rule"));
        Assert.Equal(
            2,
            pathData.Split("M ", StringSplitOptions.None).Length - 1);
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
