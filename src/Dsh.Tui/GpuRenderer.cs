using OpenTK.Graphics.OpenGL;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Dsh.Tui;

/**
 * GPU 终端渲染器: 维护格栅/布局/脏行上传, 对当前 GL 上下文(RenderFrame)画格, 再交给表面宿主呈现。
 * 窗口/上下文/交换/输入循环全在 IGlSurfaceHost 实现里; 本类不引用 GLFW 或 DRM。
 */
public sealed class GpuRenderer : IDisposable, IGpuHostClient
{
    /** TDR 重建上限: 60 秒内连续丢 3 次上下文说明驱动在反复复位, 放弃重建, 抛出后由 TuiRunner 回退 CPU 渲染。 */
    private const int MaxRebuildAttempts = 3;
    /** 输入行光标闪烁半周期(终端习惯值): 半周期亮、半周期灭。 */
    private const int CursorBlinkMs = 500;
    private static readonly TimeSpan RebuildInterval = TimeSpan.FromSeconds(60);

    private readonly GlyphAtlas _atlas;
    private readonly ChatWindow _chat;
    private IGlSurfaceHostRunner _host;
    private GpuRenderCore _core = new();
    private CellGrid _grid;
    private UiLayout _layout;
    private CellGrid? _lastGrid;
    private uint[] _packedCells = new uint[80 * 25];
    private readonly List<(int Start, int Count)> _dirtyRanges = [];
    private int _seenRenderVersion = -1;
    private bool _cursorLit = true;
    private float _mouseX;
    private float _mouseY;
    private bool _leftButtonDown;
    private bool _disposed;
    private bool _contextLost;
    private readonly List<DateTime> _rebuilds = [];
    private readonly string? _screenshotPath;
    private bool _screenshotTaken;

    public static bool TryDetectDisplay(out string reason)
    {
        reason = "";
        if (!OperatingSystem.IsLinux() || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
            return true;
        var display = Environment.GetEnvironmentVariable("DISPLAY");
        if (string.IsNullOrEmpty(display))
        {
            reason = "no display server detected (neither DISPLAY nor WAYLAND_DISPLAY is set)";
            return false;
        }
        if (display.StartsWith(':') && !File.Exists(Path.Combine("/tmp/.X11-unix", $"X{ScreenOf(display)}")))
        {
            reason = $"no display server detected (X11 socket for DISPLAY={display} is missing)";
            return false;
        }
        return true;
    }

    private static string ScreenOf(string display)
    {
        var value = display[1..];
        var dot = value.IndexOf('.');
        return dot < 0 ? value : value[..dot];
    }

    /** screenshotPath: 渲染首帧后把帧缓冲写盘并退出(裸 TTY 上没有 X/外部截图工具时唯一可行的取证手段)。 */
    public GpuRenderer(ChatWindow chat, GlyphAtlas? atlas = null, string? screenshotPath = null)
    {
        ArgumentNullException.ThrowIfNull(chat);
        _chat = chat;
        _atlas = atlas ?? GlyphAtlas.Shared;
        _screenshotPath = screenshotPath;
        _host = GpuHostFactory.CreateWindowHost(_atlas);
        _grid = new CellGrid(80, 25);
        _layout = LayoutEngine.Calculate(_grid.Width, _grid.Height);
    }

    /** 主循环: 宿主因 TDR 上下文丢失而关闭时重建宿主与 GL 资源再续跑; 用户退出则直接返回。 */
    public void Run()
    {
        while (true)
        {
            _contextLost = false;
            _host.Run(this);
            if (!_contextLost)
                return;
            RegisterRebuild();
            RebuildHost();
        }
    }

    private void RegisterRebuild()
    {
        var now = DateTime.UtcNow;
        _rebuilds.RemoveAll(at => now - at > RebuildInterval);
        _rebuilds.Add(now);
        if (_rebuilds.Count > MaxRebuildAttempts)
            throw new InvalidOperationException($"GPU context lost {MaxRebuildAttempts} times within {RebuildInterval.TotalSeconds:F0}s (driver reset); giving up GPU rendering");
    }

    /** GL 对象不跨上下文共享: 换宿主即全部重建, 网格状态清空触发整屏重绘。 */
    private void RebuildHost()
    {
        _host.Dispose();
        _core.Dispose();
        _core = new GpuRenderCore();
        _lastGrid = null;
        _seenRenderVersion = -1;
        _host = GpuHostFactory.CreateWindowHost(_atlas);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _core.Dispose();
        _atlas.SaveCacheIfDirty();
        _host.Dispose();
    }

    public void OnLoaded()
    {
        _host.MakeCurrent();
        _core.Initialize(_atlas);
        Console.Error.WriteLine($"gpu renderer: {GL.GetString(StringName.Renderer)} ({GL.GetString(StringName.Version)})");
        var size = _host.Size;
        OnResize(size.Width, size.Height);
    }

    public void OnResize(int width, int height)
    {
        GL.Viewport(0, 0, width, height);
        var gridWidth = Math.Max(1, width / _atlas.GlyphWidth);
        var gridHeight = Math.Max(1, height / _atlas.GlyphHeight);
        if (_grid.Width != gridWidth || _grid.Height != gridHeight)
        {
            _grid = new CellGrid(gridWidth, gridHeight);
            _layout = LayoutEngine.Calculate(gridWidth, gridHeight);
            _lastGrid = null;
            var required = gridWidth * gridHeight;
            if (_packedCells.Length < required)
                _packedCells = new uint[required];
        }
    }

    public bool OnFrame()
    {
        _chat.DrainUi();
        if (_chat.ExitRequested)
            return true;

        var cursorLit = IsCursorLit(Environment.TickCount64);
        var gridChanged = _chat.RenderVersion != _seenRenderVersion
            || _lastGrid is null
            || _lastGrid.Width != _grid.Width
            || _lastGrid.Height != _grid.Height;
        // 内容没变也要按闪烁相位重绘: 否则输入行光标永远不闪。
        if (!gridChanged && cursorLit == _cursorLit)
            return false;
        _cursorLit = cursorLit;
        _seenRenderVersion = _chat.RenderVersion;

        _chat.Draw(_grid, _layout);
        DrawCursor(_grid, _chat.CursorScreenX, _chat.CursorScreenY, cursorLit);
        _core.EnsureCellCapacity(_grid.Width * _grid.Height);
        if (CellPacker.CollectDirtyRowRanges(_grid, _lastGrid, _dirtyRanges) > 0)
        {
            foreach (var (start, count) in _dirtyRanges)
            {
                CellPacker.PackRows(_grid, start, count, _packedCells, _atlas);
                _core.UploadCells(_packedCells, start * _grid.Width, count * _grid.Width);
            }
        }

        _lastGrid ??= new CellGrid(_grid.Width, _grid.Height);
        (_grid, _lastGrid) = (_lastGrid, _grid);

        _core.RenderFrame(_atlas, _grid.Width, _grid.Height);

        if (!_screenshotTaken && _screenshotPath is not null)
        {
            SaveScreenshot(_screenshotPath);
            _screenshotTaken = true;
            _chat.RequestExit();
        }

        _host.Present();

        // robust 上下文下 TDR 的表现: GetError 报 CONTEXT_LOST(0x0507, ErrorCode 枚举未收录该值, 按 All 原始常量比较), 之后的 GL 调用全是空操作; 关闭宿主交回 Run() 重建。
        if ((All)GL.GetError() == All.ContextLost)
        {
            _contextLost = true;
            return true;
        }
        return false;
    }

    /** 闪烁相位: 亮/灭各占一个半周期。抽成纯函数便于单测, 不受挂钟抖动影响。 */
    internal static bool IsCursorLit(long tickCount)
        => tickCount / CursorBlinkMs % 2 == 0;

    /** 把光标所在格反显成块状光标(真实终端行为); 不可见或越界时原样返回。 */
    internal static void DrawCursor(CellGrid grid, int x, int y, bool visible)
    {
        if (!visible || (uint)x >= (uint)grid.Width || (uint)y >= (uint)grid.Height)
            return;
        var cell = grid[x, y];
        grid[x, y] = cell with { Style = cell.Style | CellStyle.Reverse };
    }

    public void OnKey(ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.V && (key.Modifiers & ConsoleModifiers.Control) != 0)
        {
            var clipboard = _host.ReadClipboard();
            if (!string.IsNullOrEmpty(clipboard))
                _chat.InsertText(clipboard);
            return;
        }

        _chat.HandleKey(key);
    }

