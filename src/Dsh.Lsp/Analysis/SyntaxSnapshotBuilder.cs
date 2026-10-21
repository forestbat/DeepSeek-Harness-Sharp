using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Lsp.Client;
using Dsh.Lsp.Model;
using Dsh.Lsp.Protocol;

namespace Dsh.Lsp.Analysis;

/** Rebuilds the structural view that LSP exposes through symbols, folding ranges, and tokens. */
public sealed class SyntaxSnapshotBuilder(ILspRequestChannel channel, LspCapabilityProfile capabilities)
{
    public async Task<SyntaxSnapshot> BuildAsync(
        LspDocument document,
        IReadOnlyList<LspDiagnostic> diagnostics,
        CancellationToken cancellationToken = default)
    {
        var symbolsTask = capabilities.DocumentSymbol
            ? channel.RequestAsync("textDocument/documentSymbol", new JsonObject
            {
                ["textDocument"] = LspJson.TextDocumentNode(document.Uri),
            }, cancellationToken)
            : Task.FromResult<JsonElement?>(null);
        var foldingTask = capabilities.FoldingRange
            ? channel.RequestAsync("textDocument/foldingRange", new JsonObject
            {
                ["textDocument"] = LspJson.TextDocumentNode(document.Uri),
            }, cancellationToken)
            : Task.FromResult<JsonElement?>(null);
        var tokensTask = capabilities.SemanticTokens
            ? channel.RequestAsync("textDocument/semanticTokens/full", new JsonObject
            {
                ["textDocument"] = LspJson.TextDocumentNode(document.Uri),
            }, cancellationToken)
            : Task.FromResult<JsonElement?>(null);

        await Task.WhenAll(symbolsTask, foldingTask, tokensTask).ConfigureAwait(false);
        return new SyntaxSnapshot(
            document.Uri,
            document.Version,
            document.LanguageId,
            ParseSymbols(await symbolsTask.ConfigureAwait(false)),
            ParseSemanticTokens(await tokensTask.ConfigureAwait(false)),
            ParseFoldingRanges(await foldingTask.ConfigureAwait(false)),
            diagnostics);
    }

    private static IReadOnlyList<SyntaxNode> ParseSymbols(JsonElement? result)
    {
        var items = LspJson.Items(result);
        if (items.Count == 0)
            return [];
        return items[0].TryGetProperty("location", out _)
            ? items.Select(ParseSymbolInformation).ToArray()
            : items.Select(ParseDocumentSymbol).ToArray();
    }

    private static SyntaxNode ParseDocumentSymbol(JsonElement item)
    {
        var range = LspJson.Range(LspJson.Property(item, "range")) ?? EmptyRange();
        var selectionRange = LspJson.Range(LspJson.Property(item, "selectionRange")) ?? range;
        return new SyntaxNode(
            LspJson.String(item, "name") ?? string.Empty,
            LspJson.String(item, "detail"),
            LspJson.Int32(item, "kind") ?? 0,
            range,
            selectionRange,
            Tags(item),
            LspJson.Items(LspJson.Property(item, "children"))
                .Select(ParseDocumentSymbol)
                .ToArray());
    }

    private static SyntaxNode ParseSymbolInformation(JsonElement item)
    {
        var location = LspJson.Property(item, "location");
        var range = LspJson.Range(LspJson.Property(location, "range")) ?? EmptyRange();
        return new SyntaxNode(
            LspJson.String(item, "name") ?? string.Empty,
            LspJson.String(item, "containerName"),
            LspJson.Int32(item, "kind") ?? 0,
            range,
            range,
            Tags(item),
            []);
    }

    private static IReadOnlyList<string> Tags(JsonElement item)
        => LspJson.Items(LspJson.Property(item, "tags"))
            .Where(tag => tag.ValueKind == JsonValueKind.Number && tag.TryGetInt32(out _))
            .Select(tag => tag.GetInt32().ToString())
            .ToArray();

    private static IReadOnlyList<FoldingRange> ParseFoldingRanges(JsonElement? result)
    {
        var ranges = new List<FoldingRange>();
        foreach (var item in LspJson.Items(result))
        {
            var startLine = LspJson.Int32(item, "startLine");
            var endLine = LspJson.Int32(item, "endLine");
            if (startLine is null || endLine is null)
                continue;
            ranges.Add(new FoldingRange(
                startLine.Value,
                LspJson.Int32(item, "startCharacter"),
                endLine.Value,
                LspJson.Int32(item, "endCharacter"),
                LspJson.String(item, "kind")));
        }

        return ranges;
    }

    private IReadOnlyList<SemanticToken> ParseSemanticTokens(JsonElement? result)
    {
        var data = LspJson.Items(LspJson.Property(result, "data"))
            .Where(item => item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out _))
            .Select(item => item.GetInt32())
            .ToArray();
        if (data.Length < 5)
            return [];

        var tokens = new List<SemanticToken>(data.Length / 5);
        var line = 0;
        var character = 0;
        for (var index = 0; index + 4 < data.Length; index += 5)
        {
            var deltaLine = data[index];
            var deltaStart = data[index + 1];
            var length = data[index + 2];
            var tokenType = data[index + 3];
            var tokenModifiers = data[index + 4];
            if (deltaLine < 0 || deltaStart < 0 || length < 0)
                continue;
            if (deltaLine > 0)
            {
                line += deltaLine;
                character = deltaStart;
            }
            else
            {
                character += deltaStart;
            }

            if (character < 0)
                continue;
            var type = tokenType >= 0 && tokenType < capabilities.SemanticTokenTypes.Count
                ? capabilities.SemanticTokenTypes[tokenType]
                : "unknown";
            tokens.Add(new SemanticToken(
                new LspRange(
                    new LspPosition(line, character),
                    new LspPosition(line, character + length)),
                type,
                Modifiers(tokenModifiers)));
        }

        return tokens;
    }

    private IReadOnlyList<string> Modifiers(int mask)
    {
        var modifiers = new List<string>();
        for (var index = 0; index < capabilities.SemanticTokenModifiers.Count; index++)
        {
            if ((mask & (1 << index)) != 0)
                modifiers.Add(capabilities.SemanticTokenModifiers[index]);
        }

        return modifiers;
    }

    private static LspRange EmptyRange() => new(LspPosition.Zero, LspPosition.Zero);
}
