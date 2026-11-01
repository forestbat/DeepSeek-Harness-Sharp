using System.Text.Json;
using Dsh.Runtime;

namespace Dsh.Plugins;

/// <summary>A plugin initialized through explicit capabilities, never a remote Context proxy.</summary>
public interface IDshIsolatedPlugin : IDshPlugin
{
    string[] IDshPlugin.Inject => [];

    ValueTask<IAsyncDisposable?> InitializeAsync(
        IPluginHost host,
        JsonElement configuration,
        CancellationToken cancellationToken = default);
}

public interface IPluginHostFactory
{
    const string ServiceName = "pluginCapabilities";

    IPluginHost Create(string package, Context context);

    Task<IDisposable?> ActivateAsync(
        string package,
        IDshIsolatedPlugin plugin,
        Context context,
        object? configuration,
        CancellationToken cancellationToken = default);
}
