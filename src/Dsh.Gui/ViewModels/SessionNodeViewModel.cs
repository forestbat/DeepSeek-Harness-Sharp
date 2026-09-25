using CommunityToolkit.Mvvm.ComponentModel;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Gui.ViewModels;

/** 侧栏里的一个会话节点: 可能对应活跃 agent, 也可能只是磁盘上的历史会话。 */
public sealed partial class SessionNodeViewModel : ObservableObject
{
    public required SessionId SessionId { get; init; }

    /** 工作区名(由 cwd 归并), 用作树的分组标题。 */
    public required string Workspace { get; init; }

    public required string Cwd { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private string _relativeTime = "";

    [ObservableProperty]
    private bool _isLive;

    [ObservableProperty]
    private string _modelLabel = "";

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isRenaming;

    [ObservableProperty]
    private string _renameDraft = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMatchSnippet))]
    private string _matchSnippet = "";

    public bool HasMatchSnippet => MatchSnippet.Length > 0;

    public AgentLoopAgent? Agent { get; set; }

    public void Refresh(AgentLoopAgent? liveAgent, string title, string modelLabel)
    {
        Agent = liveAgent;
        IsLive = liveAgent is not null;
        Title = title.Length > 0 ? title : SessionId.Value;
        ModelLabel = modelLabel;
        RelativeTime = RelativeTimeText.Format(CreatedAt);
    }
}
