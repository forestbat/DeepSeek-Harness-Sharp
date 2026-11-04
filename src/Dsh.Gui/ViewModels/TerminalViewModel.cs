using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Dsh.Gui.Services;
using Dsh.Gui.Terminal;
using Dsh.Workspace;
using Dsh.Core;
using Dsh.Pty;
using Dsh.RemoteHost;

namespace Dsh.Gui.ViewModels;

/** GUI 终端窗口: 本地 PTY 与远端 PTY 都包成 TerminalSession 交给终端控件, 只换底层连接(§14 C)。 */
public sealed partial class TerminalViewModel : ObservableObject, IAsyncDisposable
{
    /** 启动时的 PTY 尺寸; 控件完成布局后立即按实际格子数 resize。 */
    private static readonly TerminalSize InitialSize = new(80, 24);

    private RemotePtyConnection? _remote;
    private string _origin = "";
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource<int?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IWorkspaceMachine? _editorMachine;
    private bool _disposed;
    private readonly Lock _disposeGate = new();
    private Task? _disposal;

    public Task<int?> Completion => _completion.Task;
    public string? PtyId => _remote?.PtyId;
    public bool ActivateOnOpen { get; init; } = true;

    public async Task StartEditorAsync(IWorkspaceMachine machine, EditorLaunchCommand command, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        _editorMachine = machine;
        machine.SessionsChanged += ObserveEditorExit;
        try
        {
            var connection = await RemotePtyConnection.StartAsync(machine, new RemotePtyStartRequest(
                command.Executable, command.Arguments, command.WorkingDirectory,
                InitialSize.Columns, InitialSize.Rows, Title, Environment: command.Environment), linked.Token);
            if (_disposed || linked.IsCancellationRequested)
            {
                await connection.DisposeAsync();
                linked.Token.ThrowIfCancellationRequested();
                throw new ObjectDisposedException(nameof(TerminalViewModel));
            }
            AttachRemote(connection, $"{machine.Machine.Name} · {command.Editor.Value}");
            ObserveEditorExit(machine);
        }
        catch
        {
            machine.SessionsChanged -= ObserveEditorExit;
            _editorMachine = null;
            throw;
        }
    }

    private void ObserveEditorExit(IWorkspaceMachine machine)
    {
        if (_remote is null) return;
        var summary = machine.Sessions.FirstOrDefault(session => session.Id == _remote.PtyId);
        if (summary?.ExitCode is not { } exitCode) return;
        machine.SessionsChanged -= ObserveEditorExit;
        _completion.TrySetResult(exitCode);
        Dispatcher.UIThread.Post(() => Status = $"{_origin} · exit {exitCode}");
    }

    public TerminalViewModel(string title) => Title = title;

    public string Title { get; }

    [ObservableProperty]
    private TerminalSession? _session;

    [ObservableProperty]
    private string _status = "正在启动…";

    /** 子进程经 OSC 设置的标题(shell 提示符、vim 文件名等)。 */
    [ObservableProperty]
    private string _screenTitle = "";

    /** 本地机器也经常驻 host/PTY daemon 启动, 与 SSH 终端共享会话登记和断线存活语义。 */
    public async Task StartOnMachineAsync(
        IWorkspaceMachine machine,
        string workingDirectory,
        string? title = null,
        CancellationToken cancellationToken = default)
    {
        var request = machine.Machine.IsLocal
            ? LocalRequest(workingDirectory)
            : RemoteRequest(machine, workingDirectory, title ?? $"{machine.Machine.Name} · 终端");
        var connection = await RemotePtyConnection.StartAsync(machine, request, cancellationToken);
        AttachRemote(connection, machine.Machine.IsLocal ? $"本地 · {request.FileName}" : $"{machine.Machine.Name} · 终端");
    }

    private static RemotePtyStartRequest LocalRequest(string workingDirectory)
    {
        var shell = PtyShell.Resolve();
        return new RemotePtyStartRequest(
            shell,
            PtyShell.Arguments(shell),
            workingDirectory,
            Columns: InitialSize.Columns,
            Rows: InitialSize.Rows);
    }

    private static RemotePtyStartRequest RemoteRequest(IWorkspaceMachine machine, string workingDirectory, string title)
    {
        var windows = machine.Info?.Platform == "windows";
        return new RemotePtyStartRequest(
            windows ? "cmd.exe" : "/bin/sh",
            windows ? [] : ["-c", "exec \"${SHELL:-/bin/sh}\" -l"],
            Cwd: workingDirectory,
            Title: title);
    }

    /** Remote terminal: a PTY on a fleet machine, attached through the machine's connection; the view model disposes the connection. */
    public void AttachRemote(RemotePtyConnection connection, string origin)
    {
        _remote = connection;
        Attach(connection, origin);
    }

    /** 控件按自身尺寸算出新的格子数后回调, 用于状态栏显示。 */
    public void OnGridResized(TerminalSize size) => Status = $"{_origin} · {size.Columns}x{size.Rows}";

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) return new(_disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _lifetime.CancelAsync();
        if (_editorMachine is not null) _editorMachine.SessionsChanged -= ObserveEditorExit;
        _completion.TrySetResult(null);
        if (Session is not null)
            await Session.DisposeAsync();
        // 主机持有 PTY; 自己创建的终端在关闭窗口时显式停止, 附加的终端只断开视图。
        if (_remote is not null)
            await _remote.DisposeAsync();
        _lifetime.Dispose();
    }

    private void Attach(ITerminalConnection connection, string origin)
    {
        _origin = origin;
        var session = new TerminalSession(connection, InitialSize);
        session.Exited += () => Dispatcher.UIThread.Post(() => Status = _completion.Task.IsCompletedSuccessfully && _completion.Task.Result is { } code
            ? $"{_origin} · exit {code}" : $"{_origin} · 已退出");
        session.Start();
        Status = origin;
        Session = session;
    }
}
