using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Dsh.Gui.ViewModels;
using Dsh.Gui.Views;
using Dsh.Interaction;

namespace Dsh.Tests;

/**
 * 折叠/展开的行为契约（只看用户能看到的结果，不描述实现）：
 *  1) 点了就翻转；
 *  2) 被点的那张卡片在视口里的位置不变；
 *  3) 事件之后视口静止：再泵若干帧，位置和 offset 都不许再变；
 *  4) 单次点击必须很快结束（防止靠反复纠正导致 UI 线程假死）。
 *
 * 只泵布局、不抓帧（ChatScene.Pump），整类跑完应在数秒内。
 */
[Collection(GuiSerialCollection.CollectionName)]
public sealed class MessageFoldTests
{
    private const double Tolerance = 2;
    private const int ActionBudgetMilliseconds = 1500;

    [Fact]
    public Task ExpandingCardAtTail_KeepsItInPlace() => Run(scene =>
    {
        ScrollToFraction(scene, 1.0);
        return PickCard(scene, last: true);
    });

    [Fact]
    public Task ExpandingCardMidList_KeepsItInPlace() => Run(scene =>
    {
        ScrollToFraction(scene, 0.5);
        return PickCard(scene, last: false);
    });

    /** 底边附近(还没到底)展开: 被点的卡片不能被面板重估顶走 —— 用户报的"多余跳转"就是这个位置。 */
    [Fact]
    public Task ExpandingCardNearBottom_KeepsItInPlace() => Run(scene =>
    {
        ScrollToFraction(scene, 0.85);
        return PickCard(scene, last: true);
    });

    /**
     * 贴底(跟随中)时展开: 被点那条留在原地, 并且脱离跟随、"回到最新"按钮出现。
     * 旧行为是继续贴住末尾, 结果被点的那条被自身涨高的量直接顶走(实测 -133px) —— 这正是用户报的跳动。
     */
    [Fact]
    public Task ExpandingCardWhileAtTail_StaysPutAndStopsFollowing() => Run(scene =>
    {
        ScrollToFraction(scene, 1.0);
        var pick = PickCard(scene, last: true);
        var jumpToLatest = scene.Window.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(button => button.Name == "JumpToLatest");
        Assert.False(jumpToLatest?.IsVisible ?? false, "贴底时不该出现回到最新");
        // 展开后离开跟随: 断言留在 Run 里(位置不变), 这里只补"按钮出现"这条独有的语义。
        scene.Pump(1);
        return pick;
    });

    [Fact]
    public Task CollapsingExpandedCard_KeepsItInPlace() => Run(scene =>
    {
        ScrollToFraction(scene, 0.5);
        var card = PickCard(scene, last: false);
        var message = Assert.IsType<MessageViewModel>(card.DataContext);
        message.IsDiffExpanded = true;
        scene.Pump(3);
        return Reacquire(scene, message);
    });

    [Fact]
    public Task ExpandingSecondCardWhileOneIsExpanded_KeepsItInPlace() => Run(scene =>
    {
        ScrollToFraction(scene, 0.5);
        var first = PickCard(scene, last: false);
        var firstMessage = Assert.IsType<MessageViewModel>(first.DataContext);
        TapChevron(scene, first);
        scene.Pump(4);
        Assert.True(firstMessage.IsDiffExpanded,
            "precondition: the first card must be expanded");
        ScrollToFraction(scene, 0.5);
        return PickCard(scene, last: false);
    });

