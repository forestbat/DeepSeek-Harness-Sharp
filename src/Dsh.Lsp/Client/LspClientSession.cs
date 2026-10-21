using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Lsp.Analysis;
using Dsh.Lsp.Model;
using Dsh.Lsp.Protocol;

namespace Dsh.Lsp.Client;

public sealed record LspDiagnosticSnapshot(
    string Uri,
    int? Version,
    IReadOnlyList<LspDiagnostic> Items,
    string? ResultId = null);

/**
 * Owns one initialized language-server process and its open-document state.
 * High-level analysis is built on the request methods here, never on raw transport calls.
 */
public sealed class LspClientSession : ILspRequestChannel, IAsyncDisposable
{
    private readonly LspMessageConnection _connection;
    private readonly LspClientOptions _options;
    private readonly LspDocumentStore _documents = new();
    private readonly Dictionary<string, LspDiagnosticSnapshot> _diagnostics = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _pullResultIds = new(StringComparer.Ordinal);
    private readonly List<LspWorkspaceFolder> _workspaceFolders;
    private readonly HashSet<string> _dynamicMethods = new(StringComparer.Ordinal);
    private readonly Task<string> _standardError;
    private readonly Lock _stateGate = new();
    private LspCapabilityProfile _capabilities = EmptyCapabilities();
    private JsonElement? _serverCapabilities;
    private bool _initialized;

    public LspClientSession(LspMessageConnection connection, LspClientOptions options, Task<string>? standardError = null)
    {
        _connection = connection;
        _options = options;
        _standardError = standardError ?? Task.FromResult(string.Empty);
        _workspaceFolders = [.. options.EffectiveWorkspaceFolders];
        _connection.Notification += OnNotification;
        _connection.ServerRequestHandler = OnServerRequestAsync;
    }

    public event Action<LspDiagnosticSnapshot>? DiagnosticsPublished;

    public event Action<JsonElement?>? ProgressChanged;

    public LspCapabilityProfile Capabilities
    {
        get
        {
            lock (_stateGate)
                return _capabilities;
        }
    }

    public LspDocumentStore Documents => _documents;

    public IReadOnlyList<LspWorkspaceFolder> WorkspaceFolders
    {
        get
        {
            lock (_stateGate)
                return [.. _workspaceFolders];
        }
    }

    public JsonElement? ServerCapabilities
    {
        get
        {
            lock (_stateGate)
                return _serverCapabilities;
        }
    }

    public Task<string> StandardError => _standardError;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
            return;

        _connection.Start();
        var workspaceFolders = new JsonArray();
        foreach (var folder in WorkspaceFolders)
        {
            workspaceFolders.Add(new JsonObject
            {
                ["uri"] = folder.Uri,
                ["name"] = folder.Name,
            });
        }

        var parameters = new JsonObject
        {
            ["processId"] = Environment.ProcessId,
            ["clientInfo"] = new JsonObject
            {
                ["name"] = _options.ClientName,
                ["version"] = _options.ClientVersion,
            },
            ["rootUri"] = _options.RootUri,
            ["workspaceFolders"] = workspaceFolders,
            ["capabilities"] = LspClientCapabilities.Build(),
            ["trace"] = "off",
        };
        if (_options.InitializationOptions is not null)
            parameters["initializationOptions"] = _options.InitializationOptions.DeepClone();

        var result = await _connection.RequestAsync("initialize", parameters, cancellationToken).ConfigureAwait(false);
        var capabilities = LspCapabilityParser.Parse(result);
        lock (_stateGate)
        {
            _serverCapabilities = LspJson.Property(result, "capabilities");
            _capabilities = capabilities;
        }

        await _connection.NotifyAsync("initialized", new JsonObject(), cancellationToken).ConfigureAwait(false);
        if (_options.Settings is not null)
        {
            await _connection.NotifyAsync("workspace/didChangeConfiguration", new JsonObject
            {
                ["settings"] = _options.Settings.DeepClone(),
            }, cancellationToken).ConfigureAwait(false);
        }

