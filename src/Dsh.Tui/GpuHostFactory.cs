namespace Dsh.Tui;

/**
 * GPU 形态的宿主选择: 有显示服务器→GLFW 窗口; 无显示服务器且有 DRM→GBM/KMS 直连屏;
 * 否则→离屏 EGL + kitty graphics 贴回终端(无 DRM/无显示也能交互渲染 GPU 画面; 仅截图/捕获时不要求终端支持图像协议)。
 * 按环境选择只在创建时做一次; 宿主选择失败以异常携带原因, 由 TuiRunner 提示并回退。
 */
internal static class GpuHostFactory
{
    public static IGlSurfaceHostRunner CreateWindowHost(
        GlyphAtlas atlas,
        string? gpuCard = null,
        string? preferredCard = null,
        bool hidden = false,
        bool vsync = true,
        bool headlessCapture = false)
    {
        if (OperatingSystem.IsWindows() || GpuRenderer.TryDetectDisplay(out var displayReason))
            return new GlfwWindowHost(80 * atlas.GlyphWidth, 25 * atlas.GlyphHeight, visible: !hidden, vsync: vsync);
        if (EglGbmKmsHost.TryCreate(gpuCard, preferredCard, out var gbmHost, out var drmReason))
            return gbmHost;
        if (TerminalGraphicsHost.TryCreate(atlas, headlessCapture, out var terminalHost, out var terminalReason))
            return terminalHost;
        throw new InvalidOperationException($"{displayReason}; {drmReason}; {terminalReason}");
    }
}
