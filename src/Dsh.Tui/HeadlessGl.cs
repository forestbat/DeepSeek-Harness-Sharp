using OpenTK.Graphics.OpenGL;

namespace Dsh.Tui;

/**
 * 无头 GL 上下文(压测/CI): Windows 或有显示服务器走隐藏 GLFW 窗口(WGL/GLX 经驱动路由选卡), Linux 真无头走 EGL pbuffer。
 * 已从测试工程提升到 src 复用; 测试与压测直接引用本类型, 不再各写一份。
 */
public sealed class HeadlessGl : IDisposable
{
    private readonly IGlSurfaceHost _host;

    /** 选中的 GL 设备的 GL_RENDERER 字符串。 */
    public string Renderer { get; }

    /** 枚举到的全部设备描述(探针渲染器名或失败原因), 供报告多卡环境。 */
    public IReadOnlyList<string> EnumeratedDevices { get; }

    private HeadlessGl(IGlSurfaceHost host, string renderer, IReadOnlyList<string> enumeratedDevices)
    {
        _host = host;
        Renderer = renderer;
        EnumeratedDevices = enumeratedDevices;
    }

    public static HeadlessGl Create(int width, int height)
    {
        if (OperatingSystem.IsWindows() || GpuRenderer.TryDetectDisplay(out _))
        {
            var window = new GlfwWindowHost(width, height, visible: false, title: "dsh-headless-bench");
            window.MakeCurrent();
            var renderer = GL.GetString(StringName.Renderer) ?? "";
            return new HeadlessGl(window, renderer, [$"GLFW: {renderer}"]);
        }
        var pbuffer = EglPbufferHost.Create(width, height);
        return new HeadlessGl(pbuffer, pbuffer.Renderer, pbuffer.EnumeratedDevices);
    }

    public void MakeCurrent() => _host.MakeCurrent();

    public void Dispose() => _host.Dispose();
}
