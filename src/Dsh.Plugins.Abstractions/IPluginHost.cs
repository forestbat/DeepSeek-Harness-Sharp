using System.Text.Json;
using Dsh.Runtime;

namespace Dsh.Plugins;

/// <summary>The explicit capability boundary shared by managed and native plugins.</summary>
public interface IPluginHost
{
    IDisposable RegisterTool(PluginToolDefinition definition);
    ValueTask<JsonElement> CallAsync(string operation, JsonElement arguments, CancellationToken cancellationToken = default);
    void Log(int level, string message);
}

public interface IPluginHostFactory
{
    const string ServiceName = "pluginCapabilities";

    IPluginHost Create(string package, Context context);
}

public sealed record PluginToolDefinition(
    string Name,
    string Description,
    JsonElement Parameters,
    Func<JsonElement, CancellationToken, ValueTask<JsonElement>> Execute);

public static class PluginHostOperations
{
    /// <summary>Maximum encoded capability or worker protocol frame size.</summary>
    public const int MaximumFrameBytes = 1024 * 1024;

    /// <summary>Maximum tool registrations owned by a capability host or shared worker.</summary>
    public const int MaximumTools = 256;

    /// <summary>{path, offset?, count?} returns {data: base64, length, eof}.</summary>
    public const string ReadFile = "files.read";

    /// <summary>{path, data: base64, append?} returns {bytesWritten}.</summary>
    public const string WriteFile = "files.write";

    /// <summary>{url, method?, headers?, body?: base64} returns {status, headers, body: base64}.</summary>
    public const string SendHttp = "http.send";
}
