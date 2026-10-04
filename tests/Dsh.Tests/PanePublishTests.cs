using System.Text;
using Dsh.Pty;
using Dsh.Tui;

namespace Dsh.Tests;

/** 窗格快照跨进程发布的总量上限: 各 pane 尾行共享 64KB 预算, 超限裁剪并标 Truncated。 */
public sealed class PanePublishTests
{
    [Fact]
    public void CapSnapshots_Keeps_Total_Under_Budget_And_Marks_Truncated()
    {
        var line = new string('x', 1024);
        var first = new PtyPaneSnapshotDto { Id = 0, Kind = "chat", Title = "a", Lines = [.. Enumerable.Repeat(line, 40)] };
        var second = new PtyPaneSnapshotDto { Id = 1, Kind = "shell", Title = "b", Lines = [.. Enumerable.Repeat(line, 40)] };

        var capped = PaneBridge.CapSnapshots([first, second]);

        var total = capped.Sum(pane => (pane.Lines ?? []).Sum(item => Encoding.UTF8.GetByteCount(item) + 1));
        Assert.True(total <= PaneBridge.MaxPublishedBytes, $"总量 {total} 超过上限 {PaneBridge.MaxPublishedBytes}");
        Assert.True(capped[1].Truncated, "第二个 pane 应因预算耗尽被裁剪");
        Assert.True(capped[0].Lines!.Count > 0);
    }

    [Fact]
    public void CapSnapshots_Keeps_Small_Snapshot_Unchanged()
    {
        var small = PaneBridge.CapSnapshots([new PtyPaneSnapshotDto { Id = 0, Kind = "chat", Title = "a", Lines = ["hi"] }]);

        Assert.False(small[0].Truncated);
        Assert.Equal(["hi"], small[0].Lines);
    }

    [Fact]
    public void CapSnapshots_Preserves_Existing_Truncated_Flag()
    {
        var pane = new PtyPaneSnapshotDto { Id = 0, Kind = "chat", Title = "a", Truncated = true, Lines = ["hi"] };

        var capped = PaneBridge.CapSnapshots([pane]);

        Assert.True(capped[0].Truncated);
    }
}
