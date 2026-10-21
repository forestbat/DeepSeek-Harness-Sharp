using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Lsp.Client;
using Dsh.Lsp.Model;
using Dsh.Lsp.Protocol;
using Dsh.Lsp.Server;
using Dsh.Lsp.TreeSitter;
using Dsh.Lsp.Workspace;

namespace Dsh.Lsp;

public static class LspMethods
{
    public const string Initialize = "initialize";
    public const string Initialized = "initialized";
    public const string Shutdown = "shutdown";
    public const string Exit = "exit";
}

/** Headless enhanced LSP server. It is a capability, not a Dsh entrypoint. */
public sealed class LspServer : IAsyncDisposable
{
    private static readonly string[] SemanticTokenTypes =
    [
        "namespace", "type", "class", "enum", "interface", "struct", "typeParameter",
        "parameter", "variable", "property", "enumMember", "event", "function", "method",
        "macro", "keyword", "modifier", "comment", "string", "number", "regexp",
        "operator", "decorator",
    ];

    private static readonly string[] SemanticTokenModifiers =
    [
        "declaration", "definition", "readonly", "static", "deprecated", "abstract",
        "async", "modification", "documentation", "defaultLibrary",
    ];

    private readonly ILspServerBackend _backend;
    private readonly bool _ownsBackend;
    private readonly LspDocumentStore _documents = new();
    private readonly Dictionary<string, DocumentAnalysis> _analysis = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _analysisGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private Stream? _output;
    private bool _exit;

    public LspServer(ILspServerBackend backend, bool ownsBackend = true)
    {
        _backend = backend;
        _ownsBackend = ownsBackend;
    }

    public static LspServer Create(
        string rootPath,
        LspClientOptions? clientOptions = null,
        DocumentAnalysisOptions? analysisOptions = null,
        ISyntaxTreeProvider? syntaxTreeProvider = null)
        => new(new WorkspaceLspBackend(new LspWorkspace(rootPath, clientOptions, analysisOptions, syntaxTreeProvider)));

    public LspDocument? Document(string uri)
        => _documents.TryGet(uri, out var document) ? document : null;

    public async Task RunAsync(Stream input, Stream output, CancellationToken cancellationToken = default)
    {
        _output = output;
        while (!_exit && !cancellationToken.IsCancellationRequested)
        {
            var frame = await LspFrameCodec.ReadAsync(input, cancellationToken).ConfigureAwait(false);
            if (frame is not { ValueKind: JsonValueKind.Object } message)
                break;
            var method = LspJson.String(message, "method");
            if (method is null)
                continue;
            var parameters = LspJson.Property(message, "params");
            if (LspJson.Property(message, "id") is { } id)
            {
                JsonElement? result = null;
                JsonObject? error = null;
                try
                {
                    result = await HandleRequestAsync(method, parameters, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    error = ErrorObject(exception);
                }

                var response = new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = JsonNode.Parse(id.GetRawText()),
                };
                if (error is null && result is { } value)
                    response["result"] = JsonNode.Parse(value.GetRawText());
                else if (error is null)
                    response["result"] = null;
                else
                    response["error"] = error;
                await WriteFrameAsync(response, cancellationToken).ConfigureAwait(false);
                continue;
            }

            await HandleNotificationAsync(method, parameters).ConfigureAwait(false);
            if (method is "textDocument/didOpen" or "textDocument/didChange"
                && LspJson.String(LspJson.Property(parameters, "textDocument"), "uri") is { } changedUri)
                _ = PublishDiagnosticsAsync(changedUri, cancellationToken);
        }
    }

