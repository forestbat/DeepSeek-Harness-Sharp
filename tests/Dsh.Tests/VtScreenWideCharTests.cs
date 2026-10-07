using System.Text;
using Dsh.Pty;

namespace Dsh.Tests;

/** VtScreen 的宽字符/备用屏行为: attach 后画面靠它重放, 中文丢失会直接表现为"侧栏标签空白"。 */
public sealed class VtScreenWideCharTests
{
    [Fact]
    public void Wide_Cjk_Lands_In_The_Grid()
    {
        var screen = new VtScreen(10, 2);
        screen.Feed(Encoding.UTF8.GetBytes("上下文"));
        Assert.Equal('上', screen.Row(0)[0].Character);
        Assert.Equal('下', screen.Row(0)[2].Character);
        // 宽字符续格必须是 '\0' 占位(不能是空格, 否则消费方会多输出一列致字距拉大/越界)。
        Assert.Equal('\0', screen.Row(0)[1].Character);
        Assert.Equal('\0', screen.Row(0)[3].Character);
        Assert.Equal('\0', screen.Row(0)[5].Character);
    }

    [Fact]
    public void Wide_Cjk_Survives_Alt_Screen_Switch()
    {
        var screen = new VtScreen(10, 2);
        screen.Feed(Encoding.UTF8.GetBytes("\u001b[?1049h\u001b[2J\u001b[H"));
        screen.Feed(Encoding.UTF8.GetBytes("上下文"));
        Assert.Equal('上', screen.Row(0)[0].Character);
    }
}
