using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;

namespace Dsh.Tui;

public sealed class TranscriptRenderer
{
    public const int ToolResultPreviewChars = 300;
    public const int SystemMessageFoldChars = 140;
    public const string DeliverablesHint = "/deliverables 列出本会话交付物, /deliverables open <编号> 用默认程序打开";

    /** 已完成行: 正文里的 (起点, 长度) 切片, 文本本身只在 TranscriptText 里存一份。 */
    public readonly record struct Line(int Start, int Length);

    private readonly Lock _gate = new();
    private readonly IContextMaintenancePresenter? _maintenance;
    private readonly TranscriptText _text = new();
    private readonly List<TranscriptFold> _folds = [];
    private readonly List<Line> _lines = [];
    private readonly List<TranscriptTint> _tints = [];
    /** 与 _lines 对齐的事件序号: 锚点靠它判断"重放之后这一行还是不是同一条内容"。 */
    private readonly List<long> _lineSeqs = [];
    private TranscriptTint _pendingTint;
    private readonly Dictionary<ToolCallId, (string Name, string Arguments, TranscriptFold Fold)> _pendingToolCalls = [];
    private readonly List<(int Start, UserMessage Message)> _userMessageStarts = [];
    private int _tailStart;
    private long _currentSeq;
    private long _tailSeq;
    private bool _reasoningOpen;
    private bool _assistantOpen;
    private bool _assistantStreamed;
    private int _reasoningFoldStart;
    private int? _codeFenceStart;
    private string? _codeFenceKind;

    /** 呈现接缝可为空: 没装提供该能力的插件时这些进度行不出现, 其余渲染不受影响。 */
    public TranscriptRenderer(IContextMaintenancePresenter? maintenance = null) => _maintenance = maintenance;

    /** 已完成的逻辑行数(不含尾部未闭合的那一行)。 */
    public int LineCount
    {
        get
        {
            lock (_gate)
                return _lines.Count;
        }
    }

    /** 第 index 行的切片(起点/长度都在全文坐标系里)。UI 线程亲和, 与 Version 同一时序读取。 */
    public Line LineAt(int index) => _lines[index];

    /** 第 index 行的只读切片; 需要 string(导出/复制/日志)时才 ToString。UI 线程亲和(与绘制同一时序)。 */
    public ReadOnlySpan<char> LineSpan(int index)
    {
        var line = _lines[index];
        return line.Length == 0 ? ReadOnlySpan<char>.Empty : _text.Slice(line.Start, line.Length);
    }

    /** 全文坐标里的任意一段: 折行行只记区间, 绘制时按这里取切片(同一段文字只驻留一份)。 */
    public ReadOnlySpan<char> TextSpan(int start, int length) => _text.Slice(start, length);

    /** 与 LineAt 对齐的行级着色提示(diff 卡片用红删绿增; 其余行 None)。 */
    internal IReadOnlyList<TranscriptTint> LineTints => _tints;

    /** 未完成行(最后一段, 可能为空): 从 TailStart 到正文末尾。UI 线程亲和。 */
    public ReadOnlySpan<char> TailSpan => _text.SliceToEnd(_tailStart);

    public string Tail => new(TailSpan);

    public int TailStart
    {
        get
        {
            lock (_gate)
                return _tailStart;
        }
    }

    public int Version { get; private set; }

    /** 当前全文偏移; 供调用方在追加事件前记录 fold 起点。 */
    public int CurrentLength
    {
        get
        {
            lock (_gate)
                return _text.Length;
        }
    }

    public IReadOnlyList<TranscriptFold> Folds => _folds;

    /** 每清空一次自增: 行号与包裹行缓存已经作废, 消费方据此丢弃按行号缓存的状态。 */
    public int ResetVersion { get; private set; }

    /** 线程安全快照(完成行+非空尾行的拷贝): 非 UI 线程的读取方(窗格读取工具)走这里, 不碰 UI 亲和的 LineSpan/TailSpan。 */
    public IReadOnlyList<string> SnapshotLines()
    {
        lock (_gate)
        {
            var lines = new List<string>(_lines.Count + 1);
            foreach (var line in _lines)
                lines.Add(new string(_text.Slice(line.Start, line.Length)));
            var tail = _text.SliceToEnd(_tailStart);
            if (tail.Length > 0)
                lines.Add(new string(tail));
            return lines;
        }
    }

