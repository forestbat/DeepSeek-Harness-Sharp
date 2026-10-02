using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Dsh.Tui;

/**
 * 无显示服务器且无 DRM 时的 GPU 宿主: 用 EGL pbuffer 离屏渲染(可用文末 GPU, 不需要 /dev/dri),
 * 每帧把帧缓冲经终端图像协议贴回终端(kitty 按行分块只传变化行; sixel 整帧), 并按终端格尺寸映射键鼠事件 —— 保留交互。
 * 终端两种协议都不支持时: 仅截图/捕获形态可用(不呈现), 否则 TryCreate 失败由调用方回退 CPU 渲染。
 */
internal sealed class TerminalGraphicsHost : IGlSurfaceHostRunner
{
    private readonly int _cellWidth;
    private readonly int _cellHeight;
    private readonly TerminalGraphicsProtocol _protocol;
    private EglPbufferHost _surface;
    private ITerminalInputSource? _input;
    private byte[]? _previous;
    private int _columns;
    private int _rows;
    private bool _forceFull = true;
    private bool _closed;
    private bool _disposed;

    private TerminalGraphicsHost(EglPbufferHost surface, int columns, int rows, int cellWidth, int cellHeight, TerminalGraphicsProtocol protocol)
    {
        _surface = surface;
        _columns = columns;
        _rows = rows;
        _cellWidth = cellWidth;
        _cellHeight = cellHeight;
        _protocol = protocol;
    }

    private bool Presenting => _protocol != TerminalGraphicsProtocol.None;

    public (int Width, int Height) Size => _surface.Size;

    /**
     * headlessCapture: 截图/捕获形态不需要呈现到终端, 因此不要求终端支持图像协议。
     * 终端尺寸/EGL 设备不可用时返回 false, 由 GpuHostFactory 抛出原因并回退。
     */
    public static bool TryCreate(GlyphAtlas atlas, bool headlessCapture, [NotNullWhen(true)] out TerminalGraphicsHost? host, out string reason)
    {
        host = null;
        var protocol = TerminalGraphics.Detect(out var protocolReason);
        if (protocol == TerminalGraphicsProtocol.None && !headlessCapture)
        {
            reason = protocolReason;
            return false;
        }
        var columns = Math.Max(1, SafeWindowWidth());
        var rows = Math.Max(1, SafeWindowHeight());
        try
        {
            var surface = EglPbufferHost.Create(columns * atlas.GlyphWidth, rows * atlas.GlyphHeight);
            host = new TerminalGraphicsHost(surface, columns, rows, atlas.GlyphWidth, atlas.GlyphHeight, protocol);
            reason = "";
            return true;
        }
        catch (Exception error)
        {
            reason = $"离屏 EGL 初始化失败: {error.Message}";
            return false;
        }
    }

    public void MakeCurrent() => _surface.MakeCurrent();

    public void Present()
    {
        if (!Presenting || _closed)
            return;
        var size = _surface.Size;
        if (size.Width <= 0 || size.Height <= 0)
            return;
        var pixels = new byte[size.Width * size.Height * 4];
        OpenTK.Graphics.OpenGL.GL.ReadPixels(
            0, 0, size.Width, size.Height,
            OpenTK.Graphics.OpenGL.PixelFormat.Rgba,
            OpenTK.Graphics.OpenGL.PixelType.UnsignedByte,
            pixels);
        if (_protocol == TerminalGraphicsProtocol.Sixel)
        {
            Console.Out.Write(TerminalGraphics.EncodeSixel(pixels, size.Width, size.Height));
            Console.Out.Flush();
            return;
        }
        PresentKittyRows(pixels, size.Width, size.Height);
    }

    /** kitty: 逐终端行贴图; 首帧/尺寸变化全量, 之后只重传内容变化的行。 */
    private void PresentKittyRows(byte[] pixels, int width, int height)
    {
        var rowCount = Math.Max(1, height / _cellHeight);
        var builder = new StringBuilder();
        for (var row = 0; row < rowCount; row++)
        {
            if (!_forceFull && _previous is not null && !RowChanged(pixels, _previous, width, height, row))
                continue;
            builder.Append(TerminalGraphics.EncodeKittyRow(pixels, width, height, row, _cellHeight, _columns));
        }
        if (builder.Length > 0)
        {
            Console.Out.Write(builder.ToString());
            Console.Out.Flush();
        }
        _previous = (byte[])pixels.Clone();
        _forceFull = false;
    }

