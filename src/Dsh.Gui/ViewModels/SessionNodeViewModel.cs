using CommunityToolkit.Mvvm.ComponentModel;
using Dsh.Llm;

namespace Dsh.Gui.ViewModels;

/** A sidebar session, projected from the Host registry or persisted history awaiting Host migration. */
public sealed partial class SessionNodeViewModel : ObservableObject
{
    public required SessionId SessionId { get; init; }

    /** 工作区显示名(由 cwd 归并的末段名), 用于侧栏分组标题。 */
    public required string Workspace { get; init; }

    /** 分组键: 归一化后的完整工作区路径(空目录用占位名)。 */
    public required string WorkspacePath { get; init; }

    public required string Cwd { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private string _relativeTime = "";

    [ObservableProperty]
    private bool _isLive;

    /** 该会话的 agent 正在跑回合; 由 TurnStart/TurnEnd 事件更新, 侧栏据此显示"运行中"。 */
    [ObservableProperty]
    private bool _isRunning;

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

    /** 只在重命名时给侧栏模板一个非空 Content, 否则每行都会实例化一个隐藏 TextBox。 */
    public SessionNodeViewModel? RenameEditor => IsRenaming ? this : null;

    partial void OnIsRenamingChanged(bool value)
    {
        _ = value;
        OnPropertyChanged(nameof(RenameEditor));
    }
}