    /** 这个偏移属于哪条事件: 锚点重建后据此确认"还是那一行", 而不是按字数猜。 */
    public long SeqAt(int offset)
    {
        lock (_gate)
        {
            if (offset >= _text.Length)
                return _tailSeq;
            if (_lines.Count == 0)
                return _tailSeq;
            var low = 0;
            var high = _lines.Count;
            while (low < high)
            {
                var middle = (low + high) >>> 1;
                if (_lines[middle].Start <= offset)
                    low = middle + 1;
                else
                    high = middle;
            }
            return low == 0 ? _lineSeqs[0] : _lineSeqs[low - 1];
        }
    }

    public void BumpVersion() => Version++;

    /** 由调用方追加一段正文后, 把 [start, CurrentLength) 收成一个 fold; 用于包装工具调用+结果等多事件区间。 */
    public void AddFold(int start, int end, string label, string preview)
    {
        if (end <= start)
            return;
        lock (_gate)
        {
            // 同一段内容已经被收成 fold(例如工具调用 fold 已覆盖这段)时只延长它, 不叠第二个 fold。
            var existing = _folds.FirstOrDefault(fold => fold.Start == start);
            if (existing is not null)
            {
                existing.End = Math.Max(existing.End, end);
                // 后到的 fold 更具体(例如 subagent 卡片带标签)时, 用它的名字做折叠头。
                if (existing.Label == "tool" && label != "tool")
                {
                    existing.Label = label;
                    existing.Preview = preview;
                }
                BumpVersion();
                return;
            }
            _folds.Add(new TranscriptFold { Start = start, End = end, Label = label, Preview = preview });
        }
        BumpVersion();
    }

    public void AppendRaw(string text) => Append(text);

    /**
     * 清空转录, 供"接上更早的历史之后整篇重放"用。
     *
     * 转录是只增的行缓冲, 往上接内容只能从头重放: 先 Clear 再按新的起点重放, 调用方负责随后把视口挪到
     * 原来那一段(见 ChatPane.LoadOlderHistory)。清空要连同折叠、行起点、用户消息索引与流式状态一起复位,
     * 否则新内容会带着旧账渲染(折叠指向已不存在的行、行号错位)。
     */
    public void Clear()
    {
        lock (_gate)
        {
            _text.Clear();
            _folds.Clear();
            _lines.Clear();
            _tints.Clear();
            _lineSeqs.Clear();
            _pendingTint = TranscriptTint.None;
            _pendingToolCalls.Clear();
            _userMessageStarts.Clear();
            _tailStart = 0;
            _currentSeq = 0;
            _tailSeq = 0;
            _reasoningOpen = false;
            _assistantOpen = false;
            _assistantStreamed = false;
            _reasoningFoldStart = 0;
            _codeFenceStart = null;
            _codeFenceKind = null;
        }
        Version++;
        ResetVersion++;
    }

    public void AppendSystemMessage(string text)
    {
        text = UserFacingText.StripSystemReminders(text);
        if (text.Length == 0)
            return;
        var flat = text.Replace("\r\n", " ").Replace('\n', ' ').Trim();
        if (flat.Length <= SystemMessageFoldChars)
        {
            Append(text);
            return;
        }
        var start = CurrentLength;
        Append(text);
        _folds.Add(new TranscriptFold
        {
            Start = start,
            End = CurrentLength,
            Label = "output",
            Preview = Preview(flat, ToolResultPreviewChars),
        });
    }

