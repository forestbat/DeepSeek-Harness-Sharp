using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dsh.Runtime;
using Dsh.Workspace;
using Dsh.RemoteHost;

namespace Dsh.Gui.ViewModels;

/** 机器列表的一行: 机器清单里的记录 + 最近一次得知的连接状态、host 版本与平台(来自常驻连接或"测试连接")。 */
public sealed partial class MachineRowViewModel(MachineSpec spec, bool isImplicit) : ObservableObject
{
    public MachineSpec Spec => spec;

    public string Name => spec.Name;

    public string KindText => spec.IsLocal ? "本机" : spec.IsIroh ? "SSH-over-iroh" : "SSH";

    public string Target => spec.Target;

    /** 隐式的本机(local): 不在机器清单里, 不能编辑或删除。 */
    public bool IsImplicit => isImplicit;

    [ObservableProperty]
    private string _stateText = "未连接";

    [ObservableProperty]
    private string? _hostVersion;

    [ObservableProperty]
    private string? _platform;

    [ObservableProperty]
    private string? _detail;

    public void Apply(MachineConnectionState state, HostInfo? info, string? detail)
    {
        StateText = StateLabel(state);
        if (info is not null)
            (HostVersion, Platform) = (info.HostVersion, info.Platform);
        Detail = detail is null ? null : OutboundProxy.Redact(detail, spec.Password, spec.Proxy, spec.OutboundTunnel?.ProxyUrl);
    }

    /** 重新载入清单后保留同名机器最近一次得知的状态。 */
    internal void CopyStatusFrom(MachineRowViewModel other)
        => (StateText, HostVersion, Platform, Detail) = (other.StateText, other.HostVersion, other.Platform, other.Detail);

    public static string StateLabel(MachineConnectionState state) => state switch
    {
        MachineConnectionState.Connected => "已连接",
        MachineConnectionState.Connecting => "连接中",
        MachineConnectionState.Reconnecting => "重连中",
        MachineConnectionState.Deploying => "部署中",
        _ => "离线",
    };
}

/** 诊断结果的一行; Fix 是可直接复制运行的修复命令。 */
public sealed class DoctorCheckRow(DoctorCheck check)
{
    public string Name => check.Name;

    public DoctorOutcome Outcome => check.Outcome;

    public string OutcomeText => check.Outcome switch
    {
        DoctorOutcome.Ok => "正常",
        DoctorOutcome.Warning => "警告",
        DoctorOutcome.Failed => "失败",
        _ => "跳过",
    };

    public bool IsOk => check.Outcome == DoctorOutcome.Ok;

    public bool IsWarning => check.Outcome == DoctorOutcome.Warning;

    public bool IsFailed => check.Outcome == DoctorOutcome.Failed;

    public string Detail => check.Detail;

    public string? Fix => check.Fix;

    public bool HasFix => !string.IsNullOrWhiteSpace(check.Fix);
}

/**
 * 新增或编辑一台机器的表单, 覆盖 MachineSpec 的全部字段。
 * 本机条目只用名称与 harness home; 普通 SSH 用主机, SSH-over-iroh 用 Endpoint ID, 两者共用 SSH 认证与远端设置。
 */
public sealed partial class MachineEditorViewModel(bool allowIroh = false) : ObservableObject
{
    private const int MaxPort = 65535;
    private OutboundTunnelSpec? _savedOutboundTunnel;
    private string? _savedSshProxy;
    private bool _updatingIrohTarget;

    public static IReadOnlyList<string> OutboundTunnelModes { get; } = ["proxyCommand", "localForward"];

    [ObservableProperty]
    private bool _outboundTunnelEnabled;

    [ObservableProperty]
    private string _outboundTunnelMode = "proxyCommand";

    [ObservableProperty]
    private string _outboundTunnelProxyUrl = "";

    [ObservableProperty]
    private string _outboundTunnelLocalPort = "";

    private OutboundTunnelSpec? BuildOutboundTunnel()
    {
        if (!OutboundTunnelEnabled)
            return null;
        int? localPort = null;
        if (!string.IsNullOrWhiteSpace(OutboundTunnelLocalPort))
        {
            if (!int.TryParse(OutboundTunnelLocalPort.Trim(), out var port) || port is <= 0 or > MaxPort)
                throw new ArgumentException($"Local port must be empty or an integer from 1 to {MaxPort}.");
            localPort = port;
        }
        return new OutboundTunnelSpec(
            OutboundTunnelMode,
            OutboundProxy.ResolveEdited(OutboundTunnelProxyUrl, _savedOutboundTunnel?.ProxyUrl) ?? "",
            localPort);
    }

    public static IReadOnlyList<string> AuthModes { get; } = [SshAuth.Agent, SshAuth.Key, SshAuth.Password];

    private static readonly string[] AllKinds = [MachineKinds.Ssh, MachineKinds.Iroh, MachineKinds.Local];
    private static readonly string[] BasicKinds = [MachineKinds.Ssh, MachineKinds.Local];

    public IReadOnlyList<string> Kinds => allowIroh ? AllKinds : BasicKinds;