    private async Task WriteFrameAsync(JsonObject message, CancellationToken cancellationToken)
    {
        if (_output is not { } output)
            return;
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LspFrameCodec.WriteAsync(output, message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task PublishDiagnosticsAsync(string uri, CancellationToken cancellationToken)
    {
        try
        {
            var analysis = await AnalyzeAsync(uri, cancellationToken).ConfigureAwait(false);
            var document = Document(uri);
            await WriteFrameAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "textDocument/publishDiagnostics",
                ["params"] = new JsonObject
                {
                    ["uri"] = LspDocumentUri.Normalize(uri),
                    ["version"] = document?.Version,
                    ["diagnostics"] = new JsonArray(analysis.Diagnostics.Select(DiagnosticNode).ToArray()),
                },
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _ = error;
        }
    }

    public async Task<JsonElement?> HandleRequestAsync(
        string method,
        JsonElement? parameters,
        CancellationToken cancellationToken = default)
    {
        switch (method)
        {
            case LspMethods.Initialize:
                return Json(InitializeResult());
            case LspMethods.Shutdown:
                return null;
            case "textDocument/documentSymbol":
                return Json(Symbols(await AnalyzeAsync(Uri(parameters), cancellationToken).ConfigureAwait(false)));
            case "textDocument/foldingRange":
                return Json(Foldings(await AnalyzeAsync(Uri(parameters), cancellationToken).ConfigureAwait(false)));
            case "textDocument/selectionRange":
                return Json(await SelectionsAsync(parameters, cancellationToken).ConfigureAwait(false));
            case "textDocument/semanticTokens/full":
                return Json(SemanticTokens(await AnalyzeAsync(Uri(parameters), cancellationToken).ConfigureAwait(false)));
            case "textDocument/semanticTokens/full/delta":
                return Json(SemanticTokens(await AnalyzeAsync(Uri(parameters), cancellationToken).ConfigureAwait(false)));
            case "textDocument/semanticTokens/range":
                return Json(SemanticTokens(
                    await AnalyzeAsync(Uri(parameters), cancellationToken).ConfigureAwait(false),
                    LspJson.Range(LspJson.Property(parameters, "range"))));
            case "textDocument/diagnostic":
                return Json(DiagnosticResponse(await AnalyzeAsync(Uri(parameters), cancellationToken).ConfigureAwait(false)));
            case "textDocument/definition":
                return Json(Locations(await _backend.DefinitionAsync(Uri(parameters), Position(parameters), cancellationToken).ConfigureAwait(false)));
            case "textDocument/declaration":
                return Json(Locations(await _backend.DeclarationAsync(Uri(parameters), Position(parameters), cancellationToken).ConfigureAwait(false)));
            case "textDocument/typeDefinition":
                return Json(Locations(await _backend.TypeDefinitionAsync(Uri(parameters), Position(parameters), cancellationToken).ConfigureAwait(false)));
            case "textDocument/implementation":
                return Json(Locations(await _backend.ImplementationAsync(Uri(parameters), Position(parameters), cancellationToken).ConfigureAwait(false)));
            case "textDocument/references":
                return Json(Locations(await _backend.ReferencesAsync(
                    Uri(parameters),
                    Position(parameters),
                    true,
                    cancellationToken).ConfigureAwait(false)));
            case "textDocument/hover":
                var hover = await _backend.HoverAsync(Uri(parameters), Position(parameters), cancellationToken).ConfigureAwait(false);
                return hover is null
                    ? null
                    : Json(new JsonObject
                    {
                        ["contents"] = new JsonObject { ["kind"] = "plaintext", ["value"] = hover },
                    });
            case "textDocument/documentHighlight":
                return Json(Highlights(await _backend.HighlightsAsync(Uri(parameters), Position(parameters), cancellationToken).ConfigureAwait(false)));
            case "textDocument/prepareRename":
                var target = await _backend.PrepareRenameAsync(Uri(parameters), Position(parameters), cancellationToken).ConfigureAwait(false);
                return target is null ? null : Json(new JsonObject
                {
                    ["range"] = LspJson.RangeNode(target.Range),
                    ["placeholder"] = target.Placeholder,
                });
            case "textDocument/rename":
                var newName = LspJson.String(parameters, "newName")
                    ?? throw new LspResponseException(-32602, "newName is required");
                var edit = await _backend.RenameAsync(Uri(parameters), Position(parameters), newName, cancellationToken).ConfigureAwait(false);
                return Json(WorkspaceEdit(edit));
            case "textDocument/codeAction":
                var analysis = await AnalyzeAsync(Uri(parameters), cancellationToken).ConfigureAwait(false);
                var actions = await _backend.CodeActionsAsync(
                    Uri(parameters),
                    LspJson.Range(LspJson.Property(parameters, "range")) ?? new LspRange(LspPosition.Zero, LspPosition.Zero),
                    analysis.Diagnostics,
                    cancellationToken).ConfigureAwait(false);
                return Json(Actions(actions));
            case "codeAction/resolve":
                var actionDocument = _documents.OpenDocuments.FirstOrDefault();
                if (actionDocument is null || parameters is not { } actionParameters)
                    return parameters is { } same ? Json(same) : null;
                var resolvedAction = await _backend.ResolveCodeActionAsync(
                    actionDocument.Uri,
                    actionParameters,
                    cancellationToken).ConfigureAwait(false);
                return resolvedAction is null ? Json(actionParameters) : Json(ActionNode(resolvedAction));
            case "textDocument/inlayHint":
                return Json(InlayHints(
                    await AnalyzeAsync(Uri(parameters), cancellationToken).ConfigureAwait(false),
                    LspJson.Range(LspJson.Property(parameters, "range"))));
            case "textDocument/signatureHelp":
                return Json(Signatures(await AnalyzeAsync(Uri(parameters), cancellationToken).ConfigureAwait(false)));
            case "workspace/symbol":
                var query = LspJson.String(parameters, "query") ?? string.Empty;
                var languageIds = _documents.OpenDocuments
                    .Select(document => document.LanguageId)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (languageIds.Length == 0)
                    return Json(new JsonArray());
                var workspaceSymbols = new List<WorkspaceSymbolFact>();
                foreach (var languageId in languageIds)
                    workspaceSymbols.AddRange(await _backend.WorkspaceSymbolsAsync(languageId, query, cancellationToken).ConfigureAwait(false));
                return Json(WorkspaceSymbols(workspaceSymbols));
            case "textDocument/prepareCallHierarchy":
                return Json(HierarchyItems(await AnalyzeAsync(Uri(parameters), cancellationToken).ConfigureAwait(false), Position(parameters)));
            case "textDocument/prepareTypeHierarchy":
                return Json(HierarchyItems(await AnalyzeAsync(Uri(parameters), cancellationToken).ConfigureAwait(false), Position(parameters)));
            case "callHierarchy/incomingCalls":
            case "callHierarchy/outgoingCalls":
            case "typeHierarchy/supertypes":
            case "typeHierarchy/subtypes":
                return Json(await HierarchyRelationsAsync(method, parameters, cancellationToken).ConfigureAwait(false));
            case "textDocument/willSaveWaitUntil":
                return Json(new JsonArray());
            case "textDocument/completion":
            case "textDocument/formatting":
            case "textDocument/rangeFormatting":
            case "textDocument/documentLink":
            case "textDocument/documentColor":
            case "textDocument/colorPresentation":
            case "textDocument/codeLens":
                return await ForwardAsync(method, parameters, cancellationToken).ConfigureAwait(false);
            case "workspace/executeCommand":
                var openDocument = _documents.OpenDocuments.FirstOrDefault();
                return openDocument is null
                    ? null
                    : await _backend.RequestAsync(
                        openDocument.Uri,
                        method,
                        parameters is { } commandParams
                            ? JsonNode.Parse(commandParams.GetRawText()) as JsonObject
                            : null,
                        cancellationToken).ConfigureAwait(false);
            case "workspace/diagnostic":
                var diagnosticDocument = _documents.OpenDocuments.FirstOrDefault();
                return diagnosticDocument is null
                    ? null
                    : await _backend.RequestAsync(
                        diagnosticDocument.Uri,
                        method,
                        parameters is { } diagnosticParams
                            ? JsonNode.Parse(diagnosticParams.GetRawText()) as JsonObject
                            : null,
                        cancellationToken).ConfigureAwait(false);
            default:
                throw new LspResponseException(-32601, $"unknown LSP method: {method}");
        }
    }

    private Task HandleNotificationAsync(
        string method,
        JsonElement? parameters)
    {
        switch (method)
        {
            case LspMethods.Initialized:
                return Task.CompletedTask;
            case LspMethods.Exit:
                _exit = true;
                return Task.CompletedTask;
            case "textDocument/didOpen":
                OpenDocument(parameters);
                return Task.CompletedTask;
            case "textDocument/didChange":
                ChangeDocument(parameters);
                return Task.CompletedTask;
            case "textDocument/didClose":
                CloseDocument(parameters);
                return Task.CompletedTask;
            case "textDocument/didSave":
            case "textDocument/willSave":
            case "workspace/didChangeConfiguration":
            case "workspace/didChangeWatchedFiles":
            case "$/cancelRequest":
                return Task.CompletedTask;
        }
        return Task.CompletedTask;
    }

    private void OpenDocument(JsonElement? parameters)
    {
        var textDocument = LspJson.Property(parameters, "textDocument");
        var uri = LspJson.String(textDocument, "uri");
        if (uri is null)
            return;
        if (_documents.TryGet(uri, out _))
            _documents.Close(uri);
        _documents.Open(
            uri,
            LspJson.String(textDocument, "languageId") ?? LanguageOf(uri),
            LspJson.String(textDocument, "text") ?? string.Empty);
        Invalidate(uri);
    }

    private void ChangeDocument(JsonElement? parameters)
    {
        var uri = LspJson.String(LspJson.Property(parameters, "textDocument"), "uri");
        if (uri is null || !_documents.TryGet(uri, out _))
            return;
        var changes = new List<LspTextChange>();
        foreach (var item in LspJson.Items(LspJson.Property(parameters, "contentChanges")))
        {
            changes.Add(new LspTextChange(
                LspJson.Range(LspJson.Property(item, "range")),
                LspJson.String(item, "text") ?? string.Empty,
                LspJson.Int32(item, "rangeLength")));
        }

        if (changes.Count > 0)
            _documents.Change(uri, changes);
        Invalidate(uri);
    }

    private void CloseDocument(JsonElement? parameters)
    {
        var uri = LspJson.String(LspJson.Property(parameters, "textDocument"), "uri");
        if (uri is null)
            return;
        _documents.Close(uri);
        Invalidate(uri);
    }

    private void Invalidate(string uri)
    {
        lock (_analysis)
            _analysis.Remove(LspDocumentUri.Normalize(uri));
    }

    private async Task<DocumentAnalysis> AnalyzeAsync(string uri, CancellationToken cancellationToken)
    {
        var normalized = LspDocumentUri.Normalize(uri);
        if (!_documents.TryGet(normalized, out var document) || document is null)
            throw new LspResponseException(-32602, $"document is not open: {normalized}");
        lock (_analysis)
        {
            if (_analysis.TryGetValue(normalized, out var cached) && cached.Document.Version == document.Version)
                return cached;
        }

        await _analysisGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_analysis)
            {
                if (_analysis.TryGetValue(normalized, out var cached) && cached.Document.Version == document.Version)
                    return cached;
            }

            var analysis = await _backend.AnalyzeAsync(normalized, document.LanguageId, document.Text, cancellationToken)
                .ConfigureAwait(false);
            lock (_analysis)
                _analysis[normalized] = analysis;
            return analysis;
        }
        finally
        {
            _analysisGate.Release();
        }
    }

