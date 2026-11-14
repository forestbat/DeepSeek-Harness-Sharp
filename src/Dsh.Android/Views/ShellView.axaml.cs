using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dsh.Android.Services;
using Dsh.Android.ViewModels;
using Dsh.Gui.ViewModels;
using Dsh.Llm;

namespace Dsh.Android.Views;

public sealed partial class ShellView : UserControl
{
    /** 距离顶部/底部还剩这么多像素就开始自动续取。 */
    private const double AutoLoadThreshold = 400;
    private MobileShellViewModel? _viewModel;
    private IDisposable? _backRegistration;

    /** 是否跟随会话末尾: 用户停在底部时为 true; 一旦翻到更早的位置就置 false, 新事件不再把他拽回去。 */
    private bool _followTail = true;

    /** 上滑插入前的锚点消息: 补位按它在屏幕上的实际位移算, 不受尾部新增内容影响。 */
    private (MessageViewModel Message, double PanelY, double OffsetY)? _anchor;

    /** 刚进会话、还没把视图定位到末尾: 这段时间里不能往上续接, 否则一打开就顶到历史中段。 */
    private bool _initialTailPending = true;

    public ShellView()
    {
        InitializeComponent();
        /*
         * ScrollViewer 在 ItemsControl 自己的模板里, 所以 ScrollChanged 从它向上冒泡会经过 MessageScroll。
         * 之前两种写法都不对: 挂在外层 ScrollViewer 的子控件上收不到(事件只向上走),
         * 或者把 ScrollViewer 放在外面导致虚拟化失效。
         */
        MessageScroll.AddHandler(ScrollViewer.ScrollChangedEvent, OnScrollChanged, RoutingStrategies.Bubble);
        // 长按/双击由列表内部的消息发起, 冒泡到这里统一处理。
        MessageScroll.AddHandler(InputElement.HoldingEvent, OnMessageHolding, RoutingStrategies.Bubble);
        MessageScroll.AddHandler(InputElement.DoubleTappedEvent, OnMessageDoubleTapped, RoutingStrategies.Bubble);
        DataContextChanged += (_, _) => Attach();
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Attach();
        KeyboardInset.Changed += OnKeyboardInset;
        ApplyKeyboardInset(KeyboardInset.Bottom);
        // 贴边时没有滚动事件, 只能靠布局落地来发现"该往上/下接了"。
        MessageScroll.LayoutUpdated -= OnLayoutSettled;
        MessageScroll.LayoutUpdated += OnLayoutSettled;
        Dispatcher.UIThread.Post(ScrollToEnd, DispatcherPriority.Loaded);
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        MessageScroll.LayoutUpdated -= OnLayoutSettled;
        KeyboardInset.Changed -= OnKeyboardInset;
        Detach();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnKeyboardInset(double bottom) => Dispatcher.UIThread.Post(() => ApplyKeyboardInset(bottom));

    /** 输入法弹起时把外壳底部顶高同样高度, 输入框就浮在键盘上方。 */
    private void ApplyKeyboardInset(double bottom) => ShellRoot.Margin = new Thickness(0, 0, 0, bottom);

    private void Attach()
    {
        if (_viewModel is not null)
        {
            _viewModel.TranscriptChanged -= OnTranscriptChanged;
            _viewModel.ScrollToSeqRequested -= ScrollToSeq;
            _viewModel.OlderLoaded -= OnOlderLoaded;
            _viewModel.OlderCached -= OnOlderCached;
            _viewModel.WindowTrimmedFromStart -= OnWindowTrimmedFromStart;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
        _viewModel = DataContext as MobileShellViewModel;
        if (_viewModel is not null)
        {
            _viewModel.TranscriptChanged += OnTranscriptChanged;
            _viewModel.ScrollToSeqRequested += ScrollToSeq;
            _viewModel.OlderLoaded += OnOlderLoaded;
            _viewModel.OlderCached += OnOlderCached;
            _viewModel.WindowTrimmedFromStart += OnWindowTrimmedFromStart;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            // 换到另一个会话时重新走一次首次定位。
            _initialTailPending = true;
        }
        // 系统返回热键先交给界面: 能退一级就退一级, 最外层才让系统退出 App。
        _backRegistration ??= BackNavigation.Register(() =>
        {
            if (_viewModel is not { } viewModel)
                return false;
            var canGoBack = viewModel.CanGoBack;
            if (canGoBack)
                _ = viewModel.GoBackAsync();
            return canGoBack;
        });
    }

    private void Detach()
    {
        _backRegistration?.Dispose();
        _backRegistration = null;
        if (_viewModel is null)
            return;
        _viewModel.TranscriptChanged -= OnTranscriptChanged;
        _viewModel.ScrollToSeqRequested -= ScrollToSeq;
        _viewModel.OlderLoaded -= OnOlderLoaded;
        _viewModel.OlderCached -= OnOlderCached;
        _viewModel.WindowTrimmedFromStart -= OnWindowTrimmedFromStart;
        _viewModel = null;
    }

    /**
     * 队首插入更早的消息后, 把滚动位置补回原来的那一条。
     * 插入会让内容整体下移, 不补的话视线会被推走; 补的量就是这次新增的高度。
     */
    private void OnOlderLoaded(int count)
    {
        if (count <= 0)
            return;
        // 插入会让内容整体下移, 按锚点消息还原位置; 绝不拿 Extent 差值去补 ——
        // 虚拟化下 Extent 是估算值, 会随实例化的项大幅跳动, 用它补出来的位置必然是错的。
        Dispatcher.UIThread.Post(() =>
        {
            RestoreAnchor();
            // 补位之后仍然停在顶部, 说明用户就顶在上沿: 继续把缓存里的接上来。
            ScheduleTopFill();
        }, DispatcherPriority.Loaded);
    }

    /**
     * 记下视口里最上沿那条消息, 以及它此刻在面板坐标里的位置与滚动位置。
     * 面板坐标是内容里的累计位置, 与滚动无关, 插入/裁剪之后仍然可比; 这是唯一可靠的参照。
     */
    private void CaptureAnchor()
    {
        _anchor = null;
        if (MessageScroll.ItemsPanelRoot is not { } panel || Scroller is not { } scroll)
            return;
        foreach (var child in panel.Children)
        {
            if (child is not Control { DataContext: MessageViewModel message } control)
                continue;
            _anchor = (message, control.Bounds.Y, scroll.Offset.Y);
            return;
        }
    }

    /** 把锚点消息放回它在视口里的原位置: 它在面板里移动了多少, 滚动位置就跟多少。 */
    private void RestoreAnchor()
    {
        var anchor = _anchor;
        _anchor = null;
        if (anchor is not { } found || _viewModel is not { } viewModel || Scroller is not { } scroll)
            return;
        var index = viewModel.Messages.IndexOf(found.Message);
        if (index < 0 || MessageScroll.ContainerFromIndex(index) is not Control container)
            return;
        var fromTop = found.PanelY - found.OffsetY;
        scroll.Offset = new Vector(0, Math.Max(0, container.Bounds.Y - fromTop));
    }

    /**
     * 停在顶部时把缓存里已有的更早内容继续接上。
     * 顶在上沿时滚动位置已经是 0, 不会再产生 ScrollChanged, 光等滚动事件就永远接不上下一批,
     * 于是一停就停死。这里不靠滚动事件, 只靠"还在顶部且有数据"这个状态延续自己。
     */
    private void ScheduleTopFill()
    {
        if (_topFillScheduled || _viewModel is not { } viewModel || Scroller is not { } scroll)
            return;
        if (scroll.Offset.Y > AutoLoadThreshold || !viewModel.CanLoadOlder)
            return;
        // 疯狂上滑时每次插入都会想再排一次, 合并成一个回调, 否则会堆出上百次续接把视口刷爆。
        _topFillScheduled = true;
        // 不插延迟: 排到渲染之后的优先级上, 让这一批先落布局, 下一批再接上。
        Dispatcher.UIThread.Post(FillFromTop, DispatcherPriority.Background);
    }

    private void FillFromTop()
    {
        _topFillScheduled = false;
        if (_viewModel is not { } viewModel || Scroller is not { } scroll)
            return;
        if (scroll.Offset.Y > AutoLoadThreshold || !viewModel.CanLoadOlder || viewModel.IsLoadingOlder)
            return;
        TryExtendOlder(scroll);
    }

    /**
     * 布局落地就看一眼两端。
     * 贴边时 offset 已经钳住, 不会再产生 ScrollChanged; 装载结束、跳转落地、内容变高这些时刻
     * 也可能根本没有任何滚动事件。把判定挂在布局上, 这些情况才都有触发点。
     */
    private void OnLayoutSettled(object? sender, EventArgs e)
    {
        if (_viewModel is null)
            return;
        ScheduleTopFill();
        ScheduleBottomFill();
    }

    /** 顶部续接是否已排队: 单飞, 保证任意时刻只有一次待执行的续接。 */
    private bool _topFillScheduled;

    /** 底部续接是否已排队。 */
    private bool _bottomFillScheduled;

    /**
     * 停在底部时接一截更晚的内容, 一次手势只接一截。
     * 到底之后 offset 会钳在最大值, 不再产生滚动事件; 但接上这一截之后内容又撑出了可滑余量,
     * 用户继续往下滑就会再次触发, 所以不需要在这里自我延续。
     */
    private void ScheduleBottomFill()
    {
        if (_bottomFillScheduled)
            return;
        if (_viewModel is not { } viewModel || Scroller is not { } scroll)
            return;
        var remaining = scroll.Extent.Height - scroll.Viewport.Height - scroll.Offset.Y;
        // 贴在底部时拖拽不会改变 offset, 也就没有滚动事件来触发装载; 所以这里按"余量"主动补:
        // 底下不足一屏就补一截, 够了就停 —— 既不会让用户撞到底就没了反应, 也不会一次把历史全灌进来。
        if (remaining > scroll.Viewport.Height * RunwayScreens)
            return;
        if (!viewModel.CanLoadNewer)
        {
            // 本地以为到底了, 但已渲染位置已经越过本地记的总数: 那是旧数字, 先向宿主核对。
            if (viewModel.NewerTailStale)
            {
                _ = RefreshTailThenFillAsync(viewModel);
                return;
            }
            NoteEdge($"bottom-skip offset={scroll.Offset.Y:F0} remaining={remaining:F0} loading={viewModel.IsLoadingOlder} exhausted={viewModel.NewerExhausted} messages={viewModel.Messages.Count}");
            return;
        }
        _bottomFillScheduled = true;
        Dispatcher.UIThread.Post(FillFromBottom, DispatcherPriority.Background);
    }

    /** 核对完真实总数之后再决定要不要补一截。 */
    private async Task RefreshTailThenFillAsync(MobileShellViewModel viewModel)
    {
        if (await viewModel.RefreshTailAsync())
            ScheduleBottomFill();
    }

    /** 判定路径只在结论变化时记一条, 免得每次滚动都刷屏。 */
    private void NoteEdge(string note)
    {
        if (string.Equals(note, _lastEdgeNote, StringComparison.Ordinal))
            return;
        _lastEdgeNote = note;
        MobileLog.Info($"edge: {note}");
    }

    private string _lastEdgeNote = "";

    private void FillFromBottom()
    {
        _bottomFillScheduled = false;
        if (_viewModel is not { } viewModel || Scroller is not { } scroll)
            return;
        var remaining = scroll.Extent.Height - scroll.Viewport.Height - scroll.Offset.Y;
        if (remaining > scroll.Viewport.Height * RunwayScreens || !viewModel.CanLoadNewer)
            return;
        // 正在装载就先不插手: 装载结束时会通过 OnViewModelPropertyChanged 补上看一眼。
        if (viewModel.IsLoadingOlder)
        {
            _pendingBottomFill = true;
            return;
        }
        MobileLog.Info($"bottom-fill: offset={scroll.Offset.Y:F0} remaining={remaining:F0} messages={viewModel.Messages.Count}");
        _ = viewModel.LoadNewerAsync();
    }

    /**
     * 滚到两头就自动续取: 上滑到顶取更早的一段, 下滑到底取更晚的一段。
     * 程序滚动(换窗口/定位到某条)之后短暂停手, 否则新窗口从 0 开始会立刻再触发一次加载。
     */
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_viewModel is not { } viewModel)
            return;
        if (e.Source is not ScrollViewer scroll)
            return;
        var remaining = scroll.Extent.Height - scroll.Viewport.Height - scroll.Offset.Y;
        // 用户真的在滚了, 说明首次定位那一帧已经过去。
        _initialTailPending = false;
        // 只有"窗口已经铺到历史末尾"才跟随。
        // 这里不能用 CanLoadNewer: 它含"此刻是否正在装载", 装载中会短暂为 false,
        // 于是每装载一次就把视图钉到新底部, 用户接着划不动 —— 这正是"得先上滑一下"的来源。
        _followTail = remaining <= AutoLoadThreshold && viewModel.WindowAtEnd;
        if (viewModel.IsLoadingOlder)
        {
            // 装载期间这些事件本来会被直接忽略; 但用户此刻若正贴着端点, 那次触发不能丢,
            // 端点处 offset 已经钳住, 装载完之后不会再有新事件来补。
            _pendingTopFill |= scroll.Offset.Y <= AutoLoadThreshold && viewModel.CanLoadOlder;
            _pendingBottomFill |= remaining <= AutoLoadThreshold && viewModel.CanLoadNewer;
            return;
        }
        // 两个方向都不设"程序滚动后停手"的窗口: 那次停手会把触底那一帧的滚动事件吞掉,
        // 而触底之后本来就不再产生滚动事件, 用户只好先往上滑再滑下来才能续上。
        if (scroll.Offset.Y <= AutoLoadThreshold && viewModel.CanLoadOlder)
        {
            TryExtendOlder(scroll);
            return;
        }
        if (remaining <= AutoLoadThreshold && viewModel.CanLoadNewer)
        {
            ScheduleBottomFill();
            return;
        }
        // 还差两屏到底时就把后面那截备进缓存, 真滑到底时不必等往返。
        if (remaining <= scroll.Viewport.Height * PrefetchAheadScreens && viewModel.CanLoadNewer)
            _ = viewModel.PrefetchNewerAsync();
    }

