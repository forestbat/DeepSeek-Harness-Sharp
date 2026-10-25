using Dsh.Runtime;

namespace Dsh.Fleet;

/** Connection state of one machine, as shown by clients. */
public enum MachineConnectionState
{
    /** An attempt to reach a machine that has not been connected in this session, or that was offline. */
    Connecting,

    /** The host answered the handshake; sessions and pending requests are live. */
    Connected,

    /** The connection dropped and a new attempt is running. */
    Reconnecting,

    /** Not connected; waiting for the next attempt, or stopped when reconnection is off. */
    Offline,

    /** Installing or upgrading dsharp on the remote before connecting. */
    Deploying,
}

/**
 * How a client connects to its machines.
 * ClientName is shown by hosts as the one who answered an approval or question (for example "cli@laptop").
 * StartLocalHost starts the local host when its endpoint is not reachable; the composition root supplies it because only the host executable knows how to launch itself.
 * ArchiveFactory supplies a local release archive for a RID instead of downloading one (offline deployment).
 */
public sealed record FleetOptions
{
    public static readonly TimeSpan DefaultInitialBackoff = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan DefaultMaxBackoff = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DefaultHeartbeatInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DefaultHeartbeatTimeout = TimeSpan.FromSeconds(10);

    public string ClientName { get; init; } = $"fleet@{Environment.MachineName}";

    public DeploySettings Deploy { get; init; } = new();

    public OutboundProxy LocalProxy { get; init; } = new();

    /** Acquires a shared route before this client's consumers start; disposal always awaits its release. */
    public Func<CancellationToken, ValueTask<IAsyncDisposable>>? AcquireOutboundRoute { get; init; }

    public Dsh.Boot.HarnessHome? LocalHome { get; init; }

    public ISshTransportAdapter? SshTransportAdapter { get; init; }

    public bool AutoReconnect { get; init; } = true;

    public TimeSpan InitialBackoff { get; init; } = DefaultInitialBackoff;

    public TimeSpan MaxBackoff { get; init; } = DefaultMaxBackoff;

    public TimeSpan HeartbeatInterval { get; init; } = DefaultHeartbeatInterval;

    public TimeSpan HeartbeatTimeout { get; init; } = DefaultHeartbeatTimeout;

    public Func<MachineSpec, CancellationToken, Task>? StartLocalHost { get; init; }

    public Func<DeploySettings, string, CancellationToken, Task<string>>? ArchiveFactory { get; init; }

    /** Exponential backoff: the initial delay doubled per consecutive failure, capped at MaxBackoff. */
    public TimeSpan Backoff(int failures)
    {
        var factor = Math.Pow(2, Math.Min(failures, MaxDoublings));
        return TimeSpan.FromTicks((long)Math.Min(MaxBackoff.Ticks, InitialBackoff.Ticks * factor));
    }

    /** Beyond this many doublings the delay is far above any sensible cap; it also keeps the factor finite. */
    private const int MaxDoublings = 30;
}
