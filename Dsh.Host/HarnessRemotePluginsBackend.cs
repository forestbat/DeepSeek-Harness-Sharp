using Dsh.Boot;
using Dsh.Plugins;
using Dsh.RemoteHost;

namespace Dsh.Host;

/** Plugin metadata, broker permissions and execution settings backed by the installed plugin manager. */
internal sealed class HarnessRemotePluginsBackend(HarnessApp app, HarnessPluginManager manager) : IRemotePluginsBackend
{
    public Task<IReadOnlyList<RemotePluginInfo>> ListPluginsAsync(CancellationToken cancellationToken)
    {
        var settings = HarnessSettings.Load(app.Home);
        return Task.FromResult<IReadOnlyList<RemotePluginInfo>>([.. manager.PackageNames.Order(StringComparer.Ordinal).Select(package =>
        {
            var setting = settings.Plugins.GetValueOrDefault(package);
            var permissions = setting?.Permissions ?? PluginPermissions.AllAllowed;
            return new RemotePluginInfo(package, manager.Describe(package), setting?.Enabled ?? true,
                permissions.Read, permissions.Write, permissions.Network,
                setting?.Execution switch { PluginExecution.Host => "host", PluginExecution.Sandbox => "sandbox", _ => null });
        })]);
    }

    public Task<string> SetPluginPermissionAsync(string package, string permission, bool allowed, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<PluginPermission>(permission, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            throw new ArgumentException($"unknown plugin permission: {permission}", nameof(permission));
        return manager.SetPermissionAsync(package, parsed, allowed, cancellationToken);
    }

}
