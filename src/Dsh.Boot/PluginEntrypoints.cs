namespace Dsh.Boot;

public sealed record PluginEntrypointOptions(
    HarnessHome Home,
    string Cwd,
    /** `--session <id>`: 入口直接打开指定会话(GUI), 未指定时为 null。 */
    string? ResumeSessionId = null,
    /** `--gpu`: 独立窗口形态(自有 GPU 窗口即终端模拟器前端)。 */
    bool Gpu = false,
    /** `--shell`: 启动即带一个真 shell 窗格(Dsh.Pty 宿主)。 */
    bool Shell = false,
    /** `--gpu-screenshot <path>`: 渲染首帧后把 GPU 帧缓冲写盘并退出(裸 TTY 上无 X/外部截图工具时唯一取证手段)。 */
    string? GpuScreenshotPath = null,
    /** `--gpu-card <N|路径>`: 显式指定 GBM/KMS 上屏的 DRM 卡(多卡接屏时用); 严格指定, 不可用则拒绝 GPU 形态。 */
    string? GpuCard = null);

/** 入口插件契约:插件类实现它,并在类上声明 DshEntrypoint("tui") 之类的入口名。 */
public interface IDshEntrypoint
{
    Task<int> RunAsync(HarnessApp app, PluginEntrypointOptions options, CancellationToken cancellationToken);
}

