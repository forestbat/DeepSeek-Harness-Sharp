using Dsh.Core;
using Dsh.Runtime;
using Dsh.Sandbox;

namespace Dsh.Boot;

/// <summary>Reads script permissions for explicit C# script launches, without intercepting other tools.</summary>
public sealed class ScriptExecutionPolicy : IScriptExecutionPolicy
{
    private readonly Func<HarnessSettings> _read;
    private readonly ISafetyBoundary _safety;

    public ScriptExecutionPolicy(Context context, HarnessHome home, HarnessSettings settings)
    {
        _read = HarnessSettings.CreatePolicyReader(home, settings);
        _safety = context.Get<ISafetyBoundary>(ISafetyBoundary.ServiceName, false) ?? new SafetyBoundary(context, _read);
        context.Provide(IScriptExecutionPolicy.ServiceName, this);
    }

    private HarnessSettings ActiveSettings => _read();
    public bool Enabled => ActiveSettings.Sandbox?.Enabled == true;

    public Task AuthorizeAsync(ToolRunContext? run, CancellationToken signal)
        => _safety.AuthorizeOperationAsync(run, signal, scriptPermissions: true);

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
