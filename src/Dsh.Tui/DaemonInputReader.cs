using System.Net.Sockets;
using System.Threading.Channels;

namespace Dsh.Tui;

/**
 * 常驻 TUI 的输入旁路读取器: 键盘/粘贴字节来自 daemon(代理或窗口送进来的终端原始字节), 与 Unix 的 fd 0 走同一个
 * TerminalInputParser, 因此 ESC[200~ 粘贴、CSI-u / modifyOtherKeys 修饰键、中文标点都是同一条解码路径。
 *
 * 为什么不读控制台记录: 记录是从终端字节翻译出来的二手形态, 实测这条路上中文引号/省略号这类东亚歧义标点、
 * 带修饰键的按键编码与括号粘贴标记都无法完整到达(表现就是"标点打不进去""Shift+Enter 没反应""多行粘贴变多条消息")。
 * 鼠标仍由代理注入控制台记录, 所以记录这一路只取鼠标事件。
 */
internal sealed class DaemonInputReader : ITerminalInputSource
{
    /** 挂起的孤立 ESC 等这么久没有后续字节就当用户按了 Esc。 */
    private const int EscapeFlushMilliseconds = 30;

    /** 空闲时的读超时: 只为及时看到停止信号, 不影响延迟(有字节立刻返回)。 */
    private const int IdleReadMilliseconds = 250;

    private const int MouseRecordReadMilliseconds = 200;

    private readonly Stream _bytes;
    private readonly WindowsConsoleInputReader? _records;
    private readonly TerminalInputParser _parser = new();
    private readonly Channel<TerminalInputEvent> _ready = Channel.CreateUnbounded<TerminalInputEvent>();
    private readonly ManualResetEvent _signal = new(false);
    private readonly CancellationTokenSource _stop = new();
    private bool _wakePending;
    private bool _disposed;

    internal DaemonInputReader(Stream bytes, WindowsConsoleInputReader? records)
    {
        _bytes = bytes;
        _records = records;
        _ = Task.Factory.StartNew(PumpBytes, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        if (records is not null)
            _ = Task.Factory.StartNew(PumpRecords, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public bool EndOfStream { get; private set; }

    /** 输入是终端原始字节: 终端要按 kitty/modifyOtherKeys 编码修饰键时我们认得出(见 TerminalInputModes)。 */
    public bool DecodesEscapeSequences => true;

    /**
     * 给常驻 TUI 挂上输入旁路; 拿不到(不是 daemon 托管的会话, 或 daemon 版本不认识这条路)时返回 false,
     * 调用方退回平台默认输入源。鼠标那一路尽力而为: 建不出来只影响鼠标, 键盘与粘贴照常。
     */
    internal static bool TryOpen(out DaemonInputReader? reader, out string reason)
    {
        reader = null;
        var sessionId = Environment.GetEnvironmentVariable(PtySessionProtocol.SessionVariable);
        if (string.IsNullOrEmpty(sessionId))
        {
            reason = $"{PtySessionProtocol.SessionVariable} is not set (not a daemon-hosted session)";
            return false;
        }

        Stream channel;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            channel = PtyDaemonClient.OpenInputChannelAsync(sessionId, timeout.Token).GetAwaiter().GetResult();
        }
        catch (Exception error)
        {
            reason = $"{error.GetType().Name}: {error.Message}";
            return false;
        }

        WindowsConsoleInputReader? records = null;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                records = WindowsConsoleInputReader.TryCreate();
            }
            catch (Exception)
            {
                records = null;
            }
        }

        reader = new DaemonInputReader(channel, records);
        reason = "";
        return true;
    }

    public TerminalInputEvent? Read(int timeoutMs)
    {
        if (Volatile.Read(ref _wakePending))
        {
            Volatile.Write(ref _wakePending, false);
            return null;
        }

        if (_ready.Reader.TryRead(out var immediate))
            return immediate;
        _signal.WaitOne(timeoutMs);
        _signal.Reset();
        if (Volatile.Read(ref _wakePending))
        {
            Volatile.Write(ref _wakePending, false);
            return null;
        }

        return _ready.Reader.TryRead(out var next) ? next : null;
    }

    public void Wake()
    {
        Volatile.Write(ref _wakePending, true);
        _signal.Set();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _stop.Cancel();
        _signal.Set();
        _bytes.Dispose();
        _records?.Dispose();
        _ready.Writer.TryComplete();
        _signal.Dispose();
    }

    /** 终端字节泵: 按协议解码成按键/粘贴/鼠标事件; 读超时用来冲刷挂起的孤立 ESC。 */
    private void PumpBytes()
    {
        var buffer = new byte[4096];
        var timed = _bytes.CanTimeout;
        while (!_stop.IsCancellationRequested)
        {
            var timeout = _parser.HasPendingEscape ? EscapeFlushMilliseconds : IdleReadMilliseconds;
            if (timed)
            {
                try
                {
                    _bytes.ReadTimeout = timeout;
                }
                catch (InvalidOperationException)
                {
                    timed = false;
                }
            }

            int read;
            try
            {
                read = _bytes.Read(buffer, 0, buffer.Length);
            }
            catch (IOException error) when (error.InnerException is SocketException { SocketErrorCode: SocketError.TimedOut })
            {
                FlushPendingEscape();
                continue;
            }
            catch (Exception)
            {
                EndOfStream = true;
                _signal.Set();
                return;
            }

            if (read <= 0)
            {
                EndOfStream = true;
                _signal.Set();
                return;
            }

            _parser.Append(buffer.AsSpan(0, read));
            while (_parser.TryParse(out var input))
                Publish(input);
        }
    }

    /** 孤立 ESC 在读超时后当按键交出: 那说明后续字节没有跟上, 用户按的就是 Esc。 */
    private void FlushPendingEscape()
    {
        if (_parser.TryFlushPendingEscape(out var escape))
            Publish(escape);
    }

    /** 记录泵: 只转发鼠标(键盘与粘贴走字节旁路, 记录里那份是同一批按键的降级翻译)。 */
    private void PumpRecords()
    {
        var records = _records!;
        while (!_stop.IsCancellationRequested)
        {
            TerminalInputEvent? input;
            try
            {
                input = records.Read(MouseRecordReadMilliseconds);
            }
            catch (Exception)
            {
                return;
            }

            if (input is null)
            {
                if (records.EndOfStream)
                    return;
                continue;
            }

            if (input.Value.IsMouse)
                Publish(input.Value);
        }
    }

    private void Publish(TerminalInputEvent input)
    {
        _ready.Writer.TryWrite(input);
        _signal.Set();
    }
}

