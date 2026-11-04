using System.Threading.Channels;
using Dsh.Core;

namespace Dsh.Tui;

/**
 * 终端窗格: 把 Dsh.Pty 的会话渲染成窗格内容, 键盘输入原样写给 PTY。
 * 底层既可以是本进程的 shell 会话, 也可以是 daemon pty 的窗格级订阅(跨进程远程), 见 ITerminalBacking。
 * 与 ChatPane(agent 会话)并列放进分割树: 有终端焦点时按键不再进 AI 输入行, 行为对齐 tmux 的 "焦点在终端里"。
 *
 * 另外它还承载"某个 pty 这一层"的视图(layerPtyId 非空): 树是 pty > session > pane, pty 是 session 的上一层,
 * 进入 pty 就是进入这一层 —— 内容是一个能跑普通命令的真终端, 而 Ctrl+X 仍归 DSH(与 tmux 空 session 同形)。
 * 这一层可能拉不起 shell(受限环境/GPU 模式等): 此时 backing 为 null, 窗格只剩状态行, 但按键照常走 ChatWindow,
 * 因此 Ctrl+X 快捷键依旧可用。
 */
internal sealed class ShellPane : ITuiPane
{
    private readonly ChatWindow _window;
    private readonly ScrollWheel _wheel = new();
    private readonly ITerminalBacking? _backing;
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
    private string _status = "";
    private bool _exited;
    private bool _disposed;

    public ShellPane(ChatWindow window, int id, ITerminalBacking? backing, int width, int height, string? layerPtyId = null)
    {
        _window = window;
        _backing = backing;
        LayerPtyId = layerPtyId;
        _screen = new VtScreen(Math.Max(1, width), Math.Max(1, height));
        _screen.Reply += reply =>
        {
            if (_window.ShellStartupStage is { } observe)
            {
                observe(5);
                if (reply.EndsWith('R'))
                    observe(18);
                else if (reply.EndsWith('c'))
                    observe(19);
            }
            EnqueueWrite(System.Text.Encoding.UTF8.GetBytes(reply));
        };
        if (_window.ShellStartupStage is not null)
            _screen.ColorQueryObserved += code => _window.ShellStartupStage?.Invoke(code == 10 ? 21 : 22);
        Id = id;
        StatusText = layerPtyId is { Length: > 0 }
            ? $"[pty {layerPtyId}] 层 - {window.KeyBindings.Keys(TuiAction.Overview)} 总览 - {window.KeyBindings.Keys(TuiAction.PrevSession, TuiAction.NextSession)} 切该 pty 的会话 - {window.KeyBindings.Keys(TuiAction.Detach)} 离开 DSH"
            : _backing?.ProcessId is { } pid
                ? $"终端已启动: {_backing.Command} (pid {pid})"
                : $"终端已连接: {_backing?.Command ?? "(无 shell)"}";
        if (layerPtyId is { Length: > 0 })
            LayerBanner = StatusText;
        _pumpTask = Task.Run(PumpAsync);
        // 单一写者: 逐键异步写若并发发出, 在 Unix 上会重排/交错(实测 "echo dsh-$(echo pty)" 变成 "ech sdh-$e(ocho")。
        _writerTask = Task.Run(WriteLoopAsync);
    }

    public int Id { get; }

    public TuiPaneKind Kind => TuiPaneKind.Shell;

    /** 终端窗格不绑定 agent 会话。 */
    public Session? Session => null;

    /** 非空 = 这一屏代表"某个 pty 这一层"(而不是某个会话); 状态行与窗格归属都用它。 */
    internal string? LayerPtyId { get; }

    /**
     * pty 层视图的横幅(显示在底部状态栏)。非空时优先于终端字节/按键状态 —— 否则 shell 一有输出就会
     * 把"你在 pty 层"这条信息顶掉(见 PumpAsync 里对 StatusText 的更新)。
     */
    internal string? LayerBanner { get; set; }

    /** 窗格目录/读取工具用: 底层 PTY 会话标识与命令行(pty 层视图报告的是它所代表的那个 pty)。 */
    internal string PtyId => LayerPtyId ?? _backing?.PtyId ?? "";

