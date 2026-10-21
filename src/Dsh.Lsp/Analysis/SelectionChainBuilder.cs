using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Lsp.Client;
using Dsh.Lsp.Model;
using Dsh.Lsp.Protocol;

namespace Dsh.Lsp.Analysis;

/** Requests the enclosing range chain for a position; this is the only tree-like view LSP gives per offset. */
public sealed class SelectionChainBuilder(ILspRequestChannel channel, LspCapabilityProfile capabilities)
{
    public async Task<IReadOnlyList<SelectionChain>> BuildAsync(
        LspDocument document,
        IReadOnlyList<LspPosition> positions,
        CancellationToken cancellationToken = default)
    {
        if (!capabilities.SelectionRange || positions.Count == 0)
            return [];
        var requested = new JsonArray();
        foreach (var position in positions)
            requested.Add(LspJson.PositionNode(position));
        var result = await channel.RequestAsync("textDocument/selectionRange", new JsonObject
        {
            ["textDocument"] = LspJson.TextDocumentNode(document.Uri),
            ["positions"] = requested,
        }, cancellationToken).ConfigureAwait(false);

        var items = LspJson.Items(result);
        var chains = new List<SelectionChain>(Math.Min(items.Count, positions.Count));
        for (var index = 0; index < items.Count && index < positions.Count; index++)
            chains.Add(new SelectionChain(positions[index], ReadChain(items[index])));
        return chains;
    }

    private static IReadOnlyList<LspRange> ReadChain(JsonElement item)
    {
        var ranges = new List<LspRange>();
        var current = item;
        var depth = 0;
        while (current.ValueKind == JsonValueKind.Object && depth++ < 128)
        {
            if (LspJson.Range(LspJson.Property(current, "range")) is { } range)
                ranges.Add(range);
            current = LspJson.Property(current, "parent") is { } parent
                ? parent
                : default;
            if (current.ValueKind != JsonValueKind.Object)
                break;
        }

        return ranges;
    }
}
