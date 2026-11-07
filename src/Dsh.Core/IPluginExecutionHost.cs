using System.Text.Json;
using Dsh.Plugins;

namespace Dsh.Core;

/** Carries the actual tool execution explicitly without adding Core types to the plugin SDK. */
public interface IPluginExecutionHost : IPluginHost
{
    IDisposable RegisterTool(PluginToolDefinition definition,
        Func<JsonElement, ToolRunContext, CancellationToken, ValueTask<JsonElement>> execute);
    ValueTask<JsonElement> CallAsync(string operation, JsonElement arguments, ToolRunContext? run,
        CancellationToken cancellationToken);
}
