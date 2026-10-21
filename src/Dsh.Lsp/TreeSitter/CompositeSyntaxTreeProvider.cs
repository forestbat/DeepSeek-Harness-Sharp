using Dsh.Lsp.Client;
using Dsh.Lsp.Model;

namespace Dsh.Lsp.TreeSitter;

public sealed class CompositeSyntaxTreeProvider(params ISyntaxTreeProvider[] providers) : ISyntaxTreeProvider
{
    public string Name => string.Join('+', providers.Select(provider => provider.Name));

    public bool SupportsIncremental => providers.Any(provider => provider.SupportsIncremental);

    public async Task<CstNode?> TryParseAsync(
        LspDocument document,
        CancellationToken cancellationToken = default)
    {
        foreach (var provider in providers)
        {
            var result = await provider.TryParseAsync(document, cancellationToken).ConfigureAwait(false);
            if (result is not null)
                return result;
        }

        return null;
    }
}
