using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dsh.Fleet;
using Dsh.RemoteHost;
using Dsh.Runtime;
using Dsh.Transport;

namespace Dsh.Gui.ViewModels;

/**
 * 机器管理页: 机器清单(settings.yaml 的 fleet 段, 与 CLI 的 `dsharp machine` 和 WebBridge 共用)的增删改, 以及测试连接、手动部署与诊断。
 * 用法: new MachinesViewModel(store, options) 后 Load(); 集成方有常驻的 FleetClient 时调用 AttachFleet 显示实时连接状态, 并在 MachinesChanged 时按新清单重建它。
 * 测试连接与 `dsharp machine test` 相同: 只连接不部署、不自动拉起本机 host; 部署与诊断直接用 RemoteDsharpInstaller 与 FleetDoctor。
 * 后台线程的事件经构造时捕获的同步上下文回到 UI 线程(没有上下文时就地执行)。
 */
public sealed partial class MachinesViewModel(MachineStore store, FleetOptions options) : OperationViewModel
{
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private CancellationTokenSource? _operation;
    private FleetClient? _fleet;
    private readonly FleetProxyProbe _networkProbe = new(options);
    private readonly HashSet<Task> _networkOperations = [];
    private readonly object _networkOperationsGate = new();
    private CancellationTokenSource? _remoteOperation;
    private long _networkGeneration;
    private object? _observedRemoteClient;
    private bool _networkActive = true;
    private bool _networkShutdown;
    private object? _networkOwner;
    private RemoteOutboundProxySettings? _savedRemoteSettings;

    public NetworkProbeViewModel SshProbe { get; } = new(ProxyProbeLayer.Ssh);
    public NetworkProbeViewModel RemoteProbe { get; } = new(ProxyProbeLayer.RemoteOutbound);

    [ObservableProperty]
    private RemoteOutboundProxyState? _remoteOutboundState;

    [ObservableProperty]
    private string _remoteOutboundUrl = "";

    [ObservableProperty]
    private string _remoteOutboundBypass = "";

    [ObservableProperty]
    private string _remoteOutboundProbeUrl = "";

    [ObservableProperty]
    private bool _remoteOutboundRequiresRefresh;

    [ObservableProperty]
    private bool _remoteOutboundBusy;

    [ObservableProperty]
    private string _remoteOutboundStatus = "Select a connected machine and read its Host settings.";

    public string RemoteOutboundScope => RemoteOutboundState is { } state
        ? $"Shared by every workspace and machine alias owned by {state.OwnerId} in Host home {state.HostHome}."
        : "Settings belong to the selected target Host, not the local machine list.";

    partial void OnRemoteOutboundStateChanged(RemoteOutboundProxyState? value)
        => OnPropertyChanged(nameof(RemoteOutboundScope));

    partial void OnRemoteOutboundUrlChanged(string value) => RemoteProbe.Invalidate();

    partial void OnRemoteOutboundBypassChanged(string value) => RemoteProbe.Invalidate();

    partial void OnRemoteOutboundProbeUrlChanged(string value) => RemoteProbe.Invalidate();

    partial void OnEditorChanging(MachineEditorViewModel? value)
    {
        if (Editor is { } previous)
            previous.PropertyChanged -= OnEditorPropertyChanged;
    }

