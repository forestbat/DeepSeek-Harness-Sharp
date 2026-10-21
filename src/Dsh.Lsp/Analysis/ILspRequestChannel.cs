using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dsh.Lsp.Analysis;

/** Minimal request boundary shared by live sessions and deterministic test harnesses. */
public interface ILspRequestChannel
{
    Task<JsonElement?> RequestAsync(
        string method,
        JsonObject? parameters,
        CancellationToken cancellationToken = default);
}
