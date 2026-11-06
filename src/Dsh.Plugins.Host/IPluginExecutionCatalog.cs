using Dsh.Runtime;

namespace Dsh.Plugins;

public readonly record struct PluginCatalogLoadResult(IReadOnlyList<string> Packages, PluginScanResult Loaded);

public delegate IPluginRuntime? PluginRuntimeFactory(Context context,
    IPluginExecutionEnvironment environment, PluginHost host, string directory, Func<INativePlugin, IDshPlugin>? nativeBridge);

public interface IPluginRuntime : IDisposable
{
    IPluginExecutionCatalog? Catalog { get; }
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