    public void AppendUserMessage(UserMessage message)
    {
        var text = UserFacingText.StripSystemReminders(string.Concat(message.Content.OfType<TextBlock>().Select(block => block.Text)));
        if (text.Length == 0)
            return;
        var imageNames = message.Content.OfType<ImageBlock>().Select(block => ImageName(block.Attachment)).ToList();
        var suffix = imageNames.Count == 0 ? "" : $" [图片: {string.Join(", ", imageNames)}]";
        // 用户消息是回合边界: 先把还开着的折叠在这里收口。
        // 否则 CloseReasoning 会用**稍后**才到的 CurrentLength 作结束位置, 折叠区间把整条用户消息包进去 ——
        // 折叠态下用户看不到自己发的话, 跳转也只能落到折叠头 "▸ [thinking]" 上。
        CloseReasoning();
        CloseAssistant();
        // 代码块同理: 助手开了 ``` 却没收尾时, 那个折叠会一直延伸到本条消息里 ——
        // 用户看不到自己发的话, 跳转也只能落到折叠头 "▸ ```" 上。
        CloseCodeFence();
        // 锚点必须是 "❯" 那一行的起点: 记成前导换行的位置会让跳转把一条空行顶到视口最上方,
        // 目标消息被挤到第二行 —— 看上去就是"跳过去了但没跳准"。
        var start = CurrentLength + 1;
        Append($"\n❯ {text}{suffix}\n");
        _userMessageStarts.Add((start, message));
    }

    /**
     * 已排上队(在 inbox 里等着)的用户消息。
     * 它还不是 user/message 事件, 所以既不能当历史画, 也不能不画 —— 不画的话切走会话再切回来就"消失"了。
     */
    public void AppendQueuedUserMessage(UserMessage message)
    {
        var text = UserFacingText.StripSystemReminders(string.Concat(message.Content.OfType<TextBlock>().Select(block => block.Text)));
        if (text.Length == 0)
            return;
        var imageNames = message.Content.OfType<ImageBlock>().Select(block => ImageName(block.Attachment)).ToList();
        var suffix = imageNames.Count == 0 ? "" : $" [图片: {string.Join(", ", imageNames)}]";
        Append($"\n⏸ 已排队: {text}{suffix}\n");
    }

    /**
     * 用户消息在全文里的起始偏移, 供 `/timestamp jump` 按消息(而不是按文本内容)定位:
     * 按文本找首个命中会跳到更早引用过同一句话的地方, 首行被折行时也找不到。
     */
    public int? UserMessageStart(UserMessage message)
    {
        foreach (var (start, candidate) in _userMessageStarts)
        {
            if (ReferenceEquals(candidate, message))
                return start;
        }
        return null;
    }

    /** 第 index 条用户消息的全文偏移(按渲染顺序); 越界返回 null。 */
    public int? UserMessageStartAt(int index)
        => index >= 0 && index < _userMessageStarts.Count ? _userMessageStarts[index].Start : null;

    /** 当前仍留在转录里的用户消息条数。 */
    public int UserMessageCount => _userMessageStarts.Count;

    /** 只显示文件名, 绝不渲染包含账户目录的完整路径。 */
    private static string ImageName(ImageAttachmentRef attachment)
        => string.IsNullOrWhiteSpace(attachment.Name) ? "image" : attachment.Name;

    /** 转录末尾可能停在这些未闭合的构造里; 补页要把它们当成"接缝不干净"。 */
    [Flags]
    public enum OpenConstruct
    {
        None = 0,
        Reasoning = 1,
        Assistant = 2,
        CodeFence = 4,
        ToolCall = 8,
    }

    /**
     * 这份转录末尾停在哪些未闭合的构造里。
     * 补页时用它判断"这一页的结尾能不能当成干净的接缝": 停在构造中间时, 单独渲染这一页会把它截断,
     * 必须退回整段重放才能保持连续。
     */
    public OpenConstruct OpenConstructs
    {
        get
        {
            lock (_gate)
            {
                var open = OpenConstruct.None;
                if (_reasoningOpen)
                    open |= OpenConstruct.Reasoning;
                if (_assistantOpen)
                    open |= OpenConstruct.Assistant;
                if (_codeFenceStart is not null)
                    open |= OpenConstruct.CodeFence;
                if (_pendingToolCalls.Count > 0)
                    open |= OpenConstruct.ToolCall;
                return open;
            }
        }
    }

    public bool HasOpenConstruct => OpenConstructs != OpenConstruct.None;

