using System.Threading.Channels;
using Dsh.Core;

namespace Dsh.Tui;

/**
 * shell 窗格: 把 Dsh.Pty 的真 shell 会话渲染成窗格内容, 键盘输入原样写给 PTY。
 * 与 ChatPane(agent 会话)并列放进分割树: 有 shell 焦点时按键不再进 AI 输入行, 行为对齐 tmux 的 "焦点在终端里"。
 */
internal sealed class ShellPane : ITuiPane
{
    private readonly ChatWindow _window;
    private readonly ScrollWheel _wheel = new();
    private readonly PtySession _session;
    private readonly VtScreen _screen;
    /** _screen 的跨线程门: 输出泵(Feed)、UI 线程(Resize/DrawTranscript)、读取工具(SnapshotLines)三方共用。 */
    private readonly object _screenGate = new();
    private readonly CancellationTokenSource _pump = new();
    private readonly Task _pumpTask;
    private readonly Channel<byte[]> _writes = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _writerTask;
    private readonly byte[] _readBuffer = new byte[8192];
    private int _scrollOffset;
    private int _wakePending;
    private int _bytesRead;
    private int _keysSent;
    private bool _disposed;

    public ShellPane(ChatWindow window, int id, PtySession session, int width, int height)
    {
        _window = window;
        _session = session;
        _screen = new VtScreen(Math.Max(1, width), Math.Max(1, height));
        Id = id;
        StatusText = $"shell 已启动: {session.Command} (pid {session.ProcessId})";
        _pumpTask = Task.Run(PumpAsync);
        // 单一写者: 逐键异步写若并发发出, 在 Unix 上会重排/交错(实测 "echo dsh-$(echo pty)" 变成 "ech sdh-$e(ocho")。
        _writerTask = Task.Run(WriteLoopAsync);
    }

    public int Id { get; }

    public TuiPaneKind Kind => TuiPaneKind.Shell;

    /** shell 窗格不绑定 agent 会话。 */
    public Session? Session => null;

    /** 窗格目录/读取工具用: 底层 PTY 会话标识与命令行。 */
    internal string PtyId => _session.Id.Value;

    internal string PtyCommand => _session.Command;

    public bool StickToBottom { get; set; } = true;

    public string PaneTitle => $"shell - {_session.Command}";

    public string StatusText { get; set; } = "";

    public bool Exited { get; private set; }

    public void ProcessSessionEvent(SessionEvent sessionEvent, bool replay = false)
    {
    }

    public void DrawTranscript(CellGrid grid, ConsoleRect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            return;
        lock (_screenGate)
        {
            _screen.Render(grid, rect, _scrollOffset);
            if (_scrollOffset <= 0 && _screen.CursorVisible && rect.Contains(_screen.CursorX, _screen.CursorY))
            {
                var cursor = grid[_screen.CursorX, _screen.CursorY];
                grid[_screen.CursorX, _screen.CursorY] = cursor with { Style = cursor.Style | CellStyle.Reverse };
            }
        }
    }

    public bool TryToggleFoldAt(int cellY, ConsoleRect rect) => false;

    public void HandleMouseWheel(float delta)
    {
        int maximum;
        lock (_screenGate)
            maximum = Math.Max(0, _screen.ScrollbackCount);
        _scrollOffset = Math.Clamp(_scrollOffset + _wheel.Scroll(delta), 0, maximum);
        if (_scrollOffset == 0)
            StickToBottom = true;
        _window.Invalidate();
    }

    public void HandleKey(ConsoleKeyInfo key)
    {
        if (_disposed || Exited)
            return;
        Span<byte> buffer = stackalloc byte[16];
        int length;
        lock (_screenGate)
            length = TerminalKeyEncoder.Encode(key, buffer, _screen.ApplicationCursorKeys);
        if (length == 0)
            return;
        _scrollOffset = 0;
        StickToBottom = true;
        _keysSent++;
        StatusText = $"shell 键入 {_keysSent} 次 - 读取 {_bytesRead} 字节 - {_session.Command} (pid {_session.ProcessId})";
        EnqueueWrite(buffer[..length].ToArray());
    }

    public void HandleText(string text)
    {
        if (_disposed || Exited || text.Length == 0)
            return;
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        _scrollOffset = 0;
        EnqueueWrite(bytes);
    }