    private async Task<JsonElement> SelectionsAsync(JsonElement? parameters, CancellationToken cancellationToken)
    {
        var uri = Uri(parameters);
        var positions = LspJson.Items(LspJson.Property(parameters, "positions"))
            .Select(LspJson.Position)
            .ToArray();
        var chains = await _backend.SelectionChainsAsync(uri, positions, cancellationToken).ConfigureAwait(false);
        var result = new JsonArray();
        foreach (var chain in chains)
            result.Add(ChainNode(chain.Ranges, 0));
        return Json(result);
    }

    private async Task<JsonElement?> ForwardAsync(
        string method,
        JsonElement? parameters,
        CancellationToken cancellationToken)
    {
        var uri = LspJson.String(LspJson.Property(parameters, "textDocument"), "uri");
        if (uri is null)
            return null;
        return await _backend.RequestAsync(
            uri,
            method,
            parameters is { } value ? JsonNode.Parse(value.GetRawText()) as JsonObject : null,
            cancellationToken).ConfigureAwait(false);
    }

    private static JsonObject? ChainNode(IReadOnlyList<LspRange> ranges, int index)
        => index >= ranges.Count
            ? null
            : new JsonObject
            {
                ["range"] = LspJson.RangeNode(ranges[index]),
                ["parent"] = ChainNode(ranges, index + 1),
            };

