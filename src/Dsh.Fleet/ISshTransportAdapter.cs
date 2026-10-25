namespace Dsh.Fleet;

/// <summary>Supplies protocol-specific OpenSSH options while the client owns authentication and process lifetimes.</summary>
public interface ISshTransportAdapter
{
    string Kind { get; }
    bool IsAvailable { get; }
    string? FindExecutable() => null;
    SshTransportConfiguration Configure(MachineSpec machine);
}

/// <summary>Options applied before user overrides for a non-TCP SSH transport.</summary>
public readonly record struct SshTransportConfiguration(string ProxyCommand, string HostKeyAlias, bool DisableMultiplexing);
