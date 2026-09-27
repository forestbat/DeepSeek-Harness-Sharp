namespace Dsh.Tui;

/**
 * GL 表面/上下文宿主: 只负责"让当前 GL 上下文可渲染"与"呈现一帧", 以及后端帧缓冲尺寸。
 * 对当前 GL 上下文的绘制逻辑全部在 GpuRenderCore, 与宿主无关; 换宿主不改渲染核心。
 */
public interface IGlSurfaceHost : IDisposable
{
    /** 使本宿主的 GL 上下文在调用线程上成为当前上下文, 并确保 GL 函数绑定指向该上下文的加载器。 */
    void MakeCurrent();

    /** 把已渲染的一帧呈现到宿主表面(窗口交换缓冲 / EGL swap / GBM 翻页)。 */
    void Present();

    /** 后端帧缓冲像素尺寸; 无窗口宿主由创建参数给出。 */
    (int Width, int Height) Size { get; }
}

/** 宿主帧循环与输入事件的接收方, 由 GpuRenderer 实现。 */
public interface IGpuHostClient
{
    /** GL 上下文就绪, 可以创建 GL 资源。 */
    void OnLoaded();

    /** 帧缓冲像素尺寸变化。 */
    void OnResize(int width, int height);

    /** 渲染并呈现一帧; 返回 true 表示请求关闭宿主(退出或上下文丢失)。 */
    bool OnFrame();

    void OnKey(ConsoleKeyInfo key);

    void OnText(char character);

    void OnMouseMove(float x, float y);

    /** 左键按下/释放; x/y 为像素坐标。 */
    void OnMouseButton(bool pressed, float x, float y);

    void OnMouseWheel(float deltaY);
}

/** 自带事件循环的宿主: 窗口形态(GKFW)由 GLFW 事件驱动, 裸 TTY(GBM)由宿主自建循环驱动。 */
public interface IGlSurfaceHostRunner : IGlSurfaceHost
{
    /** 驱动宿主循环直到关闭; 期间回调 client。 */
    void Run(IGpuHostClient client);

    /** 请求宿主尽快关闭循环。 */
    void RequestClose();

    /** 读取宿主剪贴板(窗口形态有); 裸 TTY 无剪贴板返回 null。 */
    string? ReadClipboard();
}
