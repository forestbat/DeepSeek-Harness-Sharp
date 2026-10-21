using Dsh.Lsp.Client;
using Dsh.Lsp.Model;

namespace Dsh.Lsp.Analysis;

public interface IDeclarationProvider
{
    string Name { get; }

    Task<IReadOnlyList<StubDeclaration>> ExtractAsync(
        LspDocument document,
        CstNode? cst,
        SymbolGraph symbols,
        CancellationToken cancellationToken = default);
}
