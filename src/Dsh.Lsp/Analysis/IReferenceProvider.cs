using Dsh.Lsp.Client;
using Dsh.Lsp.Model;
using Dsh.Lsp.Protocol;

namespace Dsh.Lsp.Analysis;

public interface IReferenceProvider
{
    string Name { get; }

    Task<IReadOnlyList<SymbolEdge>> ResolveAsync(
        string rootPath,
        LspDocument document,
        LspRange range,
        CancellationToken cancellationToken = default);
}
