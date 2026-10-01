using System.Collections.Concurrent;
using System.Text;
using Dsh.Pty;
using OpenTK.Graphics.OpenGL;

namespace Dsh.Tui;

/**
 * GPU 形态的 proxy: 会话同样常驻 daemon(一个 pty 上的 TUI 进程), 本进程只开一个独立窗口渲染会话画面。
 * 窗口的键鼠编码成终端输入送进隧道, 隧道画面喂给 VtScreen 再由 GpuRenderCore 画到窗口。
 * 于是"关窗口"就是 detach: 本 proxy 结束, daemon 里的会话继续跑(`dsh tui attach` 或 `--gpu` 都能再接回)。
 */
internal sealed class GpuSessionProxy : IGpuHostClient
{
    /** SGR 鼠标报文终止符前/后的修饰位: 按下用 M、释放用 m。 */
    private const int MouseReleaseButton = 3;
    private const int MouseWheelUp = 64;
    private const int MouseWheelDown = 65;

    private readonly string _sessionId;
    private readonly GlyphAtlas _atlas;
    private readonly IGlSurfaceHostRunner _host;
    private readonly GpuRenderCore _core = new();
    private readonly VtScreen _screen;
    private readonly ConcurrentQueue<byte[]> _input = new();
    private readonly SemaphoreSlim _inputSignal = new(0);
    private readonly CancellationTokenSource _tunnel = new();
    private readonly List<(int Start, int Count)> _dirty = [];
    private CellGrid _grid;
    private CellGrid? _lastGrid;
    private uint[] _packed;
    private int _mouseCellX;
    private int _mouseCellY;
    private bool _leftDown;
    private volatile bool _sessionEnded;

    public GpuSessionProxy(string sessionId, GlyphAtlas atlas, IGlSurfaceHostRunner host)
    {
        _sessionId = sessionId;
        _atlas = atlas;
        _host = host;
        var size = host.Size;
        var columns = Math.Max(1, size.Width / atlas.GlyphWidth);
        var rows = Math.Max(1, size.Height / atlas.GlyphHeight);
        _screen = new VtScreen(columns, rows);
        _grid = new CellGrid(columns, rows);
        _packed = new uint[columns * rows];
    }

    /** 后台接会话隧道(输出喂 VtScreen, 输入来自窗口), 前台跑窗口循环; 窗口关闭或会话结束即收尾。 */
    public void Run()
    {
        var tunnel = Task.Run(async () =>
        {
            try
            {
                await PtyDaemonClient.AttachAsync(
                    _sessionId,
                    new InputPipe(this),
                    new ScreenPipe(this),
                    _tunnel.Token);
            }
            finally
            {
                // 会话自己结束(/exit、两次 Ctrl+C)时隧道会正常收流结束: 此时必须让窗口跟着关掉,
                // 否则只剩一个黑框(会话画面已经清空)且只能手动关。_tunnel 只在窗口关闭后才取消, 指望不上。
                _sessionEnded = true;
            }
        });
        try
        {
            _host.Run(this);
        }
        finally
        {
            _tunnel.Cancel();
            try
            {
                tunnel.Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
                // 会话侧已断开: 无需再等
            }
        }
    }

    public void OnLoaded()
    {
        _host.MakeCurrent();
        _core.Initialize(_atlas);
        var size = _host.Size;
        OnResize(size.Width, size.Height);
    }

    public void OnResize(int width, int height)
    {
        GL.Viewport(0, 0, width, height);
        var columns = Math.Max(1, width / _atlas.GlyphWidth);
        var rows = Math.Max(1, height / _atlas.GlyphHeight);
        lock (_screen)
        {
            if (_screen.Width == columns && _screen.Height == rows)
                return;
            _screen.Resize(columns, rows);
            _grid = new CellGrid(columns, rows);
            _lastGrid = null;
            _packed = new uint[columns * rows];
        }

        // 窗口尺寸即 proxy 的尺寸: 立刻告诉 daemon, 会话按它重排(常驻 TUI 从尺寸文件读到)。
        _ = Task.Run(async () =>
        {
            try
            {
                await PtyDaemonClient.ResizeAsync(_sessionId, rows, columns);
            }
            catch (Exception)
            {
                // daemon 不可用: 会话保持原尺寸
            }
        });
    }

