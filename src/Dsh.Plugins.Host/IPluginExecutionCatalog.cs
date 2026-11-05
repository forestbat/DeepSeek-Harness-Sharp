using Dsh.Runtime;

namespace Dsh.Plugins;

public readonly record struct PluginCatalogLoadResult(IReadOnlyList<string> Packages, PluginScanResult Loaded);

public delegate IPluginExecutionRuntime PluginExecutionRuntimeFactory(Context context,
    IPluginExecutionEnvironment environment, PluginHost host, string directory, Func<INativePlugin, IDshPlugin>? nativeBridge);

public interface IPluginExecutionRuntime : IDisposable
{
    IPluginExecutionCatalog? Catalog { get; }
}

/// <summary>Optional execution services registered by the generated module initializer.</summary>
public static class PluginExecutionRuntime
{
    public static PluginExecutionRuntimeFactory? Factory { private get; set; }

    /// <summary>Returns null for ordinary application arguments, without composing an application Context.</summary>
    public static Func<string[], ValueTask<int>?>? Worker { private get; set; }

    public static ValueTask<int>? TryRunWorkerAsync(string[] args) => Worker?.Invoke(args);

    public static IPluginExecutionRuntime? Create(Context context, IPluginExecutionEnvironment environment,
        PluginHost host, string directory, Func<INativePlugin, IDshPlugin>? nativeBridge)
    {
        if (Factory is { } factory)
            return factory(context, environment, host, directory, nativeBridge);
        if (environment.ReadSettings().SandboxEnabled)
            throw new RuntimeException("PLUGIN_SANDBOX_UNAVAILABLE", "Plugin sandbox support is not included in this application.");
        return null;
    }
}

/// <summary>An optional execution-aware catalog for discovery and plugin-manager operations.</summary>
public interface IPluginExecutionCatalog : IDisposable
{
    IReadOnlyList<PluginSkip> Failures { get; }
    string? ArtifactPath(string package);
    PluginForm? Form(string package);
    IReadOnlyList<string> DomainPackages(string package);
    bool IsFaulted(string package);
    Task RetireDomainAsync(string package, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> InspectPackagesAsync(string path, CancellationToken cancellationToken = default);
    Task<PluginCatalogLoadResult> LoadAsync(string path, CancellationToken cancellationToken = default);
    Task<PluginScanResult> ScanAsync(CancellationToken cancellationToken = default);
}
