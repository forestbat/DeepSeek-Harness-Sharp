using Dsh.Boot;
using Dsh.Core;
using Dsh.Runtime;
using Dsh.Sandbox;
using Dsh.Subprocess;

namespace Dsh.Plugins.Sandbox;

/// <summary>Reads script permissions for explicit C# script launches, without intercepting other tools.</summary>
public sealed class ScriptExecutionPolicy(PluginExecutionEnvironment environment)
{
    public Task<SubprocessHandle> SpawnAsync(SubprocessService subprocess, SubprocessSpawnSpec spec)
        => environment.ReadSettings().SandboxEnabled ? subprocess.SpawnAsync(spec, StartAsync) : subprocess.SpawnAsync(spec);

    private Task<OwnedProcess> StartAsync(SubprocessSpawnSpec spec, string executable, string temporaryDirectory)
    {
        var active = environment.ReadSettings();
        if (!active.SandboxEnabled)
            throw new InvalidOperationException("Script confinement requires sandbox.enabled to be ON.");
        var permissions = active.ScriptPermissions;
        var options = SubprocessSandboxOptions.Create(spec, executable, temporaryDirectory,
            permissions.Read, permissions.Write, permissions.Network);
        return SandboxedProcess.StartAsync(options, spec.Signal);
    }
}