    /**
     * 就地收口本转录末尾还开着的构造, 让页边界成为干净接缝。
     *
     * 为什么需要它: 页是按事件数硬切的, 页尾可能停在 thinking/代码块/工具调用中间。
     * 这时若为了"连续"而退回整段重放, 每遇到一次就要重放全部已载入前缀
     * (百万条会话实测 15 秒一步)。收口只关掉本页自己的构造, 内容一字不少,
     * 代价仅仅是折叠在页边界处多一个边界 —— 页边界本来就是人为的, 不该让内容跨越它。
     */
    public void SealBoundary()
    {
        lock (_gate)
        {
            CloseReasoning();
            CloseAssistant();
            CloseCodeFence();
            // 工具调用的参数已经写进正文、fold 也已经闭合; 这里只需丢掉"等结果再扩展"的账本:
            // 结果在更晚的页里, 而更晚的页属于本文, 不属于这一页。
            _pendingToolCalls.Clear();
            Version++;
        }
    }

    /**
     * 收口未闭合的代码块: 补一个结束围栏, 把 [开栏, 这里) 收成一个折叠。
     * 页边界与用户消息边界都要收口 —— 折叠跨过边界就会把边界之后的内容一并藏起来。
     */
    private void CloseCodeFence()
    {
        if (_codeFenceStart is not { } fence)
            return;
        Append("```\n");
        _folds.Add(new TranscriptFold
        {
            Start = fence,
            End = CurrentLength,
            Label = string.IsNullOrWhiteSpace(_codeFenceKind) ? "code" : _codeFenceKind,
            Preview = string.IsNullOrWhiteSpace(_codeFenceKind) ? "```" : $"```{_codeFenceKind}",
        });
        _codeFenceStart = null;
        _codeFenceKind = null;
    }

    /**
     * 把另一份转录(只含更早的一页)整体插到本文最前面, 本文既有内容的偏移随之平移。
     * 返回平移量, 调用方按它平移自己的偏移记账(折行行/锚点等)。
     *
     * 为什么不是"Clear 再重放整段": 那样每补一页都要把已载入的前缀重新物化一遍,
     * 补到顶的总成本是 O(页数²)(百万条会话要几分钟)。前置只付一页的渲染成本。
     */
    public int PrependFrom(TranscriptRenderer other)
    {
        ArgumentNullException.ThrowIfNull(other);
        lock (_gate)
        {
            lock (other._gate)
            {
                // 这一页若停在半行, 先补一个换行: 否则它会和本文第一行黏成同一行。
                if (other._tailStart < other._text.Length)
                    other.Append("\n");
                var delta = other._text.Length;
                if (delta == 0)
                    return 0;
                _text.Prepend(other._text.Span);
                ShiftForPrepend(delta);
                _lines.InsertRange(0, other._lines);
                _tints.InsertRange(0, other._tints);
                _lineSeqs.InsertRange(0, other._lineSeqs);
                _folds.InsertRange(0, other._folds);
                _userMessageStarts.InsertRange(0, other._userMessageStarts);
                foreach (var (callId, pending) in other._pendingToolCalls)
                    _pendingToolCalls[callId] = pending;
                _tailStart += delta;
                if (_codeFenceStart is { } fence)
                    _codeFenceStart = fence + delta;
                if (_reasoningOpen)
                    _reasoningFoldStart += delta;
                Version++;
                return delta;
            }
        }
    }

    /** 前置之后, 本文既有内容的所有偏移整体右移 delta。 */
    private void ShiftForPrepend(int delta)
    {
        for (var index = 0; index < _lines.Count; index += 1)
        {
            var line = _lines[index];
            _lines[index] = new Line(line.Start + delta, line.Length);
        }
        foreach (var fold in _folds)
        {
            fold.Start += delta;
            fold.End += delta;
        }
        for (var index = 0; index < _userMessageStarts.Count; index += 1)
        {
            var (start, message) = _userMessageStarts[index];
            _userMessageStarts[index] = (start + delta, message);
        }
    }

    /** 渲染器按事件追加时记下序号: 行->序号映射是锚点"还是同一行"的判据。 */
    public void MarkEvent(long seq)
    {
        _currentSeq = seq;
        _tailSeq = seq;
    }