    private async Task<JsonElement> HierarchyRelationsAsync(
        string method,
        JsonElement? parameters,
        CancellationToken cancellationToken)
    {
        var item = LspJson.Property(parameters, "item");
        var uri = LspJson.String(item, "uri");
        var range = LspJson.Range(LspJson.Property(item, "range"));
        if (uri is null || range is null)
            return Json(new JsonArray());
        var analysis = await AnalyzeAsync(uri, cancellationToken).ConfigureAwait(false);
        var source = FindHierarchyNode(analysis, uri, range.Value);
        if (source is null)
            return Json(new JsonArray());
        var kind = method switch
        {
            "callHierarchy/incomingCalls" => HierarchyEdgeKind.IncomingCall,
            "callHierarchy/outgoingCalls" => HierarchyEdgeKind.OutgoingCall,
            "typeHierarchy/supertypes" => HierarchyEdgeKind.Supertype,
            "typeHierarchy/subtypes" => HierarchyEdgeKind.Subtype,
            _ => (HierarchyEdgeKind?)null,
        };
        var items = new JsonArray();
        if (kind is null)
            return Json(items);
        foreach (var edge in analysis.Hierarchy.Edges.Where(edge => edge.Kind == kind && edge.FromId == source.Id))
        {
            items.Add(method.StartsWith("callHierarchy", StringComparison.Ordinal)
                ? new JsonObject
                {
                    ["from"] = HierarchyNodeJson(edge.Target),
                    ["fromRanges"] = new JsonArray(LspJson.RangeNode(edge.Target.Range)),
                }
                : HierarchyNodeJson(edge.Target));
        }

        return Json(items);
    }