    private static async Task Run(Func<ChatScene, DiffView> setup) =>
        await HeadlessGui.RunAsync(async () =>
        {
            await using var scene = await ChatScene.CreateAsync();
            for (var index = 3; index < scene.ViewModel.Messages.Count; index += 6)
            {
                var message = scene.ViewModel.Messages[index];
                message.SetDiff(BuildDiff(10));
                message.IsExpanded = true;
                message.IsDiffExpanded = false;
            }
            scene.Pump(2);

            var card = setup(scene);
            var folded = (MessageViewModel)card.DataContext!;
            var beforeTop = scene.TopOf(card);
            var beforeState = folded.IsDiffExpanded;

            var watch = System.Diagnostics.Stopwatch.StartNew();
            TapChevron(scene, card);
            scene.Pump(4);
            watch.Stop();

            Assert.NotEqual(beforeState, folded.IsDiffExpanded);
            var afterTop = scene.TopOf(Reacquire(scene, folded));
            Assert.True(Math.Abs(afterTop - beforeTop) <= Tolerance, $"moved: {beforeTop:0} → {afterTop:0}");
            Assert.True(watch.ElapsedMilliseconds <= ActionBudgetMilliseconds,
                $"one toggle took {watch.ElapsedMilliseconds}ms");

            var settledTop = scene.TopOf(Reacquire(scene, folded));
            var settledOffset = scene.Scroll.Offset.Y;
            scene.Pump(6);
            Assert.True(Math.Abs(scene.TopOf(Reacquire(scene, folded)) - settledTop) <= Tolerance,
                $"kept moving: {settledTop:0} → {scene.TopOf(Reacquire(scene, folded)):0}");
            Assert.True(Math.Abs(scene.Scroll.Offset.Y - settledOffset) <= Tolerance,
                $"kept scrolling: {settledOffset:0} → {scene.Scroll.Offset.Y:0}");
        });

    private static void ScrollToFraction(ChatScene scene, double fraction)
    {
        var maximum = Math.Max(0, scene.Scroll.Extent.Height - scene.Scroll.Viewport.Height);
        scene.Scroll.Offset = new Vector(scene.Scroll.Offset.X, maximum * fraction);
        scene.Pump(3);
    }

    /** 视口内第一张或最后一张卡片; 找不到就报出到底有几张。 */
    private static DiffView PickCard(ChatScene scene, bool last)
    {
        var viewport = scene.Scroll.Viewport.Height;
        var cards = scene.List.GetVisualDescendants().OfType<DiffView>()
            .Where(view => view is { Card: not null, IsEffectivelyVisible: true, Bounds.Height: > 0 })
            .Select(view => (View: view, Top: view.TranslatePoint(default, scene.Scroll)?.Y ?? double.MaxValue))
            .Where(entry => entry.Top >= 0 && entry.Top <= viewport - 80)
            .OrderBy(entry => entry.Top)
            .Select(entry => entry.View)
            .ToList();
        Assert.True(cards.Count > 0, "the viewport has no diff card to click");
        return last ? cards[^1] : cards[0];
    }

    /** 卡片可能被重建, 按数据项重新取一次。 */
    private static DiffView Reacquire(ChatScene scene, MessageViewModel? message)
    {
        var view = scene.List.GetVisualDescendants().OfType<DiffView>()
            .FirstOrDefault(candidate => ReferenceEquals(candidate.DataContext, message));
        Assert.True(view is not null, "the diff card is gone (the message was recycled)");
        return view!;
    }

    private static void TapChevron(ChatScene scene, DiffView card) =>
        TapChevron(scene, (MessageViewModel?)card.DataContext);

    private static void TapChevron(ChatScene scene, MessageViewModel? message)
    {
        var card = Reacquire(scene, message);
        var chevron = card.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(button => button.Name == DiffView.ChevronName);
        Assert.True(chevron is not null,
            $"the card has no chevron: children {card.GetVisualDescendants().Count()}, "
            + $"cardNull {card.Card is null}, visible {card.IsEffectivelyVisible}, "
            + $"expanded {card.IsCardExpanded}, bounds {card.Bounds}");
        scene.Tap(chevron!);
    }

    private static DiffCard BuildDiff(int lineCount)
    {
        var lines = new List<DiffLine>();
        for (var index = 0; index < lineCount; index++)
            lines.Add(new DiffLine(DiffLineKind.Add, index + 1, index + 1, $"var value{index} = Compute(a, b);"));
        return new DiffCard("契约.cs", "", lines);
    }
}
