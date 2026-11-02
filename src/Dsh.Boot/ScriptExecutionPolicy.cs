using Dsh.Core;
using Dsh.Runtime;
using Dsh.Sandbox;

namespace Dsh.Boot;

/// <summary>Reads script permissions for explicit C# script launches, without intercepting other tools.</summary>
public sealed class ScriptExecutionPolicy : IScriptExecutionPolicy
{
    private readonly HarnessHome _home;
    private readonly HarnessSettings _settings;

    public ScriptExecutionPolicy(Context context, HarnessHome home, HarnessSettings settings)
    {
        _home = home;
        _settings = settings;
        context.Provide(IScriptExecutionPolicy.ServiceName, this);
    }

    private HarnessSettings ActiveSettings => File.Exists(_home.SettingsFile) ? HarnessSettings.LoadForReview(_home) : _settings;
    public bool Enabled => ActiveSettings.Sandbox?.Enabled == true;

    public SandboxProcessOptions CreateOptions(SubprocessSpawnSpec spec, string executable, string temporaryDirectory)
    {
        var active = ActiveSettings;
        if (active.Sandbox?.Enabled != true)
            throw new InvalidOperationException("Script confinement requires sandbox.enabled to be ON.");
        var permissions = active.Scripts?.Permissions;
        return SubprocessSandboxOptions.Create(spec, executable, temporaryDirectory,
            permissions?.Read ?? true, permissions?.Write ?? true, permissions?.Network ?? true);
    }
}
