using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dsh.Fleet;
using Dsh.Gui.Services;
using Dsh.Gui.ViewModels;
using Dsh.Gui.Views;

namespace Dsh.Tests;

/**
 * 真实会话的 GUI 场景: 把本机某次真实会话日志原样复制进隔离 home, 由真实 Host 读出,
 * 再经生产路径 (HostChatSession → MainViewModel.ApplyEvents → ChatView + VirtualizingStackPanel) 渲染。
 * 折叠/滚动的断言全部建立在这条链路上, 不使用合成的等长消息。
 */
internal sealed class RealChatScene : IAsyncDisposable
{
    private readonly HarnessHostFixture _host;
    private readonly GuiTestEnvironment _gui;
    private readonly FleetWorkspace _fleet;
    private readonly HostChatSession _chat;

    private RealChatScene(
        HarnessHostFixture host,
        GuiTestEnvironment gui,
        FleetWorkspace fleet,
        HostChatSession chat,
        RealSessionProbe probe,
        MainViewModel viewModel,
        Window window,
        ChatView view)
    {
        _host = host;
        _gui = gui;
        _fleet = fleet;
        _chat = chat;
        Probe = probe;
        ViewModel = viewModel;
        Window = window;
        Chat = view;
    }

    public RealSessionProbe Probe { get; }

    public MainViewModel ViewModel { get; }

    public Window Window { get; }

    public ChatView Chat { get; }

    public ListBox List => Chat.FindControl<ListBox>("MessageList")!;

    public ScrollViewer Scroll => Assert.IsType<ScrollViewer>(List.Scroll);

    public int MessageCount => ViewModel.Messages.Count;

