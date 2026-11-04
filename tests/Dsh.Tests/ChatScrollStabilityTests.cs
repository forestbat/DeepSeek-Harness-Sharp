using Avalonia.Headless;
using Dsh.Gui.ViewModels;

namespace Dsh.Tests;

/**
 * 硬性原则: LLM 输出(流式增量、新消息追加)不得改变滚动条位置。
 * 用户在读历史时, 任何内容增长都必须保持 offset 不变; 只有内容本来就在底部时才跟随。
 */
[Collection(GuiSerialCollection.CollectionName)]
public sealed class ChatScrollStabilityTests
{
    private const double PositionTolerance = 1;

    [Fact]
    public async Task StreamingIntoLastMessage_DoesNotMoveOffset() => await HeadlessGui.RunAsync(async () =>
    {
        await using var scene = await ChatScene.CreateAsync();
        var streaming = scene.LastStreamingMessage();
        scene.Scroll.ScrollToEnd();
        scene.Render();
        scene.ScrollToMiddle();
        var offset = scene.Scroll.Offset.Y;

        for (var chunk = 0; chunk < 6; chunk++)
        {
            streaming.Append($"\n追加的一行 {chunk}: 这句是流式输出新增的内容, 会撑高当前消息。");
            streaming.FlushPending();
            scene.Render();
            Assert.Equal(offset, scene.Scroll.Offset.Y, PositionTolerance);
        }
    });

    [Fact]
    public async Task AppendingNewMessage_DoesNotMoveOffset() => await HeadlessGui.RunAsync(async () =>
    {
        await using var scene = await ChatScene.CreateAsync();
        scene.Scroll.ScrollToEnd();
        scene.Render();
        scene.ScrollToMiddle();
        var offset = scene.Scroll.Offset.Y;

        for (var index = 0; index < 3; index++)
        {
            scene.ViewModel.Messages.Add(new MessageViewModel("结果", ChatScene.LongText(index), MessageKind.Result, false));
            scene.Render();
            Assert.Equal(offset, scene.Scroll.Offset.Y, PositionTolerance);
        }
    });

    [Fact]
    public async Task ClickingTranscript_DoesNotMoveOffset() => await HeadlessGui.RunAsync(async () =>
    {
        await using var scene = await ChatScene.CreateAsync();
        scene.Scroll.ScrollToEnd();
        scene.Render();
        scene.ScrollToMiddle();
        var offset = scene.Scroll.Offset.Y;

        scene.ClickInTranscript();
        scene.Settle();
        Assert.Equal(offset, scene.Scroll.Offset.Y, PositionTolerance);
    });

    /** 用户滚到中途后, 即使紧接着有流式输出, 后续的新增也不得把视口拽回底部。 */
    [Fact]
    public async Task ScrollingUpThenStreaming_StaysWhereTheUserLeft() => await HeadlessGui.RunAsync(async () =>
    {
        await using var scene = await ChatScene.CreateAsync();
        var streaming = scene.LastStreamingMessage();
        scene.Scroll.ScrollToEnd();
        scene.Render();
        scene.Wheel(6);
        scene.Render();
        var offset = scene.Scroll.Offset.Y;
        Assert.True(offset < scene.Scroll.Extent.Height - scene.Scroll.Viewport.Height - 50,
            "precondition: the list must be scrolled away from the tail");

        for (var chunk = 0; chunk < 6; chunk++)
        {
            streaming.Append($"\n流式新增 {chunk}: 用户已经滚开, 这一行不得把视口拉回底部。");
            streaming.FlushPending();
            scene.Render();
            Assert.Equal(offset, scene.Scroll.Offset.Y, PositionTolerance);
        }
    });

    /** 展开/折叠之后再来的流式输出, 同样不得挪动用户正在看的那条消息。 */
    [Fact]
    public async Task TogglingThenStreaming_KeepsTheToggledMessageInPlace() => await HeadlessGui.RunAsync(async () =>
    {
        await using var scene = await ChatScene.CreateAsync();
        var streaming = scene.LastStreamingMessage();
        scene.Scroll.ScrollToEnd();
        scene.Render();
        scene.ScrollToMiddle();
        var target = scene.FindVisibleFoldTarget();
        scene.ToggleAndAssertPosition(target, "mid-list expand");
        var before = scene.TopOf(scene.List, target);

        for (var chunk = 0; chunk < 4; chunk++)
        {
            streaming.Append($"\n折叠后继续输出 {chunk}");
            streaming.FlushPending();
            scene.Render();
            Assert.Equal(before, scene.TopOf(scene.List, target), PositionTolerance);
        }
    });
}
