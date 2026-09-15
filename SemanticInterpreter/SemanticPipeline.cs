namespace SvgMusic.Semantics;

public interface ISemanticPass
{
    string Name { get; }

    void Run(
        SemanticDocument document,
        SemanticFacts facts);
}

public sealed class SemanticPipeline
{
    private readonly IReadOnlyList<ISemanticPass> _passes;

    public SemanticPipeline(IEnumerable<ISemanticPass> passes)
    {
        var materialized = passes.ToList();

        // Duration is a terminal derived fact: once noteheads are part of a semantic
        // pipeline, infer duration after the supplied attachment passes unless the
        // caller already positioned DurationPass explicitly.
        if (materialized.Any(pass => pass is NoteheadPass)
            && materialized.All(pass => pass is not DurationPass))
        {
            materialized.Add(new DurationPass());
        }

        _passes = materialized;
    }

    public SemanticFacts Run(SemanticDocument document)
    {
        var facts = new SemanticFacts();

        foreach (var pass in _passes)
        {
            var before = facts.Items.Count;
            pass.Run(document, facts);
            var added = facts.Items.Count - before;

            facts.AddTrace(
                $"{pass.Name}: added {added} fact(s); total={facts.Items.Count}");
        }

        return facts;
    }
}
