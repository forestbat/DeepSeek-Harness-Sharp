using Dsh.Lsp.Analysis;
using Dsh.Lsp.Client;
using Dsh.Lsp.Model;
using Dsh.Lsp.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

namespace Dsh.Lsp.CSharp;

public sealed class CSharpReferenceProvider : IReferenceProvider
{
    public string Name => "roslyn-symbolfinder";

    public async Task<IReadOnlyList<SymbolEdge>> ResolveAsync(
        string rootPath,
        LspDocument document,
        LspRange range,
        CancellationToken cancellationToken = default)
    {
        if (!document.LanguageId.Equals("csharp", StringComparison.OrdinalIgnoreCase)
            && !document.LanguageId.Equals("c#", StringComparison.OrdinalIgnoreCase))
            return [];

        CSharpProjectContext? context;
        try
        {
            context = await CSharpProjectContext.LoadAsync(rootPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            context = null;
        }

        if (context?.RoslynProject is { } project)
            return await ResolveProjectAsync(project, document, range, cancellationToken).ConfigureAwait(false);
        return await ResolveAdhocAsync(context, rootPath, document, range, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<SymbolEdge>> ResolveProjectAsync(
        Project project,
        LspDocument document,
        LspRange range,
        CancellationToken cancellationToken)
    {
        if (!LspDocumentUri.TryGetPath(document.Uri, out var path))
            return [];
        var sourcePath = Path.GetFullPath(path);
        var solution = project.Solution;
        var sourceDocument = project.Documents.FirstOrDefault(candidate =>
            string.Equals(candidate.FilePath, sourcePath, StringComparison.OrdinalIgnoreCase));
        if (sourceDocument is null)
        {
            var id = DocumentId.CreateNewId(project.Id);
            solution = solution.AddDocument(
                id,
                Path.GetFileName(sourcePath),
                SourceText.From(document.Text),
                filePath: sourcePath);
            sourceDocument = solution.GetDocument(id);
        }
        else
        {
            solution = solution.WithDocumentText(sourceDocument.Id, SourceText.From(document.Text));
            sourceDocument = solution.GetDocument(sourceDocument.Id);
        }

        if (sourceDocument is null)
            return [];
        var tree = await sourceDocument.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
        var model = await sourceDocument.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (tree is null || model is null)
            return [];
        var symbol = FindSymbol(tree, model, document, range, cancellationToken);
        if (symbol is null)
            return [];

        var edges = new List<SymbolEdge>();
        var sourceName = EdgeSource(document, range, symbol);
        AddDefinitions(edges, symbol, sourceName);
        var references = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken).ConfigureAwait(false);
        foreach (var reference in references)
        {
            foreach (var location in reference.Locations)
                AddLocation(edges, location.Location, SymbolEdgeKind.Reference, sourceName);
        }

        return Deduplicate(edges);
    }

    private static async Task<IReadOnlyList<SymbolEdge>> ResolveAdhocAsync(
        CSharpProjectContext? context,
        string rootPath,
        LspDocument document,
        LspRange range,
        CancellationToken cancellationToken)
    {
        var files = context is { SourceFiles.Count: > 0 }
            ? context.SourceFiles
            : EnumerateFiles(rootPath);
        if (files.Count == 0)
            return [];
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject(ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Create(),
            "Dsh.Lsp.CSharp.Project",
            "Dsh.Lsp.CSharp.Project",
            LanguageNames.CSharp,
            metadataReferences: context?.References ?? References(),
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)));
        var documentIds = new Dictionary<string, DocumentId>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var text = file.Equals(Path.GetFullPath(document.Uri.StartsWith("file:", StringComparison.Ordinal) ? new Uri(document.Uri).LocalPath : document.Uri), StringComparison.OrdinalIgnoreCase)
                ? document.Text
                : await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            var added = workspace.AddDocument(project.Id, Path.GetFileName(file), SourceText.From(text));
            documentIds[file] = added.Id;
        }

