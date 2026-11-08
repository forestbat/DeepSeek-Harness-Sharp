using Dsh.Runtime;

namespace Dsh.Plugins;

/// <summary>Derives the execution boundary from artifact form and the explicit YAML migration override.</summary>
public static class PluginRoleResolver
{
    public static PluginExecution Resolve(string package, PluginSetting? setting, PluginForm form,
        bool supportsCapabilities = true, string? entry = null)
    {
        var execution = setting?.Execution ?? (form == PluginForm.CompiledIn ? PluginExecution.Host : PluginExecution.Sandbox);
        if (setting?.Enabled != false && execution == PluginExecution.Sandbox && (!supportsCapabilities || entry is not null))
            throw new RuntimeException("PLUGIN_HOST_ROLE_REQUIRED",
                $"Plugin {package} requires plugins.\"{package}\".execution: host while sandbox.enabled is ON. Worker plugins use IDshPlugin with the IPluginHostFactory capability service; the application Context remains in the host, and host execution is not OS-confined.");
        return execution;
    }
}
