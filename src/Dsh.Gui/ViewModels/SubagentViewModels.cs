using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dsh.Core;
using Dsh.Gui.Services;
using Dsh.Fleet;
using Dsh.RemoteHost;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Runtime.Events;

namespace Dsh.Gui.ViewModels;

/** 子代理树条目: descriptor 与 live 状态均来自拥有父会话的 Host。 */
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

/** 子代理列表与只读查看状态: Host 查询子树, Host 事件流实时呈现子会话, GUI 不恢复子代理。 */
public sealed partial class SubagentPanelViewModel : ObservableObject, IDisposable
{
    private readonly HostChatSession _chat;
    private readonly SubagentDirectory _directory;
    private readonly Dictionary<SessionId, SubagentNodeViewModel> _byId = [];
    private SessionId? _root;
    private bool _disposed;
    private long _refreshGeneration;
    private Task? _refreshTask;
    private bool _refreshRequested;
    public event Action<string>? Failed;

    public SubagentPanelViewModel(HostChatSession chat)
    {
        _chat = chat;
        _directory = new SubagentDirectory(chat);
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
        {
            Close();
            ++_refreshGeneration;
            _refreshTask = null;
        }
        _root = root;
        Refresh();
    }

    public void Refresh() => _ = RefreshAsync();

    /** Same-root callers await one owner through publication, including refreshes requested during its RPCs. */
    public Task RefreshAsync()
    {
        _refreshRequested = true;
        return _refreshTask is { IsCompleted: false } pending
            ? pending
            : _refreshTask = RefreshAndReportAsync();
    }

    private async Task RefreshAndReportAsync()
    {
        var generation = _refreshGeneration;
        try
        {
            do
            {
                _refreshRequested = false;
                await RefreshCoreAsync();
            }
            while (!_disposed && generation == _refreshGeneration && _refreshRequested);
        }
        catch (Exception error)
        {
            if (!_disposed)
                Failed?.Invoke(error.Message);
        }
    }

    private async Task RefreshCoreAsync()
    {
        if (_disposed)
            return;
        var root = _root;
        var generation = _refreshGeneration;
        var entries = root is { } rootId ? await _directory.DescendantsAsync(rootId) : [];
        if (_disposed || root != _root || generation != _refreshGeneration)
            return;
        var seen = new HashSet<SessionId>();
        var ordered = new List<SubagentNodeViewModel>();
        foreach (var entry in entries)
        {
            var id = SessionId.Create(entry.SessionId);
            seen.Add(id);
            if (!_byId.TryGetValue(id, out var node))
            {
                node = new SubagentNodeViewModel(id, entry.Parent is null ? null : SessionId.Create(entry.Parent), entry.Depth, entry.Mode, entry.Label, entry.HasChildren)
                {
                    IsLive = entry.IsLive,
                };
                _byId[id] = node;
            }
            else
            {
                node.Parent = entry.Parent is null ? null : SessionId.Create(entry.Parent);
                node.Depth = entry.Depth;
                node.Mode = entry.Mode;
                node.Label = entry.Label;
                node.HasChildren = entry.HasChildren;
                node.IsLive = entry.IsLive;
            }
            ordered.Add(node);
            await _directory.LoadEventsAsync(id);
        }
        if (_disposed || root != _root || generation != _refreshGeneration)
            return;
        foreach (var stale in _byId.Keys.Where(id => !seen.Contains(id)).ToList())
            _byId.Remove(stale);
        Nodes.Clear();
        foreach (var node in ordered)
            Nodes.Add(node);
        SyncState();
    }


    [RelayCommand]
    private async Task OpenAsync(SubagentNodeViewModel? node)
    {
        try
        {
            await OpenCoreAsync(node);
        }
        catch (Exception error)
        {
            if (!_disposed)
                Failed?.Invoke(error.Message);
        }
    }

    private async Task OpenCoreAsync(SubagentNodeViewModel? node)
    {
        if (node is null)
            return;
        var root = _root;
        var events = await _directory.LoadEventsAsync(node.Id);
        if (_disposed || root != _root)
            return;
        Viewing?.Dispose();
        Current = node;
        Viewing = new SubagentConversationViewModel(_chat, node.Id, events);
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

    public IReadOnlyList<SessionEvent>? FindEvents(SessionId id) => _directory.FindEvents(id);

    private void Step(int delta)
    {
        if (Current is not { } current)
            return;
        var siblings = Siblings(current);
        var index = siblings.IndexOf(current);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= siblings.Count)
            return;
        _ = OpenAsync(siblings[target]);
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


    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Close();
    }
}

/** Read-only child transcript: subscribe to the Host without resuming or writing the child session. */
public sealed class SubagentConversationViewModel : ObservableObject, IDisposable
{
    private SubagentTranscript _transcript = new();
    private readonly CancellationTokenSource _lifetime = new();
    private long _renderedSeq;

    public SubagentConversationViewModel(HostChatSession chat, SessionId id, IReadOnlyList<SessionEvent> events)
    {
        SessionId = id;
        ApplyBatch(events);
        _ = FollowAsync(chat, events.Count);
    }

    public SessionId SessionId { get; }
    public ObservableCollection<MessageViewModel> Messages { get; } = [];

    private async Task FollowAsync(HostChatSession chat, long fromSeq)
    {
        try
        {
            await foreach (var item in chat.Fleet.Find(chat.Machine)!.SubscribeAsync(SessionId.Value, fromSeq, _lifetime.Token))
            {
                if (item.Reset)
                {
                    var snapshot = await (await chat.ClientAsync(_lifetime.Token)).GetSessionSnapshotAsync(SessionId.Value, _lifetime.Token);
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (_lifetime.IsCancellationRequested)
                            return;
                        Messages.Clear();
                        _transcript = new SubagentTranscript();
                        _renderedSeq = 0;
                        ApplyBatch([.. snapshot.Events]);
                    });
                }
                else if (item.Event is { } sessionEvent)
                    Dispatcher.UIThread.Post(() => ApplyBatch([sessionEvent]));
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!_lifetime.IsCancellationRequested)
                    Messages.Add(new MessageViewModel("错误", error.Message, MessageKind.System, false));
            });
        }
    }

    private void ApplyBatch(IReadOnlyList<SessionEvent> batch)
    {
        if (_lifetime.IsCancellationRequested)
            return;
        foreach (var sessionEvent in batch)
        {
            if (sessionEvent.Seq < _renderedSeq)
                continue;
            _renderedSeq = sessionEvent.Seq + 1;
            _transcript.Apply(sessionEvent, Messages);
        }
        _transcript.Flush();
    }

    public void Dispose() => _lifetime.Cancel();
}
