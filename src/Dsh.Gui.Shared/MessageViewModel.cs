using System.Globalization;
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
        IsExpanded = Kind is not (MessageKind.Tool or MessageKind.Result)
            && (!IsFoldable || (!streaming && BuildPreview(text).Length <= PreviewChars));
        _buffer.Append(text);
        Text = text;
    }

    public string Role { get; }

    /** 这条记录对应的事件序号; 时间线跳转/回退/分叉都按它定位(合成出来的界面行没有序号)。 */
    public long? Seq { get; init; }

    /** 产生这条记录的事件时间(epoch 毫秒); 合成行没有事件时间。 */
    public long? Time { get; init; }

    /** 时间线动作只对真实事件(有序号)开放。 */
    public bool HasTimelineEntry => Seq is not null;

    /** 气泡上的时刻: 今天的只显示时分, 更早的带上月日; 没有事件时间时为空。 */
    public string Timestamp => Time is { } time
        ? FormatTimestamp(DateTimeOffset.FromUnixTimeMilliseconds(time).ToLocalTime())
        : "";

    public bool HasTimestamp => Time is not null;

    private static string FormatTimestamp(DateTimeOffset time)
        => time.Date == DateTimeOffset.Now.Date
            ? time.ToString("HH:mm", CultureInfo.InvariantCulture)
            : time.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);

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
        OnPropertyChanged(nameof(BodyText));
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
    public bool IsFoldable => Kind is MessageKind.Reasoning or MessageKind.Tool or MessageKind.Result or MessageKind.Context or MessageKind.System
        || (CompactByDefault && Kind is MessageKind.User or MessageKind.Assistant);

    /**
     * 手机端把用户消息与助手正文也纳入折叠: 窄屏上一条长回复能占掉好几屏, 展开交给双击。
     * 桌面端宽度足够, 保持全展开, 所以这是一个显式开关而不是默认行为。
     */
    public static bool CompactByDefault { get; set; }

    public string FoldLabel => IsExpanded ? "▾ 折叠" : "▸ 展开";

    /**
     * 头部一行: 角色、时刻、折叠标记用" · "串起来。
     * 手机列表每条消息的控件数直接决定上滑插入的耗时(实测每个 TextBlock 约 0.4ms), 合成一行省掉一个 Grid 和两个 TextBlock。
     */
    public string HeaderLine
    {
        get
        {
            var parts = new List<string>(3);
            if (HasRole && Role.Length > 0)
                parts.Add(Role);
            if (HasTimestamp)
                parts.Add(Timestamp);
            if (IsFoldable)
                parts.Add(FoldLabel);
            return string.Join(" · ", parts);
        }
    }

    /** 折叠时显示摘要, 展开时显示全文; 两者互斥, 所以合成一个属性只占用一个 TextBlock。 */
    public string BodyText => ShowPreview ? Preview : Text;

    public bool HasDetail => Detail.Length > 0;

    public string Preview
    {
        get
        {
            if (Diff is { } diff)
                return $"{diff.Title} (+{diff.Added} -{diff.Removed})";
            if (Kind == MessageKind.Tool)
                return ToolName.Length > 0 ? $"{ToolName}" : "工具";
            // 折叠摘要只需开头一小段, 但流式刷新很频繁: 文本没变就不重算。
            if (!string.Equals(_previewFor, Text, StringComparison.Ordinal))
            {
                _preview = BuildPreview(Text);
                _previewFor = Text;
            }
            return _preview;
        }
    }

    private string? _preview;
    private string? _previewFor;

    /**
     * 只取开头一小段, 不整理全文。
     * 一条长回复有几万字, 每次流式刷新都把全文替换一遍再截断, 会把回放拖成秒级。
     */
    private static string BuildPreview(string text)
    {
        var flat = new StringBuilder(PreviewChars + 1);
        foreach (var character in text)
        {
            if (flat.Length > PreviewChars)
                break;
            if (character is not ('\r' or '\n'))
                flat.Append(character);
        }
        var compact = flat.ToString().Trim();
        return compact.Length <= PreviewChars ? compact : compact[..PreviewChars] + "…";
    }

    public bool ShowActions => Kind == MessageKind.Assistant && !IsStreaming;

    /** 文本发生变化(流式增量合并后)时触发, 供视图保持滚动到底部。 */
    public event Action<MessageViewModel>? Updated;

    /**
     * coalesce=false 用于历史重放: 那批增量是同一帧里连发过来的, 给每条消息排一个 16ms 定时器
     * 只会把上千个回调堆到 UI 线程上; 重放完由调用方统一 FlushPending 一次即可。
     */
    public void Append(string delta, bool coalesce = true)
    {
        _buffer.Append(delta);
        if (_flushScheduled || !coalesce)
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
        OnPropertyChanged(nameof(BodyText));
        Updated?.Invoke(this);
    }

    partial void OnTextChanged(string value) => OnPropertyChanged(nameof(BodyText));

    partial void OnDetailChanged(string value)
    {
        OnPropertyChanged(nameof(HasDetail));
        OnPropertyChanged(nameof(ShowDetail));
    }

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(FoldLabel));
        OnPropertyChanged(nameof(HeaderLine));
        OnPropertyChanged(nameof(BodyText));
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