        var solution = workspace.CurrentSolution;
        var sourcePath = LspDocumentUri.TryGetPath(document.Uri, out var path)
            ? Path.GetFullPath(path)
            : files[0];
        if (!documentIds.TryGetValue(sourcePath, out var sourceId))
            return [];
        var sourceDocument = solution.GetDocument(sourceId);
        if (sourceDocument is null)
            return [];
        var tree = await sourceDocument.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
        var model = await sourceDocument.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (tree is null || model is null)
            return [];
        var symbol = FindSymbol(tree, model, document, range, cancellationToken);
        if (symbol is null)
            return [];

        var edges = new List<SymbolEdge>();
        var sourceName = EdgeSource(document, range, symbol);
        AddDefinitions(edges, symbol, sourceName);
        var references = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken).ConfigureAwait(false);
        foreach (var reference in references)
        {
            foreach (var location in reference.Locations)
                AddLocation(edges, location.Location, SymbolEdgeKind.Reference, sourceName);
        }

        return Deduplicate(edges);
    }

    private static ISymbol? FindSymbol(
        SyntaxTree tree,
        SemanticModel model,
        LspDocument document,
        LspRange range,
        CancellationToken cancellationToken)
    {
        var start = document.OffsetAt(range.Start);
        var end = Math.Max(start, document.OffsetAt(range.End));
        var node = tree.GetRoot(cancellationToken).FindNode(TextSpan.FromBounds(start, end), getInnermostNodeForTie: true);
        return model.GetDeclaredSymbol(node, cancellationToken)
            ?? model.GetSymbolInfo(node, cancellationToken).Symbol;
    }

    private static string EdgeSource(LspDocument document, LspRange range, ISymbol symbol)
        => $"{document.Uri}|{range.Start.Line}:{range.Start.Character}|{symbol.Name}";

    private static void AddDefinitions(List<SymbolEdge> edges, ISymbol symbol, string sourceName)
    {
        foreach (var syntaxReference in symbol.DeclaringSyntaxReferences)
        {
            var location = syntaxReference.GetSyntax()
                .GetLocation();
            AddLocation(edges, location, SymbolEdgeKind.Definition, sourceName);
        }
    }

    private static void AddLocation(List<SymbolEdge> edges, Location location, SymbolEdgeKind kind, string sourceName)
    {
        if (location.SourceTree is not { } referenceTree || string.IsNullOrWhiteSpace(referenceTree.FilePath))
            return;
        var span = location.GetLineSpan();
        edges.Add(new SymbolEdge(
            sourceName,
            kind,
            new LspLocation(
                LspDocumentUri.FromPath(referenceTree.FilePath),
                new LspRange(
                    new LspPosition(span.StartLinePosition.Line, span.StartLinePosition.Character),
                    new LspPosition(span.EndLinePosition.Line, span.EndLinePosition.Character))),
            "roslyn-symbolfinder"));
    }

    private static IReadOnlyList<SymbolEdge> Deduplicate(List<SymbolEdge> edges)
        => edges
            .DistinctBy(edge => $"{edge.Kind}|{edge.Target.Uri}|{edge.Target.Range.Start.Line}:{edge.Target.Range.Start.Character}")
            .ToArray();

    private static IReadOnlyList<string> EnumerateFiles(string rootPath)
    {
        var root = Path.GetFullPath(rootPath);
        if (!Directory.Exists(root))
            return [];
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file =>
            {
                var relative = Path.GetRelativePath(root, file);
                return !relative.StartsWith($"bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                    && !relative.StartsWith($"obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
            })
            .Take(500)
            .ToArray();
    }

    private static IReadOnlyList<MetadataReference> References()
    {
        var paths = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string)?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            ?? [typeof(object).Assembly.Location];
        return paths.Select(path => MetadataReference.CreateFromFile(path)).ToArray();
    }
}
