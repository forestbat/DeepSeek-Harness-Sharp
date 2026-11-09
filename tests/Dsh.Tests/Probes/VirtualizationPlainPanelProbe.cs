using System.Diagnostics;
using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace Dsh.Tests;

/**
 * 探针(不进门禁, 用 -trait- "Category=Probe" 排除): 换掉虚拟化面板之后, 成本与"零误差"各是多少。
 *
 * 这是所有"真值"论证的来源: 非虚拟化时 extent 等于实测高度逐条累加, 折叠只改变被点那条自身的高度。
 * 同时量出它的代价, 用来回答"为什么不能干脆不虚拟化"。
 *
 * 实测基线(237 条消息): 首屏布局 3834ms / 237 项全实体化; 虚拟化下 extent 19103, 真值 16661。
 */
[Collection(GuiSerialCollection.CollectionName)]
[Trait("Category", "Probe")]
public sealed class VirtualizationPlainPanelProbe
{
    private static string OutputDirectory
    {
        get
        {
            var directory = Path.Combine(TempTree.RepositoryRoot, "artifacts", "repro", "probes");
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    [Fact]
    public Task CompareCostAndAccuracyAgainstAPlainPanel() => HeadlessGui.RunAsync(async () =>
    {
        var report = new StringBuilder();
        await using var scene = await RealChatScene.CreateAsync();
        var virtualizedExtent = scene.Extent;
        var virtualizedRealized = scene.List.GetVisualDescendants().OfType<ListBoxItem>().Count();
        report.AppendLine($"虚拟化: 消息 {scene.MessageCount}, extent {virtualizedExtent:0}, 实体化 {virtualizedRealized}");

        scene.List.ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Orientation = Orientation.Vertical });
        var watch = Stopwatch.StartNew();
        scene.Window.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        scene.Window.UpdateLayout();
        watch.Stop();

        var plainRealized = scene.List.GetVisualDescendants().OfType<ListBoxItem>().Count();
        var heightSum = scene.List.GetVisualDescendants().OfType<ListBoxItem>().Sum(item => item.Bounds.Height);
        report.AppendLine($"非虚拟化: 布局耗时 {watch.ElapsedMilliseconds}ms, 实体化 {plainRealized}/{scene.MessageCount}, "
            + $"extent {scene.Extent:0}, 实测高度合计 {heightSum:0}, "
            + $"差 {scene.Extent - heightSum:+0;-0;0}, "
            + $"相对虚拟化 {scene.Extent - virtualizedExtent:+0;-0;0}");

        // 非虚拟化下点一次折叠: 被点那条应精确不动, extent 只变它自己。
        scene.ScrollToFraction(1);
        scene.Pump(3);
        for (var step = 0; step < 4; step++)
        {
            scene.Wheel(3);
            scene.Render();
        }
        var candidates = scene.VisibleFoldButtons();
        if (candidates.Count > 0)
        {
            var pick = candidates[^1];
            var message = pick.Message;
            var item = scene.Container(message);
            if (item is not null)
            {
                var topBefore = scene.TopOf(item);
                var heightBefore = item.Bounds.Height;
                var extentBefore = scene.Extent;
                watch.Restart();
                scene.Tap(pick.Button);
                scene.Pump(4);
                watch.Stop();
                var heightAfter = scene.Container(message)?.Bounds.Height ?? double.NaN;
                report.AppendLine($"非虚拟化点折叠: 耗时 {watch.ElapsedMilliseconds}ms, "
                    + $"被点位移 {scene.TopOf(scene.Container(message)!) - topBefore:+0;-0;0}, "
                    + $"自身高变 {heightAfter - heightBefore:+0;-0;0}, "
                    + $"extent 变 {scene.Extent - extentBefore:+0;-0;0}");
            }
        }

        File.WriteAllText(Path.Combine(OutputDirectory, "plain-panel.txt"), report.ToString());
        Assert.True(plainRealized > 0, "换成 StackPanel 后没有实体化任何项");
    }, TimeSpan.FromMinutes(10));
}
