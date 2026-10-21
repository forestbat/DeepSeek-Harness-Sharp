using Dsh.Lsp.Client;
using Dsh.Lsp.Analysis;
using Dsh.Lsp.Model;
using Dsh.Lsp.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace Dsh.Lsp.CSharp;

public sealed class CSharpFlowProvider : IFlowProvider
{
    public string Name => "roslyn-csharp-flow";

    public async Task<FlowGraph?> BuildAsync(
        LspDocument document,
        LspRange range,
        CancellationToken cancellationToken = default)
    {
        if (!document.LanguageId.Equals("csharp", StringComparison.OrdinalIgnoreCase)
            && !document.LanguageId.Equals("c#", StringComparison.OrdinalIgnoreCase))
            return null;

        var tree = CSharpSyntaxTree.ParseText(document.Text, cancellationToken: cancellationToken);
        var root = tree.GetRoot(cancellationToken);
        var start = document.OffsetAt(range.Start);
        var end = Math.Max(start, document.OffsetAt(range.End));
        var node = root.FindNode(TextSpan.FromBounds(start, end), getInnermostNodeForTie: true);
        var method = node.AncestorsAndSelf().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault();
        if (method is null)
            return null;
        CSharpCompilation compilation;
        if (LspDocumentUri.TryGetPath(document.Uri, out var path) && File.Exists(path))
        {
            try
            {
                var context = await CSharpProjectContext.LoadAsync(
                    Path.GetDirectoryName(Path.GetFullPath(path))!,
                    cancellationToken).ConfigureAwait(false);
                compilation = context.CreateCompilation(tree);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                compilation = LocalCompilation(tree);
            }
        }
        else
        {
            compilation = LocalCompilation(tree);
        }
        var model = compilation.GetSemanticModel(tree);
        var graph = ControlFlowGraph.Create(method, model, cancellationToken);
        if (graph is null)
            return null;
        var nodes = graph.Blocks
            .Select(block => new FlowNode(
                block.Ordinal,
                block.Kind.ToString(),
                block.Operations.Select(operation => Operation(document, operation)).ToArray()))
            .ToArray();
        var edges = new List<FlowEdge>();
        var variables = new Dictionary<ISymbol, (List<LspRange> Definitions, List<LspRange> Uses)>(SymbolEqualityComparer.Default);
        foreach (var block in graph.Blocks)
        {
            AddEdge(edges, block, block.FallThroughSuccessor);
            AddEdge(edges, block, block.ConditionalSuccessor);
            foreach (var operation in block.Operations)
                CollectVariables(operation, variables, document);
        }

        return new FlowGraph(
            graph.Blocks.FirstOrDefault()?.Ordinal,
            graph.Blocks.LastOrDefault()?.Ordinal,
            nodes,
            edges,
            variables.Select(pair => new FlowVariable(
                pair.Key.Name,
                pair.Value.Definitions,
                pair.Value.Uses)).ToArray(),
            edges.Where(edge => edge.Kind.Contains("Throw", StringComparison.OrdinalIgnoreCase)).ToArray());
    }

    private static void AddEdge(
        List<FlowEdge> edges,
        BasicBlock source,
        ControlFlowBranch? branch)
    {
        if (branch?.Destination is { } destination)
            edges.Add(new FlowEdge(source.Ordinal, destination.Ordinal, branch.Semantics.ToString()));
    }

    private static FlowOperation Operation(LspDocument document, IOperation operation)
        => new(
            operation.Kind.ToString(),
            new LspRange(
                document.PositionAt(operation.Syntax.SpanStart),
                document.PositionAt(operation.Syntax.Span.End)),
            operation.IsImplicit);

    private static void CollectVariables(
        IOperation operation,
        Dictionary<ISymbol, (List<LspRange> Definitions, List<LspRange> Uses)> variables,
        LspDocument document)
    {
        var range = new LspRange(
            document.PositionAt(operation.Syntax.SpanStart),
            document.PositionAt(operation.Syntax.Span.End));
        if (operation is IVariableDeclaratorOperation declaration)
            Add(variables, declaration.Symbol, range, true);
        else if (operation is ILocalReferenceOperation local)
            Add(variables, local.Local, range, local.IsDeclaration);
        else if (operation is IParameterReferenceOperation parameter)
            Add(variables, parameter.Parameter, range, false);
        foreach (var child in operation.ChildOperations)
            CollectVariables(child, variables, document);
    }

    private static void Add(
        Dictionary<ISymbol, (List<LspRange> Definitions, List<LspRange> Uses)> variables,
        ISymbol symbol,
        LspRange range,
        bool definition)
    {
        if (!variables.TryGetValue(symbol, out var entry))
        {
            entry = ([], []);
            variables[symbol] = entry;
        }

        (definition ? entry.Definitions : entry.Uses).Add(range);
    }

    private static IReadOnlyList<MetadataReference> References()
    {
        var paths = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string)?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            ?? [typeof(object).Assembly.Location];
        return paths.Select(path => MetadataReference.CreateFromFile(path)).ToArray();
    }

    private static CSharpCompilation LocalCompilation(SyntaxTree tree)
        => CSharpCompilation.Create(
            "Dsh.Lsp.CSharpFlow",
            [tree],
            References(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
}
