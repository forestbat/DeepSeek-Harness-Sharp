using Dsh.Sandbox;

namespace Dsh.Core;

/// <summary>Applies only when a script executor explicitly calls SpawnScriptAsync.</summary>
public interface IScriptExecutionPolicy
{
    const string ServiceName = "scriptExecutionPolicy";
    const string PermissionNotice = " scripts.permissions controls this executor's workspace read/write and network access while sandbox.enabled is ON; runtime inputs and private scratch are technical grants. Host-role plugins and other process tools are not constrained by these script permissions.";

    bool Enabled { get; }
    Task AuthorizeAsync(ToolRunContext? run, CancellationToken signal);
    SandboxProcessOptions CreateOptions(SubprocessSpawnSpec spec, string executable, string temporaryDirectory);
}
