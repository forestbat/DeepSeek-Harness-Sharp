using Dsh.Tui;

namespace Dsh.Tests;

/** 剪贴板写入冒烟: 走真实系统命令(clip.exe / wl-copy / xclip ...), 不做 mock。 */
public class ClipboardTests
{
    [Fact]
    public async Task TrySetText_Writes_To_System_Clipboard()
    {
        if (!OperatingSystem.IsWindows() && !HasClipboardProvider())
            Assert.Skip("无剪贴板提供者(裸 TTY 无 wl-copy/xclip/xsel/pbcopy, 非 WSL); 优雅失败路径由产品代码返回 false 保证");

        var written = await Clipboard.TrySetTextAsync("dsh-clipboard-probe");

        Assert.True(written, "剪贴板写入应成功(Windows 走 clip.exe)");
    }

    private static bool HasClipboardProvider()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var directories = path.Split(':', StringSplitOptions.RemoveEmptyEntries);
        string[] tools = ["wl-copy", "xclip", "xsel", "pbcopy"];
        return tools.Any(tool => directories.Any(directory => File.Exists(Path.Combine(directory, tool))))
            || File.Exists("/mnt/c/Windows/System32/clip.exe");
    }
}
