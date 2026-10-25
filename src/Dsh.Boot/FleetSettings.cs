using YamlDotNet.Serialization;

namespace Dsh.Boot;

/**
 * settings.yaml 的 fleet 段: 机器清单与远端自动部署偏好, GUI、`dsharp machine|session|doctor` 与 WebBridge 共用。
 * 隐式的本机(local)不写在这里; 读写与校验见 Dsh.Fleet.MachineStore, 省略的字段取默认值。
 */
public sealed class FleetSettings
{
    [YamlMember(Alias = "machines")]
    public List<MachineSettings> Machines { get; set; } = [];

    [YamlMember(Alias = "deploy")]
    public FleetDeploySettings? Deploy { get; set; }
}

/** 一台机器; kind 为 ssh、iroh 或 local, local 只用 name 与 harnessHome。 */
public sealed class MachineSettings
{
    [YamlMember(Alias = "name")]
    public string? Name { get; set; }

    [YamlMember(Alias = "kind")]
    public string? Kind { get; set; }

    [YamlMember(Alias = "host")]
    public string? Host { get; set; }

    [YamlMember(Alias = "port")]
    public int? Port { get; set; }

    [YamlMember(Alias = "user")]
    public string? User { get; set; }

    /** agent / key / password。 */
    [YamlMember(Alias = "auth")]
    public string? Auth { get; set; }

    [YamlMember(Alias = "keyPath")]
    public string? KeyPath { get; set; }

    /** ssh 的 ProxyCommand。 */
    [YamlMember(Alias = "proxy")]
    public string? Proxy { get; set; }

    /** 远端放 dsharp 的目录, 也是远端新会话的默认工作目录。 */
    [YamlMember(Alias = "remotePath")]
    public string? RemotePath { get; set; }

    /** 密码认证时的密码; settings.yaml 仅属主可读写。 */
    [YamlMember(Alias = "password")]
    public string? Password { get; set; }

    /** 该机器上 host 使用的 harness home; 留空 = 那台机器的默认解析。 */
    [YamlMember(Alias = "harnessHome")]
    public string? HarnessHome { get; set; }

    /** 额外的 ssh -o 选项(Key=Value), 排在内置选项之前。 */
    [YamlMember(Alias = "sshOptions")]
    public List<string>? SshOptions { get; set; }

    [YamlMember(Alias = "endpointId")]
    public string? EndpointId { get; set; }

    /** iroh 专用 relay 地址, 留空使用上游默认列表。 */
    [YamlMember(Alias = "relayUrls")]
    public List<string>? RelayUrls { get; set; }

    /** 本机 iroh-ssh 可执行文件路径; 留空时从 PATH 解析。 */
    [YamlMember(Alias = "irohExecutablePath")]
    public string? IrohExecutablePath { get; set; }

    /** 传给 iroh-ssh 的其他参数。 */
    [YamlMember(Alias = "irohArguments")]
    public string? IrohArguments { get; set; }

    /** Optional application egress borrowed through SSH, independent of the machine's SSH proxy. */
    [YamlMember(Alias = "outboundTunnel")]
    public OutboundTunnelSettings? OutboundTunnel { get; set; }
}

/** Omitted fields do not enable an outbound tunnel. */
public sealed class OutboundTunnelSettings
{
    [YamlMember(Alias = "mode")]
    public string? Mode { get; set; }

    [YamlMember(Alias = "proxyUrl")]
    public string? ProxyUrl { get; set; }

    [YamlMember(Alias = "localPort")]
    public int? LocalPort { get; set; }
}

/** 远端自动部署偏好; 省略的字段取默认值(自动部署开启、官方发布地址、latest、下载位置 auto)。 */
public sealed class FleetDeploySettings
{
    [YamlMember(Alias = "autoDeploy")]
    public bool? AutoDeploy { get; set; }

    [YamlMember(Alias = "releaseBaseUrl")]
    public string? ReleaseBaseUrl { get; set; }

    /** latest 或发布标签(带不带 v 前缀均可)。 */
    [YamlMember(Alias = "releaseVersion")]
    public string? ReleaseVersion { get; set; }

    /** auto / client / server: 发布包在哪里下载。 */
    [YamlMember(Alias = "via")]
    public string? Via { get; set; }

    /** 远端下载发布包时用的代理。 */
    [YamlMember(Alias = "serverProxy")]
    public string? ServerProxy { get; set; }
}
