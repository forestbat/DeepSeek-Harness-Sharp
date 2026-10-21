using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Lsp.Client;
using Dsh.Lsp.Model;
using Dsh.Lsp.Protocol;

namespace Dsh.Lsp.Analysis;

/** Turns presentation-level hover and inlay-hint responses into separate type facts. */
public sealed class TypeFactExtractor(ILspRequestChannel channel, LspCapabilityProfile capabilities)
{
    public async Task<TypeFactsSnapshot> ExtractAsync(
        LspDocument document,
        SyntaxSnapshot snapshot,
        DocumentAnalysisOptions options,
        CancellationToken cancellationToken = default)
    {
        if (!options.IncludeTypes)
            return TypeFactsSnapshot.Empty;

        var selected = snapshot.Nodes
            .Where(node => node.Name.Length > 0)
            .Take(Math.Max(0, options.MaxSymbols))
            .ToArray();
        var facts = new IReadOnlyList<TypeFact>[selected.Length];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, selected.Length),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, options.MaxConcurrency),
                CancellationToken = cancellationToken,
            },
            async (index, token) =>
            {
                facts[index] = await HoverAsync(document, selected[index], token).ConfigureAwait(false);
            }).ConfigureAwait(false);
        var hints = options.IncludeInlayHints && capabilities.InlayHint
            ? await InlayHintsAsync(document, cancellationToken).ConfigureAwait(false)
            : [];
        var signatures = options.IncludeSignatures && capabilities.SignatureHelp
            ? await SignaturesAsync(document, selected, options, cancellationToken).ConfigureAwait(false)
            : [];
        return new TypeFactsSnapshot([.. facts.SelectMany(fact => fact)], hints, signatures);
    }

    private async Task<IReadOnlyList<TypeFact>> HoverAsync(
        LspDocument document,
        SyntaxNode node,
        CancellationToken cancellationToken)
    {
        if (!capabilities.Hover)
            return [];
        try
        {
            var parameters = new JsonObject
            {
                ["textDocument"] = LspJson.TextDocumentNode(document.Uri),
                ["position"] = LspJson.PositionNode(node.SelectionRange.Start),
            };
            var result = await channel.RequestAsync("textDocument/hover", parameters, cancellationToken)
                .ConfigureAwait(false);
            var display = HoverText(LspJson.Property(result, "contents"));
            if (string.IsNullOrWhiteSpace(display))
                return [];
            return
            [
                new TypeFact(
                    SymbolIdentity.For(document.Uri, node),
                    display,
                    "textDocument/hover",
                    LspJson.Range(LspJson.Property(result, "range")) ?? node.SelectionRange,
                    display),
            ];
        }
        catch (Exception error) when (error is LspResponseException or LspRequestTimeoutException)
        {
            return [];
        }
    }

    private async Task<IReadOnlyList<InlayHintFact>> InlayHintsAsync(
        LspDocument document,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await channel.RequestAsync("textDocument/inlayHint", new JsonObject
            {
                ["textDocument"] = LspJson.TextDocumentNode(document.Uri),
                ["range"] = LspJson.RangeNode(new LspRange(LspPosition.Zero, document.PositionAt(document.Length))),
            }, cancellationToken).ConfigureAwait(false);
            var hints = new List<InlayHintFact>();
            foreach (var item in LspJson.Items(result))
            {
                var range = InlayRange(item);
                if (range is null)
                    continue;
                hints.Add(new InlayHintFact(
                    range.Value,
                    HintLabel(LspJson.Property(item, "label")),
                    LspJson.Boolean(item, "paddingLeft"),
                    LspJson.Boolean(item, "paddingRight"),
                    HintTooltip(LspJson.Property(item, "tooltip"))));
            }

            return hints;
        }
        catch (Exception error) when (error is LspResponseException or LspRequestTimeoutException)
        {
            return [];
        }
    }

    private async Task<IReadOnlyList<SignatureFact>> SignaturesAsync(
        LspDocument document,
        IReadOnlyList<SyntaxNode> selected,
        DocumentAnalysisOptions options,
        CancellationToken cancellationToken)
    {
        var signatures = new IReadOnlyList<SignatureFact>[selected.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, selected.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, options.MaxConcurrency),
                CancellationToken = cancellationToken,
            },
            async (index, token) =>
            {
                signatures[index] = await SignatureAsync(document, selected[index], token).ConfigureAwait(false);
            }).ConfigureAwait(false);
        return [.. signatures.SelectMany(signature => signature)];
    }

    private async Task<IReadOnlyList<SignatureFact>> SignatureAsync(
        LspDocument document,
        SyntaxNode node,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await channel.RequestAsync("textDocument/signatureHelp", new JsonObject
            {
                ["textDocument"] = LspJson.TextDocumentNode(document.Uri),
                ["position"] = LspJson.PositionNode(node.SelectionRange.Start),
            }, cancellationToken).ConfigureAwait(false);
            var activeParameter = LspJson.Int32(result, "activeParameter");
            return LspJson.Items(LspJson.Property(result, "signatures"))
                .Select(signature => new SignatureFact(
                    SymbolIdentity.For(document.Uri, node),
                    LspJson.String(signature, "label") ?? string.Empty,
                    Documentation(LspJson.Property(signature, "documentation")),
                    activeParameter,
                    node.SelectionRange))
                .Where(signature => signature.Label.Length > 0)
                .ToArray();
        }
        catch (Exception error) when (error is LspResponseException or LspRequestTimeoutException)
        {
            return [];
        }
    }

    private static string? Documentation(JsonElement? documentation)
    {
        if (documentation is not { } value)
            return null;
        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : LspJson.String(value, "value");
    }

    private static LspRange? InlayRange(JsonElement item)
    {
        if (LspJson.Range(LspJson.Property(item, "range")) is { } range)
            return range;
        return LspJson.Position(LspJson.Property(item, "position")) is { } position
            ? new LspRange(position, position)
            : null;
    }

    public static string HoverText(JsonElement? contents)
    {
        if (contents is not { } value)
            return string.Empty;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Array => string.Join(
                "\n\n",
                value.EnumerateArray().Select(item => HoverText(item)).Where(text => text.Length > 0)),
            JsonValueKind.Object => LspJson.String(value, "value")
                ?? LspJson.String(value, "label")
                ?? string.Empty,
            _ => string.Empty,
        };
    }

    private static string HintLabel(JsonElement? label)
    {
        if (label is not { } value)
            return string.Empty;
        if (value.ValueKind == JsonValueKind.String)
            return value.GetString() ?? string.Empty;
        return value.ValueKind == JsonValueKind.Array
            ? string.Join(string.Empty, value.EnumerateArray().Select(part => LspJson.String(part, "value") ?? string.Empty))
            : string.Empty;
    }

    private static string? HintTooltip(JsonElement? tooltip)
        => tooltip is { } value
            ? value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : LspJson.String(value, "value")
            : null;
}
