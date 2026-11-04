using System.Text.Json.Serialization;

namespace Dsh.Workspace;

public enum ProxyProbeLayer
{
    [JsonStringEnumMemberName("ssh")] Ssh,
    [JsonStringEnumMemberName("local_outbound")] LocalOutbound,
    [JsonStringEnumMemberName("remote_outbound")] RemoteOutbound,
}

public enum ProxyProbeOutcome
{
    Passed,
    Failed,
    Cancelled,
    Unsupported,
}

public enum ProxyProbeStage
{
    [JsonStringEnumMemberName("configuration")] Configuration,
    [JsonStringEnumMemberName("ssh")] Ssh,
    [JsonStringEnumMemberName("proxy_connect")] ProxyConnect,
    [JsonStringEnumMemberName("proxy_auth")] ProxyAuth,
    [JsonStringEnumMemberName("http_target")] HttpTarget,
    [JsonStringEnumMemberName("tunnel")] Tunnel,
}

public sealed record ProxyProbeResult(ProxyProbeLayer Layer, ProxyProbeOutcome Outcome, ProxyProbeStage Stage,
    string Detail, int? HttpStatus, string Machine, string? Home, Guid OperationId)
{
    private const int InterruptedExitCode = 130;

    public int ExitCode => Outcome switch
    {
        ProxyProbeOutcome.Passed => 0,
        ProxyProbeOutcome.Cancelled => InterruptedExitCode,
        _ => 1,
    };

    public string Label => Layer switch
    {
        ProxyProbeLayer.Ssh => "SSH 连接代理",
        ProxyProbeLayer.LocalOutbound => "本机出口代理",
        ProxyProbeLayer.RemoteOutbound => "远端服务器出口代理",
        _ => throw new ArgumentOutOfRangeException(nameof(Layer)),
    };
}