    /** 编辑已有机器时的原名称(改名保存时替换原记录); 新增时为 null。 */
    public string? OriginalName { get; init; }

    public bool IsNew => OriginalName is null;

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSsh), nameof(IsIroh), nameof(IsLocal), nameof(IsRemote))]
    private string _kind = MachineKinds.Ssh;

    [ObservableProperty]
    private string _host = "";

    [ObservableProperty]
    private string _endpointId = "";

    [ObservableProperty]
    private string _irohTarget = "";

    [ObservableProperty]
    private string _irohExecutablePath = "";

    [ObservableProperty]
    private string _irohArguments = "";

    /** 自定义 relay URL, 每行一个; 留空使用 iroh 默认 relay。 */
    [ObservableProperty]
    private string _relayUrls = "";

    [ObservableProperty]
    private string _port = MachineSpec.DefaultSshPort.ToString();

    [ObservableProperty]
    private string _user = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPassword), nameof(IsKey))]
    private string _auth = SshAuth.Agent;

    [ObservableProperty]
    private string _keyPath = "";

    [ObservableProperty]
    private string _password = "";

    [ObservableProperty]
    private string _proxy = "";

    [ObservableProperty]
    private string _remotePath = "";

    [ObservableProperty]
    private string _harnessHome = "";

    /** 额外的 ssh `-o` 选项, 每行一个 Key=Value。 */
    [ObservableProperty]
    private string _sshOptions = "";

    public bool IsSsh => Kind == MachineKinds.Ssh;

    public bool IsIroh => Kind == MachineKinds.Iroh;

    public bool IsLocal => Kind == MachineKinds.Local;

    public bool IsRemote => IsSsh || IsIroh;

    public bool IsPassword => Auth == SshAuth.Password;

    public bool IsKey => Auth == SshAuth.Key;

    public static MachineEditorViewModel From(MachineSpec spec, bool allowIroh = false, string? irohExecutable = null)
    {
        var editor = new MachineEditorViewModel(allowIroh || spec.IsIroh)
        {
            OriginalName = spec.Name,
            Name = spec.Name,
            Kind = spec.Kind,
            Host = spec.Host,
            EndpointId = spec.EndpointId ?? "",
            RelayUrls = string.Join(Environment.NewLine, spec.RelayUrls ?? []),
            Port = spec.Port.ToString(),
            User = spec.User,
            Auth = spec.Auth,
            KeyPath = spec.KeyPath ?? "",
            Password = spec.Password ?? "",
            _savedSshProxy = spec.Proxy,
            Proxy = OutboundProxy.RedactUrlCredentials(spec.Proxy ?? "", spec.Proxy),
            RemotePath = spec.RemotePath ?? "",
            HarnessHome = spec.HarnessHome ?? "",
            SshOptions = string.Join(Environment.NewLine, spec.SshOptions ?? []),
            IrohExecutablePath = spec.IrohExecutablePath ?? (spec.IsIroh ? irohExecutable ?? "" : ""),
            IrohArguments = spec.IrohArguments ?? "",
            _savedOutboundTunnel = spec.OutboundTunnel,
            OutboundTunnelEnabled = spec.OutboundTunnel is not null,
            OutboundTunnelMode = spec.OutboundTunnel?.Mode ?? "proxyCommand",
            OutboundTunnelProxyUrl = OutboundProxy.RedactUrlCredentials(spec.OutboundTunnel?.ProxyUrl ?? "", spec.OutboundTunnel?.ProxyUrl),
            OutboundTunnelLocalPort = spec.OutboundTunnel?.LocalPort?.ToString() ?? "",
        };
        editor.UpdateIrohTargetFromParts();
        return editor;
    }

    /** Convert the form to a machine specification; the catalog validates persisted name rules. */
    public MachineSpec ToSpec()
    {
        var name = Name.Trim();
        if (Kind == MachineKinds.Local)
            return MachineSpec.Local(Optional(HarnessHome) ?? throw new ArgumentException("本机条目需要填写 harness home（默认的本机已经隐式存在）"), name);
        if (string.IsNullOrWhiteSpace(User))
            throw new ArgumentException("远端机器需要填写 SSH 用户");
        if (Kind == MachineKinds.Ssh && string.IsNullOrWhiteSpace(Host))
            throw new ArgumentException("SSH 机器需要填写主机");
        if (Auth is not (SshAuth.Agent or SshAuth.Key or SshAuth.Password))
            throw new ArgumentException("请选择有效的认证方式");
        if (Auth == SshAuth.Key && string.IsNullOrWhiteSpace(KeyPath))
            throw new ArgumentException("密钥认证需要填写密钥路径");
        if (Auth == SshAuth.Password && string.IsNullOrEmpty(Password))
            throw new ArgumentException("密码认证需要填写密码");
        if (!int.TryParse(Port.Trim(), out var port) || port is <= 0 or > MaxPort)
            throw new ArgumentException($"端口必须是 1-{MaxPort} 的整数");
        var irohTarget = ParseIrohTarget();
        string[] options = [.. SshOptions.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0)];
        string[] relays = [.. RelayUrls.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0)];
        return MachineSpec.Ssh(name, Host.Trim(), port, User.Trim(), Auth) with
        {
            Kind = Kind,
            EndpointId = IsIroh ? irohTarget.Endpoint : null,
            User = IsIroh ? irohTarget.User : User.Trim(),
            RelayUrls = relays.Length == 0 ? null : relays,
            KeyPath = IsKey ? Optional(KeyPath) : null,
            Password = IsPassword ? Password : null,
            Proxy = IsSsh ? OutboundProxy.ResolveEdited(Proxy, _savedSshProxy) : null,
            RemotePath = Optional(RemotePath),
            HarnessHome = Optional(HarnessHome),
            SshOptions = options.Length == 0 ? null : options,
            OutboundTunnel = BuildOutboundTunnel(),
            IrohExecutablePath = IsIroh ? Optional(IrohExecutablePath) : null,
            IrohArguments = IsIroh ? Optional(IrohArguments) : null,
        };
    }

    private (string User, string Endpoint) ParseIrohTarget()
    {
        if (!IsIroh)
            return (User.Trim(), "");
        var target = IrohTarget.Trim();
        var at = target.IndexOf('@');
        if (at <= 0 || at == target.Length - 1)
            throw new ArgumentException("Iroh 远程目标必须是 user@<64 位十六进制 endpoint id>");
        var user = target[..at].Trim();
        var endpoint = target[(at + 1)..].Trim();
        if (user.Length == 0)
            throw new ArgumentException("Iroh 远程目标缺少用户");
        if (endpoint.Length != MachineSpec.IrohEndpointIdLength || endpoint.Any(character => !char.IsAsciiHexDigit(character)))
            throw new ArgumentException($"Iroh Endpoint ID 必须是 {MachineSpec.IrohEndpointIdLength} 位十六进制");
        return (user, endpoint);
    }

    private void UpdateIrohTargetFromParts()
    {
        if (_updatingIrohTarget)
            return;
        _updatingIrohTarget = true;
        try
        {
            IrohTarget = User.Length > 0 && EndpointId.Length > 0 ? $"{User}@{EndpointId}" : EndpointId;
        }
        finally
        {
            _updatingIrohTarget = false;
        }
    }

    partial void OnUserChanged(string value) => UpdateIrohTargetFromParts();

    partial void OnEndpointIdChanged(string value) => UpdateIrohTargetFromParts();

    partial void OnKindChanged(string value) => UpdateIrohTargetFromParts();

    partial void OnIrohTargetChanged(string value)
    {
        if (_updatingIrohTarget)
            return;
        var at = value.IndexOf('@');
        if (at <= 0 || at == value.Length - 1)
            return;
        _updatingIrohTarget = true;
        try
        {
            User = value[..at].Trim();
            EndpointId = value[(at + 1)..].Trim();
        }
        finally
        {
            _updatingIrohTarget = false;
        }
    }

    /** 空白输入表示不设置(null), 否则去掉首尾空白。 */
    internal static string? Optional(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/**
 * 自动部署偏好(与机器清单同在 settings.yaml 的 fleet 段, 所有 SSH 机器共用)的表单。
 * 本机下载使用共享 network.outbound_proxy; 远端代理在远端解析, 用于远端直接下载。
 */
public sealed partial class DeploySettingsEditorViewModel : ObservableObject
{
    private string? _savedServerProxy;

    public static IReadOnlyList<string> ViaOptions { get; } = [DownloadVia.Auto, DownloadVia.Client, DownloadVia.Server];

    [ObservableProperty]
    private bool _autoDeploy = true;

    [ObservableProperty]
    private string _releaseBaseUrl = DeploySettings.DefaultReleaseBaseUrl;

    [ObservableProperty]
    private string _releaseVersion = DeploySettings.LatestVersion;

    [ObservableProperty]
    private string _via = DownloadVia.Auto;

    [ObservableProperty]
    private string _serverProxy = "";

    public static DeploySettingsEditorViewModel From(DeploySettings settings) => new()
    {
        AutoDeploy = settings.AutoDeploy,
        ReleaseBaseUrl = settings.ReleaseBaseUrl,
        ReleaseVersion = settings.ReleaseVersion,
        Via = settings.Via,
        _savedServerProxy = settings.ServerProxy,
        ServerProxy = OutboundProxy.RedactUrlCredentials(settings.ServerProxy ?? "", settings.ServerProxy),
    };

    public DeploySettings ToSettings() => new()
    {
        AutoDeploy = AutoDeploy,
        ReleaseBaseUrl = MachineEditorViewModel.Optional(ReleaseBaseUrl) ?? DeploySettings.DefaultReleaseBaseUrl,
        ReleaseVersion = MachineEditorViewModel.Optional(ReleaseVersion) ?? DeploySettings.LatestVersion,
        Via = ViaOptions.Contains(Via) ? Via : DownloadVia.Auto,
        ServerProxy = OutboundProxy.ResolveEdited(ServerProxy, _savedServerProxy),
    };
}
