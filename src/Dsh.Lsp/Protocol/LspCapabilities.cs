using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dsh.Lsp.Protocol;

public enum LspTextDocumentSyncKind
{
    None = 0,
    Full = 1,
    Incremental = 2,
}

public sealed record LspCapabilityProfile(
    string? ServerName,
    string? ServerVersion,
    LspTextDocumentSyncKind SyncKind,
    bool DocumentSymbol,
    bool SelectionRange,
    bool FoldingRange,
    bool SemanticTokens,
    bool SemanticTokensDelta,
    bool Hover,
    bool Definition,
    bool Declaration,
    bool References,
    bool DocumentHighlight,
    bool PrepareRename,
    bool Rename,
    bool SignatureHelp,
    bool InlayHint,
    bool CallHierarchy,
    bool TypeHierarchy,
    bool TypeDefinition,
    bool Implementation,
    bool PullDiagnostics,
    bool WorkspaceSymbol,
    string PositionEncoding,
    IReadOnlyList<string> SemanticTokenTypes,
    IReadOnlyList<string> SemanticTokenModifiers)
{
    public bool Supports(string method) => method switch
    {
        "textDocument/documentSymbol" => DocumentSymbol,
        "textDocument/selectionRange" => SelectionRange,
        "textDocument/foldingRange" => FoldingRange,
        "textDocument/semanticTokens/full" => SemanticTokens,
        "textDocument/semanticTokens/full/delta" => SemanticTokensDelta,
        "textDocument/hover" => Hover,
        "textDocument/definition" => Definition,
        "textDocument/declaration" => Declaration,
        "textDocument/references" => References,
        "textDocument/documentHighlight" => DocumentHighlight,
        "textDocument/prepareRename" => PrepareRename,
        "textDocument/rename" => Rename,
        "textDocument/signatureHelp" => SignatureHelp,
        "textDocument/inlayHint" => InlayHint,
        "textDocument/prepareCallHierarchy" => CallHierarchy,
        "textDocument/prepareTypeHierarchy" => TypeHierarchy,
        "textDocument/typeDefinition" => TypeDefinition,
        "textDocument/implementation" => Implementation,
        "textDocument/diagnostic" => PullDiagnostics,
        "workspace/symbol" => WorkspaceSymbol,
        _ => false,
    };
}

public static class LspCapabilityParser
{
    public static LspCapabilityProfile Parse(JsonElement? initializeResult)
    {
        var capabilities = LspJson.Property(initializeResult, "capabilities");
        var serverInfo = LspJson.Property(initializeResult, "serverInfo");
        var legend = LspJson.Property(LspJson.Property(capabilities, "semanticTokensProvider"), "legend");
        var sync = ParseSync(capabilities);
        return new LspCapabilityProfile(
            LspJson.String(serverInfo, "name"),
            LspJson.String(serverInfo, "version"),
            sync,
            Provider(capabilities, "documentSymbolProvider"),
            Provider(capabilities, "selectionRangeProvider"),
            Provider(capabilities, "foldingRangeProvider"),
            Provider(capabilities, "semanticTokensProvider"),
            SemanticTokensDelta(capabilities),
            Provider(capabilities, "hoverProvider"),
            Provider(capabilities, "definitionProvider"),
            Provider(capabilities, "declarationProvider"),
            Provider(capabilities, "referencesProvider"),
            Provider(capabilities, "documentHighlightProvider"),
            PrepareRename(capabilities),
            Provider(capabilities, "renameProvider"),
            Provider(capabilities, "signatureHelpProvider"),
            Provider(capabilities, "inlayHintProvider"),
            Provider(capabilities, "callHierarchyProvider"),
            Provider(capabilities, "typeHierarchyProvider"),
            Provider(capabilities, "typeDefinitionProvider"),
            Provider(capabilities, "implementationProvider"),
            LspJson.Property(capabilities, "diagnosticProvider") is { } diagnostic
                && diagnostic.ValueKind == JsonValueKind.Object,
            Provider(LspJson.Property(capabilities, "workspace"), "symbolProvider"),
            LspJson.String(capabilities, "positionEncoding") ?? "utf-16",
            Strings(legend, "tokenTypes"),
            Strings(legend, "tokenModifiers"));
    }

    private static LspTextDocumentSyncKind ParseSync(JsonElement? capabilities)
    {
        var sync = LspJson.Property(capabilities, "textDocumentSync");
        if (sync is { } number && number.ValueKind == JsonValueKind.Number && number.TryGetInt32(out var kind))
            return (LspTextDocumentSyncKind)kind;
        var change = LspJson.Int32(sync, "change");
        return change is null ? LspTextDocumentSyncKind.None : (LspTextDocumentSyncKind)change.Value;
    }

    private static bool Provider(JsonElement? parent, string name)
        => LspJson.Property(parent, name) is { } value
            && value.ValueKind is JsonValueKind.True or JsonValueKind.Object;

