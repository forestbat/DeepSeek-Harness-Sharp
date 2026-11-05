using Dsh.Runtime;

namespace Dsh.Plugins;

/// <summary>Host management operations accept the caller's approval capability for model-originated settings changes.</summary>
public interface IPluginManager
{
    IReadOnlyList<string> PackageNames { get; }
    IReadOnlyList<PluginSkip> LoadFailures { get; }
    string Describe(string package);
    Task<string> AddAsync(string packageOrPath, ISettingsMutationContext? run = null);
    Task<string> RemoveAsync(string package, bool force = false, ISettingsMutationContext? run = null);
    Task<string> DisableAsync(string package, ISettingsMutationContext? run = null);
    Task<string> EnableAsync(string package, ISettingsMutationContext? run = null);
    Task<string> SetPermissionAsync(string package, PluginPermission permission, bool allowed,
        CancellationToken cancellationToken = default, ISettingsMutationContext? run = null);
}
