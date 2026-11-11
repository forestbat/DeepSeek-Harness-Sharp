namespace Dsh.RemoteHost;

public sealed class HostProtocolMismatchException(int localVersion, int remoteVersion, string? detail = null)
    : IOException($"host protocol version mismatch: client {localVersion}, host {remoteVersion}{(detail is { Length: > 0 } text ? $" ({text})" : "")}")
{
    public int LocalVersion { get; } = localVersion;

    public int RemoteVersion { get; } = remoteVersion;

    public bool RemoteIsNewer => RemoteVersion > LocalVersion;
}
