using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Dsh.Gui.ViewModels;
using Dsh.Gui.Views;

namespace Dsh.Tests;

/**
 * 会话右侧的时间轨道: 每条用户消息一个刻度, 悬停出可滚动的预览, 点击跳到那条消息。
 * 这些用例跑真实的 ChatView, 不模拟业务逻辑。
 */
public sealed class TimelineRailTests
{
    /**
     * 行高差异极大、且大部分项还没实体化时, 刻度也不能挤成一堆 ——
     * 这正是用户看到的"刻度对不上消息": 实测顶端与平均行高混用, 未实体化的项全挤在锚点前面。
     */
    [Fact]
    public async Task RailMarksStayEvenlyOrderedWhenHeightsVaryWildly() => await HeadlessGui.RunAsync(async () =>
    {
        await using var scene = await ChatScene.CreateAsync();
        // 交替放"一行的提问"和"四十行的回答": 两者行高相差一个数量级。
        scene.ViewModel.Messages.Clear();
        for (var index = 0; index < 20; index++)
        {
            scene.ViewModel.Messages.Add(new MessageViewModel("用户", $"问题 {index}", MessageKind.User, false) { Seq = index * 2 + 1 });
            scene.ViewModel.Messages.Add(new MessageViewModel("结果", TallText(index), MessageKind.Result, false) { Seq = index * 2 + 2 });
        }
        scene.ScrollToEnd();
        scene.Settle();

        var rail = Rail(scene);
        var marks = rail.Marks;
        Assert.Equal(20, marks.Count);
        Assert.True(marks.Zip(marks.Skip(1)).All(pair => pair.First.Fraction < pair.Second.Fraction),
            "刻度必须严格递增");

        var gaps = marks.Zip(marks.Skip(1)).Select(pair => pair.Second.Fraction - pair.First.Fraction).ToList();
        var median = gaps.Order().ElementAt(gaps.Count / 2);
        Assert.True(gaps.Min() > median * 0.2,
            $"相邻刻度不该挤在一起: 最小间隔 {gaps.Min():0.0000}, 中位间隔 {median:0.0000}");
    });

    /** 刻度指哪儿就该跳到哪儿: 点完刻度后的滚动比例必须落在刻度位置上。 */
    [Fact]
    public async Task ClickingATickLandsAtItsOwnFractionOfTheTranscript() => await HeadlessGui.RunAsync(async () =>
    {
        await using var scene = await ChatScene.CreateAsync();
        var rail = Rail(scene);
        foreach (var index in new[] { 3, 10, 20 })
        {
            var fraction = rail.Marks[index].Fraction;
            ClickRail(scene, rail, index);
            scene.Settle();
            var actual = scene.Scroll.Offset.Y / Math.Max(scene.Scroll.Extent.Height, 1);
            Assert.InRange(Math.Abs(actual - fraction), 0, 0.05);
        }
    });

    /**
     * 刻度只对应"用户消息", 位置必须按消息在内容里的真实高度折算 ——
     * 否则竖线上的高度与右侧滚动条不是同一套坐标, 指到哪儿都跳不到那儿。
     */
    [Fact]
    public async Task RailMarksEveryUserMessageAtItsRealContentPosition() => await HeadlessGui.RunAsync(async () =>
    {
        await using var scene = await ChatScene.CreateAsync();
        scene.ScrollToMiddle();
        var rail = Rail(scene);
        var marks = rail.Marks;

        Assert.Equal(24, marks.Count);
        Assert.All(marks, mark => Assert.Equal(MessageKind.User, mark.Message.Kind));
        Assert.True(marks.Zip(marks.Skip(1)).All(pair => pair.First.Fraction < pair.Second.Fraction),
            "刻度必须按消息顺序单调递增");

        // 已实体化的那条: 刻度比例必须等于它在内容里的位置比例。
        var extent = scene.Scroll.Extent.Height;
        var measured = 0;
        foreach (var mark in marks)
        {
            var item = scene.List.GetVisualDescendants().OfType<ListBoxItem>()
                .FirstOrDefault(candidate => ReferenceEquals(candidate.DataContext, mark.Message));
            if (item?.TranslatePoint(default, scene.Scroll) is not { } position)
                continue;
            var expected = (scene.Scroll.Offset.Y + position.Y) / extent;
            Assert.InRange(Math.Abs(mark.Fraction - expected), 0, 0.01);
            measured++;
        }
        Assert.True(measured >= 2, $"至少要有两条刻度可核对, 实际 {measured}");
    });

