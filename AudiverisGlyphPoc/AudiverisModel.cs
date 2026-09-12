using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;

namespace AudiverisGlyphPoc;

public sealed class AudiverisModel
{
    public required int InputSize { get; init; }
    public required int HiddenSize { get; init; }
    public required int OutputSize { get; init; }
    public required string[] InputLabels { get; init; }
    public required string[] OutputLabels { get; init; }
    public required double[][] HiddenWeights { get; init; }
    public required double[][] OutputWeights { get; init; }
    public required double[] Means { get; init; }
    public required double[] StandardDeviations { get; init; }

    public static AudiverisModel Load(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);

        var modelDocument = LoadXml(archive, "model.xml");
        var meansDocument = LoadXml(archive, "means.xml");
        var stdsDocument = LoadXml(archive, "stds.xml");

        var root = modelDocument.Root
            ?? throw new InvalidDataException("model.xml has no root element.");

        return new AudiverisModel
        {
            InputSize = RequiredInt(root, "input-size"),
            HiddenSize = RequiredInt(root, "hidden-size"),
            OutputSize = RequiredInt(root, "output-size"),
            InputLabels = ReadLabels(root, "input-labels"),
            OutputLabels = ReadLabels(root, "output-labels"),
            HiddenWeights = ReadMatrix(root, "hidden-weights"),
            OutputWeights = ReadMatrix(root, "output-weights"),
            Means = ReadVector(meansDocument),
            StandardDeviations = ReadVector(stdsDocument)
        };
    }

    public IReadOnlyList<Prediction> Evaluate(
        IReadOnlyList<double> rawFeatures,
        int top = 8)
    {
        if (rawFeatures.Count != InputSize)
        {
            throw new ArgumentException(
                $"Expected {InputSize} features, got {rawFeatures.Count}.",
                nameof(rawFeatures));
        }

        var normalized = new double[InputSize];

        for (var i = 0; i < InputSize; i++)
        {
            var std = StandardDeviations[i];

            normalized[i] = std == 0
                ? rawFeatures[i] - Means[i]
                : (rawFeatures[i] - Means[i]) / std;
        }

        var hidden = Forward(normalized, HiddenWeights);
        var output = Forward(hidden, OutputWeights);

        return output
            .Select((score, index) => new Prediction(OutputLabels[index], score))
            .OrderByDescending(item => item.Score)
            .Take(Math.Max(1, top))
            .ToArray();
    }

    private static double[] Forward(
        IReadOnlyList<double> input,
        IReadOnlyList<double[]> weights)
    {
        var output = new double[weights.Count];

        for (var row = 0; row < weights.Count; row++)
        {
            var current = weights[row];

            if (current.Length != input.Count + 1)
            {
                throw new InvalidDataException(
                    $"Weight row {row} has {current.Length} values; expected {input.Count + 1}.");
            }

            var sum = current[0];

            for (var column = 0; column < input.Count; column++)
            {
                sum += current[column + 1] * input[column];
            }

            output[row] = Sigmoid(sum);
        }

        return output;
    }

    private static double Sigmoid(double value)
    {
        if (value >= 0)
        {
            var z = Math.Exp(-value);
            return 1.0 / (1.0 + z);
        }

        var negativeZ = Math.Exp(value);
        return negativeZ / (1.0 + negativeZ);
    }

    private static XDocument LoadXml(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name)
            ?? throw new InvalidDataException($"Missing {name} in classifier archive.");

        using var stream = entry.Open();
        return XDocument.Load(stream);
    }

    private static int RequiredInt(XElement element, string attributeName)
    {
        var value = (string?)element.Attribute(attributeName)
            ?? throw new InvalidDataException($"Missing '{attributeName}' attribute.");

        return int.Parse(value, CultureInfo.InvariantCulture);
    }

    private static string[] ReadLabels(XElement root, string elementName)
    {
        var container = root.Element(elementName)
            ?? throw new InvalidDataException($"Missing '{elementName}' element.");

        return container
            .Descendants()
            .Where(node => !node.HasElements)
            .Select(node => node.Value.Trim())
            .Where(value => value.Length > 0)
            .ToArray();
    }

    private static double[][] ReadMatrix(XElement root, string wrapperName)
    {
        var wrapper = root.Element(wrapperName)
            ?? throw new InvalidDataException($"Missing '{wrapperName}' element.");

        return wrapper
            .Elements("row")
            .Select(ReadNumericElement)
            .ToArray();
    }

    private static double[] ReadVector(XDocument document)
    {
        var root = document.Root
            ?? throw new InvalidDataException("Norm XML has no root element.");

        var leafValues = root
            .DescendantsAndSelf()
            .Where(node => !node.HasElements)
            .SelectMany(node => SplitNumbers(node.Value))
            .ToArray();

        if (leafValues.Length == 0)
        {
            throw new InvalidDataException("Norm XML contains no numeric values.");
        }

        return leafValues;
    }

    private static double[] ReadNumericElement(XElement element) =>
        SplitNumbers(element.Value).ToArray();

    private static IEnumerable<double> SplitNumbers(string text)
    {
        foreach (var token in text.Split(
                     [' ', '\t', '\r', '\n', ','],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (double.TryParse(
                    token,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var value))
            {
                yield return value;
            }
        }
    }
}

public sealed record Prediction(string Label, double Score);
