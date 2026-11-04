namespace Dsh.Workspace;

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

