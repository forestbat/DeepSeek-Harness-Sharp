using System.Text;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Dsh.Tests;

/**
 * 探针(不进门禁, 用 -trait- "Category=Probe" 排除): 验证面板外推文档总高度所用的公式。
 *
 *     extent ≈ 已实体化区间末端 + 未实体化条数 × 平均高度
 *     平均高度 = 当前已实体化项的平均 DesiredSize
 *
 * 这条公式是判断"多余跳转"的依据是否正确的前提: 由它可推出, 一条涨 ΔH 会让
 * extent 变化 ΔH × (1 + M/N), 因此"extent 变化远超自身高度变化"是固有行为而非缺陷。
 * 曾据"余量"设过 400px 阈值, 正是错在没有先验证这条公式。
 *
 * 用来回答"上游把估算算法改掉之后, 这条公式是否还成立"。
 */
[Collection(GuiSerialCollection.CollectionName)]
[Trait("Category", "Probe")]
public sealed class VirtualizationExtentFormulaProbe
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
    public Task MeasureExtentDeltaAgainstSelfHeightDelta() => HeadlessGui.RunAsync(async () =>
    {
        var report = new StringBuilder();
        report.AppendLine("每次点一条折叠, 对比: 自身高变 / extent 实测变化 / 按 M/N 公式预测的余量");
        await using var scene = await RealChatScene.CreateAsync();
        scene.ScrollToFraction(1);
        scene.Settle();
        for (var step = 0; step < 4; step++)
        {
            scene.Wheel(3);
            scene.Render();
        }

        for (var round = 0; round < 5; round++)
        {
            var candidates = scene.VisibleFoldButtons();
            if (candidates.Count == 0)
                break;
            var pick = candidates[^1];
            var message = pick.Message;
            var item = scene.Container(message);
            if (item is null)
                break;

            var heightBefore = item.Bounds.Height;
            var extentBefore = scene.Extent;
            var realizedBefore = RealizedCount(scene);

            scene.Tap(pick.Button);
            scene.Pump(8);

            var container = scene.Container(message);
            if (container is null)
            {
                report.AppendLine($"round {round}: 容器被回收, 跳过");
                break;
            }
            var heightDelta = container.Bounds.Height - heightBefore;
            var extentDelta = scene.Extent - extentBefore;
            var realizedAfter = RealizedCount(scene);
            var unrealized = scene.MessageCount - realizedAfter;
            var predictedResidual = heightDelta * unrealized / Math.Max(1, realizedAfter);
            report.AppendLine($"round {round}: 高变 {heightDelta:+0;-0;0} "
                + $"实体化 {realizedBefore}→{realizedAfter} 未实体化 {unrealized} "
                + $"extent 实测变 {extentDelta:+0;-0;0} "
                + $"预测余量 {predictedResidual:+0;-0;0} 实测余量 {extentDelta - heightDelta:+0;-0;0}");

            var back = scene.EnsureFoldButtonVisible(message);
            if (back is null)
                break;
            scene.Tap(back.Value.Button);
            scene.Pump(8);
        }

        File.WriteAllText(Path.Combine(OutputDirectory, "extent-formula.txt"), report.ToString());
        // 只校验场景可用: 公式吻合度是观测值。
        Assert.Contains("round 0", report.ToString());
    }, TimeSpan.FromMinutes(10));

    private static int RealizedCount(RealChatScene scene)
        => scene.List.GetVisualDescendants().OfType<ListBoxItem>().Count(item => item.Bounds.Height > 0);
}
