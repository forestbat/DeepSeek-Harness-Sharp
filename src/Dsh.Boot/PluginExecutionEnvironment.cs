using Dsh.Plugins;

namespace Dsh.Boot;

public readonly record struct PluginExecutionSettings(bool SandboxEnabled,
    IReadOnlyDictionary<string, PluginSetting> Plugins, (bool Read, bool Write, bool Network) ScriptPermissions);

/// <summary>Host-owned paths and settings snapshots reused until the document metadata changes.</summary>
public sealed class PluginExecutionEnvironment(HarnessHome home, HarnessSettings settings, string? workingDirectory)
{
    private readonly Lock _snapshotLock = new();
    private readonly FileInfo _settingsFile = new(home.SettingsFile);
    private HarnessSettings? _snapshot;
    private (DateTime Modified, long Length) _snapshotStamp;

    public string SettingsPath => home.SettingsFile;
    public string WorkingDirectory => workingDirectory ?? Environment.CurrentDirectory;

    public PluginExecutionSettings ReadSettings()
    {
        var active = ReadSnapshot();
        var scripts = active.Scripts?.Permissions;
        return new PluginExecutionSettings(active.Sandbox?.Enabled == true, active.Plugins,
            (scripts?.Read ?? true, scripts?.Write ?? true, scripts?.Network ?? true));
    }

    private HarnessSettings ReadSnapshot()
    {
        (DateTime Modified, long Length) stamp;
        lock (_snapshotLock)
        {
            _settingsFile.Refresh();
            if (!_settingsFile.Exists)
            {
                _snapshot = null;
                return settings;
            }
            stamp = (_settingsFile.LastWriteTimeUtc, _settingsFile.Length);
            if (_snapshot is not null && stamp == _snapshotStamp)
                return _snapshot;
        }
        var snapshot = HarnessSettings.LoadForReview(home);
        lock (_snapshotLock)
        {
            _snapshotStamp = stamp;
            _snapshot = snapshot;
        }
        return snapshot;
    }
}