    public static async Task<RealChatScene> CreateAsync(string? sessionId = null, double width = 1280, double height = 800)
    {
        var ct = TestContext.Current.CancellationToken;
        var probe = RealSessionProbe.Locate(sessionId);
        var host = await HarnessHostFixture.CreateAsync("rs", ct);
        try
        {
            probe.Seed(host.Environment.App.Home);
            var gui = await GuiTestEnvironment.CreateIsolatedAsync();
            var options = FleetConnectionTests.TestOptions with { ClientName = "real-session-scene" };
            var fleet = new FleetWorkspace(new FleetWorkspaceServices(
                new MachineStore(gui.App.Home), options, () => new FleetClient([MachineSpec.Local(host.Root)], options)));
            var chat = await HostChatSession.OpenAsync(fleet, probe.ProjectDirectory, probe.Id.Value, ct);
            var viewModel = new MainViewModel(gui.App, chat);
            var view = new ChatView { DataContext = viewModel };
            var window = new Window { Width = width, Height = height, Content = view };
            window.Show();
            var scene = new RealChatScene(host, gui, fleet, chat, probe, viewModel, window, view);
            scene.Render();
            return scene;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    /** 只跑布局与派发队列, 不抓帧(抓帧只为首帧建立渲染树)。 */
    /**
     * 只跑布局与派发队列, 不抓帧。
     * 必须把**所有优先级**都跑一遍: 折叠锚点投递在 DispatcherPriority.Loaded 上,
     * 而不带参数的 RunJobs() 只跑到 Default 及更高, Loaded 更低, 于是那批补偿任务根本不会执行 ——
     * 会得到"锚点失效"的假结论。
     */
    public void Pump(int passes = 1)
    {
        for (var pass = 0; pass < passes; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
        }
    }

    public void Render()
    {
        RunAllPriorities();
        Window.UpdateLayout();
        Window.CaptureRenderedFrame()?.Dispose();
        RunAllPriorities();
    }

    /** 所有优先级都跑一遍: 折叠锚点投递在 Loaded 上, 比 Default 低。 */
    private static void RunAllPriorities()
    {
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }

    public void Settle()
    {
        for (var pass = 0; pass < 5; pass++)
            Render();
    }

    public double MaximumOffset => Math.Max(0, Scroll.Extent.Height - Scroll.Viewport.Height);

    public double Offset => Scroll.Offset.Y;

    public double Extent => Scroll.Extent.Height;

    public double Viewport => Scroll.Viewport.Height;

    public void ScrollToFraction(double fraction)
    {
        Scroll.Offset = new Vector(Scroll.Offset.X, MaximumOffset * fraction);
        Pump(3);
    }

    public void Wheel(double delta)
    {
        var point = Scroll.TranslatePoint(new Point(Scroll.Bounds.Width / 2, Scroll.Viewport.Height / 2), Window)!.Value;
        Window.MouseWheel(point, new Vector(0, delta));
    }

    /** 点位的两种来源: 消息自身的折叠按钮(▸ 展开/▾ 折叠), 与 diff 卡片标题栏里的 chevron。 */
    public enum FoldKind
    {
        Message,
        Card,
        Other,
    }

    /**
     * 视口内**能够真正点到**的折叠控件(按屏幕位置排序)。
     * 两条都按产品里的真实标识挑, 不能只看"按钮的 DataContext 是消息":
     * 同一条消息里还有复制/点赞/重新生成等图标按钮, DataContext 也一样。
     *   - 消息折叠: 按钮内的文本就是 FoldLabel("▸ 展开"/"▾ 折叠");
     *   - 卡片折叠: 名字是 DiffView.ChevronName 的 chevron。
     * 位置判定用控件自己的屏幕矩形: 卡片比视口高时控件会落在视口外, 按坐标点等于点在别的元素上。
     */
    public List<FoldTarget> VisibleFoldButtons(FoldKind kind = FoldKind.Message)
    {
        var viewport = Scroll.Viewport.Height;
        return List.GetVisualDescendants().OfType<Button>()
            .Where(button => button is { IsEffectivelyVisible: true, DataContext: MessageViewModel })
            .Where(button => KindOf(button) == kind)
            .Select(button => (Button: button, Item: ItemOf(button)))
            .Where(entry => entry.Item is not null)
            .Select(entry =>
            {
                var top = TopOf(entry.Button);
                return new FoldTarget(
                    (MessageViewModel)entry.Button.DataContext!,
                    entry.Button,
                    top,
                    top + entry.Button.Bounds.Height,
                    TopOf(entry.Item!),
                    entry.Item!.Bounds.Height);
            })
            .Where(target => target.ButtonTop >= 1 && target.ButtonBottom <= viewport - 1)
            .OrderBy(target => target.ItemTop)
            .ThenBy(target => target.ButtonTop)
            .ToList();
    }

    /**
     * 按数据项取回该消息的折叠控件, 必要时像用户那样把它滚进视口(点一下自己的滚动条/滚轮)。
     * 展开后消息可能整条被挤出视口, 直接点坐标等于点在别处; 展开它的用户会先滚回去再点。
     */
    public FoldTarget? EnsureFoldButtonVisible(MessageViewModel message, FoldKind kind = FoldKind.Message)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            if (FoldButtonOf(message, kind) is { } visible
                && visible.ButtonTop >= 1 && visible.ButtonBottom <= Scroll.Viewport.Height - 1)
                return visible;
            var top = TryTopOf(message);
            if (top is null)
            {
                // 容器已被回收: 用 ScrollIntoView 让列表按数据项重新实体化它。
                List.ScrollIntoView(message);
                Pump(2);
                continue;
            }
            var viewport = Scroll.Viewport.Height;
            var delta = top.Value < 0 ? top.Value - 4 : top.Value - viewport / 2;
            Scroll.Offset = new Vector(Scroll.Offset.X,
                Math.Clamp(Scroll.Offset.Y + delta, 0, MaximumOffset));
            Pump(2);
        }
        var final = FoldButtonOf(message, kind);
        return final is { ButtonTop: >= 1 } candidate && candidate.ButtonBottom <= Scroll.Viewport.Height - 1
            ? candidate
            : null;
    }