    /** replay=true 用于会话切换后的历史回放: 用户消息平时由输入回显渲染, 只在回放时从事件补渲染。 */
    /**
     * 不在转录上产生任何字符的事件类型。
     *
     * 这张表等于"AppendSessionEvent 的 switch 没处理到的类型": 它们一个字符都不画, 但它们的
     * `Data` 是延迟解析的, 逐条解析要付 JSON 解析成本。会话越长这类事件占比越高
     * (实测百万条会话里 99.7% 是安全决策审计), 补页时逐条解析它们会占掉绝大部分时间。
     *
     * 信封上的 `Type` 不需要解析, 用它提前挡掉; 表与 switch 的一致性由 TranscriptLinesTests 钉住
     * (表里每个类型都必须"画不出字符"), 所以它不是针对某个会话的特判。
     */
    private static readonly HashSet<string> SilentEventTypes = new(StringComparer.Ordinal)
    {
        SessionEventTypes.TurnStart,
        SessionEventTypes.StepStart,
        SessionEventTypes.StepEnd,
        SessionEventTypes.RequestHeader,
        SessionEventTypes.RequestContext,
        SessionEventTypes.SessionEndSeed,
        SessionEventTypes.AgentInboxSpliced,
        SessionEventTypes.WorkingDirectoryChange,
        SessionEventTypes.FileChanged,
        SessionEventTypes.ToolDisclosed,
        SafetyDecisionAuditPayload.EventType,
    };

    /** 这条事件在转录上不产生任何字符(因此连载荷都不必解析)。 */
    public static bool IsSilent(SessionEvent sessionEvent) => SilentEventTypes.Contains(sessionEvent.Type);

