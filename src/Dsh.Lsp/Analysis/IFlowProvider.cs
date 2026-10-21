using Dsh.Lsp.Client;
using Dsh.Lsp.Model;
using Dsh.Lsp.Protocol;

namespace Dsh.Lsp.Analysis;

public interface IFlowProvider
{
    string Name { get; }

    Task<FlowGraph?> BuildAsync(
        LspDocument document,
        LspRange range,
        CancellationToken cancellationToken = default);
}
