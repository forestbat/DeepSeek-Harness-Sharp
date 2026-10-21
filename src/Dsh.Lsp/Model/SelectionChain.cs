using Dsh.Lsp.Protocol;

namespace Dsh.Lsp.Model;

public sealed record SelectionChain(
    LspPosition Position,
    IReadOnlyList<LspRange> Ranges);