    public bool OnFrame()
    {
        lock (_screen)
        {
            for (var y = 0; y < _grid.Height; y++)
            {
                var row = _screen.Row(y);
                for (var x = 0; x < _grid.Width; x++)
                    _grid[x, y] = row[x];
            }

            if (_screen.CursorVisible
                && _screen.CursorX >= 0 && _screen.CursorX < _grid.Width
                && _screen.CursorY >= 0 && _screen.CursorY < _grid.Height)
            {
                // 独立窗口没有宿主终端光标, 由渲染器自绘: 光标格反显。
                var cell = _grid[_screen.CursorX, _screen.CursorY];
                _grid[_screen.CursorX, _screen.CursorY] = cell with { Style = cell.Style ^ CellStyle.Reverse };
            }
        }

        _core.EnsureCellCapacity(_grid.Width * _grid.Height);
        if (CellPacker.CollectDirtyRowRanges(_grid, _lastGrid, _dirty) > 0)
        {
            foreach (var (start, count) in _dirty)
            {
                CellPacker.PackRows(_grid, start, count, _packed, _atlas);
                _core.UploadCells(_packed, start * _grid.Width, count * _grid.Width);
            }
        }

        _lastGrid ??= new CellGrid(_grid.Width, _grid.Height);
        (_grid, _lastGrid) = (_lastGrid, _grid);
        _core.RenderFrame(_atlas, _grid.Width, _grid.Height);
        // 必须交换缓冲: 只画后缓冲不 present, 窗口会一直显示未初始化内容(白屏, resize 后露出黑区)。
        _host.Present();
        return _tunnel.IsCancellationRequested || _sessionEnded;
    }

    public void OnKey(ConsoleKeyInfo key)
    {
        Span<byte> buffer = stackalloc byte[16];
        var length = TerminalKeyEncoder.Encode(key, buffer, _screen.ApplicationCursorKeys);
        if (length > 0)
            Enqueue(buffer[..length]);
    }

    public void OnText(char character)
    {
        Span<char> single = [character];
        Span<byte> buffer = stackalloc byte[4];
        var length = Encoding.UTF8.GetBytes(single, buffer);
        if (length > 0)
            Enqueue(buffer[..length]);
    }

    public void OnMouseMove(float x, float y)
    {
        UpdateMouseCell(x, y);
        if (_leftDown)
            EnqueueMouse(_mouseCellX, _mouseCellY, button: 32, release: false);
    }

    public void OnMouseButton(bool pressed, float x, float y)
    {
        UpdateMouseCell(x, y);
        _leftDown = pressed;
        EnqueueMouse(_mouseCellX, _mouseCellY, pressed ? 0 : MouseReleaseButton, release: !pressed);
    }

    public void OnMouseWheel(float deltaY)
    {
        EnqueueMouse(_mouseCellX, _mouseCellY, deltaY > 0 ? MouseWheelUp : MouseWheelDown, release: false);
    }

    public void Dispose()
    {
        _inputSignal.Dispose();
        _core.Dispose();
        _atlas.SaveCacheIfDirty();
        _host.Dispose();
    }

    private void UpdateMouseCell(float x, float y)
    {
        _mouseCellX = Math.Clamp((int)(x / _atlas.GlyphWidth), 0, Math.Max(0, _grid.Width - 1));
        _mouseCellY = Math.Clamp((int)(y / _atlas.GlyphHeight), 0, Math.Max(0, _grid.Height - 1));
    }

    private void EnqueueMouse(int cellX, int cellY, int button, bool release)
    {
        var bytes = Encoding.ASCII.GetBytes($"\u001b[<{button};{cellX + 1};{cellY + 1}{(release ? 'm' : 'M')}");
        Enqueue(bytes);
    }

    private void Enqueue(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
            return;
        _input.Enqueue(bytes.ToArray());
        _inputSignal.Release();
    }

    private bool TryDequeue(out byte[] bytes)
    {
        if (_input.TryDequeue(out var queued))
        {
            bytes = queued;
            return true;
        }

        bytes = [];
        return false;
    }

    /** 隧道输入侧: 读窗口产生的按键/鼠标字节, 没有输入时挂起直到有或会话结束。 */
    private sealed class InputPipe(GpuSessionProxy owner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await owner._inputSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (!owner.TryDequeue(out var bytes))
                return 0;
            var length = Math.Min(bytes.Length, buffer.Length);
            bytes.AsSpan(0, length).CopyTo(buffer.Span);
            return length;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /** 隧道输出侧: 会话画面字节直接喂给 VtScreen(与 daemon 里那份同样的解释器)。 */
    private sealed class ScreenPipe(GpuSessionProxy owner) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (owner._screen)
                owner._screen.Feed(buffer.AsSpan(offset, count));
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            lock (owner._screen)
                owner._screen.Feed(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
