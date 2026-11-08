using Avalonia;
using Avalonia.Controls;
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
    [Fact]
    public async Task RailMarksEveryUserMessageAtItsScrollPosition() => await HeadlessGui.RunAsync(async () =>
    {
        await using var scene = await ChatScene.CreateAsync();
        var rail = Rail(scene);
        var marks = rail.Marks;

        Assert.Equal(24, marks.Count);
        Assert.All(marks, mark => Assert.Equal(MessageKind.User, mark.Message.Kind));
        Assert.Equal(0, marks[0].Fraction);
        // 最后一条用户消息在整篇里并不在末尾(后面还有一条结果), 所以刻度不到 1。
        Assert.InRange(marks[^1].Fraction, 0.9, 1);
        Assert.True(marks.Zip(marks.Skip(1)).All(pair => pair.First.Fraction < pair.Second.Fraction),
            "刻度必须按消息顺序单调递增");
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