    private double? TryTopOf(MessageViewModel message)
        => Container(message)?.TranslatePoint(default, Scroll)?.Y;

    /**
     * 当前已实体化(不论是否落在视口内)的每条消息的屏幕顶端。
     * 逐帧记录它, 才能看出"用户眼前的内容跳了一下又被拉回来"——只看最终落点会漏掉瞬跳。
     */
    public Dictionary<MessageViewModel, double> AllRealizedTops()
    {
        var tops = new Dictionary<MessageViewModel, double>();
        foreach (var item in List.GetVisualDescendants().OfType<ListBoxItem>())
        {
            if (item.DataContext is not MessageViewModel message)
                continue;
            if (item.TranslatePoint(default, Scroll) is { } position)
                tops[message] = position.Y;
        }
        return tops;
    }

    /** 按数据项取该消息当前的折叠控件(不要求它在视口内); 容器被回收时返回 null。 */
    public FoldTarget? FoldButtonOf(MessageViewModel message, FoldKind kind = FoldKind.Message)
    {
        var item = Container(message);
        if (item is null)
            return null;
        var button = item.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(candidate => candidate.IsEffectivelyVisible
                && ReferenceEquals(candidate.DataContext, message)
                && KindOf(candidate) == kind
                && candidate.Bounds.Height > 0);
        if (button is null)
            return null;
        var top = TopOf(button);
        return new FoldTarget(message, button, top, top + button.Bounds.Height, TopOf(item), item.Bounds.Height);
    }

    /** 消息折叠按钮的文字就是 FoldLabel; diff 卡片的 chevron 有固定名字。 */
    private static FoldKind KindOf(Button button)
    {
        if (string.Equals(button.Name, DiffView.ChevronName, StringComparison.Ordinal))
            return FoldKind.Card;
        var text = button.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault()?.Text;
        return text is "▸ 展开" or "▾ 折叠" ? FoldKind.Message : FoldKind.Other;
    }

    public readonly record struct FoldTarget(
        MessageViewModel Message,
        Button Button,
        double ButtonTop,
        double ButtonBottom,
        double ItemTop,
        double ItemHeight);

    /** 卡片本体是否已展开: 从当前容器里的 DiffView 读, 因为虚拟化会换掉容器与控件。 */
    public bool? IsCardExpanded(MessageViewModel message)
        => Container(message)?.GetVisualDescendants().OfType<DiffView>().FirstOrDefault()?.IsCardExpanded;