        _initialized = true;
    }

    public async Task<LspDocument> OpenAsync(
        string uri,
        string languageId,
        string text,
        CancellationToken cancellationToken = default)
    {
        var document = _documents.Open(uri, languageId, text);
        await _connection.NotifyAsync("textDocument/didOpen", new JsonObject
        {
            ["textDocument"] = new JsonObject
            {
                ["uri"] = document.Uri,
                ["languageId"] = document.LanguageId,
                ["version"] = document.Version,
                ["text"] = document.Text,
            },
        }, cancellationToken).ConfigureAwait(false);
        return document;
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (!_initialized)
            return;
        _initialized = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await _connection.RequestAsync("shutdown", null, timeout.Token).ConfigureAwait(false);
            await _connection.NotifyAsync("exit", null, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _ = error;
        }
    }

    public async Task<LspDocumentUpdate> ChangeAsync(
        string uri,
        IReadOnlyList<LspTextChange> changes,
        CancellationToken cancellationToken = default)
    {
        var update = _documents.Change(uri, changes);
        if (Capabilities.SyncKind == LspTextDocumentSyncKind.None)
            return update;

        var contentChanges = new JsonArray();
        if (Capabilities.SyncKind == LspTextDocumentSyncKind.Full)
        {
            contentChanges.Add(new JsonObject { ["text"] = update.Document.Text });
        }
        else
        {
            foreach (var change in changes)
            {
                var item = new JsonObject { ["text"] = change.Text };
                if (change.Range is { } range)
                    item["range"] = LspJson.RangeNode(range);
                if (change.RangeLength is { } rangeLength)
                    item["rangeLength"] = rangeLength;
                contentChanges.Add(item);
            }
        }

        await _connection.NotifyAsync("textDocument/didChange", new JsonObject
        {
            ["textDocument"] = new JsonObject
            {
                ["uri"] = update.Document.Uri,
                ["version"] = update.Document.Version,
            },
            ["contentChanges"] = contentChanges,
        }, cancellationToken).ConfigureAwait(false);
        return update;
    }

    public async Task CloseAsync(string uri, CancellationToken cancellationToken = default)
    {
        var document = _documents.Close(uri);
        if (document is null)
            return;
        await _connection.NotifyAsync("textDocument/didClose", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = document.Uri },
        }, cancellationToken).ConfigureAwait(false);
        lock (_stateGate)
        {
            _diagnostics.Remove(document.Uri);
            _pullResultIds.Remove(document.Uri);
        }
    }

    public async Task SaveAsync(string uri, string? text = null, CancellationToken cancellationToken = default)
    {
        if (!_documents.TryGet(uri, out var document) || document is null)
            return;
        var parameters = new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = document.Uri },
        };
        if (text is not null)
            parameters["text"] = text;
        await _connection.NotifyAsync("textDocument/didSave", parameters, cancellationToken).ConfigureAwait(false);
    }

    public async Task WillSaveAsync(
        string uri,
        LspSaveReason? reason = null,
        CancellationToken cancellationToken = default)
    {
        var parameters = new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = LspDocumentUri.Normalize(uri) },
        };
        if (reason is { } value)
            parameters["reason"] = (int)value;
        await _connection.NotifyAsync("textDocument/willSave", parameters, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkspaceEditFact?> WillSaveWaitUntilAsync(
        string uri,
        LspSaveReason? reason = null,
        CancellationToken cancellationToken = default)
    {
        var parameters = new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = LspDocumentUri.Normalize(uri) },
        };
        if (reason is { } value)
            parameters["reason"] = (int)value;
        var result = await _connection.RequestAsync("textDocument/willSaveWaitUntil", parameters, cancellationToken)
            .ConfigureAwait(false);
        return LspOperations.ParseWorkspaceEdit(result);
    }

    public async Task AddWorkspaceFolderAsync(
        LspWorkspaceFolder folder,
        CancellationToken cancellationToken = default)
    {
        lock (_stateGate)
        {
            if (_workspaceFolders.Any(existing => existing.Uri == folder.Uri))
                return;
            _workspaceFolders.Add(folder);
        }

        await _connection.NotifyAsync("workspace/didChangeWorkspaceFolders", new JsonObject
        {
            ["event"] = new JsonObject
            {
                ["added"] = new JsonArray(FolderNode(folder)),
                ["removed"] = new JsonArray(),
            },
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveWorkspaceFolderAsync(
        string uri,
        CancellationToken cancellationToken = default)
    {
        LspWorkspaceFolder? removed;
        lock (_stateGate)
        {
            removed = _workspaceFolders.FirstOrDefault(folder => folder.Uri == uri);
            if (removed is not null)
                _workspaceFolders.Remove(removed);
        }

        if (removed is null)
            return;
        await _connection.NotifyAsync("workspace/didChangeWorkspaceFolders", new JsonObject
        {
            ["event"] = new JsonObject
            {
                ["added"] = new JsonArray(),
                ["removed"] = new JsonArray(FolderNode(removed)),
            },
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task UpdateConfigurationAsync(JsonNode? settings, CancellationToken cancellationToken = default)
        => _connection.NotifyAsync("workspace/didChangeConfiguration", new JsonObject
        {
            ["settings"] = settings?.DeepClone(),
        }, cancellationToken);

    public Task DidChangeWatchedFilesAsync(
        IReadOnlyList<LspFileEvent> changes,
        CancellationToken cancellationToken = default)
    {
        var items = new JsonArray();
        foreach (var change in changes)
        {
            items.Add(new JsonObject
            {
                ["uri"] = change.Uri,
                ["type"] = (int)change.Type,
            });
        }

        return _connection.NotifyAsync("workspace/didChangeWatchedFiles", new JsonObject
        {
            ["changes"] = items,
        }, cancellationToken);
    }

    public bool Supports(string method)
    {
        lock (_stateGate)
            return _capabilities.Supports(method) || _dynamicMethods.Contains(method);
    }

    public LspDiagnosticSnapshot? Diagnostics(string uri)
    {
        lock (_stateGate)
            return _diagnostics.GetValueOrDefault(LspDocumentUri.Normalize(uri));
    }

    public async Task<JsonElement?> DocumentSymbolsAsync(string uri, CancellationToken cancellationToken = default)
        => await RequestIfSupportedAsync("textDocument/documentSymbol", new JsonObject
        {
            ["textDocument"] = LspJson.TextDocumentNode(uri),
        }, cancellationToken).ConfigureAwait(false);

    public async Task<JsonElement?> FoldingRangesAsync(string uri, CancellationToken cancellationToken = default)
        => await RequestIfSupportedAsync("textDocument/foldingRange", new JsonObject
        {
            ["textDocument"] = LspJson.TextDocumentNode(uri),
        }, cancellationToken).ConfigureAwait(false);

    public async Task<JsonElement?> SelectionRangesAsync(
        string uri,
        IReadOnlyList<LspPosition> positions,
        CancellationToken cancellationToken = default)
    {
        var items = new JsonArray();
        foreach (var position in positions)
            items.Add(LspJson.PositionNode(position));
        return await RequestIfSupportedAsync("textDocument/selectionRange", new JsonObject
        {
            ["textDocument"] = LspJson.TextDocumentNode(uri),
            ["positions"] = items,
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<JsonElement?> SemanticTokensAsync(string uri, CancellationToken cancellationToken = default)
        => await RequestIfSupportedAsync("textDocument/semanticTokens/full", new JsonObject
        {
            ["textDocument"] = LspJson.TextDocumentNode(uri),
        }, cancellationToken).ConfigureAwait(false);

    public async Task<JsonElement?> HoverAsync(
        string uri,
        LspPosition position,
        CancellationToken cancellationToken = default)
        => await RequestIfSupportedAsync("textDocument/hover", PositionParameters(uri, position), cancellationToken)
            .ConfigureAwait(false);

    public async Task<JsonElement?> DefinitionAsync(
        string uri,
        LspPosition position,
        CancellationToken cancellationToken = default)
        => await RequestIfSupportedAsync("textDocument/definition", PositionParameters(uri, position), cancellationToken)
            .ConfigureAwait(false);

    public async Task<JsonElement?> DeclarationAsync(
        string uri,
        LspPosition position,
        CancellationToken cancellationToken = default)
        => await RequestIfSupportedAsync("textDocument/declaration", PositionParameters(uri, position), cancellationToken)
            .ConfigureAwait(false);

    public async Task<JsonElement?> TypeDefinitionAsync(
        string uri,
        LspPosition position,
        CancellationToken cancellationToken = default)
        => await RequestIfSupportedAsync("textDocument/typeDefinition", PositionParameters(uri, position), cancellationToken)
            .ConfigureAwait(false);

    public async Task<JsonElement?> ImplementationAsync(
        string uri,
        LspPosition position,
        CancellationToken cancellationToken = default)
        => await RequestIfSupportedAsync("textDocument/implementation", PositionParameters(uri, position), cancellationToken)
            .ConfigureAwait(false);

    public async Task<JsonElement?> ReferencesAsync(
        string uri,
        LspPosition position,
        bool includeDeclaration = true,
        CancellationToken cancellationToken = default)
    {
        var parameters = PositionParameters(uri, position);
        parameters["context"] = new JsonObject { ["includeDeclaration"] = includeDeclaration };
        return await RequestIfSupportedAsync("textDocument/references", parameters, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<JsonElement?> DocumentHighlightsAsync(
        string uri,
        LspPosition position,
        CancellationToken cancellationToken = default)
        => await RequestIfSupportedAsync("textDocument/documentHighlight", PositionParameters(uri, position), cancellationToken)
            .ConfigureAwait(false);

    public async Task<JsonElement?> PrepareRenameAsync(
        string uri,
        LspPosition position,
        CancellationToken cancellationToken = default)
        => await RequestIfSupportedAsync("textDocument/prepareRename", PositionParameters(uri, position), cancellationToken)
            .ConfigureAwait(false);

    public async Task<JsonElement?> RenameAsync(
        string uri,
        LspPosition position,
        string newName,
        CancellationToken cancellationToken = default)
    {
        var parameters = PositionParameters(uri, position);
        parameters["newName"] = newName;
        return await RequestIfSupportedAsync("textDocument/rename", parameters, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<JsonElement?> SignatureHelpAsync(
        string uri,
        LspPosition position,
        CancellationToken cancellationToken = default)
        => await RequestIfSupportedAsync("textDocument/signatureHelp", PositionParameters(uri, position), cancellationToken)
            .ConfigureAwait(false);

    public async Task<JsonElement?> InlayHintsAsync(
        string uri,
        LspRange range,
        CancellationToken cancellationToken = default)
        => await RequestIfSupportedAsync("textDocument/inlayHint", new JsonObject
        {
            ["textDocument"] = LspJson.TextDocumentNode(uri),
            ["range"] = LspJson.RangeNode(range),
        }, cancellationToken).ConfigureAwait(false);

    public async Task<JsonElement?> PrepareCallHierarchyAsync(
        string uri,
        LspPosition position,
        CancellationToken cancellationToken = default)
        => await RequestIfSupportedAsync("textDocument/prepareCallHierarchy", PositionParameters(uri, position), cancellationToken)
            .ConfigureAwait(false);

    public async Task<JsonElement?> PrepareTypeHierarchyAsync(
        string uri,
        LspPosition position,
        CancellationToken cancellationToken = default)
        => await RequestIfSupportedAsync("textDocument/prepareTypeHierarchy", PositionParameters(uri, position), cancellationToken)
            .ConfigureAwait(false);

    public async Task<JsonElement?> WorkspaceSymbolsAsync(string query, CancellationToken cancellationToken = default)
        => await RequestIfSupportedAsync("workspace/symbol", new JsonObject { ["query"] = query }, cancellationToken)
            .ConfigureAwait(false);

    public async Task<JsonElement?> PullDiagnosticsAsync(string uri, CancellationToken cancellationToken = default)
    {
        var normalized = LspDocumentUri.Normalize(uri);
        var parameters = new JsonObject
        {
            ["textDocument"] = LspJson.TextDocumentNode(normalized),
        };
        lock (_stateGate)
        {
            if (_pullResultIds.TryGetValue(normalized, out var resultId))
                parameters["previousResultId"] = resultId;
        }

        var result = await RequestIfSupportedAsync("textDocument/diagnostic", parameters, cancellationToken)
            .ConfigureAwait(false);
        if (result is { } value && LspJson.String(value, "resultId") is { } nextResultId)
        {
            lock (_stateGate)
                _pullResultIds[normalized] = nextResultId;
        }

        return result;
    }

    public async Task<JsonElement?> RequestAsync(
        string method,
        JsonObject? parameters,
        CancellationToken cancellationToken = default)
        => await _connection.RequestAsync(method, parameters, cancellationToken).ConfigureAwait(false);

    private async Task<JsonElement?> RequestIfSupportedAsync(
        string method,
        JsonObject parameters,
        CancellationToken cancellationToken)
        => Supports(method)
            ? await _connection.RequestAsync(method, parameters, cancellationToken).ConfigureAwait(false)
            : null;

    private static JsonObject PositionParameters(string uri, LspPosition position)
        => new()
        {
            ["textDocument"] = LspJson.TextDocumentNode(uri),
            ["position"] = LspJson.PositionNode(position),
        };

    private void OnNotification(string method, JsonElement? parameters)
    {
        if (method == "$/progress")
        {
            ProgressChanged?.Invoke(parameters);
            return;
        }

        if (method != "textDocument/publishDiagnostics" || parameters is not { } value)
            return;
        var uri = LspJson.String(value, "uri");
        if (uri is null)
            return;
        var version = LspJson.Int32(value, "version");
        var diagnostics = LspDiagnostics.ParseMany(LspJson.Property(value, "diagnostics"));
        var snapshot = new LspDiagnosticSnapshot(LspDocumentUri.Normalize(uri), version, diagnostics);
        lock (_stateGate)
        {
            if (_documents.TryGet(uri, out var document)
                && document is not null
                && version is { } publishedVersion
                && publishedVersion < document.Version)
                return;
            _diagnostics[snapshot.Uri] = snapshot;
        }

        DiagnosticsPublished?.Invoke(snapshot);
    }

    private Task<JsonElement?> OnServerRequestAsync(
        string method,
        JsonElement? parameters,
        CancellationToken cancellationToken)
        => method == "workspace/applyEdit"
            ? ApplyWorkspaceEditAsync(parameters)
            : Task.FromResult(method switch
        {
            "workspace/configuration" => WaitForConfiguration(parameters),
            "workspace/workspaceFolders" => WorkspaceFoldersResponse(),
            "client/registerCapability" => RegisterCapability(parameters, true),
            "client/unregisterCapability" => RegisterCapability(parameters, false),
            "window/workDoneProgress/create"
                or "window/showMessageRequest" => null,
            _ => throw new LspResponseException(-32601, $"unsupported server request: {method}"),
        });

    private Task<JsonElement?> ApplyWorkspaceEditAsync(JsonElement? parameters)
    {
        var edit = LspOperations.ParseWorkspaceEdit(LspJson.Property(parameters, "edit"));
        if (edit is null)
            return Task.FromResult<JsonElement?>(WorkspaceEditResponse(false, "workspace edit is empty"));
        if (edit.FileOperations is { Count: > 0 })
            return Task.FromResult<JsonElement?>(WorkspaceEditResponse(false, "file operations are not supported by the in-memory client"));
        foreach (var documentEdit in edit.Documents)
        {
            if (!_documents.TryGet(documentEdit.Uri, out var document) || document is null)
                return Task.FromResult<JsonElement?>(WorkspaceEditResponse(false, $"document is not open: {documentEdit.Uri}"));
            var application = WorkspaceEditApplier.Apply(document, documentEdit.Edits);
            if (!application.IsValid)
                return Task.FromResult<JsonElement?>(WorkspaceEditResponse(
                    false,
                    string.Join("; ", application.Issues.Select(issue => issue.Message))));
            _documents.Change(document.Uri, [LspTextChange.Full(application.Text)]);
        }

        return Task.FromResult<JsonElement?>(WorkspaceEditResponse(true, null));
    }

    private static JsonElement WorkspaceEditResponse(bool applied, string? failureReason)
    {
        var result = new JsonObject { ["applied"] = applied };
        if (failureReason is not null)
            result["failureReason"] = failureReason;
        return JsonSerializer.SerializeToElement(result);
    }

    private JsonElement? RegisterCapability(JsonElement? parameters, bool register)
    {
        var methods = LspJson.Items(LspJson.Property(parameters, "registrations"))
            .Select(item => LspJson.String(item, "method"))
            .Where(method => method is not null)
            .Select(method => method!)
            .ToArray();
        lock (_stateGate)
        {
            foreach (var method in methods)
            {
                if (register)
                    _dynamicMethods.Add(method);
                else
                    _dynamicMethods.Remove(method);
            }
        }

        return null;
    }

    private JsonElement WaitForConfiguration(JsonElement? parameters)
    {
        var items = LspJson.Items(LspJson.Property(parameters, "items"));
        var result = new JsonArray();
        foreach (var _ in items)
            result.Add(_options.Settings?.DeepClone());
        return JsonSerializer.SerializeToElement(result);
    }

    private JsonElement WorkspaceFoldersResponse()
    {
        var folders = new JsonArray();
        foreach (var folder in WorkspaceFolders)
            folders.Add(FolderNode(folder));

        return JsonSerializer.SerializeToElement(folders);
    }

    private static JsonObject FolderNode(LspWorkspaceFolder folder)
        => new()
        {
            ["uri"] = folder.Uri,
            ["name"] = folder.Name,
        };

    private static LspCapabilityProfile EmptyCapabilities()
        => new(
            null,
            null,
            LspTextDocumentSyncKind.None,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            "utf-16",
            [],
            []);

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync().ConfigureAwait(false);
        _connection.Notification -= OnNotification;
        await _connection.DisposeAsync().ConfigureAwait(false);
    }
}