    internal string PtyCommand => LayerPtyId is { Length: > 0 } ? "pty-layer" : _backing?.Command ?? "";

    public bool StickToBottom { get; set; } = true;

    public string PaneTitle => LayerPtyId is { Length: > 0 } layer
        ? $"pty 层 - {layer}"
        : $"shell - {_backing?.Command ?? "(无 shell)"}";

    public string StatusText
    {
        get => LayerBanner ?? _status;
        set => _status = value;
    }

    public bool Exited => _exited;

    public void ProcessSessionEvent(SessionEvent sessionEvent, bool replay = false)
    {
    }

    public void DrawTranscript(CellGrid grid, ConsoleRect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            return;
        _window.ShellStartupStage?.Invoke(10);
        lock (_screenGate)
        {
            _screen.Render(grid, rect, _scrollOffset);
            _window.ShellStartupStage?.Invoke(16);
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
        if (_disposed || _exited || _backing is null)
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
        StatusText = $"终端键入 {_keysSent} 次 - 读取 {_bytesRead} 字节 - {_backing.Command}";
        EnqueueWrite(buffer[..length].ToArray());
    }

    public void HandleText(string text)
    {
        if (_disposed || _exited || _backing is null || text.Length == 0)
            return;
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        _scrollOffset = 0;
        EnqueueWrite(bytes);
    }

    /** 窗格尺寸变化时同步到底层 PTY, 终端才能按真实宽高折行。 */
    public void Resize(int width, int height)
    {
        lock (_screenGate)
        {
            if (width == _screen.Width && height == _screen.Height)
                return;
            _screen.Resize(width, height);
        }
        if (_disposed || _exited || _backing is null)
            return;
        _window.ShellStartupStage?.Invoke(8);
        _backing.Resize(height, width);
        _window.ShellStartupStage?.Invoke(9);
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
        _backing?.Dispose();
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
            if (_backing is null)
                continue;
            try
            {
                _window.ShellStartupStage?.Invoke(6);
                await _backing.WriteAsync(bytes, _pump.Token);
                _window.ShellStartupStage?.Invoke(7);
            }
            catch (Exception)
            {
                _window.ShellStartupStage?.Invoke(14);
                // 写入失败(会话退出)由读取泵统一标定 Exited
                return;
            }
        }
    }

    private async Task PumpAsync()
    {
        _window.ShellStartupStage?.Invoke(1);
        // 拉不起 shell 的 pty 层(受限环境/GPU 模式): 没有输出可泵, 只保持窗格存活, 按键仍由 ChatWindow 处理。
        if (_backing is null)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, _pump.Token);
            }
            catch (OperationCanceledException)
            {
            }

            return;
        }

        while (!_pump.IsCancellationRequested)
        {
            int read;
            try
            {
                _window.ShellStartupStage?.Invoke(2);
                read = await _backing.ReadAsync(_readBuffer, _pump.Token);
            }
            catch (Exception error)
            {
                _window.ShellStartupStage?.Invoke(13);
                if (!_pump.IsCancellationRequested)
                {
                    StatusText = $"终端泵中止: {error.Message}";
                    ShowFailure(error);
                }
                break;
            }
            if (read <= 0)
            {
                _window.ShellStartupStage?.Invoke(12);
                if (!_backing.Exited)
                {
                    await DelayAsync();
                    continue;
                }
                break;
            }
            _bytesRead += read;
            _window.ShellStartupStage?.Invoke(3);
            StatusText = $"终端读取 {_bytesRead} 字节 - {_backing.Command}";
            lock (_screenGate)
                _screen.Feed(_readBuffer.AsSpan(0, read));
            _window.ShellStartupStage?.Invoke(4);
            Invalidate();
        }
        _exited = true;
        Invalidate();
    }

    /** 读取泵失败时把原因显示在窗格里(否则只剩一片空白, 用户与测试都无从判断)。 */
    private void ShowFailure(Exception error)
    {
        lock (_screenGate)
            _screen.Feed($"\r\n\u001b[31m[终端会话中止] {error.GetType().Name}: {error.Message}\u001b[0m\r\n");
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
