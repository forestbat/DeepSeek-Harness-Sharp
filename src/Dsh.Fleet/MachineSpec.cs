using System.Text.Json.Serialization;

namespace Dsh.Fleet;

/** How the client reaches a machine's host. */
public static class MachineKinds
{
    /** The host on this computer, reached through its loopback endpoint (host.sock). */
    public const string Local = "local";

    /** A remote host reached through `ssh -T ... dsharp host --serve --stdio`. */
    public const string Ssh = "ssh";

    /** A remote SSH service reached by its persistent iroh endpoint identity. */
    public const string Iroh = "iroh";
}

/** SSH authentication modes. */
public static class SshAuth
{
    public const string Password = "password";
    public const string Key = "key";
    public const string Agent = "agent";
}

/**
 * One machine of the fleet.
 * Local machines use only Name and HarnessHome; SSH machines use the connection fields as well.
 * HarnessHome is the harness home the host runs under (DSH_HOME); empty means the default resolution on that machine (DSH_HOME or ~/.dsh, following .storage-root).
 * RemotePath is the remote directory that holds ./dsharp and is the default working directory of new remote sessions.
 * SshOptions are extra `-o Key=Value` options placed before the built-in ones, so they take precedence (ssh keeps the first value it sees).
 */
public sealed record MachineSpec(
    string Name,
    string Kind,
    string Host = "",
    int Port = MachineSpec.DefaultSshPort,
    string User = "",
    string Auth = SshAuth.Agent,
    string? KeyPath = null,
    string? Proxy = null,
    string? RemotePath = null,
    string? Password = null,
    string? HarnessHome = null,
    IReadOnlyList<string>? SshOptions = null,
    string? EndpointId = null,
    IReadOnlyList<string>? RelayUrls = null,
    OutboundTunnelSpec? OutboundTunnel = null,
    string? IrohExecutablePath = null,
    string? IrohArguments = null)
{
    public const int DefaultSshPort = 22;
    public const int IrohEndpointIdLength = 64;

    /** The implicit machine every client has: the host on this computer. */
    public const string LocalName = "local";

    public static MachineSpec Local(string? harnessHome = null, string name = LocalName)
        => new(name, MachineKinds.Local, HarnessHome: harnessHome);

    public static MachineSpec Ssh(string name, string host, int port, string user, string auth)
        => new(name, MachineKinds.Ssh, host, port, user, auth);

    /** Validates persisted transport data independently of optional implementations and installed commands. */
    public void ValidateTransportSettings()
    {
        ValidateRelayUrls(RelayUrls ?? []);
        if (!IsIroh)
            return;
        if (EndpointId is not { Length: IrohEndpointIdLength } endpoint || endpoint.Any(character => !char.IsAsciiHexDigit(character)))
            throw new InvalidOperationException($"iroh machine \"{Name}\" requires a {IrohEndpointIdLength}-character hexadecimal Endpoint ID");
        if (string.IsNullOrWhiteSpace(User))
            throw new InvalidOperationException($"iroh machine \"{Name}\" requires an SSH user");
        if (IrohExecutablePath is { Length: > 0 } executable && (executable.Contains('\n') || executable.Contains('\r')))
            throw new InvalidOperationException("iroh executable path must be a single line");
        if (IrohArguments is { Length: > 0 } arguments && (arguments.Contains('\n') || arguments.Contains('\r')))
            throw new InvalidOperationException("iroh arguments must be a single line");
        if (!string.IsNullOrEmpty(Proxy) || (SshOptions ?? []).Any(IsProxyOverride))
            throw new InvalidOperationException("iroh machines manage their transport; remove ProxyCommand, ProxyJump and Proxy settings");
    }

    public static void ValidateRelayUrls(IReadOnlyList<string> relayUrls)
    {
        foreach (var relay in relayUrls)
            if (!Uri.TryCreate(relay, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("https" or "http") || uri.Host.Length == 0
                || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
                throw new InvalidOperationException("iroh relay URLs must be absolute HTTP(S) addresses without credentials or fragments");
    }

    private static bool IsProxyOverride(string option)
    {
        var name = option.AsSpan().TrimStart("= \t");
        var separator = name.IndexOfAny("= \t");
        if (separator >= 0)
            name = name[..separator];
        return name.Equals("ProxyCommand", StringComparison.OrdinalIgnoreCase)
            || name.Equals("ProxyJump", StringComparison.OrdinalIgnoreCase);
    }

    [JsonIgnore]
    public bool IsLocal => Kind == MachineKinds.Local;

    [JsonIgnore]
    public bool IsIroh => Kind == MachineKinds.Iroh;

    [JsonIgnore]
    public string ConnectionHost => IsIroh ? EndpointId ?? "" : Host;

    /** user@host:port for SSH machines, the harness home (or "this computer") for local ones. */
    [JsonIgnore]
    public string Target => IsLocal ? HarnessHome ?? "this computer" : $"{User}@{ConnectionHost}:{Port}";
}

/** A logical proxy endpoint borrowed through the machine's SSH connection. */
public sealed record OutboundTunnelSpec(string Mode, string ProxyUrl, int? LocalPort = null);
