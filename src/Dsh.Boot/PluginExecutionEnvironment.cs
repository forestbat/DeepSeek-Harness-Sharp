using Dsh.Plugins;

namespace Dsh.Boot;

public readonly record struct PluginExecutionSettings(bool SandboxEnabled,
    IReadOnlyDictionary<string, PluginSetting> Plugins, (bool Read, bool Write, bool Network) ScriptPermissions);

/// <summary>Host-owned paths and freshly reviewed settings without exposing the application composition root.</summary>
public sealed class PluginExecutionEnvironment(HarnessHome home, HarnessSettings settings, string? workingDirectory)
{
    public string SettingsPath => home.SettingsFile;
    public string WorkingDirectory => workingDirectory ?? Environment.CurrentDirectory;

    public PluginExecutionSettings ReadSettings()
    {
        var active = File.Exists(home.SettingsFile) ? HarnessSettings.LoadForReview(home) : settings;
        var scripts = active.Scripts?.Permissions;
        return new PluginExecutionSettings(active.Sandbox?.Enabled == true, active.Plugins,
            (scripts?.Read ?? true, scripts?.Write ?? true, scripts?.Network ?? true));
    }
}
