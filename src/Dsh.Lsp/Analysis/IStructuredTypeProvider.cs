using Dsh.Lsp.Client;
using Dsh.Lsp.Model;
using Dsh.Lsp.Protocol;

namespace Dsh.Lsp.Analysis;

public interface IStructuredTypeProvider
{
    string Name { get; }

    Task<TypeExpression?> ResolveAsync(
        LspDocument document,
        LspRange range,
        CancellationToken cancellationToken = default);
}
