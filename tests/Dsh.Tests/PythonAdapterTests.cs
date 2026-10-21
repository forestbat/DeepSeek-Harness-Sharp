using Dsh.Lsp.Client;
using Dsh.Lsp.Model;
using Dsh.Lsp.Python;
using Dsh.Lsp.Protocol;

namespace Dsh.Tests;

public sealed class PythonAdapterTests
{
    [Fact]
    public async Task PythonAstProvider_ExtractsDeclarations()
    {
        var document = LspDocument.Create(
            "file:///probe.py",
            "python",
            "class Demo:\n    def add(self, a, b):\n        return a + b\n\nvalue = 1\n");
        var provider = new PythonDeclarationProvider();
        var declarations = await provider.ExtractAsync(
            document,
            null,
            new SymbolGraph(document.Uri, document.Version, [], []));
        Assert.Contains(declarations, declaration => declaration.Name == "Demo");
        Assert.Contains(declarations, declaration => declaration.Name == "add");
        Assert.Contains(declarations, declaration => declaration.Name == "value");
    }

    [Fact]
    public async Task PythonAstProvider_ResolvesAnnotatedType()
    {
        const string source = "values: list[int] = []\n";
        var document = LspDocument.Create("file:///types.py", "python", source);
        var provider = new PythonTypeProvider();
        var type = await provider.ResolveAsync(
            document,
            new LspRange(new LspPosition(0, 0), new LspPosition(0, 6)));

        Assert.NotNull(type);
        Assert.Equal(TypeExpressionKind.Generic, type.Kind);
        Assert.Equal("list", type.Name);
        Assert.False(type.IsInferred);
    }

    [Fact]
    public async Task PythonAstProvider_BuildsFunctionFlow()
    {
        const string source = """
            def choose(flag, value):
                if flag:
                    result = value
                else:
                    result = 0
                return result
            """;
        var document = LspDocument.Create("file:///flow.py", "python", source);
        var provider = new PythonFlowProvider();
        var flow = await provider.BuildAsync(
            document,
            new LspRange(new LspPosition(0, 4), new LspPosition(0, 10)));

        Assert.NotNull(flow);
        Assert.NotEmpty(flow.Nodes);
        Assert.NotEmpty(flow.Edges);
        Assert.Contains(flow.Nodes, node => node.Kind == "condition");
        Assert.NotNull(flow.Variables);
        Assert.Contains(flow.Variables!, variable => variable.Name == "result");
    }

    [Fact]
    public async Task PythonAstProvider_ResolvesReferences()
    {
        const string source = "value = 1\nprint(value)\n";
        var document = LspDocument.Create("file:///references.py", "python", source);
        var provider = new PythonReferenceProvider();
        var edges = await provider.ResolveAsync(
            ".",
            document,
            new LspRange(new LspPosition(0, 0), new LspPosition(0, 5)));

        Assert.Contains(edges, edge => edge.Kind == SymbolEdgeKind.Definition);
        Assert.Contains(edges, edge => edge.Kind == SymbolEdgeKind.Reference
            && edge.Target.Range.Start.Line == 1);
    }
}
