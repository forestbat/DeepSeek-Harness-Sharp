using Dsh.Plugins;

namespace Dsh.Boot;

internal sealed class PluginExecutionEnvironment(HarnessHome home, HarnessSettings settings, string? workingDirectory)
    : IPluginExecutionEnvironment
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
