using System.Reflection;
using SvgMusic.Semantics;
using Xunit;

namespace SemanticInterpreter.Tests;

public sealed class ClassifiedSymbolPipelineOrderTests
{
    [Fact]
    public void AutoInsertedClassifiedSymbolPass_IsLast()
    {
        var pipeline = new SemanticPipeline([
            new NoteheadPass(),
            new ClefPass()
        ]);
        var field = typeof(SemanticPipeline).GetField(
            "_passes",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var passes = Assert.IsAssignableFrom<IReadOnlyList<ISemanticPass>>(
            field!.GetValue(pipeline));

        Assert.IsType<ClassifiedSymbolPass>(passes[^1]);
    }
}
