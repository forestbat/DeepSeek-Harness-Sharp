using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dsh.Gui.ViewModels;
using Dsh.Gui.Views;
using Dsh.Interaction;

namespace Dsh.Tests;

/** 折叠/展开会改变消息高度; 视图必须留在用户刚操作的那条消息上, 不能被列表锚定或触底跟随拉走。 */
[Collection(GuiSerialCollection.CollectionName)]
public sealed class MessageFoldScrollTests
{
    private const double PositionTolerance = 2;

    private static string ScreenshotDirectory
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "artifacts020")))
                directory = directory.Parent;
            var root = directory?.FullName ?? throw new InvalidOperationException("repository root not found");
            return Path.Combine(root, "artifacts020", "gui-screenshots");
        }
    }

    [Fact]
    public async Task ExpandingAtTail_KeepsMessagePosition() => await HeadlessGui.RunAsync(async () =>
    {
        await using var scene = await ChatScene.CreateAsync();
        scene.Scroll.ScrollToEnd();
        scene.Render();
        var target = scene.FindVisibleFoldTarget();
        scene.ToggleAndAssertPosition(target, "tail expand");
    });

    [Fact]
    public async Task ExpandingMidList_KeepsMessagePosition() => await HeadlessGui.RunAsync(async () =>
    {
        await using var scene = await ChatScene.CreateAsync();
        scene.Scroll.ScrollToEnd();
        scene.Render();
        scene.ScrollToMiddle();
        var target = scene.FindVisibleFoldTarget();
        scene.ToggleAndAssertPosition(target, "mid-list expand");
    });

    [Fact]
    public async Task CollapsingExpandedMessage_KeepsMessagePosition() => await HeadlessGui.RunAsync(async () =>
    {
        await using var scene = await ChatScene.CreateAsync();
        scene.Scroll.ScrollToEnd();
        scene.Render();
        scene.ScrollToMiddle();
        var target = scene.FindVisibleFoldTarget();
        scene.ToggleAndAssertPosition(target, "mid-list expand");
        scene.ToggleAndAssertPosition(target, "mid-list collapse", expanded: false);
    });

    [Fact]
    public async Task TogglingDiffCard_KeepsMessagePosition() => await HeadlessGui.RunAsync(async () =>
    {
        await using var scene = await ChatScene.CreateAsync();
        // 把 diff 卡片放在列表中部: 上下都有内容, 收起后视图才有空间保持卡片位置。
        var message = scene.ViewModel.Messages[21];
        message.SetDiff(BuildDiffCard(40));
        message.IsExpanded = true;
        // 卡片默认折叠: 先把卡片展开, 再验证收起时位置不变。
        message.IsDiffExpanded = true;
        scene.Render();
        scene.List.ScrollIntoView(message);
        scene.Render();
        scene.ScrollToTop(message, 120);
        var card = Assert.Single(scene.List.GetVisualDescendants().OfType<DiffView>(),
            view => ReferenceEquals(view.DataContext, message));
        Assert.InRange(scene.TopOf(card), 20, scene.Scroll.Viewport.Height - 40);
        var chevron = Assert.Single(card.GetVisualDescendants().OfType<Button>(),
            button => button.Name == DiffView.ChevronName);
        var before = scene.TopOf(scene.List, message);
        Assert.True(card.IsCardExpanded);
        scene.Tap(chevron);
        scene.Settle();
        Assert.False(message.IsDiffExpanded);
        var after = scene.TopOf(scene.List, message);
        Assert.True(Math.Abs(after - before) <= PositionTolerance,
            $"diff collapse moved the card item from {before:0} to {after:0}");
    });

    /** 大改动展开后最多 20 行, 其余靠卡片内滚动条查看; 折叠态不渲染任何 diff 行。 */
    [Fact]
    public async Task ExpandedDiffCard_CapsAtTwentyLines_AndScrollsInsideTheCard() => await HeadlessGui.RunAsync(async () =>
    {
        var card = BuildDiffCard(120);
        var view = new DiffView { Card = card };
        var window = new Window { Width = 900, Height = 700, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        window.CaptureRenderedFrame()?.Dispose();

        // 默认折叠: 标题栏在, 行内容一律不建。
        Assert.False(view.IsCardExpanded);
        Assert.Empty(view.BodyViewport.Content is StackPanel panel ? panel.Children : throw new InvalidOperationException());

        view.IsCardExpanded = true;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        window.CaptureRenderedFrame()?.Dispose();

        var body = view.BodyViewport;
        Assert.True(body.IsEffectivelyVisible);
        Assert.True(body.Extent.Height > body.Viewport.Height, "长 diff 必须出现卡片内滚动条");
        Assert.True(
            body.Viewport.Height <= DiffCardDisplay.PageLines * 19 + 1,
            $"展开高度必须限制在 {DiffCardDisplay.PageLines} 行内, 实际 {body.Viewport.Height:0}");
        Capture(window, "headless-diff-card-capped");

        body.Offset = new Vector(0, 400);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Assert.True(body.Offset.Y > 0, "卡片内视口必须可以滚动到后面的改动");
        window.CaptureRenderedFrame()?.Dispose();

        window.Close();
        Dispatcher.UIThread.RunJobs();
    });

    private static void Capture(Window window, string name)
    {
        Directory.CreateDirectory(ScreenshotDirectory);
        using var frame = window.CaptureRenderedFrame();
        frame?.Save(Path.Combine(ScreenshotDirectory, $"{name}.png"), PngBitmapEncoderOptions.Default);
    }

    [Fact]
    public async Task ExpandingTraceDetail_KeepsTraceItemPosition() => await HeadlessGui.RunAsync(async () =>
    {
        await using var scene = await ChatScene.CreateAsync();
        for (var index = 0; index < 40; index++)
        {
            var itemIndex = index;
            scene.ViewModel.TraceView.Add(new TraceItemViewModel
            {
                Seq = index,
                Kind = TraceKind.Tool,
                Title = $"工具调用 {index}",
                Detail = string.Join('\n', Enumerable.Range(0, 8).Select(line => $"第 {itemIndex} 条明细第 {line} 行")),
            });
        }
        scene.ViewModel.Tab = ChatTab.Trace;
        scene.Render();
        scene.Scroll.ScrollToEnd();
        scene.Render();
        var traceList = scene.Chat.FindControl<ListBox>("TraceList")!;
        var scroll = Assert.IsType<ScrollViewer>(traceList.Scroll);
        scene.ScrollToMiddle(scroll);
        var button = traceList.GetVisualDescendants().OfType<Button>()
            .Where(candidate => candidate.DataContext is TraceItemViewModel { HasDetail: true } item
                && ReferenceEquals(candidate.Command, item.ToggleExpandCommand)
                && candidate.IsEffectivelyVisible)
            .OrderBy(candidate => candidate.TranslatePoint(default, scroll)?.Y)
            .First(candidate => candidate.TranslatePoint(default, scroll)?.Y > 40);
        var traceItem = Assert.IsType<TraceItemViewModel>(button.DataContext);
        var before = scene.TopOf(traceList, traceItem);
        scene.Tap(button);
        scene.Settle();
        Assert.True(traceItem.IsExpanded);
        var after = scene.TopOf(traceList, traceItem);
        Assert.True(Math.Abs(after - before) <= PositionTolerance,
            $"trace expand moved the item from {before:0} to {after:0} (offset {scroll.Offset.Y:0})");
    });


    private static DiffCard BuildDiffCard(int lineCount)
    {
        var lines = new List<DiffLine>();
        for (var index = 0; index < lineCount; index++)
            lines.Add(new DiffLine(index % 2 == 0 ? DiffLineKind.Add : DiffLineKind.Context, index + 1, index + 1, $"第 {index} 行改动内容"));
        return new DiffCard("中部文件.cs", "", lines);
    }
}
