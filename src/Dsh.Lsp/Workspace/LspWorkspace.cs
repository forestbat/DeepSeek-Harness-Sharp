using System.Collections.Concurrent;
using System.Text.Json;
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
    private readonly ConcurrentDictionary<string, Lazy<Task<LspClientSession>>> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private int _disposed;

    public LspWorkspace(
        string rootPath,
        LspClientOptions? options = null,
        DocumentAnalysisOptions? analysisOptions = null,
        ISyntaxTreeProvider? syntaxTreeProvider = null)
    {
        _rootPath = Path.GetFullPath(rootPath);
        _options = options ?? new LspClientOptions(_rootPath);
        _analysisOptions = analysisOptions ?? DocumentAnalysisOptions.Default;
        _syntaxTreeProvider = syntaxTreeProvider;
        Index = new WorkspaceIndex(_rootPath);
    }

    public string RootPath => _rootPath;

    public WorkspaceIndex Index { get; }

    public async Task<DocumentAnalysis> AnalyzeFileAsync(
        string path,
        string? text = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var (session, document) = await EnsureFileAsync(path, text, cancellationToken).ConfigureAwait(false);
        return await new DocumentAnalysisService(session, _analysisOptions, _syntaxTreeProvider)
            .AnalyzeAsync(document, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<DocumentAnalysis> AnalyzeAndIndexFileAsync(
        string path,
        string? text = null,
        CancellationToken cancellationToken = default)
    {
        var analysis = await AnalyzeFileAsync(path, text, cancellationToken).ConfigureAwait(false);
        Index.AddOrUpdate(analysis);
        return analysis;
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

    public async Task<IReadOnlyList<SelectionChain>> GetSelectionChainsAsync(
        string path,
        IReadOnlyList<LspPosition> positions,
        CancellationToken cancellationToken = default)
    {
        var (session, document) = await EnsureFileAsync(path, null, cancellationToken).ConfigureAwait(false);
        return await new DocumentAnalysisService(session, _analysisOptions, _syntaxTreeProvider)
            .GetSelectionChainsAsync(document, positions, cancellationToken)
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
        var (session, document) = await EnsureFileAsync(path, null, cancellationToken).ConfigureAwait(false);
        var pushed = session.Diagnostics(document.Uri)?.Items ?? [];
        if (!session.Supports("textDocument/diagnostic"))
            return pushed;
        JsonElement? result;
        try
        {
            result = await session.PullDiagnosticsAsync(document.Uri, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is LspResponseException or LspRequestTimeoutException)
        {
            return pushed;
        }
        var pulled = LspJson.String(result, "kind") == "unchanged"
            ? []
            : LspDiagnostics.ParseMany(LspJson.Property(result, "items"));
        return pushed
            .Concat(pulled)
            .DistinctBy(diagnostic => (diagnostic.Range, diagnostic.Code, diagnostic.Message))
            .ToArray();
    }

    public async Task SaveIndexAsync(string path, CancellationToken cancellationToken = default)
        => await WorkspaceIndexStore.SaveAsync(path, Index, cancellationToken).ConfigureAwait(false);

    public async Task<WorkspaceEditApplyResult> ApplyWorkspaceEditAsync(
        WorkspaceEditFact edit,
        bool writeToDisk = false,
        CancellationToken cancellationToken = default)
    {
        var results = new List<DocumentEditResult>();
        foreach (var documentEdit in edit.Documents)
        {
            if (!LspDocumentUri.TryGetPath(documentEdit.Uri, out var path))
            {
                results.Add(new DocumentEditResult(
                    documentEdit.Uri,
                    null,
                    [new ValidationIssue("edit-uri", "workspace edit target is not a file URI", null)]));
                continue;
            }

            var (session, document) = await EnsureFileAsync(path, null, cancellationToken).ConfigureAwait(false);
            var application = WorkspaceEditApplier.Apply(document, documentEdit.Edits);
            if (application.IsValid && application.Text != document.Text)
            {
                if (writeToDisk)
                    await File.WriteAllTextAsync(path, application.Text, cancellationToken).ConfigureAwait(false);
                await session.ChangeAsync(
                    document.Uri,
                    [LspTextChange.Full(application.Text)],
                    cancellationToken).ConfigureAwait(false);
                Index.Remove(document.Uri);
            }

            results.Add(new DocumentEditResult(
                documentEdit.Uri,
                application.IsValid ? application.Text : null,
                application.Issues));
        }

        return new WorkspaceEditApplyResult(results);
    }

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
            ? new WorkspaceEditApplyResult([
                new DocumentEditResult(action.Title, null, [new ValidationIssue("code-action", "code action has no edit", null)]),
            ])
            : await ApplyWorkspaceEditAsync(action.Edit, writeToDisk, cancellationToken).ConfigureAwait(false);

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

    public async Task<DocumentAnalysis> AnalyzeTextAsync(
        string languageId,
        string path,
        string text,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var profile = LanguageCatalog.FindByLanguageId(languageId)
            ?? throw new NotSupportedException($"no language profile for '{languageId}'");
        var session = await GetSessionAsync(profile, cancellationToken).ConfigureAwait(false);
        var document = await EnsureDocumentAsync(session, profile, Path.GetFullPath(path), text, cancellationToken)
            .ConfigureAwait(false);
        return await new DocumentAnalysisService(session, _analysisOptions, _syntaxTreeProvider)
            .AnalyzeAsync(document, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ZoneAnalysis>> AnalyzeZonesAsync(
        IReadOnlyList<LanguageZone> zones,
        int maxConcurrency = 2,
        CancellationToken cancellationToken = default)
    {
        var results = new ZoneAnalysis?[zones.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, zones.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, maxConcurrency),
                CancellationToken = cancellationToken,
            },
            async (index, token) =>
            {
                var zone = zones[index];
                var analysis = await AnalyzeTextAsync(zone.LanguageId, zone.Path, zone.Text, token)
                    .ConfigureAwait(false);
                results[index] = new ZoneAnalysis(zone, analysis);
            }).ConfigureAwait(false);
        return [.. results.Where(result => result is not null).Select(result => result!)];
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
    }

    public async Task NotifyFileChangedAsync(
        string path,
        LspFileEventType type,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        var uri = LspDocumentUri.FromPath(fullPath);
        if (type != LspFileEventType.Created)
            Index.Remove(uri);
        var profile = LanguageCatalog.FindByPath(fullPath);
        if (profile is null || !_sessions.TryGetValue(profile.Id, out var lazy) || !lazy.IsValueCreated)
            return;
        var session = await lazy.Value.ConfigureAwait(false);
        await session.DidChangeWatchedFilesAsync(
            [new LspFileEvent(uri, type)],
            cancellationToken).ConfigureAwait(false);
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
    }
}
