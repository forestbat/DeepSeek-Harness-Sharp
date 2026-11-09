using System.Text;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;

namespace Dsh.Tui;

public sealed class TranscriptRenderer
{
    public const int ToolResultPreviewChars = 300;
    public const int SystemMessageFoldChars = 140;
    public const string DeliverablesHint = "/deliverables 列出本会话交付物, /deliverables open <编号> 用默认程序打开";

    private readonly IContextMaintenancePresenter? _maintenance;
    private readonly StringBuilder _buffer = new();
    private readonly List<TranscriptFold> _folds = [];
    private readonly List<string> _lines = [];
    private readonly List<TranscriptTint> _tints = [];
    private TranscriptTint _pendingTint;
    private readonly Dictionary<ToolCallId, (string Name, string Arguments, TranscriptFold Fold)> _pendingToolCalls = [];
    private readonly List<int> _lineStarts = [];
    private readonly List<(int Start, UserMessage Message)> _userMessageStarts = [];
    private readonly StringBuilder _tail = new();
    private int _tailStart;
    private bool _reasoningOpen;
    private bool _assistantOpen;
    private bool _assistantStreamed;
    private int _reasoningFoldStart;
    private int? _codeFenceStart;
    private string? _codeFenceKind;

    /** 呈现接缝可为空: 没装提供该能力的插件时这些进度行不出现, 其余渲染不受影响。 */
    public TranscriptRenderer(IContextMaintenancePresenter? maintenance = null) => _maintenance = maintenance;

    /** 已完成行(不含换行符)与每行在全文中的起始偏移, 随 Append 增量维护; 供 wrap 缓存免物化全文。UI 线程亲和, 与 Version 同一时序读取。 */
    public IReadOnlyList<string> CompletedLines => _lines;

    /** 与 CompletedLines 对齐的行级着色提示(diff 卡片用红删绿增; 其余行 None)。 */
    internal IReadOnlyList<TranscriptTint> LineTints => _tints;

    public IReadOnlyList<int> LineStarts => _lineStarts;

    /** 未完成行(最后一段, 可能为空字符串); TailStart 是它的全文偏移。 */
    public string Tail => _tail.ToString();

    public int TailStart => _tailStart;

    public int Version { get; private set; }

    /** 当前全文偏移; 供调用方在追加事件前记录 fold 起点。 */
    public int CurrentLength
    {
        get
        {
            lock (_buffer)
                return _buffer.Length;
        }
    }

    public IReadOnlyList<TranscriptFold> Folds => _folds;

    /** 线程安全快照(完成行+非空尾行的拷贝): 非 UI 线程的读取方(窗格读取工具)走这里, 不碰 UI 亲和的 CompletedLines/Tail。 */
    public IReadOnlyList<string> SnapshotLines()
    {
        lock (_buffer)
        {
            var lines = new List<string>(_lines);
            if (_tail.Length > 0)
                lines.Add(_tail.ToString());
            return lines;
        }
    }

    public void BumpVersion()
    {
        Version++;
    }

    /** 由调用方追加一段正文后, 把 [start, CurrentLength) 收成一个 fold; 用于包装工具调用+结果等多事件区间。 */
    public void AddFold(int start, int end, string label, string preview)
    {
        if (end <= start)
            return;
        lock (_buffer)
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
        var start = _buffer.Length;
        Append(text);
        _folds.Add(new TranscriptFold
        {
            Start = start,
            End = _buffer.Length,
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
        var start = CurrentLength;
        Append($"\n❯ {text}{suffix}\n");
        _userMessageStarts.Add((start, message));
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

    /** 只显示文件名, 绝不渲染包含账户目录的完整路径。 */
    private static string ImageName(ImageAttachmentRef attachment)
        => string.IsNullOrWhiteSpace(attachment.Name) ? "image" : attachment.Name;

    /** replay=true 用于会话切换后的历史回放: 用户消息平时由输入回显渲染, 只在回放时从事件补渲染。 */
    public void AppendSessionEvent(SessionEvent sessionEvent, bool replay = false, bool foldToolResult = true)
    {
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
                var toolStart = _buffer.Length;
                Append($"⚙ {call.Name}\n");
                AppendIndented(ToolPresentation.FormatArguments(call.Arguments));
                var toolFold = new TranscriptFold
                {
                    Start = toolStart,
                    End = _buffer.Length,
                    Label = "tool",
                    Preview = $"⚙ {call.Name}",
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
                            diffFold.End = _buffer.Length;
                            // 折叠态显示"改了什么", 和 GUI 的编辑卡片标题一致。
                            diffFold.Label = "diff";
                            diffFold.Preview = $"  ⤿ {card.Title} (+{card.Added} -{card.Removed})";
                            diffFold.Paged = true;
                        }
                        break;
                    }
                    var label = result.Error is not null ? $"x {result.Error.Code} " : "↳ ";
                    var resultStart = _buffer.Length;
                    // 失败的工具结果整行按错误色着色(颜色由 TuiTheme.Error 决定, 设 Default 即不着色)。
                    _pendingTint = result.Error is not null ? TranscriptTint.Error : TranscriptTint.None;
                    Append($"  {label}{text}\n");
                    _pendingTint = TranscriptTint.None;
                    if (pending.Fold is { } resultFold)
                    {
                        resultFold.End = _buffer.Length;
                        // 失败结果直接展开: 错误信息不该藏在"⚙ 工具名"后面。
                        if (result.Error is not null)
                            resultFold.Collapsed = false;
                    }
                    else if (foldToolResult)
                    {
                        _folds.Add(new TranscriptFold
                        {
                            Start = resultStart,
                            End = _buffer.Length,
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
        var start = _buffer.Length;
        Append(headline + "\n");
        _folds.Add(new TranscriptFold
        {
            Start = start,
            End = _buffer.Length,
            Label = "compacted summary",
            Preview = headline,
        });
        if (summarized.Summary.Length == 0)
            return;
        var bodyStart = _buffer.Length;
        Append(summarized.Summary + "\n");
        _folds.Add(new TranscriptFold
        {
            Start = bodyStart,
            End = _buffer.Length,
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
        _reasoningFoldStart = _buffer.Length;
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
        var end = _buffer.Length;
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
                _codeFenceStart = _buffer.Length;
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
                    End = _buffer.Length,
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
        var start = _buffer.Length;
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
                End = _buffer.Length,
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
        lock (_buffer)
        {
            var baseOffset = _buffer.Length;
            _buffer.Append(text);
            UpdateLines(text, baseOffset);
            BumpVersion();
        }
    }

    /** 把新追加的文本增量并入行列表: 完整行落定到 _lines, 末尾不足一行留在 _tail。 */
    private void UpdateLines(string text, int baseOffset)
    {
        var position = 0;
        while (position < text.Length)
        {
            var newline = text.IndexOf('\n', position);
            if (newline < 0)
            {
                _tail.Append(text.AsSpan(position));
                break;
            }
            _tail.Append(text.AsSpan(position, newline - position));
            _lines.Add(_tail.ToString());
            _tints.Add(_pendingTint);
            _lineStarts.Add(_tailStart);
            _tail.Clear();
            _tailStart = baseOffset + newline + 1;
            position = newline + 1;
        }
    }
}