    public bool IsSticky => (bool)(typeof(ChatView)
        .GetField("_sticky", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .GetValue(Chat) ?? false);

    /** "回到最新"按钮此刻是否可见。 */
    public bool JumpToLatestVisible
        => Chat.FindControl<Button>("JumpToLatest")?.IsVisible ?? false;

    /** 点"回到最新"按钮; 按钮不可见时返回 false。 */
    public bool ClickJumpToLatest()
    {
        if (Chat.FindControl<Button>("JumpToLatest") is not { IsVisible: true } button)
            return false;
        Tap(button);
        Pump(3);
        return true;
    }

    private ListBoxItem? ItemOf(Visual visual)
        => visual.GetVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();

    /** 这个点按下时会不会真的落到该按钮上(被裁剪、被遮挡都会失败); 失败时给出命中链便于定位。 */
    public bool HitsButton(Button button, out string? reason)
    {
        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), Window);
        if (point is not { } target)
        {
            reason = "无法换算到窗口坐标";
            return false;
        }
        if (target.Y < 0 || target.Y > Window.Bounds.Height || target.X < 0 || target.X > Window.Bounds.Width)
        {
            reason = $"点在窗口外 ({target.X:0},{target.Y:0})";
            return false;
        }
        var hit = Window.InputHitTest(target);
        // 命中测试必须"落到该按钮或它的子元素上": 按钮内容是 TextBlock, 直接比按钮本身会误判成被遮挡。
        // 被同一条消息里更高的兄弟元素(正文、卡片正文)盖住时, 命中的是那些元素, 祖先里也不会有这个按钮。
        for (var visual = hit as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (ReferenceEquals(visual, button))
            {
                reason = null;
                return true;
            }
            if (visual is ListBoxItem)
                break;
        }
        var chain = string.Join("<", ((Visual?)hit)?.GetVisualAncestors().Take(6)
            .Select(visual => visual.GetType().Name) ?? []);
        var owner = ItemOf(button) as Visual ?? button;
        reason = $"命中别的元素 {hit?.GetType().Name ?? "null"} [{chain}] "
            + $"按钮 y={target.Y:0} 高={button.Bounds.Height:0} 所属项 top={TopOf(owner):0} "
            + $"项高={owner.Bounds.Height:0}";
        return false;
    }

    /** 该控件是否还挂在当前消息列表的视觉树里(容器被回收后, 采样代码手里的是过期控件)。 */
    public bool IsInsideList(Visual visual)
        => List.GetVisualDescendants().Any(candidate => ReferenceEquals(candidate, visual));

    /** 当前已实体化区间的最小下标(-1 表示没有)。 */
    public int FirstRealizedIndex()
        => List.GetVisualDescendants().OfType<ListBoxItem>()
            .Select(item => List.IndexFromContainer(item))
            .DefaultIfEmpty(-1)
            .Min();

    /** 当前已实体化区间的最大下标(-1 表示没有)。 */
    public int LastRealizedIndex()
        => List.GetVisualDescendants().OfType<ListBoxItem>()
            .Select(item => List.IndexFromContainer(item))
            .DefaultIfEmpty(-1)
            .Max();

    /** 按数据项取当前容器(虚拟化会重建)。 */
    public ListBoxItem? Container(MessageViewModel message)
        => List.GetVisualDescendants().OfType<ListBoxItem>()
            .FirstOrDefault(item => ReferenceEquals(item.DataContext, message));

    public double TopOf(Visual visual) => visual.TranslatePoint(default, Scroll)?.Y ?? double.NaN;

    public double? TopOf(MessageViewModel message)
    {
        var item = Container(message);
        return item is null ? null : item.TranslatePoint(default, Scroll)?.Y;
    }

    /**
     * 当前视口里能看见的每条消息(数据项 + 屏幕顶端)。
     * "用户眼前的内容有没有动"必须按这份清单前后对比: 只盯着被点那条或顶部那条都会漏。
     */
    public Dictionary<MessageViewModel, double> VisibleMessages()
    {
        var viewport = Scroll.Viewport.Height;
        var visible = new Dictionary<MessageViewModel, double>();
        foreach (var item in List.GetVisualDescendants().OfType<ListBoxItem>())
        {
            if (item.Bounds.Height <= 0 || item.DataContext is not MessageViewModel message)
                continue;
            if (item.TranslatePoint(default, Scroll) is not { } position)
                continue;
            if (position.Y >= viewport || position.Y + item.Bounds.Height <= 0)
                continue;
            visible[message] = position.Y;
        }
        return visible;
    }

    /** 用真实指针事件点击按钮中心; 控件已被虚拟化回收时抛错(调用方应该按数据项重取)。 */
    public void Tap(Button button)
    {
        if (button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), Window) is not { } point)
            throw new InvalidOperationException("目标控件已被移出视觉树(容器被回收), 需要按数据项重新获取");
        Window.MouseDown(point, MouseButton.Left);
        Window.MouseUp(point, MouseButton.Left);
    }

    public async ValueTask DisposeAsync()
    {
        Window.Close();
        Dispatcher.UIThread.RunJobs();
        await ViewModel.DisposeAsync();
        _chat.Dispose();
        await _fleet.DisposeAsync();
        await _gui.DisposeAsync();
        await _host.DisposeAsync();
    }
}
