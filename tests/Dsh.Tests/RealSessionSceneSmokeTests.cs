using Avalonia.VisualTree;

namespace Dsh.Tests;

/** 真实会话场景的夹具自检: 能读到会话、列表真实虚拟化、能找到可点的折叠按钮。 */
[Collection(GuiSerialCollection.CollectionName)]
public sealed class RealSessionSceneSmokeTests
{
    [Fact]
    public Task RealSession_LoadsIntoTheChatViewWithVirtualizedMessages() => HeadlessGui.RunAsync(async () =>
    {
        await using var scene = await RealChatScene.CreateAsync();

        Assert.True(scene.MessageCount > 20, $"真实会话应有足量消息, 实际 {scene.MessageCount}");
        var realized = scene.List.GetVisualDescendants().OfType<Avalonia.Controls.ListBoxItem>().Count();
        Assert.True(realized is > 0 and var count && count < scene.MessageCount,
            $"列表必须虚拟化: 已实体化 {realized}, 总条数 {scene.MessageCount}");
        Assert.True(scene.Extent > scene.Viewport,
            $"内容应可滚动: extent {scene.Extent:0} viewport {scene.Viewport:0}");

        scene.ScrollToFraction(1);
        scene.Settle();
        scene.Wheel(6);
        scene.Render();
        Assert.True(scene.VisibleFoldButtons().Count > 0, "视口内应能找到可折叠消息");
    });
}
