using Dsh.Core;

namespace Dsh.Plugins;

/// <summary>Host management operations accept the real tool context for model-originated settings changes.</summary>
public interface IPluginManager
{
    IReadOnlyList<string> PackageNames { get; }
    IReadOnlyList<PluginSkip> LoadFailures { get; }
    string Describe(string package);
    Task<string> AddAsync(string packageOrPath, ToolRunContext? run = null);
    Task<string> RemoveAsync(string package, bool force = false, ToolRunContext? run = null);
    Task<string> DisableAsync(string package, ToolRunContext? run = null);
    Task<string> EnableAsync(string package, ToolRunContext? run = null);
    Task<string> SetPermissionAsync(string package, PluginPermission permission, bool allowed,
        CancellationToken cancellationToken = default, ToolRunContext? run = null);
    Task<string> SetExecutionAsync(string package, string? execution,
        CancellationToken cancellationToken = default, ToolRunContext? run = null);
}
