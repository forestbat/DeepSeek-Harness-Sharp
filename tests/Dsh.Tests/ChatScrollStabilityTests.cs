using Avalonia.Headless;
using Avalonia.Controls;
using Dsh.Gui.ViewModels;

namespace Dsh.Tests;

/**
 * 硬性原则: LLM 输出(流式增量、新消息追加)不得改变用户正在看的位置。
 *
 * 断言的量是"用户看到的位置", 不是 offset: 列表面板在内容长高时会重估坐标并按锚点修正 offset,
 * 这时候 offset 会变、但屏幕上的内容不该动。只有"顶部那条消息的屏幕坐标"才是用户能感知的位置。
 */
[Collection(GuiSerialCollection.CollectionName)]
public sealed class ChatScrollStabilityTests
{
    private const double PositionTolerance = 2;

    /**
     * 流式期间允许的位置抖动(px): 面板按已实体化项重估行高, 布局取整以 0.5px 为步长, 一帧 1~3px 属于取整噪声;
     * 真正的"跳"是一整行以上(≥19px), 用这个阈值把两者分开。
     */
    private const double StreamingTolerance = 4;

    [Fact]
    public async Task StreamingIntoLastMessage_KeepsTheVisibleContentStill() => await HeadlessGui.RunAsync(async () =>
    {
        await using var scene = await ChatScene.CreateAsync();
        var streaming = scene.LastStreamingMessage();
        scene.ScrollToEnd();
        scene.Render();
        scene.ScrollToMiddle();
        var (anchor, anchorTop) = scene.TopVisible();

        for (var chunk = 0; chunk < 6; chunk++)
        {
            streaming.Append($"\n追加的一行 {chunk}: 这句是流式输出新增的内容, 会撑高当前消息。");
            streaming.FlushPending();
            scene.Render();
            AssertStill(scene, anchor, anchorTop, $"streaming chunk {chunk}", StreamingTolerance);
        }
    });

    [Fact]
    public async Task AppendingNewMessage_KeepsTheVisibleContentStill() => await HeadlessGui.RunAsync(async () =>
    {
        await using var scene = await ChatScene.CreateAsync();
        scene.ScrollToEnd();
        scene.Render();
        scene.ScrollToMiddle();
        var (anchor, anchorTop) = scene.TopVisible();

        for (var index = 0; index < 3; index++)
        {
            scene.ViewModel.Messages.Add(new MessageViewModel("结果", ChatScene.LongText(index), MessageKind.Result, false));
            scene.Render();
            AssertStill(scene, anchor, anchorTop, $"append {index}", StreamingTolerance);
        }
    });

    [Fact]
    public async Task ClickingTranscript_KeepsTheVisibleContentStill() => await HeadlessGui.RunAsync(async () =>
    {
        await using var scene = await ChatScene.CreateAsync();
        scene.ScrollToEnd();
        scene.Render();
        scene.ScrollToMiddle();
        var (anchor, anchorTop) = scene.TopVisible();

        scene.ClickInTranscript();
        scene.Settle();
        AssertStill(scene, anchor, anchorTop, "click");
    });

    /** 用户滚到中途后继续流式: 视口必须停在他滚到的位置。 */
    [Fact]
    public async Task ScrollingUpThenStreaming_StaysWhereTheUserLeft() => await HeadlessGui.RunAsync(async () =>
    {
        await using var scene = await ChatScene.CreateAsync();
        var streaming = scene.LastStreamingMessage();
        scene.ScrollToEnd();
        scene.Render();
        scene.Wheel(6);
        scene.Render();
        Assert.True(scene.Scroll.Offset.Y < scene.Scroll.Extent.Height - scene.Scroll.Viewport.Height - 50,
            "precondition: the list must be scrolled away from the tail");
        var (anchor, anchorTop) = scene.TopVisible();

        for (var chunk = 0; chunk < 6; chunk++)
        {
            streaming.Append($"\n流式新增 {chunk}: 用户已经滚开, 这一行不得把视口拉回底部。");
            streaming.FlushPending();
            scene.Render();
            AssertStill(scene, anchor, anchorTop, $"streaming chunk {chunk}", StreamingTolerance);
        }
    });

    /** 用户往上滚、紧接着流式内容长高: 同一帧里的两个意图, 用户的滚动画赢。 */
    [Fact]
    public async Task WheelUpWhileStreaming_KeepsTheUserPosition() => await HeadlessGui.RunAsync(async () =>
    {
        await using var scene = await ChatScene.CreateAsync();
        var streaming = scene.LastStreamingMessage();
        scene.ScrollToEnd();
        scene.Render();
        var tail = scene.Scroll.Offset.Y;

        scene.Wheel(3);
        scene.Render();
        Assert.True(scene.Scroll.Offset.Y < tail - PositionTolerance,
            $"precondition: the wheel must move the viewport (tail {tail:0}, now {scene.Scroll.Offset.Y:0})");
        var (anchor, anchorTop) = scene.TopVisible();

        for (var chunk = 0; chunk < 4; chunk++)
        {
            streaming.Append($"\n刷消息 {chunk}: 用户正在往上滚, 这句不得把视口拉回底部。");
            streaming.FlushPending();
            scene.Render();
            AssertStill(scene, anchor, anchorTop, $"streaming chunk {chunk}", StreamingTolerance);
        }
    });

    /** 中途往下滚一点点是"继续读下去", 不是"回到末尾"。 */
    [Fact]
    public async Task WheelDownMidListWhileStreaming_DoesNotAttachToTheTail() => await HeadlessGui.RunAsync(async () =>
    {
        await using var scene = await ChatScene.CreateAsync();
        var streaming = scene.LastStreamingMessage();
        scene.ScrollToEnd();
        scene.Render();
        scene.Wheel(8);
        scene.Render();
        Assert.True(scene.Scroll.Offset.Y < scene.Scroll.Extent.Height - scene.Scroll.Viewport.Height - 40,
            "precondition: the user is reading somewhere in the middle");

        scene.Wheel(-1);
        scene.Render();
        var (anchor, anchorTop) = scene.TopVisible();

        for (var chunk = 0; chunk < 5; chunk++)
        {
            streaming.Append($"\n刷消息 {chunk}: 用户在中途往下滚, 这不等于想贴底。");
            streaming.FlushPending();
            scene.Render();
            AssertStill(scene, anchor, anchorTop, $"streaming chunk {chunk}", StreamingTolerance);
        }
    });

    /** 展开/折叠之后再来的流式输出, 同样不得挪动用户正在看的那条消息。 */
    [Fact]
    public async Task TogglingThenStreaming_KeepsTheToggledMessageInPlace() => await HeadlessGui.RunAsync(async () =>
    {
        await using var scene = await ChatScene.CreateAsync();
        var streaming = scene.LastStreamingMessage();
        scene.ScrollToEnd();
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
            var after = scene.TopOf(scene.List, target);
            Assert.True(Math.Abs(after - before) <= PositionTolerance,
                $"streaming moved the toggled message: {before:0} → {after:0}");
        }
    });

    /** 顶部那条消息的屏幕位置不许动(它被回收时按"不在视口里"处理, 直接失败)。 */
    private static void AssertStill(ChatScene scene, MessageViewModel anchor, double anchorTop, string label,
        double tolerance = PositionTolerance)
    {
        var after = scene.TopOf(scene.List, anchor);
        Assert.True(Math.Abs(after - anchorTop) <= tolerance,
            $"{label}: the visible content moved {anchorTop:0} → {after:0} "
            + $"(offset {scene.Scroll.Offset.Y:0}, extent {scene.Scroll.Extent.Height:0})");
    }
}