    public void AppendSessionEvent(SessionEvent sessionEvent, bool replay = false, bool foldToolResult = true)
    {
        MarkEvent(sessionEvent.Seq);
        // 画不出字符的事件: 直接返回, 不解析载荷, 也不问维护提供方。
        if (IsSilent(sessionEvent))
            return;
        // 上下文整理(压缩)的进度由提供方翻译成中立视图; 渲染器只按视图出图, 不引用产生它的插件。
        if (_maintenance?.Describe(sessionEvent) is { } maintenance)
        {
            AppendMaintenance(maintenance, replay);
            return;
        }
        switch (sessionEvent.Data)
        {
            // 整理产生的检查点正文已由摘要事件渲染过, 再当消息画一遍会出现两段同样的摘要。
            case UserMessagePayload user when _maintenance?.IsCheckpointMessage(user.Message.Source) == true:
                Append("  （更早的历史已由上面这段摘要取代, 不再发送给模型）\n");
                break;
            case UserMessagePayload user when replay && user.Message.Source is UserMessageSource:
                AppendUserMessage(user.Message);
                break;
            case AssistantChunkPayload chunk:
                AppendChunk(chunk.Chunk);
                break;
            case AssistantMessagePayload assistant:
                AppendFinalAssistant(assistant.Message);
                break;
            case ToolCallPayload call:
                CloseReasoning();
                CloseAssistant();
                _assistantStreamed = false;
                var toolStart = CurrentLength;
                Append($"⚙ {call.Name}\n");
                AppendIndented(ToolPresentation.FormatArguments(call.Arguments));
                var toolFold = new TranscriptFold
                {
                    Start = toolStart,
                    End = CurrentLength,
                    Label = "tool",
                    // 折叠头只给名字: 三角标记由渲染侧统一加(⚙ 在部分字体里缺字形, 会显示成 *).
                    Preview = call.Name,
                    // 工具参数与结果可能很长: 展开后按 20 行分页, 而不是一次铺满屏幕。
                    Paged = true,
                };
                _folds.Add(toolFold);
                _pendingToolCalls[call.CallId] = (call.Name, call.Arguments, toolFold);
                break;
            case ToolResultPayload result:
                {
                    var text = ToolPresentation.FormatResult(MessageText.Flatten(result.Message.Content));
                    _pendingToolCalls.Remove(result.Message.Block.ToolCallId, out var pending);
                    var card = result.Error is null
                        ? DiffCardExtractor.TryExtract(pending.Name, pending.Arguments, result.Meta, text)
                        : null;
                    if (card is not null)
                    {
                        AppendDiffCard(card, fold: pending.Fold is null && foldToolResult);
                        if (pending.Fold is { } diffFold)
                        {
                            diffFold.End = CurrentLength;
                            // 折叠态显示"改了什么", 和 GUI 的编辑卡片标题一致。
                            diffFold.Label = "diff";
                            diffFold.Preview = $"  ⤿ {card.Title} (+{card.Added} -{card.Removed})";
                            diffFold.Paged = true;
                        }
                        break;
                    }
                    var label = result.Error is not null ? $"x {result.Error.Code} " : "↳ ";
                    var resultStart = CurrentLength;
                    // 失败的工具结果整行按错误色着色(颜色由 TuiTheme.Error 决定, 设 Default 即不着色)。
                    _pendingTint = result.Error is not null ? TranscriptTint.Error : TranscriptTint.None;
                    Append($"  {label}{text}\n");
                    _pendingTint = TranscriptTint.None;
                    if (pending.Fold is { } resultFold)
                    {
                        resultFold.End = CurrentLength;
                        // 失败结果直接展开: 错误信息不该藏在"⚙ 工具名"后面。
                        if (result.Error is not null)
                            resultFold.Collapsed = false;
                    }
                    else if (foldToolResult)
                    {
                        _folds.Add(new TranscriptFold
                        {
                            Start = resultStart,
                            End = CurrentLength,
                            Label = "tool result",
                            Preview = $"  {label}{Preview(text, ToolResultPreviewChars)}",
                        });
                    }

                    break;
                }
            case ApprovalAskedPayload asked when ApprovalHints.SettingsDiff(asked.Arguments) is { Length: > 0 } diff:
                Append($"\nSettings approval · {asked.ToolName}\n{ApprovalHints.PrimaryArgument(asked.ToolName, asked.Arguments)}\n{diff}\n");
                break;
            case ApprovalDecidedPayload decided:
                Append($"  approval: {decided.Outcome}\n");
                break;
            case SettingsMutationAuditPayload mutation:
                Append($"  settings {mutation.Outcome} · {mutation.ToolName} · {mutation.Path}\n");
                break;
            case DeliverablesPresentedPayload presented:
                AppendDeliverables(presented);
                break;
            case TurnEndPayload { Reason: TurnEndReason.Error error }:
                _assistantStreamed = false;
                _pendingTint = TranscriptTint.Error;
                Append($"  x turn failed: {error.Failure.Code}: {error.Failure.Message}\n");
                _pendingTint = TranscriptTint.None;
                break;
            case TurnEndPayload:
                _assistantStreamed = false;
                Append("\n");
                break;
        }
    }

    /** 上下文整理的进度行: 只按中立视图出图, 因此这里不出现任何压缩实现类型。 */
    private void AppendMaintenance(ContextMaintenanceEvent maintenance, bool replay)
    {
        switch (maintenance)
        {
            // 重放时不画"进行中": 历史上那次整理要么已有结果行, 要么是残留的未闭合标记。
            case ContextMaintenanceEvent.Started when !replay:
                CloseReasoning();
                CloseAssistant();
                _assistantStreamed = false;
                Append("\n⏳ 正在压缩上下文…\n");
                break;
            case ContextMaintenanceEvent.Pruned pruned:
                Append($"✂ 已裁剪 {pruned.Items} 条过大的工具结果 (~{pruned.Tokens} tokens)\n");
                break;
            case ContextMaintenanceEvent.Summarized summarized:
                AppendSummarized(summarized);
                break;
            case ContextMaintenanceEvent.Failed failed:
                _pendingTint = TranscriptTint.Error;
                Append($"x 压缩失败: {Preview(failed.Error, ToolResultPreviewChars)}\n");
                _pendingTint = TranscriptTint.None;
                break;
        }
    }