    /**
     * 装载结束时补上"装载期间被丢掉的那次触发"。
     * 只在用户当时确实贴到了端点时才补 —— 无条件重查会和"跟随末尾"互相喂, 一次手势连装好几批。
     */
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not nameof(MobileShellViewModel.IsLoadingOlder) || _viewModel is not { } viewModel
            || viewModel.IsLoadingOlder)
            return;
        if (_pendingTopFill)
        {
            _pendingTopFill = false;
            ScheduleTopFill();
        }
        if (_pendingBottomFill)
        {
            _pendingBottomFill = false;
            ScheduleBottomFill();
        }
    }

    /** 装载期间用户贴到了端点: 那次触发不能丢, 装载结束再补。 */
    private bool _pendingTopFill;

    private bool _pendingBottomFill;


    /**


     * 距离底部还剩几屏时开始预取更晚的内容。


     * 取数要一两百毫秒, 而快划一屏只要一百多毫秒, 两屏余量跑不赢手速; 多留几屏才有缓冲。


     * 按屏算, 与具体机器的屏幕大小无关。


     */


    private const double PrefetchAheadScreens = 6;


    /**
     * 往上接一截。
     * 记下插入前的锚点, 插入后按锚点的实际位移补位。
     */
    private void TryExtendOlder(ScrollViewer scroll)
    {
        if (_viewModel is not { } viewModel || !viewModel.CanLoadOlder || viewModel.IsLoadingOlder)
            return;
        // 刚打开会话、首次定位还没落到底: 这一帧 offset 还是 0, 不挡住就会把视图顶到历史中段。
        // 这里只看这一个状态, 不看是否"在末尾": 从时间线跳到某条之后窗口常停在顶部且滚不动,
        // 拿几何判断会把向上续接永久挡住。
        if (_initialTailPending)
            return;
        CaptureAnchor();
        _ = viewModel.LoadOlderAsync();
    }

    /**
     * 后台刚把一截更早的历史放进缓存。
     * 用户停在顶部时列表已经滚不动, 不会再产生 ScrollChanged, 不在这里接上就永远等不到下一次续取。
     */
    private void OnOlderCached()
    {
        if (_viewModel is not { } viewModel || Scroller is not { } scroll)
            return;
        // 已经到顶才算卡住; 用户在看中间时不能凭空把内容插到他头顶上。
        if (scroll.Offset.Y > AutoLoadThreshold || !viewModel.CanLoadOlder)
            return;
        TryExtendOlder(scroll);
    }

    /** 双击一条消息: 折叠 ↔ 展开。长消息默认折叠, 否则一条就占掉好几屏。 */
    private void OnMessageDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (FindMessage(e.Source) is not { } message || !message.IsFoldable)
            return;
        message.IsExpanded = !message.IsExpanded;
    }

    /**
     * 长按一条消息: 复制全文并提示。
     * 列表本身不参与选中, 所以滑动永远不会误选, 手指也不必和选中逻辑抢手势。
     */
    private async void OnMessageHolding(object? sender, HoldingRoutedEventArgs e)
    {
        if (_viewModel is null || e.HoldingState != HoldingState.Started)
            return;
        if (FindMessage(e.Source) is not { } message)
            return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
            return;
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateText(message.Text));
        await clipboard.SetDataAsync(transfer);
        _viewModel.ReportNotice("已复制这条消息");
    }

    /** 事件源向上找第一条带消息 DataContext 的控件。 */
    private static MessageViewModel? FindMessage(object? source)
        => source is not Visual visual
            ? null
            : visual.GetSelfAndVisualAncestors()
                .OfType<Control>()
                .Select(control => control.DataContext)
                .OfType<MessageViewModel>()
                .FirstOrDefault();

    /** 列表模板里的滚动容器; 模板套用前为 null。 */
    private ScrollViewer? Scroller => MessageScroll.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

    /** 消息有变化: 跟到底、保证底部余量, 并把头部被裁/队首插入造成的位移补回去。 */
    private void OnTranscriptChanged()
    {
        ScrollToEnd();
        ScheduleBottomFill();
        // 头部裁掉一批消息会让内容整体上移, 布局落地后把锚点放回原处。
        Dispatcher.UIThread.Post(RestoreAnchor, DispatcherPriority.Loaded);
    }

    /** 头部要裁消息了: 先记下锚点, 裁完之后由 RestoreAnchor 把它放回原处。 */
    private void OnWindowTrimmedFromStart(int count) => CaptureAnchor();

    /**
     * 新事件到达时跟随到底部 —— 但只在用户本来就停在底部时。
     * 翻到更早的位置之后再被拉回末尾, 等于把刚看到的东西丢掉。
     */
    private void ScrollToEnd()
    {
        if (_viewModel is null || _viewModel.Messages.Count == 0)
            return;
        if (!_followTail)
            return;
        // 先把末尾这一条带进来, 面板才会生成它并算出真实高度; 否则 Extent 还是旧的估计值。
        MessageScroll.ScrollIntoView(_viewModel.Messages.Count - 1);
        Dispatcher.UIThread.Post(() =>
        {
            // 排到这一刻之间用户可能已经往上滑走了, 再滚到底就把他刚看到的内容甩掉一屏。
            if (!_followTail)
                return;
            if (Scroller is { } scroll)
                scroll.Offset = new Vector(0, scroll.Extent.Height);
            // 首次定位已经落到末尾, 之后用户再往上滑就该正常续接了。
            _initialTailPending = false;
        }, DispatcherPriority.Loaded);
    }

    /** 时间线"跳到"落在这里: 按事件序号在已渲染的消息里找到那条, 滚过去并停在它的上沿。 */
    private void ScrollToSeq(long seq)
    {
        if (_viewModel is null)
            return;
        // 定位到某条之后不再跟随末尾, 否则下一条事件就会把视图拽回去。
        _followTail = false;
        // 这是一次显式定位, 与"刚进会话还没落到底"无关, 两个方向都该能继续续取。
        _initialTailPending = false;
        // 列表刚换了整窗内容, 要等这一次布局量出真实位置再滚, 否则会作用在旧坐标上。
        Dispatcher.UIThread.Post(() =>
        {
            if (_viewModel is null)
                return;
            var index = -1;
            for (var position = 0; position < _viewModel.Messages.Count; position += 1)
            {
                if (_viewModel.Messages[position].Seq is { } value && value <= seq)
                    index = position;
            }
            // 找不到说明这一窗确实不包含锚点, ViewModel 会再取一窗; 但这一窗若不可滚就彻底停住,
            // 所以定位失败也要把内容补到能滚。
            if (index < 0)
            {
                EnsureScrollable();
                return;
            }
            // 虚拟化下容器只在可见区附近存在, 因此让面板自己把这一条带进来, 再对齐到它的上沿。
            _ = BringIntoViewAsync(index);
        }, DispatcherPriority.Loaded);
    }

    /** 把第 index 条对齐到视口上沿; 虚拟化面板会为此先生成这一条的容器。 */
    private async Task BringIntoViewAsync(int index)
    {
        if (Scroller is not { } scroll)
            return;
        // 目标容器还没生成时先粗滚到大致位置, 让面板把它造出来, 再精确对齐。
        if (MessageScroll.ContainerFromIndex(index) is not Control container)
        {
            var estimate = scroll.Extent.Height * index / Math.Max(1, _viewModel!.Messages.Count);
            scroll.Offset = new Vector(0, Math.Max(0, estimate));
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            container = MessageScroll.ContainerFromIndex(index) as Control;
            if (container is null)
            {
                // 定位失败也要补内容: 这一窗可能只有一两条消息, 不可滚就再没有任何事件,
                // 不在这里补就彻底停住了。
                EnsureScrollable();
                return;
            }
        }
        container.BringIntoView();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        scroll.Offset = new Vector(0, Math.Max(0, container.Bounds.Y - 8));
        EnsureScrollable();
        // 落地时可能正好贴着某一端: 那里 offset 已经钳住, 不会再产生滚动事件,
        // 不给它一次装载就永远等不到触发, 用户只好先往回滑一点再滑回来。
        ScheduleTopFill();
        ScheduleBottomFill();
    }

    /**
     * 跳到的那一窗可能只有几条消息, 撑不满一屏就滚不动, 也就永远不会产生 ScrollChanged ——
     * 用户看到的就是"跳过去之后上滑下滑都没反应"。这里把内容补到够滚两屏,
     * 落地时就有可滑的余量, 而不是刚好一屏、一动就要等网络。
     * 一段长回复可能横跨几千个事件却只折成一条消息, 所以这个情况并不罕见。
     */
    private void EnsureScrollable()
    {
        if (_viewModel is not { } viewModel || Scroller is not { } scroll)
            return;
        if (scroll.Extent.Height >= scroll.Viewport.Height * RunwayScreens)
            return;
        // 这一步只是"现在就补一截": 若此刻正在装载, 装载结束和布局落地都会再次触发这里。
        if (viewModel.CanLoadNewer)
            _ = viewModel.LoadNewerAsync();
        else if (viewModel.CanLoadOlder)
            _ = viewModel.LoadOlderAsync();
    }

    /** 视口上下各留出几屏可滑余量: 贴边之后就没有滚动事件了, 余量必须提前给足。 */
    private const double RunwayScreens = 2;
    /** 图片选择走 Android 的系统选择器; 只把字节读进内存, 不保留 content URI 或流。 */
    private async void OnPickAttachment(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
            return;
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
            return;
        try
        {
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择图片",
                AllowMultiple = false,
                FileTypeFilter = [FilePickerFileTypes.ImageAll],
            });
            if (files.Count == 0)
                return;
            var file = files[0];
            using var buffer = new MemoryStream();
            await using (var stream = await file.OpenReadAsync())
                await stream.CopyToAsync(buffer);
            if (buffer.Length > ImageAttachments.MaxBytes)
            {
                _viewModel.ReportError($"图片不能超过 {ImageAttachments.MaxBytes / (1024 * 1024)} MB");
                return;
            }
            var bytes = buffer.ToArray();
            var mediaType = ImageAttachments.Sniff(bytes)
                ?? ImageAttachments.MediaTypeForExtension(Path.GetExtension(file.Name));
            if (mediaType is null)
            {
                _viewModel.ReportError("不支持的图片格式");
                return;
            }
            _viewModel.SetAttachment(file.Name, mediaType, bytes);
        }
        catch (Exception error)
        {
            _viewModel.ReportError($"选择图片失败: {error.Message}");
        }
    }

    private void OnClearAttachment(object? sender, RoutedEventArgs e) => _viewModel?.ClearAttachment();

    private async void OnCopyPhoneToolsUrl(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _viewModel.PhoneToolsUrl.Length == 0)
            return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
            return;
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateText(_viewModel.PhoneToolsUrl));
        await clipboard.SetDataAsync(transfer);
    }

    private async void OnCopyPublicKey(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _viewModel.PublicKeyLine.Length == 0)
            return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
            return;
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateText(_viewModel.PublicKeyLine));
        await clipboard.SetDataAsync(transfer);
    }
}