    private static HierarchyNode? FindHierarchyNode(DocumentAnalysis analysis, string uri, LspRange range)
        => analysis.Hierarchy.Nodes.FirstOrDefault(node => node.Uri == uri && node.Range.Contains(range));

    private static JsonArray HierarchyItems(DocumentAnalysis analysis, LspPosition position)
    {
        var items = new JsonArray();
        foreach (var node in analysis.Hierarchy.Nodes.Where(node => node.Range.Contains(position)))
            items.Add(HierarchyNodeJson(node));
        return items;
    }

    private static JsonObject HierarchyNodeJson(HierarchyNode node)
        => new()
        {
            ["name"] = node.Name,
            ["kind"] = SymbolKind(node.Kind),
            ["uri"] = node.Uri,
            ["range"] = LspJson.RangeNode(node.Range),
            ["selectionRange"] = LspJson.RangeNode(node.SelectionRange),
        };

    private static JsonObject InitializeResult()
        => new()
        {
            ["capabilities"] = new JsonObject
            {
                ["textDocumentSync"] = 2,
                ["documentSymbolProvider"] = true,
                ["completionProvider"] = new JsonObject
                {
                    ["triggerCharacters"] = new JsonArray(),
                    ["resolveProvider"] = false,
                },
                ["documentFormattingProvider"] = true,
                ["documentRangeFormattingProvider"] = true,
                ["documentLinkProvider"] = new JsonObject { ["resolveProvider"] = false },
                ["documentColorProvider"] = true,
                ["codeLensProvider"] = new JsonObject { ["resolveProvider"] = false },
                ["executeCommandProvider"] = new JsonObject { ["commands"] = new JsonArray() },
                ["foldingRangeProvider"] = true,
                ["selectionRangeProvider"] = true,
                ["definitionProvider"] = true,
                ["declarationProvider"] = true,
                ["typeDefinitionProvider"] = true,
                ["implementationProvider"] = true,
                ["referencesProvider"] = true,
                ["documentHighlightProvider"] = true,
                ["hoverProvider"] = true,
                ["renameProvider"] = new JsonObject { ["prepareProvider"] = true },
                ["codeActionProvider"] = new JsonObject { ["codeActionKinds"] = new JsonArray("quickfix", "refactor", "source") },
                ["signatureHelpProvider"] = new JsonObject(),
                ["inlayHintProvider"] = true,
                ["callHierarchyProvider"] = true,
                ["typeHierarchyProvider"] = true,
                ["workspaceSymbolProvider"] = true,
                ["diagnosticProvider"] = new JsonObject
                {
                    ["interFileDependencies"] = false,
                    ["workspaceDiagnostics"] = true,
                },
                ["semanticTokensProvider"] = new JsonObject
                {
                    ["full"] = new JsonObject { ["delta"] = true },
                    ["range"] = true,
                    ["legend"] = new JsonObject
                    {
                        ["tokenTypes"] = new JsonArray(SemanticTokenTypes.Select(value => (JsonNode?)value).ToArray()),
                        ["tokenModifiers"] = new JsonArray(SemanticTokenModifiers.Select(value => (JsonNode?)value).ToArray()),
                    },
                },
            },
            ["serverInfo"] = new JsonObject { ["name"] = "dsh-lsp", ["version"] = "0.3.0" },
        };

