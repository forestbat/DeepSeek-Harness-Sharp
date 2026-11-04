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

    /**
     * 消息往下刷的同时用户往上滚(同一帧内先后到达): 用户的滚动必须赢。
     * 输入事件与布局刷新的先后顺序不保证, 贴底逻辑若不避让就会把视口拽回底部。
     */
    [Fact]
    public async Task WheelUpWhileStreaming_KeepsTheUserPosition() => await HeadlessGui.RunAsync(async () =>
    {
        await using var scene = await ChatScene.CreateAsync();
        var streaming = scene.LastStreamingMessage();
        scene.Scroll.ScrollToEnd();
        scene.Render();
        var tail = scene.Scroll.Offset.Y;

        // 同一帧: 用户先把视口滚走, 紧接着流式内容长高。
        scene.Wheel(3);
        scene.Render();
        var afterWheel = scene.Scroll.Offset.Y;
        Assert.True(afterWheel < tail - PositionTolerance,
            $"precondition: the wheel must move the viewport (tail {tail:0}, now {afterWheel:0})");

        for (var chunk = 0; chunk < 4; chunk++)
        {
            streaming.Append($"\n刷消息 {chunk}: 用户正在往上滚, 这句不得把视口拉回底部。");
            streaming.FlushPending();
            scene.Render();
            Assert.Equal(afterWheel, scene.Scroll.Offset.Y, PositionTolerance);
        }
    });

    /**
     * 用户在中途往下滚(没有到到底的意愿), 同时消息继续往下刷: 视口必须停在他滚到的位置。
     * 跟随只按"用户的落点是否到底"判定, 中途向下滚不得被当成想跟随。
     */
    [Fact]
    public async Task WheelDownMidListWhileStreaming_DoesNotAttachToTheTail() => await HeadlessGui.RunAsync(async () =>
    {
        await using var scene = await ChatScene.CreateAsync();
        var streaming = scene.LastStreamingMessage();
        scene.Scroll.ScrollToEnd();
        scene.Render();
        scene.Wheel(8);
        scene.Render();
        Assert.True(scene.Scroll.Offset.Y < scene.Scroll.Extent.Height - scene.Scroll.Viewport.Height - 40,
            "precondition: the user is reading somewhere in the middle");

        // 中途往下滚一点点: 这是"继续读下去", 不是"回到末尾"。
        scene.Wheel(-1);
        scene.Render();
        var afterWheel = scene.Scroll.Offset.Y;

        for (var chunk = 0; chunk < 5; chunk++)
        {
            streaming.Append($"\n刷消息 {chunk}: 用户在中途往下滚, 这不等于想贴底。");
            streaming.FlushPending();
            scene.Render();
            Assert.Equal(afterWheel, scene.Scroll.Offset.Y, PositionTolerance);
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