    partial void OnEditorChanged(MachineEditorViewModel? value)
    {
        SshProbe.Invalidate();
        if (value is not null)
            value.PropertyChanged += OnEditorPropertyChanged;
    }

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(MachineEditorViewModel.Name)
            or nameof(MachineEditorViewModel.Kind)
            or nameof(MachineEditorViewModel.Host)
            or nameof(MachineEditorViewModel.EndpointId)
            or nameof(MachineEditorViewModel.IrohTarget)
            or nameof(MachineEditorViewModel.IrohExecutablePath)
            or nameof(MachineEditorViewModel.IrohArguments)
            or nameof(MachineEditorViewModel.RelayUrls)
            or nameof(MachineEditorViewModel.Port)
            or nameof(MachineEditorViewModel.User)
            or nameof(MachineEditorViewModel.Auth)
            or nameof(MachineEditorViewModel.KeyPath)
            or nameof(MachineEditorViewModel.Password)
            or nameof(MachineEditorViewModel.Proxy)
            or nameof(MachineEditorViewModel.RemotePath)
            or nameof(MachineEditorViewModel.HarnessHome)
            or nameof(MachineEditorViewModel.SshOptions)
            or nameof(MachineEditorViewModel.OutboundTunnelEnabled)
            or nameof(MachineEditorViewModel.OutboundTunnelMode)
            or nameof(MachineEditorViewModel.OutboundTunnelProxyUrl)
            or nameof(MachineEditorViewModel.OutboundTunnelLocalPort))
            SshProbe.Invalidate();
    }

    public void ActivateNetwork(object? owner = null)
    {
        if (_networkShutdown)
            return;
        if (!ReferenceEquals(owner, _networkOwner))
            InvalidateNetwork();
        _networkOwner = owner;
        _networkActive = true;
        RefreshFromFleet();
    }

    public bool DeactivateNetwork(object? owner = null)
    {
        if (owner is not null && !ReferenceEquals(owner, _networkOwner))
            return false;
        _networkOwner = null;
        _networkActive = false;
        InvalidateNetwork();
        return true;
    }

    public void BeginNetworkShutdown()
    {
        _networkShutdown = true;
        DeactivateNetwork();
    }

    private void InvalidateNetwork()
    {
        _networkGeneration++;
        _remoteOperation?.Cancel();
        SshProbe.Invalidate();
        RemoteProbe.Invalidate();
        _observedRemoteClient = null;
    }

    public async Task WaitForNetworkOperationsAsync()
    {
        await Task.WhenAll(SshProbe.WaitForCompletionAsync(), RemoteProbe.WaitForCompletionAsync());
        while (true)
        {
            Task[] operations;
            lock (_networkOperationsGate)
                operations = _networkOperations.ToArray();
            if (operations.Length == 0)
                return;
            await Task.WhenAll(operations);
        }
    }

    [RelayCommand]
    private Task ReadRemoteOutboundAsync() => RunRemoteOutboundAsync(write: false, clear: false, refreshDraft: true);

    [RelayCommand]
    private Task SaveRemoteOutboundAsync() => RunRemoteOutboundAsync(write: true, clear: false, refreshDraft: true);

    [RelayCommand]
    private Task ClearRemoteOutboundAsync() => RunRemoteOutboundAsync(write: true, clear: true, refreshDraft: true);

    private RemoteOutboundProxySettings RemoteDraft() => new(
        OutboundProxy.ResolveEdited(RemoteOutboundUrl, _savedRemoteSettings?.Url),
        RemoteOutboundBypass.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        OutboundProxy.ResolveEdited(RemoteOutboundProbeUrl, _savedRemoteSettings?.ProbeUrl));

    private Task RunRemoteOutboundAsync(bool write, bool clear, bool refreshDraft)
    {
        if (_remoteOperation is not null || !_networkActive)
            return Task.CompletedTask;
        var task = RunRemoteOutboundCoreAsync(write, clear, refreshDraft);
        lock (_networkOperationsGate)
            _networkOperations.Add(task);
        _ = RemoveNetworkOperationAsync(task);
        return task;
    }

    private async Task RemoveNetworkOperationAsync(Task task)
    {
        try
        {
            await task;
        }
        finally
        {
            lock (_networkOperationsGate)
                _networkOperations.Remove(task);
        }
    }

    private async Task RunRemoteOutboundCoreAsync(bool write, bool clear, bool refreshDraft)
    {
        var generation = _networkGeneration;
        var machine = Selected?.Name;
        using var cancellation = new CancellationTokenSource();
        _remoteOperation = cancellation;
        RemoteOutboundBusy = true;
        bool Current() => _networkActive && generation == _networkGeneration && Selected?.Name == machine;
        try
        {
            var connection = SelectedConnection();
            var network = new FleetNetwork(connection);
            RemoteOutboundProxyState state;
            if (write)
            {
                if (RemoteOutboundRequiresRefresh)
                    throw new InvalidOperationException("The revision is stale. Explicitly read the target Host settings before saving; your draft has been retained.");
                var revision = RemoteOutboundState?.Revision
                    ?? throw new InvalidOperationException("Read the target Host settings before saving or clearing.");
                state = await network.SetOutboundAsync(new(revision, clear ? new() : RemoteDraft()), cancellation.Token);
            }
            else
                state = await network.GetOutboundAsync(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!Current())
                return;
            RemoteOutboundState = state;
            if (refreshDraft || _savedRemoteSettings is null)
            {
                _savedRemoteSettings = state.Settings;
                RemoteOutboundUrl = OutboundProxy.RedactUrlCredentials(state.Settings.Url ?? "", state.Settings.Url);
                RemoteOutboundBypass = string.Join(Environment.NewLine, state.Settings.Bypass ?? []);
                RemoteOutboundProbeUrl = OutboundProxy.RedactUrlCredentials(state.Settings.ProbeUrl ?? "", state.Settings.ProbeUrl);
                RemoteOutboundRequiresRefresh = false;
            }
            RemoteOutboundStatus = refreshDraft
                ? write ? "Target Host settings saved. The local machine list was not changed." : "Target Host settings read."
                : "Reconnected: current Host settings read; no draft was written. Explicitly read again before saving a retained draft.";
            if (!refreshDraft && _savedRemoteSettings is not null)
                RemoteOutboundRequiresRefresh = true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (Current())
            {
                if (write)
                    RemoteOutboundRequiresRefresh = true;
                RemoteOutboundStatus = write
                    ? "Target Host operation cancelled; the draft was retained. A write may already have committed. Explicitly read the target Host settings before saving again."
                    : "Target Host operation cancelled; the draft was retained.";
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or NotSupportedException
            or ArgumentException or JsonRpcException or UnauthorizedAccessException)
        {
            if (!Current())
                return;
            if (!write || error is JsonRpcException { Code: NetworkProtocol.RevisionConflict })
                RemoteOutboundRequiresRefresh = true;
            RemoteOutboundStatus = OutboundProxy.Redact(
                $"{error.Message} The draft was retained; use Read / Refresh to load the actual target Host settings.",
                _savedRemoteSettings?.Url, RemoteOutboundUrl, Selected?.Spec.Password, Selected?.Spec.Proxy);
        }
        finally
        {
            _remoteOperation = null;
            RemoteOutboundBusy = false;
            if (_networkActive && generation != _networkGeneration)
                RefreshFromFleet();
        }
    }

    private MachineConnection SelectedConnection()
    {
        var machine = Selected ?? throw new InvalidOperationException("Select a saved, connected machine first.");
        return _fleet?.Find(machine.Name)
            ?? throw new InvalidOperationException("The selected machine has no active Fleet connection. Connect it before reading target Host settings.");
    }

    [RelayCommand]
    private Task ProbeSshAsync()
    {
        if (!_networkActive)
            return Task.CompletedTask;
        var editor = Editor;
        var selected = Selected;
        return SshProbe.RunAsync(editor?.Name ?? selected?.Name ?? "", editor?.HarnessHome ?? selected?.Spec.HarnessHome,
            (id, token) => _networkProbe.ProbeSshAsync(
                editor?.ToSpec() ?? selected?.Spec ?? throw new InvalidOperationException("Select a machine first."), id, token),
            selected?.Spec.Password, selected?.Spec.Proxy, selected?.Spec.OutboundTunnel?.ProxyUrl,
            editor?.Password, editor?.Proxy, editor?.OutboundTunnelProxyUrl);
    }

    [RelayCommand]
    private Task ProbeRemoteAsync()
    {
        if (!_networkActive)
            return Task.CompletedTask;
        var draft = _savedRemoteSettings is null ? null : RemoteDraft();
        return RemoteProbe.RunAsync(Selected?.Name ?? "", RemoteOutboundState?.HostHome,
            (id, token) => _networkProbe.ProbeRemoteAsync(SelectedConnection(), new(draft), id, token),
            _savedRemoteSettings?.Url, RemoteOutboundUrl, Selected?.Spec.Password, Selected?.Spec.Proxy);
    }

    [RelayCommand]
    private Task CancelRemoteOutboundAsync()
    {
        _remoteOperation?.Cancel();
        lock (_networkOperationsGate)
            return Task.WhenAll(_networkOperations.ToArray());
    }

    public ObservableCollection<MachineRowViewModel> Machines { get; } = [];

    /** 部署进度(RemoteDsharpInstaller 的每一步)与测试连接的结果, 按时间顺序。 */
    public ObservableCollection<string> OperationLog { get; } = [];

    public ObservableCollection<DoctorCheckRow> DoctorChecks { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    private MachineRowViewModel? _selected;

    [ObservableProperty]
    private MachineEditorViewModel? _editor;

    [ObservableProperty]
    private bool _isDrafting;

    [ObservableProperty]
    private string _draftLabel = "";

    [ObservableProperty]
    private string? _draftKind;

    public bool IsDraftingSsh => IsDrafting && DraftKind == MachineKinds.Ssh;

    public bool IsDraftingIroh => IsDrafting && DraftKind == MachineKinds.Iroh;

    public bool IsDraftingLocal => IsDrafting && DraftKind == MachineKinds.Local;

    public Func<Task<string?>>? IrohFilePicker { get; set; }

    /** 自动部署偏好; 第一次 Load 时从清单读入, 之后只在"保存部署设置"时写回, 保存机器不会覆盖未保存的修改。 */
    [ObservableProperty]
    private DeploySettingsEditorViewModel? _deploy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NewIrohCommand), nameof(EnableIrohCommand))]
    private bool _irohAvailable;

    /** 部署时无论远端版本都重新部署。 */
    [ObservableProperty]
    private bool _forceDeploy;

    /** 清单被保存或删除了机器; 集成方据此重建常驻连接。 */
    public event Action? MachinesChanged;

    public void Load(string? select = null)
    {
        var config = store.Load();
        IrohAvailable = options.SshTransportAdapter is { Kind: MachineKinds.Iroh };
        Deploy ??= DeploySettingsEditorViewModel.From(config.Deploy);
        var known = Machines.ToDictionary(row => row.Name, StringComparer.Ordinal);
        Machines.Clear();
        foreach (var spec in config.AllMachines())
        {
            var row = new MachineRowViewModel(spec, spec.Name == MachineSpec.LocalName && spec.IsLocal && spec.HarnessHome is null);
            if (known.GetValueOrDefault(spec.Name) is { } previous)
                row.CopyStatusFrom(previous);
            Machines.Add(row);
        }
        RefreshFromFleet();
        Selected = Machines.FirstOrDefault(row => row.Name == select) ?? Machines.FirstOrDefault();
        IsDrafting = false;
        DraftKind = null;
    }

    /** 显示常驻连接的实时状态(FleetClient.Changed 在后台线程触发)。 */
    public void AttachFleet(FleetClient? fleet)
    {
        _fleet?.Changed -= OnFleetChanged;
        _fleet = fleet;
        InvalidateNetwork();
        RemoteOutboundRequiresRefresh = _savedRemoteSettings is not null;
        _fleet?.Changed += OnFleetChanged;
        RefreshFromFleet();
    }

    /** 诊断结果只属于诊断时的那台机器, 换机器或新建时清掉。 */
    partial void OnSelectedChanged(MachineRowViewModel? value)
    {
        if (value is not null)
        {
            IsDrafting = false;
            DraftKind = null;
        }
        DoctorChecks.Clear();
        InvalidateNetwork();
        RemoteOutboundState = null;
        _savedRemoteSettings = null;
        RemoteOutboundUrl = "";
        RemoteOutboundBypass = "";
        RemoteOutboundProbeUrl = "";
        RemoteOutboundRequiresRefresh = false;
        RemoteOutboundStatus = "";
        if (value is not null)
            Editor = value.IsImplicit ? null : MachineEditorViewModel.From(value.Spec, IrohAvailable, IrohExecutable);
        else
            Editor = null;
        RefreshFromFleet();
    }

    [RelayCommand]
    private void NewSsh() => StartNew(MachineKinds.Ssh);

    [RelayCommand(CanExecute = nameof(IrohAvailable))]
    private void NewIroh() => StartNew(MachineKinds.Iroh);

    [RelayCommand]
    private void NewLocal() => StartNew(MachineKinds.Local);

    [RelayCommand]
    private Task SaveAsync() => RunAsync(() =>
    {
        if (Editor is not { } editor)
            return Task.CompletedTask;
        var spec = editor.ToSpec();
        Persist(spec, editor.OriginalName);
        Load(spec.Name);
        Report($"已保存 {spec.Name}（{store.FilePath}）");
        MachinesChanged?.Invoke();
        return Task.CompletedTask;
    });

    [RelayCommand]
    private Task SaveDeploySettingsAsync() => RunAsync(() =>
    {
        if (Deploy is not { } deploy)
            return Task.CompletedTask;
        store.Save(store.Load() with { Deploy = deploy.ToSettings() });
        MachinesChanged?.Invoke();
        return Task.CompletedTask;
    }, "已保存部署设置");

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void Delete()
    {
        if (Selected is not { IsImplicit: false } row)
            return;
        Ask($"从机器清单删除 {row.Name}（{row.Target}）？远端的 dsharp 与会话不受影响。", "删除", () => RunAsync(() =>
        {
            store.Remove(row.Name);
            Load();
            MachinesChanged?.Invoke();
            return Task.CompletedTask;
        }, $"已删除 {row.Name}"));
    }

    private bool CanDelete() => Selected is { IsImplicit: false };

    /** 用表单当前内容(可以是尚未保存的修改)连接一次, 显示 host 版本与平台; 协议不匹配时按当前自动部署设置升级。 */
    [RelayCommand]
    private Task TestConnectionAsync() => RunOperationAsync(async (spec, cancellationToken) =>
    {
        var testOptions = options with { AutoReconnect = false, StartLocalHost = null };
        await using var connection = new MachineConnection(spec, testOptions);
        connection.Start();
        await connection.WaitSettledAsync(cancellationToken);
        RowOf(spec.Name)?.Apply(connection.State, connection.Info, connection.Detail);
        if (connection.State != MachineConnectionState.Connected || connection.Info is not { } info)
            throw new IOException($"{spec.Name}（{spec.Target}）连接失败: {connection.Detail}");
        Log($"{spec.Name}: host {info.HostVersion}，平台 {info.Platform}，harness home {info.HomeRoot}，{connection.Sessions.Count} 个会话");
    });

    /** 手动部署(或升级)远端 dsharp, 按表单里的部署设置(可以尚未保存)逐步输出进度。 */
    [RelayCommand]
    private Task DeployAsync() => RunOperationAsync(async (spec, cancellationToken) =>
    {
        if (spec.IsLocal)
            throw new InvalidOperationException($"{spec.Name} 是本机，运行的就是这份 dsharp，不需要部署");
        var result = await new RemoteDsharpInstaller(new SshTransport(spec, options.SshTransportAdapter), options.SshTransportAdapter).EnsureAsync(Deploy?.ToSettings() ?? store.Load().Deploy, Log, cancellationToken, options.ArchiveFactory, ForceDeploy, options.LocalProxy,
            options.LocalHome ?? new Dsh.Boot.HarnessHome(Path.GetDirectoryName(store.FilePath)!));
        Log(result.AlreadyPresent
            ? $"{spec.Name}: {result.CommandPath} 已是 {HostProtocol.ReleaseVersion} 或更新的版本（勾选“强制重新部署”可覆盖）"
            : $"{spec.Name}: 已部署 {HostProtocol.ReleaseVersion} 到 {result.CommandPath}");
    });

    /** 经当前普通 SSH 配置启用目标端 iroh, 实测隧道可用后才保存转换后的清单。 */
    [RelayCommand(CanExecute = nameof(IrohAvailable))]
    private Task EnableIrohAsync()
    {
        var originalName = Editor?.OriginalName;
        var installOptions = options with { Deploy = Deploy?.ToSettings() ?? store.Load().Deploy };
        return RunOperationAsync(async (spec, cancellationToken) =>
        {
            if (spec.Kind != MachineKinds.Ssh)
                throw new InvalidOperationException("请先选择或填写一台可通过普通 SSH 连接的机器");
            MachineStore.Validate(PlanChange(spec, originalName).Machines);
            var setup = FleetService.IrohSetup
                ?? throw new InvalidOperationException("iroh 插件未加载");
            var converted = await setup.EnableAndSaveAsync(spec, installOptions, converted => Persist(converted, originalName), Log, cancellationToken);
            Load(converted.Name);
            Report($"已启用 {converted.Name} 的 SSH-over-iroh（{converted.EndpointId}）");
            MachinesChanged?.Invoke();
        });
    }

    [RelayCommand]
    private Task RunDoctorAsync() => RunOperationAsync(async (spec, cancellationToken) =>
    {
        var checks = await FleetDoctor.CheckAsync(spec, options, cancellationToken);
        DoctorChecks.Clear();
        foreach (var check in checks)
            DoctorChecks.Add(new DoctorCheckRow(check));
        Log($"{spec.Name}: 诊断完成，{checks.Count(check => check.Outcome == DoctorOutcome.Failed)} 项失败");
    });

    [RelayCommand]
    private void CancelOperation() => _operation?.Cancel();

    private void StartNew(string kind)
    {
        Selected = null;
        IsDrafting = true;
        DraftKind = kind;
        DraftLabel = kind == MachineKinds.Local ? "正在新增本地 Harness Host" : "正在新增机器";
        Editor = new MachineEditorViewModel(IrohAvailable)
        {
            Kind = kind,
            IrohExecutablePath = kind == MachineKinds.Iroh ? IrohExecutable ?? "" : "",
        };
    }

    [RelayCommand]
    private async Task BrowseIrohExecutableAsync()
    {
        if (Editor is null || IrohFilePicker is null || await IrohFilePicker() is not { Length: > 0 } path)
            return;
        Editor.IrohExecutablePath = path;
    }

    private string? IrohExecutable => options.SshTransportAdapter is { Kind: MachineKinds.Iroh } adapter
        ? adapter.FindExecutable()
        : null;

    partial void OnIsDraftingChanged(bool value) => NotifyDraftKind();

    partial void OnDraftKindChanged(string? value) => NotifyDraftKind();

    private void NotifyDraftKind()
    {
        OnPropertyChanged(nameof(IsDraftingSsh));
        OnPropertyChanged(nameof(IsDraftingIroh));
        OnPropertyChanged(nameof(IsDraftingLocal));
    }

    /** 改名时替换原记录且保持位置; 新名称不能与另一台机器重名。 */
    private void Persist(MachineSpec spec, string? originalName) => store.Save(PlanChange(spec, originalName));

    private FleetConfig PlanChange(MachineSpec spec, string? originalName)
    {
        var config = store.Load();
        IrohAvailable = options.SshTransportAdapter is { Kind: MachineKinds.Iroh };
        if (spec.IsIroh && !IrohAvailable && !config.Machines.Any(machine => machine.Name == originalName && machine.IsIroh))
            throw new InvalidOperationException("Iroh requires an installed iroh-ssh command available to this application.");
        if (spec.Name != originalName && config.Machines.Any(machine => machine.Name == spec.Name))
            throw new InvalidOperationException($"已有名为 {spec.Name} 的机器");
        var replaced = originalName is not null && config.Machines.Any(machine => machine.Name == originalName);
        List<MachineSpec> machines = [.. config.Machines.Select(machine => machine.Name == originalName ? spec : machine)];
        if (!replaced)
            machines.Add(spec);
        return config with { Machines = machines };
    }

    /** 测试连接、部署、iroh 引导与诊断作用于表单, 没有表单时作用于选中的隐式本机, 可经 CancelOperation 取消。 */
    private Task RunOperationAsync(Func<MachineSpec, CancellationToken, Task> operation) => RunAsync(async () =>
    {
        var spec = Editor?.ToSpec() ?? Selected?.Spec ?? throw new InvalidOperationException("请先选择或填写一台机器");
        using var cancellation = new CancellationTokenSource();
        _operation = cancellation;
        try
        {
            await operation(spec, cancellation.Token);
        }
        finally
        {
            _operation = null;
        }
    });

    private MachineRowViewModel? RowOf(string name) => Machines.FirstOrDefault(row => row.Name == name);

    private void Log(string line) => OnUi(() => OperationLog.Add(line));

    private void OnFleetChanged() => OnUi(RefreshFromFleet);

    private void RefreshFromFleet()
    {
        if (_fleet is null)
            return;
        foreach (var row in Machines)
        {
            if (_fleet.Find(row.Name) is { } connection)
                row.Apply(connection.State, connection.Info, connection.Detail);
        }
        if (!_networkActive || Selected is not { } selected)
            return;
        var selectedConnection = _fleet.Find(selected.Name);
        var current = selectedConnection?.State == MachineConnectionState.Connected ? selectedConnection.Info : null;
        if (current is null)
        {
            if (_observedRemoteClient is not null)
            {
                InvalidateNetwork();
                RemoteOutboundRequiresRefresh = _savedRemoteSettings is not null;
                RemoteOutboundStatus = "Target Host disconnected. The draft was retained; reconnect only reads current settings.";
            }
            return;
        }
        if (!ReferenceEquals(current, _observedRemoteClient) && _remoteOperation is null)
        {
            _observedRemoteClient = current;
            _ = RunRemoteOutboundAsync(write: false, clear: false, refreshDraft: _savedRemoteSettings is null);
        }
    }

    private void OnUi(Action action)
    {
        if (_context is null || SynchronizationContext.Current == _context)
            action();
        else
            _context.Post(_ => action(), null);
    }
}
