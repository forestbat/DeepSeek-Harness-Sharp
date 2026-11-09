using System.Reflection;
using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace Dsh.Tests;

/**
 * 探针(不进门禁, 用 -trait- "Category=Probe" 排除): 面板算出的文档总高度, 与"实测高度逐条累加"差多少。
 *
 * 背景: `VirtualizingStackPanel` 把总高度估成"已实体化区间末端 + 未实体化条数 × 平均行高",
 * 而"平均行高"只取当前已实体化项。滚到末尾时未实体化条数为 0, 于是
 *     extent ≡ StartU + Σ已实体化高度
 * StartU 一旦被 ValidateStartU 判为不可信, 面板就用 `offset / 平均行高` 反推, 误差从此常驻。
 *
 * 真值只有一条路能拿到: 换成非虚拟化面板铺一次, Extent 就等于实测高度逐条累加。
 *
 * 用来回答"升级 Avalonia 后上游 #17831 有没有修好"。实测基线(237 条消息的会话): 虚拟化 19103 / 真值 16661, 高估 2442px(+15%)。
 */
[Collection(GuiSerialCollection.CollectionName)]
[Trait("Category", "Probe")]
public sealed class VirtualizationStartUProbe
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
    public Task ReportStartUErrorAgainstTheTrueTotal() => HeadlessGui.RunAsync(async () =>
    {
        var report = new StringBuilder();
        var (realTotal, realPrefix) = await MeasureTrueHeightsAsync();
        report.AppendLine($"真实总高 {realTotal:0}");

        await using var scene = await RealChatScene.CreateAsync();
        scene.ScrollToFraction(1);
        scene.Settle();
        for (var step = 0; step < 4; step++)
        {
            scene.Wheel(3);
            scene.Render();
        }
        var panel = scene.List.GetVisualDescendants().OfType<VirtualizingStackPanel>().First();
        var realizedField = typeof(VirtualizingStackPanel)
            .GetField("_realizedElements", BindingFlags.Instance | BindingFlags.NonPublic)!;

        // 静置一次, 再点一次折叠, 各记一帧: 后者正是"高度变化把 StartU 判废"的触发点。
        for (var frame = 0; frame < 4; frame++)
        {
            Record(report, scene, panel, realizedField, realTotal, realPrefix, frame);
            if (frame != 1)
            {
                scene.Pump(1);
                continue;
            }
            var candidates = scene.VisibleFoldButtons();
            if (candidates.Count > 0)
                scene.Tap(candidates[^1].Button);
            scene.Pump(1);
        }

        File.WriteAllText(Path.Combine(OutputDirectory, "startu-error.txt"), report.ToString());
        // 探针只校验"场景可用", 不设阈值: 具体误差是观测值, 不是门禁。
        Assert.True(realTotal > 0, "拿到真实总高失败");
        Assert.True(scene.MessageCount > 0);
    }, TimeSpan.FromMinutes(10));

    private static void Record(StringBuilder report, RealChatScene scene, VirtualizingStackPanel panel,
        FieldInfo realizedField, double realTotal, double[] realPrefix, int frame)
    {
        var realized = realizedField.GetValue(panel);
        if (realized is null)
        {
            report.AppendLine($"frame {frame}: 读不到 _realizedElements");
            return;
        }
        var type = realized.GetType();
        var startU = (double)type.GetProperty("StartU")!.GetValue(realized)!;
        var first = (int)type.GetProperty("FirstIndex")!.GetValue(realized)!;
        var truePrefix = first >= 0 && first < realPrefix.Length ? realPrefix[first] : double.NaN;
        report.AppendLine($"frame {frame}: 区间起点下标={first} "
            + $"StartU={(double.IsNaN(startU) ? "NaN(不可信)" : startU.ToString("0"))} "
            + $"真实前缀和={truePrefix:0} "
            + $"StartU误差={(double.IsNaN(startU) ? double.NaN : startU - truePrefix):+0;-0;0} "
            + $"extent={scene.Extent:0} 真值={realTotal:0} extent误差={scene.Extent - realTotal:+0;-0;0} "
            + $"高估={(scene.Extent / Math.Max(1, realTotal) - 1) * 100:+0.0;-0.0;0}%");
    }

    /** 用非虚拟化面板拿每条的实测高度, 返回(总高, 前缀和数组)。 */
    private static async Task<(double Total, double[] Prefix)> MeasureTrueHeightsAsync()
    {
        await using var probe = await RealChatScene.CreateAsync();
        probe.List.ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Orientation = Orientation.Vertical });
        probe.Render();
        probe.Window.UpdateLayout();

        var count = probe.MessageCount;
        var heights = new double[count];
        foreach (var item in probe.List.GetVisualDescendants().OfType<ListBoxItem>())
        {
            var index = probe.List.IndexFromContainer(item);
            if (index >= 0 && index < count)
                heights[index] = item.Bounds.Height;
        }
        var prefix = new double[count + 1];
        for (var index = 0; index < count; index++)
            prefix[index + 1] = prefix[index] + heights[index];
        return (prefix[count], prefix);
    }
}
