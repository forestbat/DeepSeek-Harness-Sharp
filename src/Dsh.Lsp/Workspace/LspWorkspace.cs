using System.Collections.Concurrent;
using Dsh.Lsp.Analysis;
using Dsh.Lsp.Catalog;
using Dsh.Lsp.Client;
using Dsh.Lsp.Model;
using Dsh.Lsp.Protocol;

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
    private readonly ConcurrentDictionary<string, Lazy<Task<LspClientSession>>> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private int _disposed;

    public LspWorkspace(
        string rootPath,
        LspClientOptions? options = null,
        DocumentAnalysisOptions? analysisOptions = null)
    {
        _rootPath = Path.GetFullPath(rootPath);
        _options = options ?? new LspClientOptions(_rootPath);
        _analysisOptions = analysisOptions ?? DocumentAnalysisOptions.Default;
    }

    public string RootPath => _rootPath;

    public async Task<DocumentAnalysis> AnalyzeFileAsync(
        string path,
        string? text = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var fullPath = Path.GetFullPath(path);
        var profile = LanguageCatalog.ResolveByPath(fullPath);
        var session = await GetSessionAsync(profile, cancellationToken).ConfigureAwait(false);
        var document = await EnsureDocumentAsync(session, profile, fullPath, text, cancellationToken)
            .ConfigureAwait(false);
        return await new DocumentAnalysisService(session, _analysisOptions)
            .AnalyzeAsync(document, cancellationToken)
            .ConfigureAwait(false);
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
        return await new DocumentAnalysisService(session, _analysisOptions)
            .AnalyzeAsync(document, cancellationToken)
            .ConfigureAwait(false);
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
