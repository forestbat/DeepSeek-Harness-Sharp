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

    public async Task<(DocumentAnalysis Analysis, IndexInvalidation Invalidation)> AnalyzeAndIndexWithInvalidationAsync(
        string path,
        string? text = null,
        CancellationToken cancellationToken = default)
    {
        var analysis = await AnalyzeFileAsync(path, text, cancellationToken).ConfigureAwait(false);
        return (analysis, Index.AddOrUpdate(analysis));
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
        if (request["textDocument"] is null)
            request["textDocument"] = LspJson.TextDocumentNode(document.Uri);
        return await session.RequestAsync(method, request, cancellationToken).ConfigureAwait(false);
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
        var textPlans = new List<TextPlan>();
        foreach (var documentEdit in edit.Documents)
        {
            if (!LspDocumentUri.TryGetPath(documentEdit.Uri, out var path))
            {
                results.Add(new DocumentEditResult(
                    documentEdit.Uri,
                    null,
                    [new ValidationIssue("edit-uri", "workspace edit target is not a file URI", null)]));
                return new WorkspaceEditApplyResult(results);
            }

            var (session, document) = await EnsureFileAsync(path, null, cancellationToken).ConfigureAwait(false);
            if (documentEdit.Version is { } version && version != document.Version)
            {
                results.Add(new DocumentEditResult(
                    documentEdit.Uri,
                    null,
                    [new ValidationIssue("edit-version", $"document version {version} does not match {document.Version}", null)]));
                return new WorkspaceEditApplyResult(results);
            }

            var application = WorkspaceEditApplier.Apply(document, documentEdit.Edits);
            if (!application.IsValid)
            {
                results.Add(new DocumentEditResult(documentEdit.Uri, null, application.Issues));
                return new WorkspaceEditApplyResult(results);
            }

            results.Add(new DocumentEditResult(
                documentEdit.Uri,
                application.Text,
                []));
            if (application.Text != document.Text)
                textPlans.Add(new TextPlan(document.Uri, path, session, document, application.Text));
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

        if (operationPlans.Count > 0 && !writeToDisk)
        {
            results.Add(new DocumentEditResult(
                edit.Documents.FirstOrDefault()?.Uri ?? "",
                null,
                [new ValidationIssue("edit-disk", "file operations require writeToDisk", null)]));
            return new WorkspaceEditApplyResult(results);
        }

        var textBackups = textPlans.ToDictionary(plan => plan.Path, plan => plan.Original.Text, StringComparer.Ordinal);
        var fileBackups = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var created = new List<string>();
        var renamed = new List<(string Source, string Target)>();
        try
        {
            if (writeToDisk)
            {
                foreach (var plan in textPlans)
                    await File.WriteAllTextAsync(plan.Path, plan.Text, cancellationToken).ConfigureAwait(false);
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
            await RollbackAsync(textPlans, textBackups, fileBackups, created, renamed, writeToDisk, cancellationToken)
                .ConfigureAwait(false);
            results.Add(new DocumentEditResult(
                textPlans.FirstOrDefault()?.Uri ?? "",
                null,
                [new ValidationIssue("edit-rollback", $"workspace edit rolled back: {error.Message}", null)]));
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
            if (!textBackups.TryGetValue(plan.Path, out var original))
                continue;
            if (writeToDisk)
                await File.WriteAllTextAsync(plan.Path, original, cancellationToken).ConfigureAwait(false);
            await plan.Session.ChangeAsync(
                plan.Uri,
                [LspTextChange.Full(original)],
                cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed record TextPlan(
        string Uri,
        string Path,
        LspClientSession Session,
        LspDocument Original,
        string Text);

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
            ? new WorkspaceEditApplyResult([
                new DocumentEditResult(action.Title, null, [new ValidationIssue("code-action", "code action has no edit", null)]),
            ])
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

        return await ApplyCodeActionEditAsync(action, writeToDisk, cancellationToken).ConfigureAwait(false);
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
                results[index] = new ZoneAnalysis(
                    zone,
                    analysis,
                    new ZoneMapping(zone.ParentRange ?? new LspRange(LspPosition.Zero, LspPosition.Zero)));
            }).ConfigureAwait(false);
        return [.. results.Where(result => result is not null).Select(result => result!)];
    }

    public async Task<DocumentAnalysis> AnalyzeWithZonesAsync(
        string path,
        string? text = null,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        var profile = LanguageCatalog.ResolveByPath(fullPath);
        var analysis = await AnalyzeFileAsync(fullPath, text, cancellationToken).ConfigureAwait(false);
        var zones = LanguageZoneDetector.Detect(analysis.Document, profile.Id);
        if (zones.Count == 0)
            return analysis;
        var zoneAnalyses = await AnalyzeZonesAsync(zones, 2, cancellationToken).ConfigureAwait(false);
        var diagnostics = analysis.Diagnostics
            .Concat(zoneAnalyses.SelectMany(zone => zone.MappedDiagnostics))
            .ToArray();
        return analysis with { Diagnostics = diagnostics, Zones = zoneAnalyses };
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
            Index.Invalidate(uri);
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
