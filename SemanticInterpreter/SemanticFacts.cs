namespace SvgMusic.Semantics;

public abstract record SemanticFact(
    string Pass,
    string Reason,
    IReadOnlyList<string> SourceShapeIds);

public sealed record ClefFact(
    int MeasureNumber,
    int Staff,
    string Sign,
    int Line,
    double X,
    double Confidence,
    string ShapeId,
    string Reason,
    IReadOnlyList<string> SourceShapeIds)
    : SemanticFact(
        "ClefPass",
        Reason,
        SourceShapeIds);

public sealed class SemanticFacts
{
    private readonly List<SemanticFact> _items = [];
    private readonly List<string> _trace = [];

    public IReadOnlyList<SemanticFact> Items => _items;
    public IReadOnlyList<string> Trace => _trace;

    public void Add(SemanticFact fact)
    {
        _items.Add(fact);
    }

    public IEnumerable<TFact> OfType<TFact>()
        where TFact : SemanticFact
    {
        return _items.OfType<TFact>();
    }

    public void AddTrace(string message)
    {
        _trace.Add(message);
    }
}
