namespace Dsh.Tui;

/**
 * GPU 形态的宿主选择: 有显示服务器→GLFW 窗口; 无显示服务器且有 DRM→GBM/KMS 直连屏; 都不行→拒绝 GPU 形态, 调用方回退 VT。
 * 按环境选择只在创建时做一次; 宿主选择失败以异常携带原因, 由 TuiRunner 提示并回退。
 */
internal static class GpuHostFactory
{
    public static IGlSurfaceHostRunner CreateWindowHost(GlyphAtlas atlas, string? gpuCard = null, string? preferredCard = null)
    {
        if (OperatingSystem.IsWindows() || GpuRenderer.TryDetectDisplay(out var displayReason))
            return new GlfwWindowHost(80 * atlas.GlyphWidth, 25 * atlas.GlyphHeight);
        if (EglGbmKmsHost.TryCreate(gpuCard, preferredCard, out var gbmHost, out var drmReason))
            return gbmHost;
        throw new InvalidOperationException($"{displayReason}; {drmReason}");
    }
}
