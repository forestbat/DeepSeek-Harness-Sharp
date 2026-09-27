using Dsh.Tui;

namespace Dsh.Tests;

/** 剪贴板写入冒烟: 走真实系统命令(clip.exe / wl-copy / xclip ...), 不做 mock。 */
public class ClipboardTests
{
    [Fact]
    public async Task TrySetText_Writes_To_System_Clipboard()
    {
        var written = await Clipboard.TrySetTextAsync("dsh-clipboard-probe");

        Assert.True(written, "剪贴板写入应成功(Windows 走 clip.exe)");
    }
}