    public void OnText(char character)
    {
        if (character == '\0')
            return;
        _chat.HandleKey(new ConsoleKeyInfo(character, ConsoleKey.NoName, false, false, false));
    }

    public void OnMouseMove(float x, float y)
    {
        _mouseX = x;
        _mouseY = y;
        if (_leftButtonDown)
            _chat.HandleMouseDrag((int)(_mouseX / _atlas.GlyphWidth), (int)(_mouseY / _atlas.GlyphHeight), _layout);
    }

    public void OnMouseButton(bool pressed, float x, float y)
    {
        _mouseX = x;
        _mouseY = y;
        var cellX = (int)(x / _atlas.GlyphWidth);
        var cellY = (int)(y / _atlas.GlyphHeight);
        if (pressed)
        {
            _leftButtonDown = true;
            _chat.HandleMouseClick(cellX, cellY, _layout);
        }
        else
        {
            _leftButtonDown = false;
            _chat.HandleMouseRelease(cellX, cellY, _layout);
        }
    }

    public void OnMouseWheel(float deltaY)
    {
        if (deltaY == 0)
            return;
        var cellX = (int)(_mouseX / _atlas.GlyphWidth);
        var cellY = (int)(_mouseY / _atlas.GlyphHeight);
        _chat.HandleMouseWheel((int)deltaY, cellX, cellY, _layout);
    }

    private void SaveScreenshot(string path)
    {
        var size = _host.Size;
        if (size.Width <= 0 || size.Height <= 0)
            return;
        var pixels = new byte[size.Width * size.Height * 4];
        GL.ReadPixels(0, 0, size.Width, size.Height, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
        using var image = new Image<Rgba32>(size.Width, size.Height);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                var sourceY = accessor.Height - 1 - y;
                for (var x = 0; x < accessor.Width; x++)
                {
                    var source = ((sourceY * accessor.Width) + x) * 4;
                    row[x] = new Rgba32(pixels[source], pixels[source + 1], pixels[source + 2], pixels[source + 3]);
                }
            }
        });
        if (path.EndsWith(".tif", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase))
            image.SaveAsTiff(path);
        else
            image.SaveAsPng(path);
    }
}

