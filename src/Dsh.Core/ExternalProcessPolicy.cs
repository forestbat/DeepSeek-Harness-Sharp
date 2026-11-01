using Dsh.Sandbox;
using Dsh.Transport;

namespace Dsh.Core;

/// <summary>Builds the OS boundary for opaque model processes when sandboxing is enabled.</summary>
public interface IExternalProcessPolicy
{
    const string ServiceName = "externalProcessPolicy";
    const string SettingsProtectionNotice =
        " Change active settings.yaml through mediated write/edit tools or plugin capabilities so the actual proposed diff can be approved. "
        + "When sandbox.enabled is ON, direct settings writes, aliases and replacement are denied by the OS; "
        + "scripts.permissions independently controls script workspace read/write and network access. "
        + "On Linux, read=false/write=false gives an empty private guest directory at the requested cwd path, not the host workspace; runtime inputs and private scratch remain technical grants. "
        + "When OFF, rules needing opaque filesystem enforcement fail before execution; mediated approvals remain active.";

    bool Enabled { get; }
    string Revision { get; }
    string InvocationRevision { get; }
    Task DemandAsync(SubprocessSpawnSpec spec);
    void DemandUnconfined(string surface);
    void DemandConfiguredMcp(string surface, string lifecycleOwner, CancellationToken signal);

    SandboxProcessOptions CreateOptions(SubprocessSpawnSpec spec, string executable, string temporaryDirectory);
}

public interface IRuntimeCodeIntegrity
{
    const string ServiceName = "runtimeCodeIntegrity";
    void DemandWritable(PinnedFileTarget target);
}
