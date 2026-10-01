using Microsoft.Win32.SafeHandles;

namespace Dsh.Tui;

/**
 * Windows 控制台输入记录读取器: 直接用 ReadConsoleInputW 取 MOUSE_EVENT, 不经 SGR 转义序列。
 * ConPTY 的鼠标转义序列翻译受 Windows build 限制, 控制台输入记录是 crossterm/psmux 采用的本地路径。
 * 控制台输入句柄可等待, 与唤醒事件一起 WaitAny: 支持超时(trailing 渲染窗口)与 UI 事件跨线程唤醒。
 * 记录布局与 VK 映射来自共享的 WindowsConsoleRecord(终端代理读宿主终端时用同一份)。
 */
internal sealed class WindowsConsoleInputReader : ITerminalInputSource
{
    private const int WheelUpButton = 64;
    private const int WheelDownButton = 65;
    private const int EscapeFlushMilliseconds = 30;

    private readonly WindowsConsoleRecord.InputRecord[] _records = new WindowsConsoleRecord.InputRecord[64];
    private readonly MouseReportKeyGuard _guard = new();

    private readonly Queue<TerminalInputEvent> _ready = new();
    private readonly nint _handle;
    private readonly ManualResetEvent _consoleReady;
    private readonly ManualResetEvent _wake = new(false);
    private readonly WaitHandle[] _waitHandles;

    private WindowsConsoleInputReader(nint handle)
    {
        _handle = handle;
        _consoleReady = new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(handle, ownsHandle: false) };
        _waitHandles = [_consoleReady, _wake];
    }

    public bool EndOfStream { get; private set; }

    /** 控制台句柄不可用时返回 null, 由调用方回退到 Console.ReadKey。 */
    public static WindowsConsoleInputReader? TryCreate()
    {
        if (Console.IsInputRedirected)
            return null;
        var handle = WindowsConsoleRecord.InputHandle();
        if (handle == 0 || handle == -1 || !WindowsConsoleRecord.TryGetConsoleMode(handle, out _))
            return null;
        return new WindowsConsoleInputReader(handle);
    }

    public TerminalInputEvent? Read(int timeoutMs)
    {
        while (true)
        {
            if (_ready.Count > 0)
                return _ready.Dequeue();
            var wait = _guard.HasPending ? Math.Min(timeoutMs, EscapeFlushMilliseconds) : timeoutMs;
            var signaled = WaitHandle.WaitAny(_waitHandles, wait);
            if (signaled == WaitHandle.WaitTimeout)
            {
                // 静默期: 挂起的 ESC 是用户按下的 Esc, 不是被切开的鼠标报文报头
                foreach (var pending in _guard.Flush())
                    _ready.Enqueue(pending);
                if (_ready.Count > 0)
                    continue;
                return null;   // 空闲: 保持超时语义(渲染循环靠它周期醒来重排/重绘)
            }

            if (signaled == 1)
            {
                _wake.Reset();
                return null;
            }

            if (!WindowsConsoleRecord.ReadConsoleInputW(_handle, _records, (uint)_records.Length, out var count) || count == 0)
            {
                EndOfStream = true;
                return null;
            }

            for (var index = 0; index < count; index++)
            {
                var record = _records[index];
                if (record.EventType == WindowsConsoleRecord.KeyEventType
                    && WindowsConsoleRecord.MapKey(
                        record.KeyEvent.VirtualKeyCode,
                        record.KeyEvent.UnicodeChar,
                        record.KeyEvent.ControlKeyState,
                        record.KeyEvent.KeyDown != 0) is { } key)
                    AcceptKey(key);
                else if (record.EventType == WindowsConsoleRecord.MouseEventType
                    && MapMouse(
                        record.MouseEvent.MousePosition.X,
                        record.MouseEvent.MousePosition.Y,
                        record.MouseEvent.ButtonState,
                        record.MouseEvent.EventFlags) is { } mouse)
                {
                    foreach (var pending in _guard.Flush())
                        _ready.Enqueue(pending);
                    _ready.Enqueue(TerminalInputEvent.FromMouse(mouse));
                }
            }
        }
    }

    /** 普通按键直接放行; 只有可能是鼠标报文报头的按键才进兜底状态机(MouseReportKeyGuard)。 */
    private void AcceptKey(ConsoleKeyInfo key)
    {
        if (!_guard.HasPending && !MouseReportKeyGuard.MayStart(key))
        {
            _ready.Enqueue(TerminalInputEvent.FromKey(key));
            return;
        }

        foreach (var accepted in _guard.Accept(key))
            _ready.Enqueue(accepted);
    }

    public void Wake() => _wake.Set();

    public void Dispose()
    {
        _wake.Dispose();
        _consoleReady.Dispose();
    }

    /** 坐标已是 0 基; 按钮编号沿用 SGR 约定(0 左键 / 1 中键 / 2 右键 / 64/65 滚轮)。 */
    internal static TerminalMouseEvent? MapMouse(short x, short y, uint buttonState, uint eventFlags)
    {
        if ((eventFlags & WindowsConsoleRecord.MouseWheeled) != 0)
        {
            var delta = (short)(buttonState >> 16);
            return new TerminalMouseEvent(x, y, delta >= 0 ? WheelUpButton : WheelDownButton, true);
        }

        if ((eventFlags & WindowsConsoleRecord.MouseMoved) != 0)
        {
            // 放行移动: 拖动选择需要它; 无键移动(button 3)也放行, 渲染侧有 10ms 合并
            var moved = (buttonState & WindowsConsoleRecord.LeftButtonPressed) != 0 ? 0
                : (buttonState & WindowsConsoleRecord.MiddleButtonPressed) != 0 ? 1
                : (buttonState & WindowsConsoleRecord.RightButtonPressed) != 0 ? 2
                : 3;
            return new TerminalMouseEvent(x, y, moved, moved != 3, IsMove: true);
        }

        if ((buttonState & WindowsConsoleRecord.LeftButtonPressed) != 0)
            return new TerminalMouseEvent(x, y, 0, true);
        if ((buttonState & WindowsConsoleRecord.MiddleButtonPressed) != 0)
            return new TerminalMouseEvent(x, y, 1, true);
        if ((buttonState & WindowsConsoleRecord.RightButtonPressed) != 0)
            return new TerminalMouseEvent(x, y, 2, true);
        return new TerminalMouseEvent(x, y, 0, false);
    }
}