    private static bool PrepareRename(JsonElement? capabilities)
        => LspJson.Property(capabilities, "renameProvider") is { } value
            && value.ValueKind == JsonValueKind.Object
            && LspJson.Boolean(value, "prepareProvider");

    private static bool SemanticTokensDelta(JsonElement? capabilities)
    {
        var full = LspJson.Property(LspJson.Property(capabilities, "semanticTokensProvider"), "full");
        return full is { } value
            && value.ValueKind == JsonValueKind.Object
            && LspJson.Boolean(value, "delta");
    }

    private static IReadOnlyList<string> Strings(JsonElement? parent, string name)
        => LspJson.Items(LspJson.Property(parent, name))
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .ToArray();
}

public static class LspClientCapabilities
{
    public static JsonObject Build()
    {
        return new JsonObject
        {
            ["workspace"] = new JsonObject
            {
                ["workspaceFolders"] = true,
                ["configuration"] = true,
                ["symbol"] = new JsonObject
                {
                    ["dynamicRegistration"] = false,
                },
            },
            ["textDocument"] = new JsonObject
            {
                ["synchronization"] = new JsonObject
                {
                    ["dynamicRegistration"] = false,
                    ["willSave"] = true,
                    ["willSaveWaitUntil"] = false,
                    ["didSave"] = true,
                },
                ["documentSymbol"] = new JsonObject
                {
                    ["dynamicRegistration"] = false,
                    ["hierarchicalDocumentSymbolSupport"] = true,
                    ["symbolKind"] = new JsonObject
                    {
                        ["valueSet"] = new JsonArray(Enumerable.Range(1, 26).Select(value => (JsonNode?)value).ToArray()),
                    },
                },
                ["selectionRange"] = new JsonObject { ["dynamicRegistration"] = false },
                ["foldingRange"] = new JsonObject { ["dynamicRegistration"] = false, ["lineFoldingOnly"] = false },
                ["semanticTokens"] = new JsonObject
                {
                    ["dynamicRegistration"] = false,
                    ["requests"] = new JsonObject { ["range"] = true, ["full"] = true },
                    ["tokenTypes"] = new JsonArray(SemanticTokenTypes.Select(type => (JsonNode?)type).ToArray()),
                    ["tokenModifiers"] = new JsonArray(SemanticTokenModifiers.Select(modifier => (JsonNode?)modifier).ToArray()),
                    ["formats"] = new JsonArray("relative"),
                    ["overlappingTokenSupport"] = false,
                    ["multilineTokenSupport"] = true,
                },
                ["hover"] = new JsonObject
                {
                    ["dynamicRegistration"] = false,
                    ["contentFormat"] = new JsonArray("markdown", "plaintext"),
                },
                ["definition"] = Registration(),
                ["declaration"] = Registration(),
                ["references"] = Registration(),
                ["documentHighlight"] = Registration(),
                ["rename"] = new JsonObject { ["dynamicRegistration"] = false, ["prepareSupport"] = true },
                ["signatureHelp"] = new JsonObject
                {
                    ["dynamicRegistration"] = false,
                    ["signatureInformation"] = new JsonObject
                    {
                        ["documentationFormat"] = new JsonArray("markdown", "plaintext"),
                        ["parameterInformation"] = new JsonObject { ["labelOffsetSupport"] = true },
                    },
                },
                ["inlayHint"] = new JsonObject { ["dynamicRegistration"] = false },
                ["callHierarchy"] = new JsonObject { ["dynamicRegistration"] = false },
                ["typeHierarchy"] = new JsonObject { ["dynamicRegistration"] = false },
                ["typeDefinition"] = Registration(),
                ["implementation"] = Registration(),
                ["publishDiagnostics"] = new JsonObject
                {
                    ["relatedInformation"] = true,
                    ["versionSupport"] = true,
                    ["tagSupport"] = new JsonObject { ["valueSet"] = new JsonArray(1, 2) },
                },
            },
            ["window"] = new JsonObject { ["workDoneProgress"] = true },
            ["general"] = new JsonObject { ["positionEncodings"] = new JsonArray("utf-16") },
        };
    }

    private static readonly string[] SemanticTokenTypes =
    [
        "namespace",
        "type",
        "class",
        "enum",
        "interface",
        "struct",
        "typeParameter",
        "parameter",
        "variable",
        "property",
        "enumMember",
        "event",
        "function",
        "method",
        "macro",
        "keyword",
        "modifier",
        "comment",
        "string",
        "number",
        "regexp",
        "operator",
        "decorator",
    ];

    private static readonly string[] SemanticTokenModifiers =
    [
        "declaration",
        "definition",
        "readonly",
        "static",
        "deprecated",
        "abstract",
        "async",
        "modification",
        "documentation",
        "defaultLibrary",
    ];

    private static JsonObject Registration() => new() { ["dynamicRegistration"] = false };
}
