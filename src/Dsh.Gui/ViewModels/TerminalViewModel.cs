using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Dsh.Gui.Services;
using Dsh.Gui.Terminal;
using Dsh.Fleet;
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
        MachineConnection machine,
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

    private static RemotePtyStartRequest RemoteRequest(MachineConnection machine, string workingDirectory, string title)
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

    public async ValueTask DisposeAsync()
    {
        if (Session is not null)
            await Session.DisposeAsync();
        // 主机持有 PTY; 自己创建的终端在关闭窗口时显式停止, 附加的终端只断开视图。
        if (_remote is not null)
            await _remote.DisposeAsync();
    }

    private void Attach(ITerminalConnection connection, string origin)
    {
        _origin = origin;
        var session = new TerminalSession(connection, InitialSize);
        session.Exited += () => Dispatcher.UIThread.Post(() => Status = $"{_origin} · 已退出");
        session.Start();
        Status = origin;
        Session = session;
    }
}
