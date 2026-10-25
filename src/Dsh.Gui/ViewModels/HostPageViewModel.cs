using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dsh.Fleet;
using Dsh.Gui.Services;
using Dsh.RemoteHost;

namespace Dsh.Gui.ViewModels;

/**
 * A workspace page that works on one machine's host, such as files or Git: the user picks the machine and the path, the page builds its content on that machine's connection.
 * The content holds the client of the link it was built on, so it is rebuilt after every reconnection and after the machine list was reloaded.
 * A host without the page's capability gets an explanation instead of content.
 */
public sealed partial class HostPageViewModel : ObservableObject, IDisposable
{
    private readonly FleetWorkspace _fleet;
    private readonly string _capability;
    private readonly Func<RemoteHostClient, string, string, RemotePathStyle, Task<OperationViewModel>> _build;
    private readonly Func<Task<string?>>? _folderPicker;
    private readonly Action<string, string>? _pathSelected;
    private CancellationTokenSource? _run;
    private MachineConnection? _connection;

    /** build receives the connected client, machine, path and the connection's already-known path style. */
    public HostPageViewModel(
        FleetWorkspace fleet,
        string title,
        string capability,
        string machine,
        string path,
        Func<RemoteHostClient, string, string, RemotePathStyle, Task<OperationViewModel>> build,
        Func<Task<string?>>? folderPicker = null,
        Action<string, string>? pathSelected = null)
    {
        (_fleet, _capability, _build, Title, _folderPicker, _pathSelected) = (fleet, capability, build, title, folderPicker, pathSelected);
        _machine = machine;
        _path = path;
        RefreshMachineNames();
        _fleet.Changed += OnFleetChanged;
        _ = OpenAsync();
    }

    public string Title { get; }

    public ObservableCollection<string> MachineNames { get; } = [];

    [ObservableProperty]
    private string _machine;

    [ObservableProperty]
    private string _path;

    public bool IsPathReadOnly => _folderPicker is not null;

    [ObservableProperty]
    private OperationViewModel? _content;

    /** Why there is no content: waiting for the machine, a missing path, or a host without the capability. */
    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    private bool _isOpening;

    /** Opens the chosen machine and path, dropping the current content. */
    [RelayCommand]
    private async Task OpenAsync()
    {
        if (IsOpening)
            return;
        _run?.Cancel();
        _run?.Dispose();
        _run = new CancellationTokenSource();
        _connection = _fleet.Find(Machine);
        IsOpening = true;
        await RunAsync(_connection, Machine, Path.Trim(), _run.Token);
    }

    /** The visible Open button: Git picks a repository folder instead of asking for a typed path. */
    [RelayCommand]
    private async Task PickAndOpenAsync()
    {
        if (_folderPicker is null)
        {
            await OpenAsync();
            return;
        }
        var path = await _folderPicker();
        if (string.IsNullOrWhiteSpace(path))
            return;
        Path = path;
        _pathSelected?.Invoke(Machine, path);
        await OpenAsync();
    }

    public void Dispose()
    {
        _fleet.Changed -= OnFleetChanged;
        _run?.Cancel();
        _run?.Dispose();
        _run = null;
        Content = null;
    }

    private async Task RunAsync(MachineConnection? connection, string machine, string path, CancellationToken cancellationToken)
    {
        Content = null;
        if (connection is null)
        {
            Status = $"没有名为 {machine} 的机器";
            IsOpening = false;
            return;
        }
        if (path.Length == 0)
        {
            Status = _folderPicker is null ? "请输入路径后打开" : "点击“打开”选择 Git 仓库目录";
            IsOpening = false;
            return;
        }
        try
        {
            connection.Start();
            while (!cancellationToken.IsCancellationRequested)
                await ServeLinkAsync(connection, machine, path, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            if (!cancellationToken.IsCancellationRequested)
                Status = $"打开失败: {error.Message}";
        }
        finally
        {
            IsOpening = false;
        }
    }

    /** One link of the machine: the content lives until that link drops. */
    private async Task ServeLinkAsync(MachineConnection connection, string machine, string path, CancellationToken cancellationToken)
    {
        Content = null;
        Status = $"等待 {machine} 连接…";
        HostLease lease;
        try
        {
            lease = await connection.WaitConnectedAsync(cancellationToken);
        }
        finally
        {
            IsOpening = false;
        }
        if (lease.Client.Supports(_capability))
        {
            var style = RemotePaths.StyleFor(connection.Info!.Platform);
            var content = await _build(lease.Client, machine, path, style);
            cancellationToken.ThrowIfCancellationRequested();
            (Content, Status) = (content, null);
        }
        else
            Status = $"{machine} 上的 host {lease.Client.Hello.HostVersion} 不支持 {_capability}，请先升级该机器上的 dsharp";
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.Lifetime);
        try
        {
            await Task.Delay(Timeout.Infinite, linked.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
    }

    /** The machine list was reloaded: the page follows the new connection of its machine. */
    private void OnFleetChanged()
    {
        RefreshMachineNames();
        if (!ReferenceEquals(_fleet.Find(Machine), _connection))
            _ = OpenAsync();
    }

    private void RefreshMachineNames()
    {
        List<string> names = [.. _fleet.Client.Machines.Select(machine => machine.Machine.Name)];
        if (names.SequenceEqual(MachineNames))
            return;
        // Clearing the list makes the machine picker push an empty selection back; the chosen machine is kept.
        var machine = Machine;
        MachineNames.Clear();
        foreach (var name in names)
            MachineNames.Add(name);
        Machine = machine;
    }
}