    private static JsonArray Symbols(DocumentAnalysis analysis)
        => new(analysis.Syntax.Roots.Select(SymbolNode).ToArray());

    private static JsonObject SymbolNode(SyntaxNode node)
    {
        var result = new JsonObject
        {
            ["name"] = node.Name,
            ["detail"] = node.Detail,
            ["kind"] = node.SymbolKind,
            ["range"] = LspJson.RangeNode(node.Range),
            ["selectionRange"] = LspJson.RangeNode(node.SelectionRange),
        };
        if (node.Children.Count > 0)
            result["children"] = new JsonArray(node.Children.Select(SymbolNode).ToArray());
        return result;
    }

    private static JsonArray Foldings(DocumentAnalysis analysis)
        => new(analysis.Syntax.FoldingRanges.Select(folding => (JsonNode?)new JsonObject
        {
            ["startLine"] = folding.StartLine,
            ["startCharacter"] = folding.StartCharacter,
            ["endLine"] = folding.EndLine,
            ["endCharacter"] = folding.EndCharacter,
            ["kind"] = folding.Kind,
        }).ToArray());

    private static JsonObject SemanticTokens(DocumentAnalysis analysis, LspRange? requestedRange = null)
    {
        var typeIndex = SemanticTokenTypes
            .Select((type, index) => (type, index))
            .ToDictionary(item => item.type, item => item.index, StringComparer.Ordinal);
        var modifierIndex = SemanticTokenModifiers
            .Select((modifier, index) => (modifier, index))
            .ToDictionary(item => item.modifier, item => item.index, StringComparer.Ordinal);
        var data = new JsonArray();
        var previousLine = 0;
        var previousCharacter = 0;
        foreach (var token in analysis.Syntax.SemanticTokens
                     .Where(token => requestedRange is not { } limit || token.Range.Overlaps(limit))
                     .OrderBy(token => token.Range.Start)
                     .ThenBy(token => token.Range.End))
        {
            var deltaLine = token.Range.Start.Line - previousLine;
            var deltaCharacter = deltaLine == 0 ? token.Range.Start.Character - previousCharacter : token.Range.Start.Character;
            var length = token.Range.End.Character - token.Range.Start.Character;
            if (length <= 0)
                continue;
            var modifiers = 0;
            foreach (var modifier in token.Modifiers)
            {
                if (modifierIndex.TryGetValue(modifier, out var index))
                    modifiers |= 1 << index;
            }

            data.Add(deltaLine);
            data.Add(deltaCharacter);
            data.Add(length);
            data.Add(typeIndex.GetValueOrDefault(token.Type, 0));
            data.Add(modifiers);
            previousLine = token.Range.Start.Line;
            previousCharacter = token.Range.Start.Character;
        }

        return new JsonObject { ["data"] = data };
    }

    private static JsonObject DiagnosticResponse(DocumentAnalysis analysis)
        => new()
        {
            ["kind"] = "full",
            ["items"] = new JsonArray(analysis.Diagnostics.Select(DiagnosticNode).ToArray()),
        };