    /** 最上面那个刻度 = 第一条用户消息: 从底部点它必须真的滚回开头(用户报的"跳不到第一条")。 */
    [Fact]
    public async Task ClickingTheFirstTickReachesTheFirstUserMessage() => await HeadlessGui.RunAsync(async () =>
    {
        await using var scene = await ChatScene.CreateAsync();
        var rail = Rail(scene);
        scene.ScrollToEnd();
        scene.Render();
        var first = rail.Marks[0].Message;
        Assert.False(IsVisible(scene, first), "precondition: 滚到底部时第一条不在视口里");

        ClickRail(scene, rail, 0);

        scene.Settle();
        var item = scene.List.GetVisualDescendants().OfType<ListBoxItem>()
            .FirstOrDefault(candidate => ReferenceEquals(candidate.DataContext, first));
        var top = item?.TranslatePoint(default, scene.Scroll)?.Y;
        Assert.True(IsVisible(scene, first),
            $"点最上面的刻度必须能看到第一条用户消息 (offset {scene.Scroll.Offset.Y:0}/{scene.Scroll.Extent.Height:0}, "
            + $"viewport {scene.Scroll.Viewport.Height:0}, top {top?.ToString("0") ?? "未实体化"}, "
            + $"firstMarkFraction {rail.Marks[0].Fraction:0.0000})");
        Assert.InRange(top!.Value, -1, 12);
    });

    /** 刻度之间的空白不是刻度: 点在那里什么都不做, 不能"就近跳到别的消息"。 */
    [Fact]
    public async Task ClickingTheEmptyRailDoesNotJump() => await HeadlessGui.RunAsync(async () =>
    {
        await using var scene = await ChatScene.CreateAsync();
        var rail = Rail(scene);
        scene.ScrollToEnd();
        scene.Render();
        var offset = scene.Scroll.Offset.Y;

        // 找一段离所有刻度都超过容差的纵坐标。
        var gap = Enumerable.Range(0, (int)rail.Bounds.Height)
            .Select(y => (double)y + 0.5)
            .First(y => rail.MarkAt(y) < 0);
        var fired = false;
        rail.Activated += _ => fired = true;

        var point = rail.TranslatePoint(new Point(rail.Bounds.Width / 2, gap), scene.Window)!.Value;
        scene.Window.MouseDown(point, MouseButton.Left);
        scene.Window.MouseUp(point, MouseButton.Left);
        scene.Settle();

        Assert.False(fired);
        Assert.Equal(offset, scene.Scroll.Offset.Y, 0);
    });

    /** 想拖转录自己的滚动条时不能触发预览: 轨道与滚动条必须是两件事。 */
    [Fact]
    public async Task PressingTheTranscriptScrollBarDoesNotOpenThePreview() => await HeadlessGui.RunAsync(async () =>
    {
        await using var scene = await ChatScene.CreateAsync();
        var preview = scene.Chat.FindControl<Border>("TimelinePreview")!;
        scene.ScrollToMiddle();
        scene.Render();
        var thumb = scene.Scroll.GetVisualDescendants().OfType<ScrollBar>()
            .First(bar => bar.Orientation == Avalonia.Layout.Orientation.Vertical && bar.IsEffectivelyVisible)
            .GetVisualDescendants().OfType<Thumb>().First(candidate => candidate.IsEffectivelyVisible);
        var start = thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), scene.Window)!.Value;

        scene.Window.MouseDown(start, MouseButton.Left);
        scene.Window.MouseMove(new Point(start.X, start.Y + 40), RawInputModifiers.LeftMouseButton);
        scene.Pump(2);

        Assert.False(preview.IsVisible, "拖转录滚动条不该弹出时间预览");