    /**
     * 整理结果: 标题行给出"收起了多少历史、省了多少 token", 摘要正文放进折叠块。
     * 折叠是必要的 —— 摘要可能长达数千字符, 直接铺开会把用户正在看的内容顶出屏幕。
     */
    private void AppendSummarized(ContextMaintenanceEvent.Summarized summarized)
    {
        var headline = $"✔ 上下文已压缩: 收起 {summarized.Items} 条历史 (~{summarized.Tokens} tokens)";
        var start = CurrentLength;
        Append(headline + "\n");
        _folds.Add(new TranscriptFold
        {
            Start = start,
            End = CurrentLength,
            Label = "compacted summary",
            Preview = headline,
        });
        if (summarized.Summary.Length == 0)
            return;
        var bodyStart = CurrentLength;
        Append(summarized.Summary + "\n");
        _folds.Add(new TranscriptFold
        {
            Start = bodyStart,
            End = CurrentLength,
            Label = "summary",
            Preview = Preview(summarized.Summary, SystemMessageFoldChars),
        });
    }

    /** 助手最终消息只在没有任何流式文本时补渲染(非流式适配器), 否则正文已由 chunk 渲染过。 */
    private void AppendFinalAssistant(Message message)
    {
        if (_assistantStreamed)
        {
            _assistantStreamed = false;
            return;
        }
        var text = string.Concat(message.Content.OfType<TextBlock>().Select(block => block.Text));
        if (text.Length == 0)
            return;
        CloseReasoning();
        OpenAssistant();
        AppendCodeFenceAware(text);
        CloseAssistant();
    }

    private void AppendChunk(StreamChunk chunk)
    {
        switch (chunk)
        {
            case StreamChunk.BlockStart { BlockType: "reasoning" }:
                CloseAssistant();
                OpenReasoning();
                break;
            case StreamChunk.ReasoningDelta reasoning:
                OpenReasoning();
                Append(reasoning.Text);
                break;
            case StreamChunk.BlockEnd { Block: ReasoningBlock }:
                CloseReasoning();
                break;
            case StreamChunk.BlockStart { BlockType: "text" }:
                CloseReasoning();
                OpenAssistant();
                break;
            case StreamChunk.TextDelta text:
                CloseReasoning();
                OpenAssistant();
                _assistantStreamed = true;
                AppendCodeFenceAware(text.Text);
                break;
            case StreamChunk.BlockEnd { Block: TextBlock }:
                CloseAssistant();
                break;
            case StreamChunk.Finish or StreamChunk.Usage:
                break;
        }
    }

    private void OpenReasoning()
    {
        if (_reasoningOpen)
            return;
        Append("\n");
        _reasoningFoldStart = CurrentLength;
        Append("[thinking] ");
        _reasoningOpen = true;
    }

    private void OpenAssistant()
    {
        if (_assistantOpen)
            return;
        Append("\n");
        _assistantOpen = true;
    }

    private void CloseReasoning()
    {
        if (!_reasoningOpen)
            return;
        var end = CurrentLength;
        Append("\n");
        _folds.Add(new TranscriptFold
        {
            Start = _reasoningFoldStart,
            End = end,
            Label = "thinking",
            Preview = "[thinking]",
        });
        _reasoningOpen = false;
    }

    private void CloseAssistant()
    {
        if (!_assistantOpen)
            return;
        Append("\n");
        _assistantOpen = false;
    }

    private void AppendCodeFenceAware(string text)
    {
        var searchStart = 0;
        while (true)
        {
            var marker = text.IndexOf("```", searchStart, StringComparison.Ordinal);
            if (marker < 0)
            {
                Append(text[searchStart..]);
                return;
            }

            Append(text[searchStart..marker]);
            if (_codeFenceStart is null)
            {
                _codeFenceStart = CurrentLength;
                var lineEnd = text.IndexOf('\n', marker);
                var markerLine = lineEnd < 0 ? text[marker..] : text[marker..lineEnd];
                _codeFenceKind = markerLine.TrimStart('`').Trim();
                Append(markerLine);
                if (lineEnd >= 0)
                {
                    Append("\n");
                    searchStart = lineEnd + 1;
                }
                else
                {
                    searchStart = text.Length;
                }
            }
            else
            {
                Append("```");
                _folds.Add(new TranscriptFold
                {
                    Start = _codeFenceStart.Value,
                    End = CurrentLength,
                    Label = string.IsNullOrWhiteSpace(_codeFenceKind) ? "code" : _codeFenceKind,
                    Preview = string.IsNullOrWhiteSpace(_codeFenceKind) ? "```" : $"```{_codeFenceKind}",
                });
                _codeFenceStart = null;
                _codeFenceKind = null;
                searchStart = marker + 3;
            }
        }
    }

