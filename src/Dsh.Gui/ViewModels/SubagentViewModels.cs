using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dsh.Core;
using Dsh.Gui.Services;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Runtime.Events;
using Dsh.Subagent;

namespace Dsh.Gui.ViewModels;

/** 子代理树里的一个条目: 身份来自持久化 descriptor, IsLive 由 start/end 通知与运行时查询共同判定。 */
public sealed partial class SubagentNodeViewModel(
    SessionId id, SessionId? parent, int depth, string mode, string? label, bool hasChildren) : ObservableObject
{
    public SessionId Id { get; } = id;

    [ObservableProperty]
    private SessionId? _parent = parent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Indent))]
    private int _depth = depth;

    [ObservableProperty]
    private string _mode = mode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private string? _label = label;

    [ObservableProperty]
    private bool _hasChildren = hasChildren;

    [ObservableProperty]
    private bool _isLive;

    public string DisplayName => string.IsNullOrWhiteSpace(Label) ? Id.Value : Label;

    public string ModeLabel => Mode == SubagentDescriptorPayload.ContinuableMode ? "continuable" : "one-shot";

    /** 树形缩进像素: 根层子代理不缩进, 每深一层加 14px。 */
    public double Indent => Math.Max(0, Depth - 1) * TreeIndentPixels;

    private const double TreeIndentPixels = 14;
}

/** 子代理列表与只读查看状态: 列表来自 SubagentRuntime 的持久化子树, live 由通知与 GetLive 共同判定。 */
public sealed partial class SubagentPanelViewModel : ObservableObject, IDisposable
{
    private readonly Context _ctx;
    private readonly SubagentDirectory _directory;
    private readonly Dictionary<SessionId, SubagentNodeViewModel> _byId = [];
    private readonly Func<bool> _unsubscribeStart;
    private readonly Func<bool> _unsubscribeEnd;
    private SessionId? _root;
    private bool _disposed;

    public SubagentPanelViewModel(Context ctx)
    {
        _ctx = ctx;
        _directory = new SubagentDirectory(ctx);
        _unsubscribeStart = ctx.On<SubagentStartNotification>(
            notification => Dispatch(() => NotifyStart(notification.Info.Id)),
            new EventOptions { Global = true });
        _unsubscribeEnd = ctx.On<SubagentEndNotification>(
            notification => Dispatch(() => NotifyEnd(notification.Info.Id)),
            new EventOptions { Global = true });
    }

    public ObservableCollection<SubagentNodeViewModel> Nodes { get; } = [];

    /** 列表/live 状态变化后触发, 供主消息流刷新已展开的子代理卡片。 */
    public event Action? Changed;

    [ObservableProperty]
    private int _liveCount;

    [ObservableProperty]
    private bool _isViewing;

    [ObservableProperty]
    private SubagentConversationViewModel? _viewing;

    [ObservableProperty]
    private SubagentNodeViewModel? _current;

    [ObservableProperty]
    private bool _canPrevious;

    [ObservableProperty]
    private bool _canNext;

    [ObservableProperty]
    private string _positionText = "";

    public bool HasNodes => Nodes.Count > 0;

    public string Badge => LiveCount.ToString();

    /** 切换父会话时重设子树根并关闭可能打开的子代理视图。 */
    public void SetRoot(SessionId root)
    {
        if (_root != root)
            Close();
        _root = root;
        Refresh();
    }

    public void Refresh()
    {
        if (_disposed)
            return;
        var entries = _root is { } root ? _directory.Descendants(root) : [];
        var seen = new HashSet<SessionId>();
        var ordered = new List<SubagentNodeViewModel>();
        foreach (var entry in entries)
        {
            seen.Add(entry.Id);
            if (!_byId.TryGetValue(entry.Id, out var node))
            {
                node = new SubagentNodeViewModel(entry.Id, entry.Parent, entry.Depth ?? 0, entry.Mode, entry.Label, entry.HasChildren)
                {
                    IsLive = _directory.IsLive(entry.Id),
                };
                _byId[entry.Id] = node;
            }
            else
            {
                node.Parent = entry.Parent;
                node.Depth = entry.Depth ?? 0;
                node.Mode = entry.Mode;
                node.Label = entry.Label;
                node.HasChildren = entry.HasChildren;
            }
            ordered.Add(node);
        }
        foreach (var stale in _byId.Keys.Where(id => !seen.Contains(id)).ToList())
            _byId.Remove(stale);
        Nodes.Clear();
        foreach (var node in ordered)
            Nodes.Add(node);
        SyncState();
    }

