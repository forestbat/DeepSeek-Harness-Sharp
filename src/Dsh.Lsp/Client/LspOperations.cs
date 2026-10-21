using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Lsp.Analysis;
using Dsh.Lsp.Model;
using Dsh.Lsp.Protocol;

namespace Dsh.Lsp.Client;

/** Typed operations for navigation, highlights, rename, and code actions. */
public sealed class LspOperations(
    ILspRequestChannel channel,
    LspCapabilityProfile capabilities,
    Func<string, bool>? isSupported = null)
{
    private bool Supported(string method) => isSupported?.Invoke(method) ?? capabilities.Supports(method);

    public async Task<IReadOnlyList<DocumentHighlightFact>> HighlightsAsync(
        string uri,
        LspPosition position,
        CancellationToken cancellationToken = default)
    {
        if (!Supported("textDocument/documentHighlight"))
            return [];
        var result = await channel.RequestAsync("textDocument/documentHighlight", PositionParameters(uri, position), cancellationToken)
            .ConfigureAwait(false);
        return LspJson.Items(result)
            .Select(item => new DocumentHighlightFact(
                LspJson.Range(LspJson.Property(item, "range")) ?? EmptyRange(),
                HighlightKind(LspJson.Int32(item, "kind"))))
            .ToArray();
    }

    public async Task<RenameTarget?> PrepareRenameAsync(
        string uri,
        LspPosition position,
        CancellationToken cancellationToken = default)
    {
        if (!Supported("textDocument/prepareRename") && !Supported("textDocument/rename"))
            return null;
        var result = await channel.RequestAsync("textDocument/prepareRename", PositionParameters(uri, position), cancellationToken)
            .ConfigureAwait(false);
        if (result is not { } value)
            return null;
        if (value.ValueKind == JsonValueKind.Object)
        {
            var range = LspJson.Range(LspJson.Property(value, "range"));
            return range is null
                ? null
                : new RenameTarget(range.Value, LspJson.String(value, "placeholder") ?? string.Empty);
        }

        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 2)
            return null;
        var positions = value.EnumerateArray().ToArray();
        return new RenameTarget(
            new LspRange(LspJson.Position(positions[0]), LspJson.Position(positions[1])),
            string.Empty);
    }

    public async Task<WorkspaceEditFact?> RenameAsync(
        string uri,
        LspPosition position,
        string newName,
        CancellationToken cancellationToken = default)
    {
        if (!Supported("textDocument/rename"))
            return null;
        var parameters = PositionParameters(uri, position);
        parameters["newName"] = newName;
        var result = await channel.RequestAsync("textDocument/rename", parameters, cancellationToken)
            .ConfigureAwait(false);
        return ParseWorkspaceEdit(result);
    }

    public async Task<IReadOnlyList<CodeActionFact>> CodeActionsAsync(
        string uri,
        LspRange range,
        IReadOnlyList<LspDiagnostic> diagnostics,
        CancellationToken cancellationToken = default)
    {
        if (!Supported("textDocument/codeAction"))
            return [];
        var context = new JsonObject
        {
            ["diagnostics"] = new JsonArray(diagnostics.Select(DiagnosticNode).ToArray()),
        };
        var result = await channel.RequestAsync("textDocument/codeAction", new JsonObject
        {
            ["textDocument"] = LspJson.TextDocumentNode(uri),
            ["range"] = LspJson.RangeNode(range),
            ["context"] = context,
        }, cancellationToken).ConfigureAwait(false);
        return ParseCodeActions(result);
    }

    public async Task<IReadOnlyList<WorkspaceSymbolFact>> WorkspaceSymbolsAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        if (!Supported("workspace/symbol"))
            return [];
        var result = await channel.RequestAsync("workspace/symbol", new JsonObject { ["query"] = query }, cancellationToken)
            .ConfigureAwait(false);
        return WorkspaceSymbols(result);
    }

    public static WorkspaceEditFact? ParseWorkspaceEdit(JsonElement? result)
    {
        if (result is not { } value || value.ValueKind != JsonValueKind.Object)
            return null;
        var documents = new List<DocumentEdits>();
        if (LspJson.Property(value, "changes") is { ValueKind: JsonValueKind.Object } changes)
        {
            foreach (var property in changes.EnumerateObject())
                AddDocument(documents, property.Name, property.Value);
        }

        foreach (var change in LspJson.Items(LspJson.Property(value, "documentChanges")))
        {
            var uri = LspJson.String(LspJson.Property(change, "textDocument"), "uri");
            if (uri is not null)
                AddDocument(documents, uri, LspJson.Property(change, "edits"));
        }

        return documents.Count == 0 ? null : new WorkspaceEditFact(documents);
    }

    public static IReadOnlyList<CodeActionFact> ParseCodeActions(JsonElement? result)
        => LspJson.Items(result)
            .Select(item => new CodeActionFact(
                LspJson.String(item, "title") ?? string.Empty,
                LspJson.String(item, "kind"),
                LspJson.Boolean(item, "isPreferred"),
                LspJson.Boolean(item, "disabled"),
                LspJson.String(LspJson.Property(item, "disabled"), "reason"),
                ParseWorkspaceEdit(LspJson.Property(item, "edit"))))
            .Where(action => action.Title.Length > 0)
            .ToArray();

    public static IReadOnlyList<WorkspaceSymbolFact> WorkspaceSymbols(JsonElement? result)
        => LspJson.Items(result)
            .Select(item =>
            {
                var location = LspJson.Property(item, "location");
                var uri = LspJson.String(location, "uri");
                var range = LspJson.Range(LspJson.Property(location, "range"));
                return uri is null || range is null
                    ? null
                    : new WorkspaceSymbolFact(
                        LspJson.String(item, "name") ?? string.Empty,
                        LspSymbolKinds.Name(LspJson.Int32(item, "kind") ?? 0),
                        LspJson.String(item, "containerName"),
                        uri,
                        range.Value);
            })
            .Where(symbol => symbol is not null)
            .Select(symbol => symbol!)
            .ToArray();

    private static void AddDocument(List<DocumentEdits> documents, string uri, JsonElement? edits)
    {
        var textEdits = LspJson.Items(edits)
            .Select(edit => new TextEditFact(
                LspJson.Range(LspJson.Property(edit, "range")) ?? EmptyRange(),
                LspJson.String(edit, "newText") ?? string.Empty))
            .ToArray();
        if (textEdits.Length > 0)
            documents.Add(new DocumentEdits(uri, textEdits));
    }

    private static JsonObject DiagnosticNode(LspDiagnostic diagnostic)
    {
        var node = new JsonObject
        {
            ["range"] = LspJson.RangeNode(diagnostic.Range),
            ["message"] = diagnostic.Message,
        };
        if (diagnostic.Severity is { } severity)
            node["severity"] = (int)severity;
        if (diagnostic.Code is { } code)
            node["code"] = code;
        if (diagnostic.Source is { } source)
            node["source"] = source;
        return node;
    }

    private static JsonObject PositionParameters(string uri, LspPosition position)
        => new()
        {
            ["textDocument"] = LspJson.TextDocumentNode(uri),
            ["position"] = LspJson.PositionNode(position),
        };

    private static string HighlightKind(int? kind) => kind switch
    {
        1 => "text",
        2 => "read",
        3 => "write",
        _ => "unknown",
    };

    private static LspRange EmptyRange() => new(LspPosition.Zero, LspPosition.Zero);
}