    /** 该终端行在自上而下坐标里的像素带是否变化(GL 缓冲自下而上)。 */
    private bool RowChanged(byte[] current, byte[] previous, int width, int height, int row)
    {
        var topY = row * _cellHeight;
        for (var y = Math.Min(height, topY + _cellHeight) - 1; y >= topY; y--)
        {
            var offset = y * width * 4;
            if (!current.AsSpan(offset, width * 4).SequenceEqual(previous.AsSpan(offset, width * 4)))
                return true;
        }
        return false;
    }

    public void Run(IGpuHostClient client)
    {
        var interactive = !Console.IsInputRedirected;
        using var raw = interactive ? TerminalRawMode.TryEnable(enableMouse: true) : null;
        _input = interactive ? CreateInputSource() : null;
        EnterTerminal();
        try
        {
            client.OnLoaded();
            while (!_closed)
            {
                if (client.OnFrame())
                    break;
                var inputEvent = _input?.Read(8);
                if (inputEvent is null)
                {
                    if (_input is { EndOfStream: true })
                        break;
                    if (_input is null)
                        Thread.Sleep(8);
                }
                else
                {
                    Dispatch(client, inputEvent.Value);
                }
                MaybeResize(client);
            }
        }
        finally
        {
            _input?.Dispose();
            _input = null;
            LeaveTerminal();
        }
    }

    public void RequestClose()
    {
        _closed = true;
        _input?.Wake();
    }

    public string? ReadClipboard() => null;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _surface.Dispose();
    }

    /** 终端尺寸变化: 复用同一 EGL 上下文重建 pbuffer 表面(GL 资源不失效), 再通知客户端重排。 */
    private void MaybeResize(IGpuHostClient client)
    {
        var columns = Math.Max(1, SafeWindowWidth());
        var rows = Math.Max(1, SafeWindowHeight());
        if (columns == _columns && rows == _rows)
            return;
        _columns = columns;
        _rows = rows;
        _surface.Resize(columns * _cellWidth, rows * _cellHeight);
        _surface.MakeCurrent();
        _forceFull = true;
        _previous = null;
        client.OnResize(_surface.Size.Width, _surface.Size.Height);
    }

    private void Dispatch(IGpuHostClient client, TerminalInputEvent inputEvent)
    {
        if (inputEvent.Mouse is not { } mouse)
        {
            client.OnKey(inputEvent.Key);
            return;
        }
        var x = mouse.X * _cellWidth;
        var y = mouse.Y * _cellHeight;
        if (mouse.IsWheel)
        {
            client.OnMouseWheel(mouse.WheelDelta);
            return;
        }
        if (mouse.IsMove)
        {
            client.OnMouseMove(x, y);
            return;
        }
        client.OnMouseButton(mouse.Pressed, x, y);
    }

    private static ITerminalInputSource? CreateInputSource()
        => OperatingSystem.IsWindows()
            ? WindowsConsoleInputReader.TryCreate()
            : new TerminalInputReader();

    private void EnterTerminal()
    {
        if (!Presenting)
            return;
        Console.Out.Write("\x1b[?1049h\x1b[?25l\x1b[2J\x1b[H");
        Console.Out.Flush();
    }

    private void LeaveTerminal()
    {
        if (!Presenting)
            return;
        if (_protocol == TerminalGraphicsProtocol.Kitty)
            Console.Out.Write(TerminalGraphics.DeleteRows(Math.Max(1, _surface.Size.Height / _cellHeight)));
        Console.Out.Write("\x1b[?25h\x1b[?1049l");
        Console.Out.Flush();
    }

    private static int SafeWindowWidth()
    {
        try
        {
            return Console.WindowWidth;
        }
        catch (IOException)
        {
            return 80;
        }
        catch (ArgumentOutOfRangeException)
        {
            return 80;
        }
    }

    private static int SafeWindowHeight()
    {
        try
        {
            return Console.WindowHeight;
        }
        catch (IOException)
        {
            return 25;
        }
        catch (ArgumentOutOfRangeException)
        {
            return 25;
        }
    }
}