    public void NotifyStart(SessionId id)
    {
        if (_disposed)
            return;
        if (_byId.TryGetValue(id, out var node))
            node.IsLive = true;
        else
            Refresh();
        SyncState();
    }

    public void NotifyEnd(SessionId id)
    {
        if (_disposed)
            return;
        if (_byId.TryGetValue(id, out var node))
            node.IsLive = false;
        SyncState();
    }

    [RelayCommand]
    private void Open(SubagentNodeViewModel? node)
    {
        if (node is null || _directory.FindSession(node.Id) is not { } session)
            return;
        Viewing?.Dispose();
        Current = node;
        Viewing = new SubagentConversationViewModel(_ctx, session, node.IsLive);
        IsViewing = true;
        RefreshNavigation();
    }

    [RelayCommand]
    private void Close()
    {
        Viewing?.Dispose();
        Viewing = null;
        Current = null;
        IsViewing = false;
        CanPrevious = false;
        CanNext = false;
        PositionText = "";
    }

    [RelayCommand]
    private void Previous() => Step(-1);

    [RelayCommand]
    private void Next() => Step(1);

    public Session? FindSession(SessionId id) => _directory.FindSession(id);

    private void Step(int delta)
    {
        if (Current is not { } current)
            return;
        var siblings = Siblings(current);
        var index = siblings.IndexOf(current);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= siblings.Count)
            return;
        Open(siblings[target]);
    }

    private List<SubagentNodeViewModel> Siblings(SubagentNodeViewModel current)
        => [.. Nodes.Where(node => node.Parent == current.Parent)];

    private void SyncState()
    {
        LiveCount = Nodes.Count(node => node.IsLive);
        OnPropertyChanged(nameof(HasNodes));
        OnPropertyChanged(nameof(Badge));
        if (Current is { } current && !Nodes.Contains(current))
            Close();
        else
            RefreshNavigation();
        Changed?.Invoke();
    }

    private void RefreshNavigation()
    {
        if (Current is not { } current)
        {
            CanPrevious = false;
            CanNext = false;
            PositionText = "";
            return;
        }
        var siblings = Siblings(current);
        var index = siblings.IndexOf(current);
        CanPrevious = index > 0;
        CanNext = index >= 0 && index < siblings.Count - 1;
        PositionText = index >= 0 ? $"{index + 1} / {siblings.Count}" : "";
    }

    /** 通知可能来自后台线程: 已在 UI 线程时直接处理, 否则投递过去。 */
    private static void Dispatch(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _unsubscribeStart();
        _unsubscribeEnd();
        Close();
    }
}

/** 只读子代理视图: 用子会话事件渲染消息, 活代理后续事件追加, 关闭时退订, 绝不写入子会话。 */
public sealed class SubagentConversationViewModel : ObservableObject, IDisposable
{
    private readonly SubagentTranscript _transcript = new();
    private readonly Func<bool>? _unsubscribe;
    private bool _disposed;

    public SubagentConversationViewModel(Context ctx, Session session, bool live)
    {
        Session = session;
        var events = new DispatcherBridge<SessionEvent>(ApplyBatch);
        ApplyBatch(session.SnapshotEvents());
        if (live)
            _unsubscribe = ctx.On<SessionEventNotification>(notification =>
            {
                if (ReferenceEquals(notification.Session, session))
                    events.Enqueue(notification.Event);
            });
    }

    public Session Session { get; }

    public ObservableCollection<MessageViewModel> Messages { get; } = [];

    private void ApplyBatch(IReadOnlyList<SessionEvent> batch)
    {
        if (_disposed)
            return;
        foreach (var sessionEvent in batch)
            _transcript.Apply(sessionEvent, Messages);
        _transcript.Flush();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _unsubscribe?.Invoke();
    }
}