    /** diff 卡片命中时替换原始结果文本: 标题栏即折叠 preview, 展开为双列行号 + +/- 标记的行列表。 */
    private void AppendDiffCard(DiffCard card, bool fold)
    {
        var start = CurrentLength;
        var title = $"  ⤿ {card.Title} (+{card.Added} -{card.Removed})";
        _pendingTint = TranscriptTint.DiffTitle;
        Append($"{title}\n");
        var display = DiffCardDisplay.ForDisplay(card);
        var numberWidth = LineNumberWidth(display);
        foreach (var line in display)
        {
            _pendingTint = line.Kind switch
            {
                DiffLineKind.Add => TranscriptTint.DiffAdded,
                DiffLineKind.Delete => TranscriptTint.DiffRemoved,
                _ => TranscriptTint.None,
            };
            Append(FormatDiffLine(line, numberWidth));
        }
        _pendingTint = TranscriptTint.None;
        if (fold)
        {
            _folds.Add(new TranscriptFold
            {
                Start = start,
                End = CurrentLength,
                Label = "diff",
                Preview = title,
                Paged = true,
            });
        }
    }

    /** 交付物卡片: 每行一个绝对路径(可直接复制), 末行提示用 /deliverables 打开。 */
    private void AppendDeliverables(DeliverablesPresentedPayload presented)
    {
        Append("  [交付物]\n");
        foreach (var file in presented.Files)
            Append(file.Description is { } description ? $"    {file.Path} - {description}\n" : $"    {file.Path}\n");
        Append($"    {DeliverablesHint}\n");
    }

    private static int LineNumberWidth(IReadOnlyList<DiffLine> lines)
    {
        var max = 0;
        foreach (var line in lines)
            max = Math.Max(max, Math.Max(line.OldLine ?? 0, line.NewLine ?? 0));
        var width = 1;
        while (max >= 10)
        {
            max /= 10;
            width++;
        }
        return width;
    }

    private static string FormatDiffLine(DiffLine line, int width)
    {
        if (line.OldLine is null && line.NewLine is null)
            return $"  {line.Text}\n";
        var marker = line.Kind switch
        {
            DiffLineKind.Add => "+",
            DiffLineKind.Delete => "-",
            _ => " ",
        };
        var oldNo = line.OldLine?.ToString().PadLeft(width) ?? new string(' ', width);
        var newNo = line.NewLine?.ToString().PadLeft(width) ?? new string(' ', width);
        return $"  {oldNo} {newNo} {marker} {line.Text}\n";
    }

    private static string Preview(string text, int limit)
    {
        var flat = text.Replace("\r\n", " ").Replace('\n', ' ').Trim();
        return flat.Length <= limit ? flat : $"{flat[..limit]}~";
    }

    private void AppendIndented(string text)
    {
        if (text.Length == 0)
            return;
        foreach (var line in text.Split('\n'))
            Append($"  {line}\n");
    }

    private void Append(string text)
    {
        lock (_gate)
        {
            var baseOffset = _text.Length;
            _text.Append(text);
            UpdateLines(text, baseOffset);
            Version++;
        }
    }

    /** 把新追加的文本增量并入行列表: 完整行落定到 _lines, 末尾不足一行留作尾部(偏移即 _tailStart..末尾)。 */
    private void UpdateLines(string text, int baseOffset)
    {
        var position = 0;
        while (position < text.Length)
        {
            var newline = text.IndexOf('\n', position);
            if (newline < 0)
                break;
            var end = baseOffset + newline;
            _lines.Add(new Line(_tailStart, end - _tailStart));
            _tints.Add(_pendingTint);
            _lineSeqs.Add(_currentSeq);
            _tailStart = end + 1;
            position = newline + 1;
        }
        _tailSeq = _currentSeq;
    }
}

