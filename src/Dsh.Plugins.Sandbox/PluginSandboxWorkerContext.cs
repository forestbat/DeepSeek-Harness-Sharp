using System.Text.Json;
using Dsh.Runtime;

namespace Dsh.Plugins.Sandbox;

/// <summary>Owns canonical plugin activation against worker-local broker capabilities.</summary>
internal sealed class PluginSandboxWorkerContext(IPluginHostFactory factory) : IAsyncDisposable
{
    private readonly Context _root = new();

    internal static bool Supports(IReadOnlyList<string> inject, IReadOnlyList<Type> injectTypes)
        => (inject.Contains(IPluginHostFactory.ServiceName, StringComparer.Ordinal)
                || injectTypes.Contains(typeof(IPluginHostFactory)))
            && inject.All(static name => name == IPluginHostFactory.ServiceName)
            && injectTypes.All(static type => type.IsAssignableFrom(typeof(PluginSandboxWorkerHost)));

    internal async Task ActivateAsync(PluginDefinition definition, JsonElement configuration, CancellationToken cancellationToken)
    {
        _root.Provide(IPluginHostFactory.ServiceName, factory);
        var activation = _root.Plugin(definition, PluginSandboxWorkerWire.Configuration(configuration));
        await _root.Scheduler.SettleAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!activation.IsActive)
            throw new InvalidOperationException(activation.Error ?? "The plugin requires services unavailable in the worker Context.");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            var activations = _root.Scheduler.Snapshot();
            for (var index = activations.Count - 1; index >= 0; index--)
                await activations[index].DeactivateAsync().ConfigureAwait(false);
        }
        finally
        {
            await _root.Activation.DeactivateAsync().ConfigureAwait(false);
        }
    }
}
