namespace Dsh.Plugins;

public readonly record struct PluginExecutionSettings(bool SandboxEnabled,
    IReadOnlyDictionary<string, PluginSetting> Plugins, (bool Read, bool Write, bool Network) ScriptPermissions);

/// <summary>Host-owned paths and freshly reviewed settings without exposing the application composition root.</summary>
public interface IPluginExecutionEnvironment
{
    string SettingsPath { get; }
    string WorkingDirectory { get; }
    PluginExecutionSettings ReadSettings();
}
