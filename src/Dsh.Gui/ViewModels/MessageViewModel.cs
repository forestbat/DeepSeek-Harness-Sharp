using System.Text;
using Avalonia.Layout;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;

namespace Dsh.Gui.ViewModels;

public enum MessageKind
{
    User,
    Assistant,
    Reasoning,
    Context,
    Tool,
    Result,
    System,
    Approval,
}

public enum MessageFeedback
{
    None,
    Liked,
    Disliked,
}

/** 会话流里的一条记录; 流式内容按 16ms 合并刷新, 长内容可折叠。 */
public sealed partial class MessageViewModel : ObservableObject
{
    private const int CoalesceMilliseconds = 16;
    public const int PreviewChars = 140;

    private readonly StringBuilder _buffer = new();
    private bool _flushScheduled;

    public MessageViewModel(string role, string text, MessageKind kind, bool streaming)
    {
        Role = role;
        Kind = kind;
        IsStreaming = streaming;
        // 工具/结果默认折叠, 只显示工具名; 其余可折叠消息在短文本时直接展开。
        IsExpanded = Kind is not (MessageKind.Tool or MessageKind.Result) && (!IsFoldable || (!streaming && Flatten(text).Length <= PreviewChars));
        _buffer.Append(text);
        Text = text;
    }

    public string Role { get; }

    /** 这条记录对应的事件序号; 时间线跳转/回退/分叉都按它定位(合成出来的界面行没有序号)。 */
    public long? Seq { get; init; }

    /** 时间线动作只对真实事件(有序号)开放。 */
    public bool HasTimelineEntry => Seq is not null;

    public MessageKind Kind { get; }

    [ObservableProperty]
    private string _text = "";

    /** 折叠状态下展示的一行摘要; 上下文注入行把注入正文放在 Detail 里。 */
    [ObservableProperty]
    private string _detail = "";

    [ObservableProperty]
    private bool _isStreaming;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private MessageFeedback _feedback;

    public bool IsUser => Kind == MessageKind.User;

    public bool IsAssistant => Kind == MessageKind.Assistant;

    public bool IsReasoning => Kind == MessageKind.Reasoning;

    public bool IsTool => Kind is MessageKind.Tool or MessageKind.Result;

    /** 合并卡片的工具名; 折叠态只显示它。 */
    public string ToolName { get; init; } = "";

    /** 展开态显示的命令/参数; 已由 ToolPresentation 去掉 JSON 转义。 */
    public string ArgumentText { get; init; } = "";

    /** 工具结果文本; 工具卡片收到结果后填充。 */
    [ObservableProperty]
    private string _resultText = "";

    [ObservableProperty]
    private bool _hasResult;

    [ObservableProperty]
    private bool _isToolError;

    public bool IsContext => Kind == MessageKind.Context;

    public bool IsSystem => Kind is MessageKind.System or MessageKind.Approval;

    public bool ShowBody => !IsFoldable || IsExpanded;

    public bool ShowMarkdown => IsAssistant && ShowBody;

    public bool ShowPlainBody => ShowBody && !IsAssistant && !HasDiff && Kind != MessageKind.Tool;

    /** 展开的工具卡片显示命令/参数; 命中 diff 时由 diff 卡片替代。 */
    public bool ShowToolArguments => Kind == MessageKind.Tool && IsExpanded && !HasDiff && ArgumentText.Length > 0;

    /** 展开的工具卡片显示结果; 命中 diff 时由 diff 卡片替代。 */
    public bool ShowToolResult => Kind == MessageKind.Tool && IsExpanded && !HasDiff && HasResult;

    /** 工具结果命中 diff 提取时挂载的卡片; 展开时替代纯文本正文。 */
    public DiffCard? Diff { get; private set; }

    public bool HasDiff => Diff is not null;

    public bool ShowDiff => HasDiff && ShowBody;

    /** diff 卡片自身的展开态(卡片内 chevron 控制; 独立于消息折叠, 置于 VM 以免列表虚拟化后丢失)。 */
    [ObservableProperty]
    private bool _isDiffExpanded;

    public void SetDiff(DiffCard diff)
    {
        Diff = diff;
        // 消息级展开以露出卡片标题栏, 卡片本体仍保持折叠(IsDiffExpanded 默认 false)。
        IsExpanded = true;
        OnPropertyChanged(nameof(Diff));
        OnPropertyChanged(nameof(HasDiff));
        OnPropertyChanged(nameof(ShowDiff));
        OnPropertyChanged(nameof(ShowPlainBody));
        OnPropertyChanged(nameof(ShowToolArguments));
        OnPropertyChanged(nameof(ShowToolResult));
        OnPropertyChanged(nameof(Preview));
    }