    /** 窗格尺寸变化时同步到 PTY, shell 才能按真实宽高折行。 */
    public void Resize(int width, int height)
    {
        lock (_screenGate)
        {
            if (width == _screen.Width && height == _screen.Height)
                return;
            _screen.Resize(width, height);
        }
        if (_disposed || Exited)
            return;
        try
        {
            _session.Resize(height, width);
        }
        catch (Exception)
        {
            // 会话已退出时忽略尺寸同步失败
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _pump.Cancel();
        _writes.Writer.TryComplete();
        _ = _pumpTask;
        _ = _writerTask;
        try
        {
            PtyHost.Default.StopAsync(_session.Id.Value).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // 关闭竞态(进程已退出)不阻塞 UI 拆卸
        }
        _session.Dispose();
        _pump.Dispose();
    }

    /** 滚回+当前屏的纯文本快照(去行尾空白; 屏幕末尾的全空行也去掉, 避免快照被空屏淹没)。 */
    public IReadOnlyList<string> SnapshotLines()
    {
        lock (_screenGate)
        {
            var lines = new List<string>(_screen.ScrollbackCount + _screen.Height);
            for (var offset = _screen.ScrollbackCount - 1; offset >= 0; offset--)
                lines.Add(RowText(_screen.ScrollbackRow(offset)));
            for (var y = 0; y < _screen.Height; y++)
                lines.Add(RowText(_screen.Row(y)));
            while (lines.Count > 0 && lines[^1].Length == 0)
                lines.RemoveAt(lines.Count - 1);
            return lines;
        }
    }

    public bool HasOverlay => false;

    public void DrawOverlay(CellGrid grid, ConsoleRect rect)
    {
    }

    private static string RowText(ReadOnlySpan<Cell> row)
    {
        var chars = new char[row.Length];
        for (var index = 0; index < row.Length; index++)
            chars[index] = row[index].Character;
        return new string(chars).TrimEnd();
    }

    private void EnqueueWrite(byte[] bytes)
        => _writes.Writer.TryWrite(bytes);

    private async Task WriteLoopAsync()
    {
        await foreach (var bytes in _writes.Reader.ReadAllAsync(_pump.Token))
        {
            try
            {
                await _session.WriteAsync(bytes, _pump.Token);
            }
            catch (Exception)
            {
                // 写入失败(会话退出)由读取泵统一标定 Exited
                return;
            }
        }
    }

    private async Task PumpAsync()
    {
        while (!_pump.IsCancellationRequested)
        {
            int read;
            try
            {
                read = await _session.ReadAsync(_readBuffer, _pump.Token);
            }
            catch (Exception error)
            {
                if (!_pump.IsCancellationRequested)
                {
                    StatusText = $"shell 泵中止: {error.Message}";
                    ShowFailure(error);
                }
                break;
            }
            if (read <= 0)
            {
                if (_session.Status == PtySessionStatus.Running)
                {
                    await DelayAsync();
                    continue;
                }
                break;
            }
            _bytesRead += read;
            StatusText = $"shell 读取 {_bytesRead} 字节 - {_session.Command} (pid {_session.ProcessId})";
            lock (_screenGate)
                _screen.Feed(_readBuffer.AsSpan(0, read));
            Invalidate();
        }
        Exited = true;
        Invalidate();
    }

    /** 读取泵失败时把原因显示在窗格里(否则只剩一片空白, 用户与测试都无从判断)。 */
    private void ShowFailure(Exception error)
    {
        lock (_screenGate)
            _screen.Feed($"\r\n\u001b[31m[shell 会话中止] {error.GetType().Name}: {error.Message}\u001b[0m\r\n");
    }

    private async Task DelayAsync()
    {
        try
        {
            await Task.Delay(20, _pump.Token);
        }
        catch (OperationCanceledException)
        {
            // 取消即退出泵
        }
    }

    /** 输出到达后合并唤醒: 只让 TUI 循环重绘一次, 不按块刷屏。 */
    private void Invalidate()
    {
        if (Interlocked.Exchange(ref _wakePending, 1) == 1)
            return;
        _window.QueueAction(() =>
        {
            Interlocked.Exchange(ref _wakePending, 0);
            _window.Invalidate();
        });
    }
}




