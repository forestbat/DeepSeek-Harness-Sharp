using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Lsp.Client;
using Dsh.Lsp.Model;
using Dsh.Lsp.Protocol;

namespace Dsh.Lsp.Analysis;

/** Collects call and type hierarchy edges through the server's hierarchy requests. */
public sealed class HierarchyGraphBuilder(ILspRequestChannel channel, LspCapabilityProfile capabilities)
{
    public async Task<HierarchyGraph> BuildAsync(
        LspDocument document,
        SyntaxSnapshot snapshot,
        DocumentAnalysisOptions options,
        CancellationToken cancellationToken = default)
    {
        if (!options.IncludeHierarchy || (!capabilities.CallHierarchy && !capabilities.TypeHierarchy))
            return HierarchyGraph.Empty(document.Uri, document.Version);

        var candidates = snapshot.Nodes
            .Where(node => node.Name.Length > 0)
            .Take(Math.Max(0, options.MaxHierarchySymbols))
            .ToArray();
        var nodes = new ConcurrentDictionary<string, HierarchyNode>(StringComparer.Ordinal);
        var edges = new ConcurrentBag<HierarchyEdge>();
        await Parallel.ForEachAsync(
            candidates,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, options.MaxConcurrency),
                CancellationToken = cancellationToken,
            },
            async (candidate, token) =>
            {
                await CollectAsync(document, candidate, nodes, edges, token).ConfigureAwait(false);
            }).ConfigureAwait(false);

        return new HierarchyGraph(
            document.Uri,
            document.Version,
            [.. nodes.Values],
            [.. edges]);
    }

    private async Task CollectAsync(
        LspDocument document,
        SyntaxNode candidate,
        ConcurrentDictionary<string, HierarchyNode> nodes,
        ConcurrentBag<HierarchyEdge> edges,
        CancellationToken cancellationToken)
    {
        var position = new JsonObject
        {
            ["textDocument"] = LspJson.TextDocumentNode(document.Uri),
            ["position"] = LspJson.PositionNode(candidate.SelectionRange.Start),
        };
        if (capabilities.CallHierarchy)
        {
            var prepared = await PrepareAsync(
                "textDocument/prepareCallHierarchy",
                position,
                "callHierarchy/incomingCalls",
                "callHierarchy/outgoingCalls",
                HierarchyEdgeKind.IncomingCall,
                HierarchyEdgeKind.OutgoingCall,
                nodes,
                edges,
                cancellationToken).ConfigureAwait(false);
            _ = prepared;
        }

        if (capabilities.TypeHierarchy)
        {
            var prepared = await PrepareAsync(
                "textDocument/prepareTypeHierarchy",
                position,
                "typeHierarchy/supertypes",
                "typeHierarchy/subtypes",
                HierarchyEdgeKind.Supertype,
                HierarchyEdgeKind.Subtype,
                nodes,
                edges,
                cancellationToken).ConfigureAwait(false);
            _ = prepared;
        }
    }

    private async Task<bool> PrepareAsync(
        string prepareMethod,
        JsonObject position,
        string firstMethod,
        string secondMethod,
        HierarchyEdgeKind firstKind,
        HierarchyEdgeKind secondKind,
        ConcurrentDictionary<string, HierarchyNode> nodes,
        ConcurrentBag<HierarchyEdge> edges,
        CancellationToken cancellationToken)
    {
        JsonElement? prepared;
        try
        {
            prepared = await channel.RequestAsync(prepareMethod, position, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is LspResponseException or LspRequestTimeoutException)
        {
            return false;
        }

        foreach (var raw in LspJson.Items(prepared))
        {
            if (ParseNode(raw) is not { } node)
                continue;
            nodes[node.Id] = node;
            await CollectEdgesAsync(firstMethod, firstKind, raw, node, nodes, edges, cancellationToken).ConfigureAwait(false);
            await CollectEdgesAsync(secondMethod, secondKind, raw, node, nodes, edges, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    private async Task CollectEdgesAsync(
        string method,
        HierarchyEdgeKind kind,
        JsonElement item,
        HierarchyNode source,
        ConcurrentDictionary<string, HierarchyNode> nodes,
        ConcurrentBag<HierarchyEdge> edges,
        CancellationToken cancellationToken)
    {
        JsonElement? result;
        try
        {
            result = await channel.RequestAsync(method, new JsonObject
            {
                ["item"] = JsonNode.Parse(item.GetRawText()),
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is LspResponseException or LspRequestTimeoutException)
        {
            return;
        }

        foreach (var raw in LspJson.Items(result))
        {
            var related = LspJson.Property(raw, "from") ?? LspJson.Property(raw, "to") ?? raw;
            if (ParseNode(related) is not { } target)
                continue;
            nodes[target.Id] = target;
            edges.Add(new HierarchyEdge(source.Id, kind, target));
        }
    }

    private static HierarchyNode? ParseNode(JsonElement item)
    {
        var uri = LspJson.String(item, "uri");
        var range = LspJson.Range(LspJson.Property(item, "range"));
        var selection = LspJson.Range(LspJson.Property(item, "selectionRange")) ?? range;
        if (uri is null || range is null || selection is null)
            return null;
        var name = LspJson.String(item, "name") ?? string.Empty;
        return new HierarchyNode(
            $"{uri}|{selection.Value.Start.Line}:{selection.Value.Start.Character}|{name}",
            name,
            LspSymbolKinds.Name(LspJson.Int32(item, "kind") ?? 0),
            uri,
            range.Value,
            selection.Value);
    }
}