        scene.Window.MouseUp(new Point(start.X, start.Y + 40), MouseButton.Left);
        scene.Pump(2);
    });

    [Fact]
    public async Task ClickingATickScrollsToThatMessageAndStopsFollowing() => await HeadlessGui.RunAsync(async () =>
    {
        await using var scene = await ChatScene.CreateAsync();
        var rail = Rail(scene);
        scene.ScrollToEnd();
        scene.Render();
        var target = rail.Marks[3];
        Assert.False(IsVisible(scene, target.Message), "precondition: 目标消息在底部时不可见");
        var fired = -1;
        rail.Activated += index => fired = index;

        ClickRail(scene, rail, 3);

        scene.Settle();
        Assert.Equal(3, fired);
        Assert.True(IsVisible(scene, target.Message),
            $"点击刻度后目标消息必须出现在视口里 (rail {rail.Bounds.Width:0}x{rail.Bounds.Height:0}, marks {rail.Marks.Count}, offset {scene.Scroll.Offset.Y:0})");

        // 跳过去之后继续流式输出不许把人拉回底部。
        var before = scene.Scroll.Offset.Y;
        var streaming = scene.LastStreamingMessage();
        streaming.Append("跳转后继续输出");
        streaming.FlushPending();
        scene.Settle();
        Assert.Equal(before, scene.Scroll.Offset.Y, 0);
    });

    [Fact]
    public async Task HoveringATickOpensAScrollablePreviewAroundIt() => await HeadlessGui.RunAsync(async () =>
    {
        await using var scene = await ChatScene.CreateAsync();
        var rail = Rail(scene);
        var preview = scene.Chat.FindControl<Border>("TimelinePreview")!;
        var list = scene.Chat.FindControl<ListBox>("TimelinePreviewList")!;
        Assert.False(preview.IsVisible);

        Hover(scene, rail, 6);
        scene.Pump();

        Assert.True(preview.IsVisible);
        var scroll = Assert.IsType<ScrollViewer>(list.Scroll);
        var realized = list.GetVisualDescendants().OfType<ListBoxItem>().ToList();
        var centered = realized.FirstOrDefault(item => ReferenceEquals(item.DataContext, rail.Marks[6].Message));
        Assert.True(centered is not null,
            $"panel visible={preview.IsVisible} bounds={preview.Bounds} list={list.Bounds} "
            + $"viewport={scroll.Viewport.Height} items={list.ItemCount} offset={scroll.Offset.Y} "
            + $"extent={scroll.Extent.Height} realized={realized.Count} "
            + $"hosts={string.Join(" | ", realized.Select(item => (item.DataContext as MessageViewModel)?.Preview))}");
        var top = centered.TranslatePoint(default, scroll)!.Value.Y;
        Assert.InRange(top, 0, scroll.Viewport.Height);

        // 预览面板自己滚: 滚轮不许带动正文列表。
        var transcriptOffset = scene.Scroll.Offset.Y;
        list.Scroll.Offset = new Vector(0, scroll.Offset.Y + 40);
        scene.Pump();
        Assert.Equal(transcriptOffset, scene.Scroll.Offset.Y, 0);
        Assert.True(scroll.Offset.Y > 0);
    });

    /**
     * 从刻度往左移到预览面板上, 面板不能在被指针够到之前就消失。
     * 必须是**连续的小步移动**: 一次跳到面板中心会漏掉"轨道与面板之间那条缝"上的判定,
     * 而真实指针正是一帧一帧穿过那条缝的。
     */
    [Fact]
    public async Task MovingFromATickIntoThePreviewKeepsItOpen() => await HeadlessGui.RunAsync(async () =>
    {
        await using var scene = await ChatScene.CreateAsync();
        var rail = Rail(scene);
        var preview = scene.Chat.FindControl<Border>("TimelinePreview")!;

        Hover(scene, rail, 8);
        scene.Pump(2);
        Assert.True(preview.IsVisible);

        var scroll = Assert.IsType<ScrollViewer>(scene.Chat.FindControl<ListBox>("TimelinePreviewList")!.Scroll);
        var offsetBefore = scroll.Offset.Y;
        var start = rail.TranslatePoint(new Point(rail.Bounds.Width / 2, rail.OffsetOf(8)), scene.Window)!.Value;
        var target = preview.TranslatePoint(new Point(preview.Bounds.Width / 2, preview.Bounds.Height / 2), scene.Window)!.Value;

        // 按 1 像素一步一步走: 真实指针就是这么穿过轨道与面板之间那条缝的,
        // 大步跳过会把缝整段略过, 让"面板在缝上被关掉"这种回归测不出来。
        var steps = (int)Math.Min(400, Math.Max(1, Math.Abs(target.X - start.X)));
        for (var step = 1; step <= steps; step++)
        {
            var fraction = (double)step / steps;
            scene.Window.MouseMove(new Point(
                start.X + (target.X - start.X) * fraction,
                start.Y + (target.Y - start.Y) * fraction));
            scene.Pump();
            Assert.True(preview.IsVisible, $"指针走到第 {step}/{steps} 步时预览就消失了");
        }

        // 已经在面板上就不再重算悬停: 否则面板会随着指针在面板里移动而反复跳。
        Assert.Equal(offsetBefore, scroll.Offset.Y, 0);
    });

    /** 预览只列用户消息: 工具/思考这些中间状态不该出现在时间线的放大镜里。 */
    [Fact]
    public async Task PreviewListsOnlyUserMessages() => await HeadlessGui.RunAsync(async () =>
    {
        await using var scene = await ChatScene.CreateAsync();
        var rail = Rail(scene);
        var list = scene.Chat.FindControl<ListBox>("TimelinePreviewList")!;

        Hover(scene, rail, 5);
        scene.Pump(2);

        Assert.NotEmpty(list.Items);
        Assert.All(list.Items.Cast<MessageViewModel>(), message => Assert.Equal(MessageKind.User, message.Kind));
        Assert.All(rail.Marks, mark => Assert.Contains(list.Items.Cast<MessageViewModel>(), item => ReferenceEquals(item, mark.Message)));
    });

    /** 拖预览自己的滚动条时面板不能关: 指针会被 Thumb 捕获, 甩到面板外也不该触发隐藏。 */
    [Fact]
    public async Task DraggingThePreviewScrollBarKeepsThePreviewOpen() => await HeadlessGui.RunAsync(async () =>
    {
        await using var scene = await ChatScene.CreateAsync();
        var rail = Rail(scene);
        var preview = scene.Chat.FindControl<Border>("TimelinePreview")!;
        var list = scene.Chat.FindControl<ListBox>("TimelinePreviewList")!;

        Hover(scene, rail, 10);
        scene.Pump(2);
        Assert.True(preview.IsVisible);
        var thumb = list.GetVisualDescendants().OfType<ScrollBar>()
            .First(bar => bar.Orientation == Avalonia.Layout.Orientation.Vertical && bar.IsEffectivelyVisible)
            .GetVisualDescendants().OfType<Thumb>().First(candidate => candidate.IsEffectivelyVisible);
        var start = thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), scene.Window)!.Value;

        scene.Window.MouseDown(start, MouseButton.Left);
        // 往左边拖出面板: 指针已经在面板外了, 但拖拽期间不许关。
        scene.Window.MouseMove(new Point(start.X - 120, start.Y + 60), RawInputModifiers.LeftMouseButton);
        scene.Pump(2);
        Assert.True(preview.IsVisible, "拖动预览滚动条时预览不能消失");

        scene.Window.MouseUp(new Point(start.X - 120, start.Y + 60), MouseButton.Left);
        scene.Pump(2);
    });

    [Fact]
    public async Task LeavingTheRailHidesThePreview() => await HeadlessGui.RunAsync(async () =>
    {
        await using var scene = await ChatScene.CreateAsync();
        var rail = Rail(scene);
        var preview = scene.Chat.FindControl<Border>("TimelinePreview")!;

        Hover(scene, rail, 2);
        scene.Pump();
        Assert.True(preview.IsVisible);

        var outside = scene.Scroll.TranslatePoint(new Point(40, 40), scene.Window)!.Value;
        scene.Window.MouseMove(outside);
        scene.Pump(2);
        Assert.False(preview.IsVisible);
    });

    /** 四十行的回答: 与一行提问的行高差一个数量级, 用来暴露"按平均行高估算"的破绽。 */
    private static string TallText(int index)
    {
        var text = new System.Text.StringBuilder();
        for (var line = 0; line < 40; line++)
            text.AppendLine($"第 {index} 条回答第 {line} 行: 用来撑高消息卡片。");
        return text.ToString();
    }

    private static TimelineRail Rail(ChatScene scene) => scene.Chat.FindControl<TimelineRail>("Rail")!;

    private static void Hover(ChatScene scene, TimelineRail rail, int index)
    {
        rail.Rebuild();
        var point = rail.TranslatePoint(new Point(rail.Bounds.Width / 2, rail.OffsetOf(index)), scene.Window)!.Value;
        scene.Window.MouseMove(point);
    }

    private static void ClickRail(ChatScene scene, TimelineRail rail, int index)
    {
        rail.Rebuild();
        var point = rail.TranslatePoint(new Point(rail.Bounds.Width / 2, rail.OffsetOf(index)), scene.Window)!.Value;
        scene.Window.MouseDown(point, MouseButton.Left);
        scene.Window.MouseUp(point, MouseButton.Left);
    }

    private static bool IsVisible(ChatScene scene, MessageViewModel message)
    {
        var item = scene.List.GetVisualDescendants().OfType<ListBoxItem>()
            .FirstOrDefault(candidate => ReferenceEquals(candidate.DataContext, message));
        if (item?.TranslatePoint(default, scene.Scroll) is not { } position)
            return false;
        return position.Y >= -1 && position.Y + item.Bounds.Height <= scene.Scroll.Viewport.Height + 1;
    }
}