    public void SetToolResult(string text, bool isError)
    {
        ResultText = text;
        HasResult = true;
        IsToolError = isError;
        OnPropertyChanged(nameof(ShowToolResult));
    }

    public bool ShowPreview => IsFoldable && !IsExpanded;

    public bool ShowDetail => IsContext && IsExpanded && Detail.Length > 0;

    public bool IsLiked => Feedback == MessageFeedback.Liked;

    public bool IsDisliked => Feedback == MessageFeedback.Disliked;

    /** 该工具消息来自 subagent 工具调用时携带子会话信息, 展开后内联显示子会话工具流摘要。 */
    public bool IsSubagentTool { get; set; }

    public string? SubagentLabel { get; set; }

    public SessionId? SubagentSessionId { get; set; }

    public IReadOnlyList<MessageViewModel> SubagentStream { get; private set; } = [];

    public bool ShowSubagentStream => IsSubagentTool && IsExpanded && SubagentStream.Count > 0;

    public bool ShowSubagentEmpty => IsSubagentTool && IsExpanded && SubagentStream.Count == 0;

    public void SetSubagentStream(IReadOnlyList<MessageViewModel> stream)
    {
        SubagentStream = stream;
        OnPropertyChanged(nameof(ShowSubagentStream));
        OnPropertyChanged(nameof(ShowSubagentEmpty));
    }

    /** 上下文注入行自带说明文案, 不再重复显示角色标签。 */
    public bool HasRole => Kind != MessageKind.Context;

    public HorizontalAlignment Align => IsUser ? HorizontalAlignment.Right : HorizontalAlignment.Left;

    /** 思考/工具/结果/上下文/系统消息可折叠; 摊平后不超过 PreviewChars 的短消息默认展开, 流式消息构造时内容未定, 默认折叠。 */
    public bool IsFoldable => Kind is MessageKind.Reasoning or MessageKind.Tool or MessageKind.Result or MessageKind.Context or MessageKind.System;

    public string FoldLabel => IsExpanded ? "▾ 折叠" : "▸ 展开";

    public bool HasDetail => Detail.Length > 0;

    public string Preview
    {
        get
        {
            if (Diff is { } diff)
                return $"{diff.Title} (+{diff.Added} -{diff.Removed})";
            if (Kind == MessageKind.Tool)
                return ToolName.Length > 0 ? $"{ToolName}" : "工具";
            var flat = Flatten(Text);
            return flat.Length <= PreviewChars ? flat : flat[..PreviewChars] + "…";
        }
    }

    private static string Flatten(string text) => text.Replace('\r', ' ').Replace('\n', ' ').Trim();

    public bool ShowActions => Kind == MessageKind.Assistant && !IsStreaming;

    /** 文本发生变化(流式增量合并后)时触发, 供视图保持滚动到底部。 */
    public event Action<MessageViewModel>? Updated;

    public void Append(string delta)
    {
        _buffer.Append(delta);
        if (_flushScheduled)
            return;
        _flushScheduled = true;
        DispatcherTimer.RunOnce(FlushPending, TimeSpan.FromMilliseconds(CoalesceMilliseconds));
    }

    public void SetText(string text)
    {
        _buffer.Clear();
        _buffer.Append(text);
        FlushPending();
    }

    public void FlushPending()
    {
        _flushScheduled = false;
        var text = _buffer.ToString();
        if (string.Equals(text, Text, StringComparison.Ordinal))
            return;
        Text = text;
        OnPropertyChanged(nameof(Preview));
        Updated?.Invoke(this);
    }

    partial void OnDetailChanged(string value)
    {
        OnPropertyChanged(nameof(HasDetail));
        OnPropertyChanged(nameof(ShowDetail));
    }

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(FoldLabel));
        OnPropertyChanged(nameof(ShowBody));
        OnPropertyChanged(nameof(ShowMarkdown));
        OnPropertyChanged(nameof(ShowPlainBody));
        OnPropertyChanged(nameof(ShowPreview));
        OnPropertyChanged(nameof(ShowDetail));
        OnPropertyChanged(nameof(ShowDiff));
        OnPropertyChanged(nameof(ShowToolArguments));
        OnPropertyChanged(nameof(ShowToolResult));
        OnPropertyChanged(nameof(ShowSubagentStream));
        OnPropertyChanged(nameof(ShowSubagentEmpty));
    }

    partial void OnFeedbackChanged(MessageFeedback value)
    {
        OnPropertyChanged(nameof(IsLiked));
        OnPropertyChanged(nameof(IsDisliked));
    }

    [RelayCommand]
    private void ToggleFold() => IsExpanded = !IsExpanded;
}
