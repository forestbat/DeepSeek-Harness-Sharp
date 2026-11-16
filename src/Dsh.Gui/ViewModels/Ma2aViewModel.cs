using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dsh.Ma2a;

namespace Dsh.Gui.ViewModels;

/** The operator console attaches to a Runtime; disposing the page only detaches its local client. */
public sealed partial class Ma2aViewModel : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _observation;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private JsonObject? _pendingCommand;
    private string? _pendingDirectory;
    private string? _stamp;
    private Task? _observer;
    private bool _disposed;
    private const int RefreshIntervalSeconds = 5;

    public Ma2aViewModel()
    {
        _selectedGroup = Groups[0];
        SelectGroup();
        Remote = new Ma2aRemoteViewModel(() => CreateClient());
    }

    public IReadOnlyList<string> Groups { get; } = Ma2aCommandCatalog.Commands.Select(command => command.Group).Distinct().ToArray();
    public ObservableCollection<Ma2aCommandDefinition> Commands { get; } = [];
    public ObservableCollection<Ma2aFieldViewModel> Fields { get; } = [];
    public ObservableCollection<Ma2aResultNode> Snapshot { get; } = [];
    public ObservableCollection<Ma2aResultNode> Result { get; } = [];
    public Ma2aRemoteViewModel Remote { get; }

    [ObservableProperty] private string _stateDirectory = DefaultStateDirectory();
    [ObservableProperty] private string _executable = "ma2a";
    [ObservableProperty] private string _selectedGroup;
    [ObservableProperty] private Ma2aCommandDefinition? _selectedCommand;
    [ObservableProperty] private string _status = "选择 MA2A 状态目录并连接。连接不会启动 Runtime。";
    [ObservableProperty] private string _endpoint = "未连接";
    [ObservableProperty] private bool _connected;
    [ObservableProperty] private bool _confirmed;
    [ObservableProperty] private bool _lifecycleConfirmed;
    [ObservableProperty] private bool _hasPending;
    [ObservableProperty] private string _requestId = "";

    partial void OnStateDirectoryChanged(string value)
    {
        StopObservation();
        _stamp = null;
        Connected = false;
        Snapshot.Clear();
    }

    public bool RequiresConfirmation => SelectedCommand?.RequiresConfirmation ?? false;
    public bool CanExecuteSelected => !HasPending || SelectedCommand?.IsMutation != true;
    public string OperationHint => SelectedCommand?.Operation switch
    {
        "space_grant" => "权限替换该成员的八项授权。授权与本机资源暴露分别管理。",
        "service_expose" or "service_unexpose" => "仅填写所选 kind 对应的字段；其余可选字段保持空值。",
        "space_invite" => "邀请写入 Runtime 所在机器的 output path；邀请内容不会出现在普通状态列表中。",
        "process_delete" => "仅能归档已结束的进程，并删除其输出。",
        "agent_define" => "program 必须实现 MA2A Agent stdio 协议；普通命令行程序不能直接作为 Agent。",
        "graceful_shutdown" => "这会显式停止当前 Runtime。关闭本页面不会执行此操作。",
        _ => "参数由 MA2A 原生协议定义。空值、授权和服务生命周期以 Runtime 的响应为准。",
    };

    partial void OnSelectedGroupChanged(string value) => SelectGroup();
    partial void OnSelectedCommandChanged(Ma2aCommandDefinition? value)
    {
        Fields.Clear();
        foreach (var field in value?.CreateFields() ?? []) Fields.Add(field);
        Confirmed = false;
        OnPropertyChanged(nameof(RequiresConfirmation));
        OnPropertyChanged(nameof(CanExecuteSelected));
        OnPropertyChanged(nameof(OperationHint));
    }

    partial void OnHasPendingChanged(bool value) => OnPropertyChanged(nameof(CanExecuteSelected));

    private void SelectGroup()
    {
        Commands.Clear();
        foreach (var command in Ma2aCommandCatalog.Commands.Where(command => command.Group == SelectedGroup)) Commands.Add(command);
        SelectedCommand = Commands.FirstOrDefault();
    }

    public Ma2aClient CreateClient()
    {
        if (!Path.IsPathFullyQualified(StateDirectory)) throw new InvalidOperationException("状态目录必须为绝对路径。");
        return new Ma2aClient(StateDirectory);
    }

    [RelayCommand]
    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        StopObservation();
        _stamp = null;
        await RefreshAsync(cancellationToken);
        if (!Connected || _disposed) return;
        _observation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _observer = ObserveAsync(_observation.Token);
    }

    [RelayCommand]
    private void Disconnect()
    {
        StopObservation();
        Remote.Cancel();
        Connected = false;
        Snapshot.Clear();
        Endpoint = "未连接";
        Status = "已解除附着；Runtime、持久进程和 Agent 保持运行。";
    }

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (!await _refreshGate.WaitAsync(0, cancellationToken)) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        try { await ReadSnapshotAsync(linked.Token); }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (Exception error) { Invalidate(error); }
        finally { _refreshGate.Release(); }
    }

    private async Task ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        var directory = StateDirectory;
        var client = CreateClient();
        var stamp = await client.CallAsync(new JsonObject { ["operation"] = "snapshot_stamp" }, cancellationToken);
        var fingerprint = Stamp(stamp);
        if (_stamp == fingerprint && Connected) return;
        var snapshot = await client.GetSnapshotAsync(cancellationToken);
        var after = await client.CallAsync(new JsonObject { ["operation"] = "snapshot_stamp" }, cancellationToken);
        if (fingerprint != Stamp(after) || directory != StateDirectory || _disposed)
        {
            Snapshot.Clear();
            _stamp = null;
            Status = "状态在读取期间变化，正在等待重新同步。";
            return;
        }
        var payload = snapshot.GetProperty("result").GetProperty("payload");
        if (snapshot.GetProperty("runtime_boot_id").GetString() != stamp.GetProperty("result").GetProperty("payload").GetProperty("runtime_boot_id").GetString())
            throw new InvalidDataException("MA2A snapshot boot identifier differs from its stamp.");
        if (payload.GetProperty("revision").GetUInt64() != stamp.GetProperty("result").GetProperty("payload").GetProperty("revision").GetUInt64())
            throw new InvalidDataException("MA2A snapshot revision differs from its stamp.");
        ReplaceNodes(Snapshot, payload);
        Endpoint = payload.GetProperty("endpoint").GetProperty("endpoint_id").GetString()!;
        _stamp = fingerprint;
        Connected = true;
        Status = "已连接；状态来自 Runtime 完整快照。";
    }

    private async Task ObserveAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(RefreshIntervalSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken)) await RefreshAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    [RelayCommand]
    private async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (SelectedCommand is not { } command) return;
        if (HasPending && command.IsMutation) { Status = "上次操作的结果尚未确认，请先读取状态或重试同一请求。"; return; }
        if (command.RequiresConfirmation && !Confirmed) { Status = "请先确认所选操作及其参数。"; return; }
        try
        {
            var request = new JsonObject { ["operation"] = command.Operation };
            foreach (var field in Fields) request[field.Name] = field.BuildValue();
            if (command.IsMutation) RequestId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            else if (!HasPending) RequestId = "";
            if (command.IsMutation) request["request_id"] = RequestId;
            await SendAsync(request, StateDirectory, command.IsMutation, cancellationToken);
        }
        catch (Exception error) { Status = error.Message; }
    }

    [RelayCommand]
    private async Task RetryAsync(CancellationToken cancellationToken)
    {
        if (_pendingCommand is null || _pendingDirectory is null) return;
        await SendAsync(_pendingCommand, _pendingDirectory, true, cancellationToken);
    }

    private async Task SendAsync(JsonObject request, string directory, bool mutation, CancellationToken cancellationToken)
    {
        if (!await _commandGate.WaitAsync(0, cancellationToken)) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        try
        {
            var response = await new Ma2aClient(directory).CallAsync(request, linked.Token);
            ReplaceNodes(Result, response.GetProperty("result"));
            if (mutation) ClearPending();
            Status = "操作已完成。";
            _stamp = null;
            if (Connected) await RefreshAsync(linked.Token);
        }
        catch (Exception error)
        {
            if (mutation) { _pendingCommand = (JsonObject)request.DeepClone(); _pendingDirectory = directory; HasPending = true; }
            Status = mutation ? $"结果尚未确认；保留相同 RequestId，读取状态后再决定。{error.Message}" : error.Message;
        }
        finally { _commandGate.Release(); }
    }

    [RelayCommand]
    private void AcknowledgePending()
    {
        if (!Confirmed) { Status = "核实 Runtime 状态后，勾选操作确认以解除未知结果提示。"; return; }
        ClearPending();
        Status = "已确认上次结果；下一次执行是独立的新请求。";
    }

    private void ClearPending()
    {
        _pendingCommand = null;
        _pendingDirectory = null;
        HasPending = false;
    }

    [RelayCommand]
    private async Task LifecycleAsync(string operation)
    {
        if (operation is not ("start" or "stop" or "restart")) return;
        if (!LifecycleConfirmed) { Status = "请确认显式改变 Runtime 生命周期。"; return; }
        try
        {
            _ = CreateClient();
            var start = new ProcessStartInfo(Executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            start.ArgumentList.Add("--state-dir");
            start.ArgumentList.Add(StateDirectory);
            start.ArgumentList.Add(operation);
            using var process = Process.Start(start) ?? throw new IOException("Unable to start MA2A lifecycle command.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await WaitForLauncherAsync(process, _lifetime.Token);
            var standardOutput = await output;
            var standardError = await error;
            _lifetime.Token.ThrowIfCancellationRequested();
            LifecycleConfirmed = false;
            if (process.ExitCode != 0) { Status = string.IsNullOrWhiteSpace(standardError) ? $"MA2A exit {process.ExitCode}: {standardOutput}" : standardError; return; }
            if (operation == "stop") { Disconnect(); Status = "Runtime 已停止。"; }
            else await ConnectAsync(_lifetime.Token);
        }
        catch (Exception error) { Status = error.Message; }
    }

    private static async Task WaitForLauncherAsync(Process process, CancellationToken cancellationToken)
    {
        using var cancellation = cancellationToken.Register(() => StopLauncher(process));
        await process.WaitForExitAsync();
    }

    private static void StopLauncher(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: false); }
        catch (InvalidOperationException) when (process.HasExited) { }
    }

    private void Invalidate(Exception error)
    {
        _stamp = null;
        Connected = false;
        Snapshot.Clear();
        Endpoint = "状态不可用";
        Status = error.Message;
    }

    private static string Stamp(JsonElement response) => response.GetProperty("result").GetProperty("payload").GetRawText();

    private static void ReplaceNodes(ObservableCollection<Ma2aResultNode> target, JsonElement value)
    {
        target.Clear();
        foreach (var item in Ma2aResultNode.ChildrenOf(value)) target.Add(item);
    }

    private static string DefaultStateDirectory() => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ma2a")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state", "ma2a");

    private void StopObservation()
    {
        _observation?.Cancel();
        _observation?.Dispose();
        _observation = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopObservation();
        _lifetime.Cancel();
        Remote.Dispose();
        _lifetime.Dispose();
    }
}

public sealed record Ma2aResultNode(string Label, IReadOnlyList<Ma2aResultNode> Children)
{
    public static IReadOnlyList<Ma2aResultNode> ChildrenOf(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().Select(item => Create(item.Name, item.Value)).ToArray(),
        JsonValueKind.Array => value.EnumerateArray().Select((item, index) => Create($"[{index}]", item)).ToArray(),
        _ => [Create("value", value)],
    };

    private static Ma2aResultNode Create(string name, JsonElement value) => value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
        ? new(name.Replace('_', ' '), ChildrenOf(value)) : new($"{name.Replace('_', ' ')}: {value}", []);
}
