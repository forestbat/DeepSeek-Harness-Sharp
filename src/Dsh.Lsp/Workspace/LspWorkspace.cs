using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Lsp.Analysis;
using Dsh.Lsp.Catalog;
using Dsh.Lsp.Client;
using Dsh.Lsp.Model;
using Dsh.Lsp.Protocol;
using Dsh.Lsp.TreeSitter;

namespace Dsh.Lsp.Workspace;

/**
 * Keeps one language-server session per language for a workspace and returns layered analysis for files.
 * Server installation remains explicit: a missing executable produces a launch error with the install hint.
 */
public sealed class LspWorkspace : IAsyncDisposable
{
    private readonly string _rootPath;
    private readonly LspClientOptions _options;
    private readonly DocumentAnalysisOptions _analysisOptions;
    private readonly ISyntaxTreeProvider? _syntaxTreeProvider;
    private readonly IStructuredTypeProvider? _structuredTypeProvider;
    private readonly IFlowProvider? _flowProvider;
    private readonly IReferenceProvider? _referenceProvider;
    private readonly IDeclarationProvider? _declarationProvider;
    private readonly IZoneProvider _zoneProvider;
    private readonly ConcurrentDictionary<string, Lazy<Task<LspClientSession>>> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DocumentSnapshot> _snapshots = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, LayerCache> _layers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<SymbolEdge>>>> _symbolReferences = new(StringComparer.Ordinal);
    private int _disposed;

    public LspWorkspace(
        string rootPath,
        LspClientOptions? options = null,
        DocumentAnalysisOptions? analysisOptions = null,
        ISyntaxTreeProvider? syntaxTreeProvider = null,
        IStructuredTypeProvider? structuredTypeProvider = null,
        IFlowProvider? flowProvider = null,
        IDeclarationProvider? declarationProvider = null,
        IReferenceProvider? referenceProvider = null,
        IZoneProvider? zoneProvider = null)
    {
        _rootPath = Path.GetFullPath(rootPath);
        _options = options ?? new LspClientOptions(_rootPath);
        _analysisOptions = analysisOptions ?? DocumentAnalysisOptions.Default;
        _syntaxTreeProvider = syntaxTreeProvider;
        _structuredTypeProvider = structuredTypeProvider;
        _flowProvider = flowProvider;
        _declarationProvider = declarationProvider;
        _referenceProvider = referenceProvider;
        _zoneProvider = zoneProvider ?? new LanguageZoneProvider();
        Index = new WorkspaceIndex(_rootPath);
    }

    public string RootPath => _rootPath;

    public WorkspaceIndex Index { get; }

    public IReadOnlyList<StubDeclaration> FindStubs(string query, int max = 100)
        => Index.Stubs.Find(query, max);

    public async Task<SymbolGraph> ExpandStubAsync(
        StubDeclaration stub,
        CancellationToken cancellationToken = default)
    {
        if (!LspDocumentUri.TryGetPath(stub.Uri, out var path))
            return new SymbolGraph(stub.Uri, stub.DocumentVersion, [], []);
        var symbols = await GetSymbolsAsync(path, null, cancellationToken).ConfigureAwait(false);
        return symbols with
        {
            Nodes = symbols.Nodes
                .Where(node => node.Range.Overlaps(stub.Range) || node.SelectionRange.Overlaps(stub.SelectionRange))
                .ToArray(),
            Edges = symbols.Edges
                .Where(edge => edge.FromId == stub.Id || edge.Target.Range.Overlaps(stub.Range))
                .ToArray(),
        };
    }

    public async Task<DocumentSnapshot> GetSnapshotAsync(
        string path,
        string? text = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var (session, document) = await EnsureFileAsync(path, text, cancellationToken).ConfigureAwait(false);
        if (_snapshots.TryGetValue(document.Uri, out var cached)
            && cached.Document.Version == document.Version)
            return cached;
        var snapshot = await new DocumentAnalysisService(session, _analysisOptions, _syntaxTreeProvider, _structuredTypeProvider, _flowProvider)
            .BuildSnapshotAsync(document, cancellationToken)
            .ConfigureAwait(false);
        _snapshots[document.Uri] = snapshot;
        return snapshot;
    }

    public async Task<LspDocumentUpdate> ApplyDocumentChangesAsync(
        string path,
        IReadOnlyList<LspTextChange> changes,
        CancellationToken cancellationToken = default)
    {
        var (session, document) = await EnsureFileAsync(path, null, cancellationToken).ConfigureAwait(false);
        var update = await session.ChangeAsync(document.Uri, changes, cancellationToken).ConfigureAwait(false);
        _snapshots.TryRemove(document.Uri, out _);
        Index.InvalidateRange(document.Uri, update.ChangedRange);
        ClearReferenceCache(document.Uri);
        await BuildIndexAsync(path, update.Document.Text, cancellationToken, update.ChangedRange).ConfigureAwait(false);
        return update;
    }

    public async Task<SymbolGraph> GetSymbolsAsync(
        string path,
        string? text = null,
        CancellationToken cancellationToken = default)
    {
        var symbols = await GetCachedLayerAsync(
            path,
            text,
            (service, snapshot, token) => service.BuildSymbolsAsync(snapshot, token),
            static cache => cache.Symbols,
            static (cache, value) => cache.Symbols = value,
            cancellationToken).ConfigureAwait(false);
        var snapshot = await GetSnapshotAsync(path, text, cancellationToken).ConfigureAwait(false);
        var scopes = ScopeGraphBuilder.Build(snapshot.Document, snapshot.Syntax);
        var stubs = await ExtractStubsAsync(snapshot, symbols, cancellationToken).ConfigureAwait(false);
        Index.AddOrUpdate(snapshot, symbols, scopes, stubs);
        return symbols;
    }