    private static JsonArray Locations(IReadOnlyList<LspLocation> locations)
        => new(locations.Select(location => (JsonNode?)new JsonObject
        {
            ["uri"] = location.Uri,
            ["range"] = LspJson.RangeNode(location.Range),
        }).ToArray());

    private static JsonArray Highlights(IReadOnlyList<DocumentHighlightFact> highlights)
        => new(highlights.Select(highlight => (JsonNode?)new JsonObject
        {
            ["range"] = LspJson.RangeNode(highlight.Range),
            ["kind"] = highlight.Kind switch { "text" => 1, "read" => 2, "write" => 3, _ => 1 },
        }).ToArray());

    private static JsonArray InlayHints(DocumentAnalysis analysis, LspRange? requestedRange = null)
        => new(analysis.Types.Hints
            .Where(hint => requestedRange is not { } limit || hint.Range.Overlaps(limit))
            .Select(hint => (JsonNode?)new JsonObject
        {
            ["position"] = LspJson.PositionNode(hint.Range.Start),
            ["label"] = hint.Label,
            ["paddingLeft"] = hint.PaddingLeft,
            ["paddingRight"] = hint.PaddingRight,
            ["tooltip"] = hint.Tooltip,
        }).ToArray());

    private static JsonObject? Signatures(DocumentAnalysis analysis)
        => analysis.Types.Signatures.Count == 0
            ? null
            : new JsonObject
            {
                ["signatures"] = new JsonArray(analysis.Types.Signatures.Select(signature => (JsonNode?)new JsonObject
                {
                    ["label"] = signature.Label,
                    ["documentation"] = signature.Documentation,
                }).ToArray()),
                ["activeParameter"] = analysis.Types.Signatures[0].ActiveParameter,
            };

    private static JsonArray WorkspaceSymbols(IReadOnlyList<WorkspaceSymbolFact> symbols)
        => new(symbols.Select(symbol => (JsonNode?)new JsonObject
        {
            ["name"] = symbol.Name,
            ["kind"] = SymbolKind(symbol.Kind),
            ["containerName"] = symbol.ContainerName,
            ["location"] = new JsonObject
            {
                ["uri"] = symbol.Uri,
                ["range"] = LspJson.RangeNode(symbol.Range),
            },
        }).ToArray());

    private static JsonObject? WorkspaceEdit(WorkspaceEditFact? edit)
    {
        if (edit is null)
            return null;
        var changes = new JsonObject();
        var documentChanges = new JsonArray();
        foreach (var document in edit.Documents)
        {
            var edits = new JsonArray(document.Edits.Select(textEdit => (JsonNode?)new JsonObject
            {
                ["range"] = LspJson.RangeNode(textEdit.Range),
                ["newText"] = textEdit.NewText,
            }).ToArray());
            changes[document.Uri] = edits.DeepClone();
            documentChanges.Add(new JsonObject
            {
                ["textDocument"] = new JsonObject
                {
                    ["uri"] = document.Uri,
                    ["version"] = document.Version,
                },
                ["edits"] = edits,
            });
        }

        foreach (var operation in edit.FileOperations ?? [])
        {
            documentChanges.Add(operation.Kind switch
            {
                WorkspaceFileOperationKind.Create => new JsonObject
                {
                    ["kind"] = "create",
                    ["uri"] = operation.Uri,
                    ["options"] = new JsonObject
                    {
                        ["overwrite"] = operation.Overwrite,
                        ["ignoreIfExists"] = operation.IgnoreIfExists,
                    },
                },
                WorkspaceFileOperationKind.Rename => new JsonObject
                {
                    ["kind"] = "rename",
                    ["oldUri"] = operation.Uri,
                    ["newUri"] = operation.NewUri,
                    ["options"] = new JsonObject
                    {
                        ["overwrite"] = operation.Overwrite,
                        ["ignoreIfExists"] = operation.IgnoreIfExists,
                    },
                },
                _ => new JsonObject
                {
                    ["kind"] = "delete",
                    ["uri"] = operation.Uri,
                    ["options"] = new JsonObject { ["ignoreIfExists"] = operation.IgnoreIfExists },
                },
            });
        }

        return edit.FileOperations is { Count: > 0 } || edit.Documents.Any(document => document.Version is not null)
            ? new JsonObject { ["documentChanges"] = documentChanges }
            : new JsonObject { ["changes"] = changes };
    }

