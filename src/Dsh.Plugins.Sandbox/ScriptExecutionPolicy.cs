using Dsh.Core;
using Dsh.Runtime;
using Dsh.Sandbox;

namespace Dsh.Plugins.Sandbox;

/// <summary>Reads script permissions for explicit C# script launches, without intercepting other tools.</summary>
public sealed class ScriptExecutionPolicy(IPluginExecutionEnvironment environment) : IScriptExecutionPolicy
{
    public bool Enabled => environment.ReadSettings().SandboxEnabled;

    public SandboxProcessOptions CreateOptions(SubprocessSpawnSpec spec, string executable, string temporaryDirectory)
    {
        var active = environment.ReadSettings();
        if (!active.SandboxEnabled)
            throw new InvalidOperationException("Script confinement requires sandbox.enabled to be ON.");
        var permissions = active.ScriptPermissions;
        return SubprocessSandboxOptions.Create(spec, executable, temporaryDirectory,
            permissions.Read, permissions.Write, permissions.Network);
    }
}
