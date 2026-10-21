using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Lsp.Client;
using Dsh.Lsp.Model;
using Dsh.Lsp.Protocol;

namespace Dsh.Lsp.Analysis;

/** Connects outline symbols through definition, declaration, and reference requests. */
public sealed class SymbolGraphBuilder(ILspRequestChannel channel, LspCapabilityProfile capabilities)
{
    public async Task<SymbolGraph> BuildAsync(
        LspDocument document,
        SyntaxSnapshot snapshot,
        DocumentAnalysisOptions options,
        CancellationToken cancellationToken = default)
    {
        var selected = snapshot.Nodes
            .Where(node => node.Name.Length > 0)
            .Take(Math.Max(0, options.MaxSymbols))
            .Select(node => new SymbolNode(
                SymbolIdentity.For(document.Uri, node),
                node.Name,
                node.Kind,
                node.Detail,
                document.Uri,
                node.Range,
                node.SelectionRange,
                document.Version,
                "textDocument/documentSymbol"))
            .ToArray();
        if (selected.Length == 0)
            return new SymbolGraph(document.Uri, document.Version, [], []);

        var edges = new IReadOnlyList<SymbolEdge>[selected.Length];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, selected.Length),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, options.MaxConcurrency),
                CancellationToken = cancellationToken,
            },
            async (index, token) =>
            {
                edges[index] = await ResolveSymbolAsync(selected[index], options, token).ConfigureAwait(false);
            }).ConfigureAwait(false);
        return new SymbolGraph(document.Uri, document.Version, selected, [.. edges.SelectMany(edge => edge)]);
    }

    public async Task<IReadOnlyList<SymbolEdge>> ResolveSymbolAsync(
        SymbolNode node,
        DocumentAnalysisOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            var edges = new List<SymbolEdge>();
            if (capabilities.Definition)
                await AddLocationsAsync(edges, node, "textDocument/definition", SymbolEdgeKind.Definition, cancellationToken)
                    .ConfigureAwait(false);
            if (capabilities.Declaration)
                await AddLocationsAsync(edges, node, "textDocument/declaration", SymbolEdgeKind.Declaration, cancellationToken)
                    .ConfigureAwait(false);
            if (capabilities.TypeDefinition)
                await AddLocationsAsync(edges, node, "textDocument/typeDefinition", SymbolEdgeKind.TypeDefinition, cancellationToken)
                    .ConfigureAwait(false);
            if (capabilities.Implementation)
                await AddLocationsAsync(edges, node, "textDocument/implementation", SymbolEdgeKind.Implementation, cancellationToken)
                    .ConfigureAwait(false);
            if (capabilities.References && options.IncludeReferences)
                await AddReferencesAsync(edges, node, cancellationToken).ConfigureAwait(false);
            return edges;
        }
        catch (Exception error) when (error is LspResponseException or LspRequestTimeoutException)
        {
            return [];
        }
    }

    private async Task AddLocationsAsync(
        List<SymbolEdge> edges,
        SymbolNode node,
        string method,
        SymbolEdgeKind kind,
        CancellationToken cancellationToken)
    {
        var result = await channel.RequestAsync(method, PositionParameters(node), cancellationToken).ConfigureAwait(false);
        foreach (var location in ParseLocations(result))
            edges.Add(new SymbolEdge(node.Id, kind, location, method));
    }

    private async Task AddReferencesAsync(
        List<SymbolEdge> edges,
        SymbolNode node,
        CancellationToken cancellationToken)
    {
        var parameters = PositionParameters(node);
        parameters["context"] = new JsonObject { ["includeDeclaration"] = true };
        var result = await channel.RequestAsync("textDocument/references", parameters, cancellationToken).ConfigureAwait(false);
        foreach (var location in ParseLocations(result))
            edges.Add(new SymbolEdge(node.Id, SymbolEdgeKind.Reference, location, "textDocument/references"));
    }

    private static JsonObject PositionParameters(SymbolNode node)
        => new()
        {
            ["textDocument"] = LspJson.TextDocumentNode(node.Uri),
            ["position"] = LspJson.PositionNode(node.SelectionRange.Start),
        };

    public static IReadOnlyList<LspLocation> ParseLocations(JsonElement? result)
    {
        if (result is not { } value)
            return [];
        if (value.ValueKind == JsonValueKind.Object)
            return ParseLocation(value) is { } single ? [single] : [];
        if (value.ValueKind != JsonValueKind.Array)
            return [];
        var locations = new List<LspLocation>();
        foreach (var item in value.EnumerateArray())
        {
            if (ParseLocation(item) is { } location)
                locations.Add(location);
        }

        return locations;
    }

    private static LspLocation? ParseLocation(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;
        var targetUri = LspJson.String(item, "targetUri") ?? LspJson.String(item, "uri");
        var range = LspJson.Range(LspJson.Property(item, "targetSelectionRange"))
            ?? LspJson.Range(LspJson.Property(item, "targetRange"))
            ?? LspJson.Range(LspJson.Property(item, "range"));
        return targetUri is null || range is null ? null : new LspLocation(targetUri, range.Value);
    }

}
