using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dsh.Fleet;
using Dsh.Fleet.Iroh;
using Dsh.RemoteHost;

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

    public ObservableCollection<MachineRowViewModel> Machines { get; } = [];

    /** 部署进度(RemoteDsharpInstaller 的每一步)与测试连接的结果, 按时间顺序。 */
    public ObservableCollection<string> OperationLog { get; } = [];

    public ObservableCollection<DoctorCheckRow> DoctorChecks { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    private MachineRowViewModel? _selected;

    [ObservableProperty]
    private MachineEditorViewModel? _editor;

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
        IrohAvailable = options.SshTransportAdapter is { Kind: MachineKinds.Iroh, IsAvailable: true };
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
    }

    /** 显示常驻连接的实时状态(FleetClient.Changed 在后台线程触发)。 */
    public void AttachFleet(FleetClient? fleet)
    {
        _fleet?.Changed -= OnFleetChanged;
        _fleet = fleet;
        _fleet?.Changed += OnFleetChanged;
        RefreshFromFleet();
    }

    /** 诊断结果只属于诊断时的那台机器, 换机器或新建时清掉。 */
    partial void OnSelectedChanged(MachineRowViewModel? value)
    {
        DoctorChecks.Clear();
        if (value is not null)
            Editor = value.IsImplicit ? null : MachineEditorViewModel.From(value.Spec, IrohAvailable);
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

    /** 用表单当前内容(可以是尚未保存的修改)连接一次, 显示 host 版本与平台。 */
    [RelayCommand]
    private Task TestConnectionAsync() => RunOperationAsync(async (spec, cancellationToken) =>
    {
        var testOptions = options with { AutoReconnect = false, StartLocalHost = null, Deploy = options.Deploy with { AutoDeploy = false } };
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
            ? $"{spec.Name}: {result.CommandPath} 已是 {RemoteHostServer.ReleaseVersion} 或更新的版本（勾选“强制重新部署”可覆盖）"
            : $"{spec.Name}: 已部署 {RemoteHostServer.ReleaseVersion} 到 {result.CommandPath}");
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
            var converted = await IrohRemoteInstaller.EnableAndSaveAsync(spec, installOptions, converted => Persist(converted, originalName), Log, cancellationToken);
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
        Editor = new MachineEditorViewModel(IrohAvailable) { Kind = kind };
    }

    /** 改名时替换原记录且保持位置; 新名称不能与另一台机器重名。 */
    private void Persist(MachineSpec spec, string? originalName) => store.Save(PlanChange(spec, originalName));

    private FleetConfig PlanChange(MachineSpec spec, string? originalName)
    {
        var config = store.Load();
        IrohAvailable = options.SshTransportAdapter is { Kind: MachineKinds.Iroh, IsAvailable: true };
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
    }

    private void OnUi(Action action)
    {
        if (_context is null || SynchronizationContext.Current == _context)
            action();
        else
            _context.Post(_ => action(), null);
    }
}
