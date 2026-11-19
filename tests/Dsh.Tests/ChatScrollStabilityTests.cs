using Avalonia;
using Avalonia.Headless;
using Avalonia.VisualTree;
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

    /**
     * 用户已经在底部、再往下滚一格: 视口一动没动, 这不等于"离开底部"。
     * 这趟手势留下的标记若泄漏到随后那条流式增长的布局帧, 就会按已经长高的 extent 判成"不在底部",
     * 视口被拽回中段(现场表现为跳回会话开头)。
     */
    [Fact]
    public async Task WheelDownAtTheTailThenStreaming_KeepsFollowing() => await HeadlessGui.RunAsync(async () =>
    {
        await using var scene = await ChatScene.CreateAsync();
        var streaming = scene.LastStreamingMessage();
        scene.ScrollToEnd();
        scene.Render();
        var tail = scene.Scroll.Offset.Y;

        scene.Wheel(-1);
        scene.Render();
        Assert.True(Math.Abs(scene.Scroll.Offset.Y - tail) <= PositionTolerance,
            $"precondition: the wheel at the tail must not move the viewport ({tail:0} → {scene.Scroll.Offset.Y:0})");

        for (var chunk = 0; chunk < 5; chunk++)
        {
            streaming.Append($"\n刷消息 {chunk} A: 用户已经在底部还往下滚, 这不等于离开底部。"
                + $"\n刷消息 {chunk} B: 这一行只是撑高当前消息, 好让误判有可见后果。"
                + $"\n刷消息 {chunk} C: 视口必须停在末尾。");
            streaming.FlushPending();
            scene.Render();
            AssertAtTail(scene, $"streaming chunk {chunk}");
        }
    });

    /**
     * 滚轮打在内层滚动区域(工具的长时间结果)上: 内层吃掉这一格, 外层的跟随意图不该被改判。
     * 随后流式追加仍须贴底。
     */
    [Fact]
    public async Task WheelOverNestedScrollAreaThenStreaming_KeepsFollowing() => await HeadlessGui.RunAsync(async () =>
    {
        await using var scene = await ChatScene.CreateAsync();
        var streaming = scene.LastStreamingMessage();
        AddLongToolResultBeforeTail(scene);
        scene.ScrollToEnd();
        scene.Render();

        var inner = scene.List.GetVisualDescendants().OfType<ScrollViewer>()
            .FirstOrDefault(candidate => !ReferenceEquals(candidate, scene.Scroll)
                && candidate.IsEffectivelyVisible
                && candidate.Extent.Height - candidate.Viewport.Height > 1);
        Assert.NotNull(inner);
        var outerBefore = scene.Scroll.Offset.Y;
        var point = inner!.TranslatePoint(new Point(inner.Bounds.Width / 2, inner.Bounds.Height / 2), scene.Window)!.Value;

        scene.Window.MouseWheel(point, new Vector(0, -1));
        scene.Render();
        Assert.True(inner.Offset.Y > 0, "precondition: the wheel must scroll the inner area");
        Assert.True(Math.Abs(scene.Scroll.Offset.Y - outerBefore) <= PositionTolerance,
            $"precondition: the outer list must not move ({outerBefore:0} → {scene.Scroll.Offset.Y:0})");

        for (var chunk = 0; chunk < 5; chunk++)
        {
            streaming.Append($"\n刷消息 {chunk} A: 滚轮落在内层, 外层不该因此判成离开底部。"
                + $"\n刷消息 {chunk} B: 这一行只是撑高当前消息, 好让误判有可见后果。"
                + $"\n刷消息 {chunk} C: 视口必须停在末尾。");
            streaming.FlushPending();
            scene.Render();
            AssertAtTail(scene, $"streaming chunk {chunk}");
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

    /** 底边附近(还没贴底)展开折叠, 之后继续流式输出: 被点那条不许被顶走。 */
    [Fact]
    public async Task TogglingNearTheBottomThenStreaming_KeepsTheToggledMessageInPlace() => await HeadlessGui.RunAsync(async () =>
    {
        await using var scene = await ChatScene.CreateAsync();
        // 小窗口更接近用户实际的阅读区: 视口里只放得下几条, 虚拟化的边界效应更明显。
        scene.Window.Width = 900;
        scene.Window.Height = 520;
        scene.Render();
        var streaming = scene.LastStreamingMessage();
        scene.ScrollToEnd();
        scene.Render();
        // 用真实滚轮滚到底边附近(不能用程序写 offset: 跟随意图只在用户输入时重判)。
        var maximum = Math.Max(0, scene.Scroll.Extent.Height - scene.Scroll.Viewport.Height);
        for (var attempt = 0; attempt < 40; attempt++)
        {
            if (scene.Scroll.Offset.Y < maximum - 300)
                break;
            scene.Wheel(6);
            scene.Render();
        }
        Assert.True(scene.Scroll.Offset.Y < maximum - 100,
            $"precondition: the list must sit near, but not at, the tail (offset {scene.Scroll.Offset.Y:0}, max {maximum:0})");

        var target = scene.List.GetVisualDescendants().OfType<Button>()
            .Where(candidate => candidate is { IsEffectivelyVisible: true, DataContext: MessageViewModel { IsFoldable: true } })
            .OrderBy(candidate => scene.TopOf(candidate))
            .LastOrDefault(candidate => scene.TopOf(candidate) < scene.Scroll.Viewport.Height - 40);
        Assert.NotNull(target);
        var message = Assert.IsType<MessageViewModel>(target!.DataContext);

        scene.ToggleAndAssertPosition(message, "near-bottom expand");
        var before = scene.TopOf(scene.List, message);

        for (var chunk = 0; chunk < 4; chunk++)
        {
            streaming.Append($"\n折叠后继续输出 {chunk}");
            streaming.FlushPending();
            scene.Render();
            var after = scene.TopOf(scene.List, message);
            Assert.True(Math.Abs(after - before) <= PositionTolerance,
                $"streaming moved the toggled message: {before:0} → {after:0}");
        }
    });

    /**
     * 用户报的回归: 展开底部附近的折叠后再向上滚, 流式输出必须完全不影响视口
     * (曾经表现为滚动条被消息刷新一路下拉)。
     */
    [Fact]
    public async Task ScrollingUpAfterTogglingNearTheBottom_IsUntouchedByStreaming() => await HeadlessGui.RunAsync(async () =>
    {
        await using var scene = await ChatScene.CreateAsync();
        scene.Window.Width = 900;
        scene.Window.Height = 520;
        scene.Render();
        var streaming = scene.LastStreamingMessage();
        scene.ScrollToEnd();
        scene.Render();
        var maximum = Math.Max(0, scene.Scroll.Extent.Height - scene.Scroll.Viewport.Height);
        for (var attempt = 0; attempt < 40 && scene.Scroll.Offset.Y >= maximum - 300; attempt++)
        {
            scene.Wheel(6);
            scene.Render();
        }

        var target = scene.List.GetVisualDescendants().OfType<Button>()
            .Where(candidate => candidate is { IsEffectivelyVisible: true, DataContext: MessageViewModel { IsFoldable: true } })
            .OrderBy(candidate => scene.TopOf(candidate))
            .Last();
        scene.Tap(target);
        scene.Settle();

        scene.Wheel(6);
        scene.ScrollToMiddle();
        var (anchor, anchorTop) = scene.TopVisible();

        for (var chunk = 0; chunk < 4; chunk++)
        {
            streaming.Append($"\n展开后继续输出 {chunk}");
            streaming.FlushPending();
            scene.Render();
            AssertStill(scene, anchor, anchorTop, $"streaming after toggle {chunk}", StreamingTolerance);
        }
    });

    /** 视口离末尾 24px 以内就算"跟随中"(与 ChatView 的贴底容差同一量级)。 */
    private const double TailTolerance = 24;

    /** 在末尾那条(正在流式输出的)消息之前插一条展开的长工具结果: 它自带一个可滚动的内层视口。 */
    private static void AddLongToolResultBeforeTail(ChatScene scene)
    {
        var tool = new MessageViewModel("bash", "工具调用", MessageKind.Tool, false)
        {
            Seq = 1_000,
            ArgumentText = "dotnet test",
        };
        tool.SetToolResult(string.Join('\n', Enumerable.Range(0, 60)
            .Select(line => $"第 {line} 行工具结果: 用来撑高内层滚动区域。")), false);
        tool.IsExpanded = true;
        scene.ViewModel.Messages.Insert(scene.ViewModel.Messages.Count - 1, tool);
        scene.Render();
    }

    /** 跟随态判据: 视口离末尾不超过容差。 */
    private static void AssertAtTail(ChatScene scene, string label)
    {
        var scroll = scene.Scroll;
        var maximum = scroll.Extent.Height - scroll.Viewport.Height;
        Assert.True(maximum - scroll.Offset.Y <= TailTolerance,
            $"{label}: following was lost: offset {scroll.Offset.Y:0}, max {maximum:0}, gap {maximum - scroll.Offset.Y:0}");
    }

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
