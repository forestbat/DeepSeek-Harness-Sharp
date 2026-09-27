using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace Dsh.Tui;

/** 窗口形态宿主: GLFW 窗口 + GL 上下文 + 交换缓冲, 由 GLFW 事件驱动帧循环与输入。 */
public sealed class GlfwWindowHost : IGlSurfaceHostRunner
{
    private readonly GameWindow _window;
    private IGpuHostClient? _client;
    private bool _disposed;

    public GlfwWindowHost(int clientWidth, int clientHeight, bool visible = true, string title = "dsh --gpu")
    {
        // OpenTK 的 GLFW"主线程"认定要求入口方法在调用栈上且非线程池线程; async Main 的续体不满足, 直接关掉该检查(GLFW 在 Windows/X11/Wayland 对调用线程无要求)。
        GLFWProvider.CheckForMainThread = false;
        RequestRobustnessOnAmd();
        _window = CreateWindow(clientWidth, clientHeight, visible, title);
        HookEvents();
    }

    public (int Width, int Height) Size
    {
        get
        {
            var size = _window.FramebufferSize;
            return (Math.Max(1, size.X), Math.Max(1, size.Y));
        }
    }

    public void MakeCurrent()
    {
        _window.Context.MakeCurrent();
        OpenTK.Graphics.GLLoader.LoadBindings(new GLFWBindingsContext());
    }

    public void Present() => _window.SwapBuffers();

    public void Run(IGpuHostClient client)
    {
        _client = client;
        _window.Run();
        _client = null;
    }

    public void RequestClose() => _window.Close();

    public string? ReadClipboard() => _window.ClipboardString;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _window.Dispose();
    }

    /** AMD 驱动在高负载下可能会 TDR(超时复位): 有 AMD 卡在场时请求 robust 上下文, TDR 后表现为 GL_CONTEXT_LOST 而非进程崩溃; 其他厂商不请求, 避免无谓开销。 */
    private static void RequestRobustnessOnAmd()
    {
        if (!GpuCatalog.ListAdapters().Any(adapter => adapter.Vendor.Equals("AMD", StringComparison.OrdinalIgnoreCase)))
            return;
        GLFWProvider.EnsureInitialized();
        GLFW.WindowHint(WindowHintRobustness.ContextRobustness, Robustness.LoseContextOnReset);
    }

    private static GameWindow CreateWindow(int clientWidth, int clientHeight, bool visible, string title)
        => new(GameWindowSettings.Default, new NativeWindowSettings
        {
            ClientSize = new Vector2i(clientWidth, clientHeight),
            Title = title,
            API = ContextAPI.OpenGL,
            Profile = ContextProfile.Core,
            APIVersion = new Version(3, 3),
            StartVisible = visible,
        });

    private void HookEvents()
    {
        _window.Load += () => _client?.OnLoaded();
        _window.Resize += _ => Resize();
        _window.RenderFrame += _ => Frame();
        _window.KeyDown += OnKeyDown;
        _window.TextInput += OnTextInput;
        _window.MouseMove += OnMouseMove;
        _window.MouseDown += OnMouseButton;
        _window.MouseUp += OnMouseButton;
        _window.MouseWheel += OnMouseWheel;
    }

    private void Resize()
    {
        var size = Size;
        _client?.OnResize(size.Width, size.Height);
    }

    private void Frame()
    {
        if (_client is { } client && client.OnFrame())
            _window.Close();
    }

    private void OnKeyDown(KeyboardKeyEventArgs e)
    {
        if (_client is not { } client)
            return;
        if (!TryMapKey(e.Key, out var consoleKey))
            return;
        client.OnKey(new ConsoleKeyInfo('\0', consoleKey, e.Shift, e.Alt, e.Control));
    }

    private void OnTextInput(TextInputEventArgs e)
    {
        var text = e.AsString;
        if (text.Length == 0)
            return;
        _client?.OnText(text[0]);
    }

    private void OnMouseMove(MouseMoveEventArgs e)
        => _client?.OnMouseMove(e.X, e.Y);

    private void OnMouseButton(MouseButtonEventArgs e)
    {
        if (e.Button != MouseButton.Left)
            return;
        _client?.OnMouseButton(e.IsPressed, _window.MousePosition.X, _window.MousePosition.Y);
    }

    private void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (e.OffsetY != 0)
            _client?.OnMouseWheel(e.OffsetY);
    }

    private static bool TryMapKey(Keys key, out ConsoleKey consoleKey)
    {
        if (key is >= Keys.A and <= Keys.Z)
        {
            consoleKey = ConsoleKey.A + (key - Keys.A);
            return true;
        }

        if (key is >= Keys.D0 and <= Keys.D9)
        {
            consoleKey = ConsoleKey.D0 + (key - Keys.D0);
            return true;
        }

        switch (key)
        {
            case Keys.Enter:
                consoleKey = ConsoleKey.Enter;
                return true;
            case Keys.Escape:
                consoleKey = ConsoleKey.Escape;
                return true;
            case Keys.Tab:
                consoleKey = ConsoleKey.Tab;
                return true;
            case Keys.Backspace:
                consoleKey = ConsoleKey.Backspace;
                return true;
            case Keys.Delete:
                consoleKey = ConsoleKey.Delete;
                return true;
            case Keys.Up:
                consoleKey = ConsoleKey.UpArrow;
                return true;
            case Keys.Down:
                consoleKey = ConsoleKey.DownArrow;
                return true;
            case Keys.Left:
                consoleKey = ConsoleKey.LeftArrow;
                return true;
            case Keys.Right:
                consoleKey = ConsoleKey.RightArrow;
                return true;
            case Keys.Home:
                consoleKey = ConsoleKey.Home;
                return true;
            case Keys.End:
                consoleKey = ConsoleKey.End;
                return true;
            case Keys.PageUp:
                consoleKey = ConsoleKey.PageUp;
                return true;
            case Keys.PageDown:
                consoleKey = ConsoleKey.PageDown;
                return true;
            case Keys.Space:
                consoleKey = ConsoleKey.Spacebar;
                return true;
            default:
                consoleKey = default;
                return false;
        }
    }
}