    private async Task<IReadOnlyList<StubDeclaration>?> ExtractStubsAsync(
        DocumentSnapshot snapshot,
        SymbolGraph symbols,
        CancellationToken cancellationToken)
        => _declarationProvider is null
            ? null
            : await _declarationProvider.ExtractAsync(
                snapshot.Document,
                snapshot.Syntax.Cst,
                symbols,
                cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<SymbolEdge>> ResolveSymbolAsync(
        string path,
        LspPosition position,
        string? text = null,
        CancellationToken cancellationToken = default)
    {
        var (session, document) = await EnsureFileAsync(path, text, cancellationToken).ConfigureAwait(false);
        var snapshot = await GetSnapshotAsync(path, text, cancellationToken).ConfigureAwait(false);
        var node = snapshot.Syntax.FindAt(position);
        if (node is null)
            return [];
        var symbol = new SymbolNode(
            SymbolIdentity.For(document.Uri, node),
            node.Name,
            node.Kind,
            node.Detail,
            document.Uri,
            node.Range,
            node.SelectionRange,
            document.Version,
            "textDocument/documentSymbol");
        if (_referenceProvider is not null)
            return await _referenceProvider.ResolveAsync(
                _rootPath,
                document,
                node.SelectionRange,
                cancellationToken).ConfigureAwait(false);
        var key = $"{document.Uri}|{document.Version}|{node.SelectionRange.Start.Line}:{node.SelectionRange.Start.Character}|{symbol.Id}";
        var lazy = _symbolReferences.GetOrAdd(
            key,
            _ => new Lazy<Task<IReadOnlyList<SymbolEdge>>>(() =>
                new SymbolGraphBuilder(session, snapshot.Capabilities)
                    .ResolveSymbolAsync(symbol, _analysisOptions, CancellationToken.None)));
        return await lazy.Value.ConfigureAwait(false);
    }

    public async Task<ScopeGraph> GetScopesAsync(
        string path,
        string? text = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await GetSnapshotAsync(path, text, cancellationToken).ConfigureAwait(false);
        var cache = LayerCacheFor(snapshot);
        if (cache.Scopes is { } cached)
            return cached;
        var scopes = ScopeGraphBuilder.Build(snapshot.Document, snapshot.Syntax);
        cache.Scopes = scopes;
        return scopes;
    }

    public Task<HierarchyGraph> GetHierarchyAsync(
        string path,
        string? text = null,
        CancellationToken cancellationToken = default)
        => GetCachedLayerAsync(
            path,
            text,
            (service, snapshot, token) => service.BuildHierarchyAsync(snapshot, token),
            static cache => cache.Hierarchy,
            static (cache, value) => cache.Hierarchy = value,
            cancellationToken);

    public Task<TypeFactsSnapshot> GetTypesAsync(
        string path,
        string? text = null,
        CancellationToken cancellationToken = default)
        => GetCachedLayerAsync(
            path,
            text,
            (service, snapshot, token) => service.ExtractTypesAsync(snapshot, token),
            static cache => cache.Types,
            static (cache, value) => cache.Types = value,
            cancellationToken);

    public async Task<FlowGraph?> GetFlowAsync(
        string path,
        LspRange range,
        string? text = null,
        CancellationToken cancellationToken = default)
    {
        var (session, document) = await EnsureFileAsync(path, text, cancellationToken).ConfigureAwait(false);
        return await new DocumentAnalysisService(
                session,
                _analysisOptions,
                _syntaxTreeProvider,
                _structuredTypeProvider,
                _flowProvider)
            .GetFlowAsync(document, range, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SemanticToken>> GetSemanticTokensAsync(
        string path,
        string? text = null,
        CancellationToken cancellationToken = default)
    {
        var (session, document) = await EnsureFileAsync(path, text, cancellationToken).ConfigureAwait(false);
        return await new DocumentAnalysisService(session, _analysisOptions, _syntaxTreeProvider, _structuredTypeProvider, _flowProvider)
            .GetSemanticTokensAsync(document, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<SyntaxSnapshot> GetCstAsync(
        string path,
        string? text = null,
        CancellationToken cancellationToken = default)
    {
        var (session, document) = await EnsureFileAsync(path, text, cancellationToken).ConfigureAwait(false);
        var service = new DocumentAnalysisService(session, _analysisOptions, _syntaxTreeProvider, _structuredTypeProvider, _flowProvider);
        var snapshot = await service.BuildSnapshotAsync(document, cancellationToken).ConfigureAwait(false);
        return await service.WithCstAsync(document, snapshot.Syntax, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> GetCachedLayerAsync<T>(
        string path,
        string? text,
        Func<DocumentAnalysisService, DocumentSnapshot, CancellationToken, Task<T>> build,
        Func<LayerCache, T?> read,
        Action<LayerCache, T> write,
        CancellationToken cancellationToken)
    {
        var (session, _) = await EnsureFileAsync(path, text, cancellationToken).ConfigureAwait(false);
        var service = new DocumentAnalysisService(session, _analysisOptions, _syntaxTreeProvider, _structuredTypeProvider, _flowProvider);
        var snapshot = await GetSnapshotAsync(path, text, cancellationToken).ConfigureAwait(false);
        var cache = LayerCacheFor(snapshot);
        if (read(cache) is { } cached)
            return cached;
        var value = await build(service, snapshot, cancellationToken).ConfigureAwait(false);
        write(cache, value);
        return value;
    }

    private LayerCache LayerCacheFor(DocumentSnapshot snapshot)
        => _layers.GetOrAdd(
            $"{snapshot.Document.Uri}|{snapshot.Document.Version}",
            _ => new LayerCache());

    private async Task<(DocumentSnapshot Snapshot, SymbolGraph Symbols, ScopeGraph Scopes, IndexInvalidation Invalidation)>
        BuildIndexAsync(
            string path,
            string? text,
            CancellationToken cancellationToken,
            LspRange? changedRange = null)
    {
        var (session, document) = await EnsureFileAsync(path, text, cancellationToken).ConfigureAwait(false);
        var service = new DocumentAnalysisService(session, _analysisOptions, _syntaxTreeProvider, _structuredTypeProvider, _flowProvider);
        var snapshot = await service.BuildSnapshotAsync(document, cancellationToken).ConfigureAwait(false);
        var symbols = await service.BuildSymbolsAsync(snapshot, cancellationToken).ConfigureAwait(false);
        var scopes = service.BuildScopes(snapshot);
        var stubs = await ExtractStubsAsync(snapshot, symbols, cancellationToken).ConfigureAwait(false);
        var invalidation = changedRange is { } range
            ? Index.UpdateRange(snapshot, symbols, scopes, range, stubs)
            : Index.AddOrUpdate(snapshot, symbols, scopes, stubs);
        return (snapshot, symbols, scopes, invalidation);
    }

    public async Task<IReadOnlyList<DocumentHighlightFact>> GetHighlightsAsync(
        string path,
        LspPosition position,
        CancellationToken cancellationToken = default)
    {
        var (session, document) = await EnsureFileAsync(path, null, cancellationToken).ConfigureAwait(false);
        return await Operations(session)
            .HighlightsAsync(document.Uri, position, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<RenameTarget?> PrepareRenameAsync(
        string path,
        LspPosition position,
        CancellationToken cancellationToken = default)
    {
        var (session, document) = await EnsureFileAsync(path, null, cancellationToken).ConfigureAwait(false);
        return await Operations(session)
            .PrepareRenameAsync(document.Uri, position, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<WorkspaceEditFact?> RenameAsync(
        string path,
        LspPosition position,
        string newName,
        CancellationToken cancellationToken = default)
    {
        var (session, document) = await EnsureFileAsync(path, null, cancellationToken).ConfigureAwait(false);
        return await Operations(session)
            .RenameAsync(document.Uri, position, newName, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CodeActionFact>> GetCodeActionsAsync(
        string path,
        LspRange range,
        CancellationToken cancellationToken = default)
    {
        var (session, document) = await EnsureFileAsync(path, null, cancellationToken).ConfigureAwait(false);
        var diagnostics = session.Diagnostics(document.Uri)?.Items ?? [];
        return await Operations(session)
            .CodeActionsAsync(document.Uri, range, diagnostics, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<CodeActionFact?> ResolveCodeActionAsync(
        string path,
        JsonElement action,
        CancellationToken cancellationToken = default)
    {
        var (session, _) = await EnsureFileAsync(path, null, cancellationToken).ConfigureAwait(false);
        return await new LspOperations(session, session.Capabilities, session.Supports)
            .ResolveCodeActionAsync(action, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<WorkspaceSymbolFact>> GetWorkspaceSymbolsAsync(
        string languageId,
        string query,
        CancellationToken cancellationToken = default)
    {
        var profile = LanguageCatalog.FindByLanguageId(languageId)
            ?? throw new NotSupportedException($"no language profile for '{languageId}'");
        var session = await GetSessionAsync(profile, cancellationToken).ConfigureAwait(false);
        return await Operations(session)
            .WorkspaceSymbolsAsync(query, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<LspLocation>> GetDefinitionAsync(
        string path,
        LspPosition position,
        CancellationToken cancellationToken = default)
    {
        var (session, document) = await EnsureFileAsync(path, null, cancellationToken).ConfigureAwait(false);
        var result = await session.DefinitionAsync(document.Uri, position, cancellationToken).ConfigureAwait(false);
        return SymbolGraphBuilder.ParseLocations(result);
    }

    public async Task<JsonElement?> RequestAsync(
        string path,
        string method,
        JsonObject? parameters,
        CancellationToken cancellationToken = default)
    {
        var (session, document) = await EnsureFileAsync(path, null, cancellationToken).ConfigureAwait(false);
        var request = parameters?.DeepClone() as JsonObject ?? new JsonObject();
        if (request["textDocument"] is null && method.StartsWith("textDocument/", StringComparison.Ordinal))
            request["textDocument"] = LspJson.TextDocumentNode(document.Uri);
        return await session.RequestAsync(method, request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SelectionChain>> GetSelectionChainsAsync(
        string path,
        IReadOnlyList<LspPosition> positions,
        CancellationToken cancellationToken = default)
    {
        var (session, document) = await EnsureFileAsync(path, null, cancellationToken).ConfigureAwait(false);
        return await new DocumentAnalysisService(session, _analysisOptions, _syntaxTreeProvider, _structuredTypeProvider, _flowProvider)
            .GetSelectionChainsAsync(document, positions, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<SyntaxSnapshot> GetSelectionSnapshotAsync(
        string path,
        IReadOnlyList<LspPosition> positions,
        string? text = null,
        CancellationToken cancellationToken = default)
    {
        var (session, document) = await EnsureFileAsync(path, text, cancellationToken).ConfigureAwait(false);
        var service = new DocumentAnalysisService(session, _analysisOptions, _syntaxTreeProvider, _structuredTypeProvider, _flowProvider);
        var snapshot = await service.BuildSnapshotAsync(document, cancellationToken).ConfigureAwait(false);
        return await service.WithSelectionChainsAsync(document, snapshot.Syntax, positions, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<LspLocation>> GetReferencesAsync(
        string path,
        LspPosition position,
        bool includeDeclaration = true,
        CancellationToken cancellationToken = default)
    {
        var (session, document) = await EnsureFileAsync(path, null, cancellationToken).ConfigureAwait(false);
        var result = await session.ReferencesAsync(document.Uri, position, includeDeclaration, cancellationToken)
            .ConfigureAwait(false);
        return SymbolGraphBuilder.ParseLocations(result);
    }

    public async Task<IReadOnlyList<LspLocation>> GetDeclarationAsync(
        string path,
        LspPosition position,
        CancellationToken cancellationToken = default)
    {
        var (session, document) = await EnsureFileAsync(path, null, cancellationToken).ConfigureAwait(false);
        var result = await session.DeclarationAsync(document.Uri, position, cancellationToken).ConfigureAwait(false);
        return SymbolGraphBuilder.ParseLocations(result);
    }

    public async Task<IReadOnlyList<LspLocation>> GetTypeDefinitionAsync(
        string path,
        LspPosition position,
        CancellationToken cancellationToken = default)
    {
        var (session, document) = await EnsureFileAsync(path, null, cancellationToken).ConfigureAwait(false);
        var result = await session.TypeDefinitionAsync(document.Uri, position, cancellationToken).ConfigureAwait(false);
        return SymbolGraphBuilder.ParseLocations(result);
    }

    public async Task<IReadOnlyList<LspLocation>> GetImplementationAsync(
        string path,
        LspPosition position,
        CancellationToken cancellationToken = default)
    {
        var (session, document) = await EnsureFileAsync(path, null, cancellationToken).ConfigureAwait(false);
        var result = await session.ImplementationAsync(document.Uri, position, cancellationToken).ConfigureAwait(false);
        return SymbolGraphBuilder.ParseLocations(result);
    }

    public async Task<string?> GetHoverAsync(
        string path,
        LspPosition position,
        CancellationToken cancellationToken = default)
    {
        var (session, document) = await EnsureFileAsync(path, null, cancellationToken).ConfigureAwait(false);
        var result = await session.HoverAsync(document.Uri, position, cancellationToken).ConfigureAwait(false);
        return TypeFactExtractor.HoverText(LspJson.Property(result, "contents")) is { Length: > 0 } text
            ? text
            : null;
    }

    public async Task<IReadOnlyList<LspDiagnostic>> GetDiagnosticsAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await GetSnapshotAsync(path, null, cancellationToken).ConfigureAwait(false);
        return snapshot.Diagnostics;
    }

    public async Task SaveIndexAsync(string path, CancellationToken cancellationToken = default)
        => await WorkspaceIndexStore.SaveAsync(path, Index, cancellationToken).ConfigureAwait(false);

    public async Task<WorkspaceEditApplyResult> ApplyWorkspaceEditAsync(
        WorkspaceEditFact edit,
        bool writeToDisk = false,
        CancellationToken cancellationToken = default)
        => await ApplyWorkspaceEditAsync(
            edit,
            new WorkspaceEditApplyOptions(writeToDisk),
            cancellationToken).ConfigureAwait(false);

    public async Task<WorkspaceEditApplyResult> ApplyWorkspaceEditAsync(
        WorkspaceEditFact edit,
        WorkspaceEditApplyOptions options,
        CancellationToken cancellationToken = default)
    {
        var results = new List<DocumentEditResult>();
        var textPlans = new List<TextPlan>();
        foreach (var documentEdit in edit.Documents)
        {
            string? path = null;
            LspClientSession session;
            LspDocument document;
            if (!LspDocumentUri.TryGetPath(documentEdit.Uri, out var filePath))
            {
                if (!options.AllowNonFileUris)
                {
                    results.Add(new DocumentEditResult(
                        documentEdit.Uri,
                        null,
                        [new ValidationIssue("edit-uri", "workspace edit target is not a file URI", null)]));
                    return new WorkspaceEditApplyResult(results);
                }

                if (await FindOpenDocumentAsync(documentEdit.Uri, cancellationToken).ConfigureAwait(false) is not { } open)
                {
                    results.Add(new DocumentEditResult(
                        documentEdit.Uri,
                        null,
                        [new ValidationIssue("edit-uri", "non-file edit target is not open", null)]));
                    return new WorkspaceEditApplyResult(results);
                }

                (session, document) = open;
            }
            else
            {
                path = filePath;
                (session, document) = await EnsureFileAsync(path, null, cancellationToken).ConfigureAwait(false);
            }

            if (documentEdit.Version is { } version && version != document.Version)
            {
                results.Add(new DocumentEditResult(
                    documentEdit.Uri,
                    null,
                    [new ValidationIssue("edit-version", $"document version {version} does not match {document.Version}", null)]));
                return new WorkspaceEditApplyResult(results, Conflicted: true);
            }

            var application = WorkspaceEditApplier.Apply(document, documentEdit.Edits);
            if (!application.IsValid)
            {
                results.Add(new DocumentEditResult(documentEdit.Uri, null, application.Issues));
                return new WorkspaceEditApplyResult(
                    results,
                    Conflicted: application.Issues.Any(issue => issue.Code is "edit-overlap" or "edit-range" or "edit-outside-document"));
            }

            results.Add(new DocumentEditResult(
                documentEdit.Uri,
                application.Text,
                []));
            if (application.Text != document.Text)
                textPlans.Add(new TextPlan(
                    document.Uri,
                    path,
                    session,
                    document,
                    application.Text,
                    DiskWriteStamp(path)));
        }

        var operations = edit.FileOperations ?? [];
        var operationPlans = new List<FilePlan>();
        foreach (var operation in operations)
        {
            if (!LspDocumentUri.TryGetPath(operation.Uri, out var sourcePath))
            {
                results.Add(new DocumentEditResult(
                    operation.Uri,
                    null,
                    [new ValidationIssue("edit-uri", "file operation target is not a file URI", null)]));
                return new WorkspaceEditApplyResult(results);
            }

            var targetPath = sourcePath;
            if (operation.NewUri is { } newUri)
            {
                if (!LspDocumentUri.TryGetPath(newUri, out targetPath))
                {
                    results.Add(new DocumentEditResult(
                        newUri,
                        null,
                        [new ValidationIssue("edit-uri", "rename target is not a file URI", null)]));
                    return new WorkspaceEditApplyResult(results);
                }
            }

            var issue = ValidateFileOperation(operation, sourcePath, targetPath);
            if (issue is null)
            {
                operationPlans.Add(new FilePlan(operation, sourcePath, targetPath));
                results.Add(new DocumentEditResult(operation.Uri, null, []));
            }
            else
            {
                results.Add(new DocumentEditResult(operation.Uri, null, [issue]));
                return new WorkspaceEditApplyResult(results);
            }
        }

        if (operationPlans.Count > 0 && !options.WriteToDisk && !options.DryRun)
        {
            results.Add(new DocumentEditResult(
                edit.Documents.FirstOrDefault()?.Uri ?? "",
                null,
                [new ValidationIssue("edit-disk", "file operations require writeToDisk", null)]));
            return new WorkspaceEditApplyResult(results);
        }

        var diskConflict = textPlans
            .Where(plan => plan.Path is { } path
                && plan.DiskWriteStamp is { } stamp
                && File.Exists(path)
                && File.GetLastWriteTimeUtc(path) != stamp)
            .Select(plan => new DocumentEditResult(
                plan.Uri,
                null,
                [new ValidationIssue("edit-disk-changed", $"file changed after it was read: {plan.Path}", null)]))
            .ToArray();
        if (diskConflict.Length > 0)
        {
            results.AddRange(diskConflict);
            return new WorkspaceEditApplyResult(results, Conflicted: true);
        }

        var textBackups = textPlans.ToDictionary(plan => plan.Uri, plan => plan.Original.Text, StringComparer.Ordinal);
        var fileBackups = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var created = new List<string>();
        var renamed = new List<(string Source, string Target)>();
        FileStream? transactionLock = null;
        try
        {
            if (options.DryRun)
                return new WorkspaceEditApplyResult(results, DryRun: true);
            transactionLock = AcquireTransactionLock();
            if (options.WriteToDisk)
            {
                foreach (var plan in textPlans)
                {
                    if (plan.Path is { } textPath)
                        await File.WriteAllTextAsync(textPath, plan.Text, cancellationToken).ConfigureAwait(false);
                }
                foreach (var plan in operationPlans)
                {
                    switch (plan.Operation.Kind)
                    {
                        case WorkspaceFileOperationKind.Create:
                            if (File.Exists(plan.SourcePath) && plan.Operation.IgnoreIfExists)
                                break;
                            var existed = File.Exists(plan.SourcePath);
                            if (!existed || plan.Operation.Overwrite)
                            {
                                if (existed)
                                    fileBackups[plan.SourcePath] = await File.ReadAllBytesAsync(plan.SourcePath, cancellationToken).ConfigureAwait(false);
                                Directory.CreateDirectory(Path.GetDirectoryName(plan.SourcePath)!);
                                await File.WriteAllTextAsync(plan.SourcePath, string.Empty, cancellationToken).ConfigureAwait(false);
                                if (!existed)
                                    created.Add(plan.SourcePath);
                            }
                            break;
                        case WorkspaceFileOperationKind.Rename:
                            if (File.Exists(plan.TargetPath) && plan.Operation.IgnoreIfExists)
                                break;
                            fileBackups[plan.SourcePath] = await File.ReadAllBytesAsync(plan.SourcePath, cancellationToken).ConfigureAwait(false);
                            File.Move(plan.SourcePath, plan.TargetPath, plan.Operation.Overwrite);
                            renamed.Add((plan.SourcePath, plan.TargetPath));
                            break;
                        case WorkspaceFileOperationKind.Delete:
                            if (!File.Exists(plan.SourcePath) && plan.Operation.IgnoreIfExists)
                                break;
                            fileBackups[plan.SourcePath] = await File.ReadAllBytesAsync(plan.SourcePath, cancellationToken).ConfigureAwait(false);
                            File.Delete(plan.SourcePath);
                            break;
                    }
                }
            }

            foreach (var plan in textPlans)
            {
                await plan.Session.ChangeAsync(
                    plan.Uri,
                    [LspTextChange.Full(plan.Text)],
                    cancellationToken).ConfigureAwait(false);
                Index.Remove(plan.Uri);
            }
        }
        catch (Exception error)
        {
            await RollbackAsync(textPlans, textBackups, fileBackups, created, renamed, options.WriteToDisk, cancellationToken)
                .ConfigureAwait(false);
            results.Add(new DocumentEditResult(
                textPlans.FirstOrDefault()?.Uri ?? "",
                null,
                [new ValidationIssue("edit-rollback", $"workspace edit rolled back: {error.Message}", null)]));
            return new WorkspaceEditApplyResult(results);
        }
        finally
        {
            transactionLock?.Dispose();
        }

        return new WorkspaceEditApplyResult(results);
    }

    private static ValidationIssue? ValidateFileOperation(
        WorkspaceFileOperation operation,
        string sourcePath,
        string targetPath)
    {
        return operation.Kind switch
        {
            WorkspaceFileOperationKind.Create when File.Exists(sourcePath)
                && !operation.Overwrite && !operation.IgnoreIfExists
                => new ValidationIssue("file-exists", $"file already exists: {sourcePath}", null),
            WorkspaceFileOperationKind.Rename when !File.Exists(sourcePath)
                && !operation.IgnoreIfExists
                => new ValidationIssue("file-missing", $"rename source does not exist: {sourcePath}", null),
            WorkspaceFileOperationKind.Rename when File.Exists(targetPath)
                && !operation.Overwrite && !operation.IgnoreIfExists
                => new ValidationIssue("file-exists", $"rename target already exists: {targetPath}", null),
            WorkspaceFileOperationKind.Delete when !File.Exists(sourcePath)
                && !operation.IgnoreIfExists
                => new ValidationIssue("file-missing", $"delete target does not exist: {sourcePath}", null),
            _ => null,
        };
    }

    private static DateTime? DiskWriteStamp(string? path)
        => path is not null && File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;

    private FileStream AcquireTransactionLock()
    {
        var directory = Path.Combine(_rootPath, ".dsh-lsp");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "edit.lock");
        try
        {
            return new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                1,
                FileOptions.DeleteOnClose);
        }
        catch (IOException error)
        {
            throw new InvalidOperationException("another workspace edit is in progress", error);
        }
    }

    private static async Task RollbackAsync(
        IReadOnlyList<TextPlan> plans,
        IReadOnlyDictionary<string, string> textBackups,
        IReadOnlyDictionary<string, byte[]> fileBackups,
        IReadOnlyList<string> created,
        IReadOnlyList<(string Source, string Target)> renamed,
        bool writeToDisk,
        CancellationToken cancellationToken)
    {
        foreach (var (source, target) in renamed.AsEnumerable().Reverse())
        {
            if (File.Exists(target))
                File.Move(target, source, overwrite: true);
        }

        foreach (var path in created)
        {
            if (File.Exists(path))
                File.Delete(path);
        }

        foreach (var (path, bytes) in fileBackups)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
        }

        foreach (var plan in plans)
        {
            if (!textBackups.TryGetValue(plan.Uri, out var original))
                continue;
            if (writeToDisk && plan.Path is { } textPath)
                await File.WriteAllTextAsync(textPath, original, cancellationToken).ConfigureAwait(false);
            await plan.Session.ChangeAsync(
                plan.Uri,
                [LspTextChange.Full(original)],
                cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed record TextPlan(
        string Uri,
        string? Path,
        LspClientSession Session,
        LspDocument Original,
        string Text,
        DateTime? DiskWriteStamp);

    private sealed record FilePlan(
        WorkspaceFileOperation Operation,
        string SourcePath,
        string TargetPath);

    public async Task<WorkspaceEditApplyResult> ApplyRenameAsync(
        string path,
        LspPosition position,
        string newName,
        bool writeToDisk = false,
        CancellationToken cancellationToken = default)
    {
        var edit = await RenameAsync(path, position, newName, cancellationToken).ConfigureAwait(false);
        return edit is null
            ? new WorkspaceEditApplyResult([
                new DocumentEditResult(path, null, [new ValidationIssue("rename", "rename returned no edit", null)]),
            ])
            : await ApplyWorkspaceEditAsync(edit, writeToDisk, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkspaceEditApplyResult> ApplyCodeActionEditAsync(
        CodeActionFact action,
        bool writeToDisk = false,
        CancellationToken cancellationToken = default)
        => action.Edit is null
            ? action.Command is null
                ? new WorkspaceEditApplyResult([
                    new DocumentEditResult(action.Title, null, [new ValidationIssue("code-action", "code action has no edit", null)]),
                ])
                : new WorkspaceEditApplyResult([])
            : await ApplyWorkspaceEditAsync(action.Edit, writeToDisk, cancellationToken).ConfigureAwait(false);

    public async Task<WorkspaceEditApplyResult> ApplyCodeActionAsync(
        string path,
        LspRange range,
        string title,
        bool writeToDisk = false,
        CancellationToken cancellationToken = default)
    {
        var actions = await GetCodeActionsAsync(path, range, cancellationToken).ConfigureAwait(false);
        var action = actions.FirstOrDefault(candidate => candidate.Title.Equals(title, StringComparison.Ordinal));
        if (action is null)
        {
            return new WorkspaceEditApplyResult([
                new DocumentEditResult(path, null, [new ValidationIssue("code-action", $"code action not found: {title}", null)]),
            ]);
        }

        if (action.Edit is null && action.Raw is { } raw)
        {
            action = await ResolveCodeActionAsync(path, raw, cancellationToken).ConfigureAwait(false) ?? action;
        }

        var result = await ApplyCodeActionEditAsync(action, writeToDisk, cancellationToken).ConfigureAwait(false);
        if (result.IsValid && action.Command is { } command)
            result = result with { Command = await ExecuteCommandAsync(path, command, cancellationToken).ConfigureAwait(false) };
        return result;
    }

    public async Task<CommandExecutionResult> ExecuteCommandAsync(
        string path,
        CommandFact command,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var (session, _) = await EnsureFileAsync(path, null, cancellationToken).ConfigureAwait(false);
            var parameters = new JsonObject { ["command"] = command.Command };
            if (command.Arguments is { } arguments)
                parameters["arguments"] = JsonNode.Parse(arguments.GetRawText());
            var result = await session.RequestAsync("workspace/executeCommand", parameters, cancellationToken)
                .ConfigureAwait(false);
            return new CommandExecutionResult(command.Command, result);
        }
        catch (Exception error) when (error is LspResponseException or LspRequestTimeoutException or IOException)
        {
            return new CommandExecutionResult(command.Command, null, error.Message);
        }
    }

    public async Task RestartSessionAsync(string languageId, CancellationToken cancellationToken = default)
    {
        var profile = LanguageCatalog.FindByLanguageId(languageId)
            ?? throw new NotSupportedException($"no language profile for '{languageId}'");
        if (_sessions.TryRemove(profile.Id, out var existing) && existing.IsValueCreated)
        {
            var session = await existing.Value.ConfigureAwait(false);
            await session.DisposeAsync().ConfigureAwait(false);
        }

        await GetSessionAsync(profile, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(LspClientSession Session, LspDocument Document)> EnsureFileAsync(
        string path,
        string? text,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var fullPath = Path.GetFullPath(path);
        var profile = LanguageCatalog.ResolveByPath(fullPath);
        var session = await GetSessionAsync(profile, cancellationToken).ConfigureAwait(false);
        var document = await EnsureDocumentAsync(session, profile, fullPath, text, cancellationToken)
            .ConfigureAwait(false);
        return (session, document);
    }

    public async Task<IReadOnlyList<ZoneView>> GetZoneViewsAsync(
        string path,
        string? text = null,
        bool includeSymbols = false,
        bool includeTypes = false,
        CancellationToken cancellationToken = default)
    {
        var profile = LanguageCatalog.ResolveByPath(Path.GetFullPath(path));
        var (_, document) = await EnsureFileAsync(path, text, cancellationToken).ConfigureAwait(false);
        var zones = _zoneProvider.Detect(document, profile.Id).Take(8).ToArray();
        var views = new ConcurrentBag<ZoneView>();
        await Parallel.ForEachAsync(
            zones,
            new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = cancellationToken },
            async (zone, token) =>
            {
                var (session, zoneDocument) = await EnsureFileAsync(zone.Path, zone.Text, token).ConfigureAwait(false);
                var service = new DocumentAnalysisService(
                    session,
                    _analysisOptions,
                    _syntaxTreeProvider,
                    _structuredTypeProvider,
                    _flowProvider);
                var snapshot = await service.BuildSnapshotAsync(zoneDocument, token).ConfigureAwait(false);
                var symbols = includeSymbols
                    ? await service.BuildSymbolsAsync(snapshot, token).ConfigureAwait(false)
                    : null;
                var types = includeTypes
                    ? await service.ExtractTypesAsync(snapshot, token).ConfigureAwait(false)
                    : null;
                views.Add(new ZoneView(
                    zone,
                    new ZoneMapping(zone.ParentRange ?? new LspRange(LspPosition.Zero, LspPosition.Zero), zone.ParentUri),
                    snapshot,
                    symbols,
                    types));
            }).ConfigureAwait(false);
        return [.. views];
    }

    public async Task<LspClientSession> GetSessionAsync(
        LanguageServerProfile profile,
        CancellationToken cancellationToken = default)
    {
        var lazy = _sessions.GetOrAdd(
            profile.Id,
            _ => new Lazy<Task<LspClientSession>>(
                () => LspClientFactory.StartAsync(profile, _options, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return await lazy.Value.ConfigureAwait(false);
        }
        catch
        {
            _sessions.TryRemove(profile.Id, out _);
            throw;
        }
    }

    public async Task<bool> SupportsAsync(
        string path,
        string method,
        CancellationToken cancellationToken = default)
    {
        var (session, _) = await EnsureFileAsync(path, null, cancellationToken).ConfigureAwait(false);
        return session.Supports(method);
    }

    public async Task<JsonElement?> GetServerCapabilitiesAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var (session, _) = await EnsureFileAsync(path, null, cancellationToken).ConfigureAwait(false);
        return session.ServerCapabilities;
    }

    public async Task OpenFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        var profile = LanguageCatalog.ResolveByPath(fullPath);
        var session = await GetSessionAsync(profile, cancellationToken).ConfigureAwait(false);
        var text = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
        await EnsureDocumentAsync(session, profile, fullPath, text, cancellationToken).ConfigureAwait(false);
    }

    public async Task CloseFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        var profile = LanguageCatalog.FindByPath(fullPath);
        if (profile is null || !_sessions.TryGetValue(profile.Id, out var lazy) || !lazy.IsValueCreated)
            return;
        var session = await lazy.Value.ConfigureAwait(false);
        await session.CloseAsync(LspDocumentUri.FromPath(fullPath), cancellationToken).ConfigureAwait(false);
        _snapshots.TryRemove(LspDocumentUri.FromPath(fullPath), out _);
        ClearReferenceCache(LspDocumentUri.FromPath(fullPath));
    }

    public async Task NotifyFileChangedAsync(
        string path,
        LspFileEventType type,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        var uri = LspDocumentUri.FromPath(fullPath);
        _snapshots.TryRemove(uri, out _);
        ClearReferenceCache(uri);
        IReadOnlyList<string> affected;
        if (type == LspFileEventType.Created && File.Exists(fullPath))
        {
            var indexed = await BuildIndexAsync(fullPath, null, cancellationToken).ConfigureAwait(false);
            affected = indexed.Invalidation.AffectedUris;
        }
        else
        {
            affected = Index.Invalidate(uri);
        }

        await ReanalyzeUrisAsync(affected, cancellationToken).ConfigureAwait(false);
        var profile = LanguageCatalog.FindByPath(fullPath);
        if (profile is null || !_sessions.TryGetValue(profile.Id, out var lazy) || !lazy.IsValueCreated)
            return;
        var session = await lazy.Value.ConfigureAwait(false);
        await session.DidChangeWatchedFilesAsync(
            [new LspFileEvent(uri, type)],
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<IndexInvalidation>> ReanalyzeDependentsAsync(
        string uri,
        CancellationToken cancellationToken = default)
    {
        var dependents = Index.Dependents(uri);
        var results = new List<IndexInvalidation>();
        foreach (var dependent in dependents.Take(64))
        {
            if (!LspDocumentUri.TryGetPath(dependent, out var path) || !File.Exists(path))
                continue;
            var indexed = await BuildIndexAsync(path, null, cancellationToken).ConfigureAwait(false);
            results.Add(indexed.Invalidation);
        }

        return results;
    }

    private async Task ReanalyzeUrisAsync(
        IReadOnlyList<string> uris,
        CancellationToken cancellationToken)
    {
        foreach (var uri in uris.Distinct(StringComparer.Ordinal).Take(64))
        {
            if (!LspDocumentUri.TryGetPath(uri, out var path) || !File.Exists(path))
                continue;
            await BuildIndexAsync(path, null, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<(LspClientSession Session, LspDocument Document)?> FindOpenDocumentAsync(
        string uri,
        CancellationToken cancellationToken)
    {
        foreach (var lazy in _sessions.Values)
        {
            if (!lazy.IsValueCreated)
                continue;
            var session = await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (session.Documents.TryGet(uri, out var document) && document is not null)
                return (session, document);
        }

        return null;
    }

    private static async Task<LspDocument> EnsureDocumentAsync(
        LspClientSession session,
        LanguageServerProfile profile,
        string fullPath,
        string? text,
        CancellationToken cancellationToken)
    {
        var uri = LspDocumentUri.FromPath(fullPath);
        text ??= await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
        if (session.Documents.TryGet(uri, out var existing) && existing is not null)
        {
            return existing.Text == text
                ? existing
                : (await session.ChangeAsync(uri, [LspTextChange.Full(text)], cancellationToken).ConfigureAwait(false)).Document;
        }

        return await session.OpenAsync(uri, profile.LanguageIds[0], text, cancellationToken).ConfigureAwait(false);
    }

    private static LspOperations Operations(LspClientSession session)
        => new(session, session.Capabilities, session.Supports);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        foreach (var lazy in _sessions.Values)
        {
            if (!lazy.IsValueCreated)
                continue;
            try
            {
                var session = await lazy.Value.ConfigureAwait(false);
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                _ = error;
            }
        }

        _sessions.Clear();
        _snapshots.Clear();
        _layers.Clear();
        _symbolReferences.Clear();
    }

    private void ClearReferenceCache(string uri)
    {
        foreach (var key in _symbolReferences.Keys)
        {
            if (key.StartsWith($"{uri}|", StringComparison.Ordinal))
                _symbolReferences.TryRemove(key, out _);
        }

        foreach (var key in _layers.Keys)
        {
            if (key.StartsWith($"{uri}|", StringComparison.Ordinal))
                _layers.TryRemove(key, out _);
        }
    }

    private sealed class LayerCache
    {
        public SymbolGraph? Symbols { get; set; }
        public ScopeGraph? Scopes { get; set; }
        public HierarchyGraph? Hierarchy { get; set; }
        public TypeFactsSnapshot? Types { get; set; }
    }
}