    private static JsonArray Actions(IReadOnlyList<CodeActionFact> actions)
        => new(actions.Select(action => (JsonNode?)ActionNode(action)).ToArray());

    private static JsonObject ActionNode(CodeActionFact action)
        => new()
        {
            ["title"] = action.Title,
            ["kind"] = action.Kind,
            ["isPreferred"] = action.IsPreferred,
            ["disabled"] = action.IsDisabled
                ? new JsonObject { ["reason"] = action.DisabledReason }
                : null,
            ["edit"] = WorkspaceEdit(action.Edit),
            ["data"] = action.Data is { } data ? JsonNode.Parse(data.GetRawText()) : null,
        };

    private static JsonObject DiagnosticNode(LspDiagnostic diagnostic)
    {
        var node = new JsonObject
        {
            ["range"] = LspJson.RangeNode(diagnostic.Range),
            ["severity"] = (int)diagnostic.Severity,
            ["message"] = diagnostic.Message,
        };
        if (diagnostic.Code is { } code)
            node["code"] = code;
        if (diagnostic.Source is { } source)
            node["source"] = source;
        if (diagnostic.CodeDescription is { } codeDescription)
            node["codeDescription"] = new JsonObject { ["href"] = codeDescription };
        if (diagnostic.Tags is { Count: > 0 } tags)
            node["tags"] = new JsonArray(tags.Select(tag => (JsonNode?)int.Parse(tag)).ToArray());
        if (diagnostic.RelatedInformation is { Count: > 0 } related)
        {
            node["relatedInformation"] = new JsonArray(related.Select(item => (JsonNode?)new JsonObject
            {
                ["location"] = new JsonObject
                {
                    ["uri"] = item.Location.Uri,
                    ["range"] = LspJson.RangeNode(item.Location.Range),
                },
                ["message"] = item.Message,
            }).ToArray());
        }

        return node;
    }

    private static JsonObject ErrorObject(Exception exception)
    {
        var code = exception is LspResponseException response ? response.Code ?? -32603 : -32603;
        return new JsonObject
        {
            ["code"] = code,
            ["message"] = exception.Message,
        };
    }

    private static JsonElement Json(JsonNode? node)
        => JsonSerializer.SerializeToElement(node);

    private static JsonElement Json(JsonElement value) => value;

    private static string Uri(JsonElement? parameters)
        => LspJson.String(LspJson.Property(parameters, "textDocument"), "uri")
            ?? throw new LspResponseException(-32602, "textDocument.uri is required");

    private static LspPosition Position(JsonElement? parameters)
        => LspJson.Position(LspJson.Property(parameters, "position"))
            ?? throw new LspResponseException(-32602, "position is required");

    private string LanguageOf(string uri)
        => _documents.TryGet(uri, out var document) && document is not null
            ? document.LanguageId
            : "unknown";

    private static int SymbolKind(string kind) => kind switch
    {
        "file" => 1,
        "module" => 2,
        "namespace" => 3,
        "package" => 4,
        "class" => 5,
        "method" => 6,
        "property" => 7,
        "field" => 8,
        "constructor" => 9,
        "enum" => 10,
        "interface" => 11,
        "function" => 12,
        "variable" => 13,
        "constant" => 14,
        "string" => 15,
        "number" => 16,
        "boolean" => 17,
        "array" => 18,
        "object" => 19,
        "key" => 20,
        "null" => 21,
        "enum-member" => 22,
        "struct" => 23,
        "event" => 24,
        "operator" => 25,
        "type-parameter" => 26,
        _ => 0,
    };

    public async ValueTask DisposeAsync()
    {
        if (_ownsBackend)
            await _backend.DisposeAsync().ConfigureAwait(false);
        _analysisGate.Dispose();
        _writeGate.Dispose();
    }
}
