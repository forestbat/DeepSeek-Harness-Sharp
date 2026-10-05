using System.Text;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;

namespace Dsh.Tui;

public sealed class TranscriptRenderer
{
    public const int ToolArgumentsPreviewChars = 120;
    public const int ToolResultPreviewChars = 300;
    public const int SystemMessageFoldChars = 140;

    private readonly StringBuilder _buffer = new();
    private readonly List<TranscriptFold> _folds = [];
    private readonly List<string> _lines = [];
    private readonly List<TranscriptTint> _tints = [];
    private TranscriptTint _pendingTint;
    private readonly Dictionary<ToolCallId, (string Name, string Arguments)> _pendingToolCalls = [];
    private readonly List<int> _lineStarts = [];
    private readonly StringBuilder _tail = new();
    private int _tailStart;
    private bool _reasoningOpen;
    private bool _assistantOpen;
    private bool _assistantStreamed;
    private int _reasoningFoldStart;
    private int? _codeFenceStart;
    private string? _codeFenceKind;

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
            _folds.Add(new TranscriptFold { Start = start, End = end, Label = label, Preview = preview });
        BumpVersion();
    }

    public void AppendRaw(string text) => Append(text);

    public void AppendSystemMessage(string text)
    {
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
        var text = string.Concat(message.Content.OfType<TextBlock>().Select(block => block.Text));
        var imageNames = message.Content.OfType<ImageBlock>().Select(block => ImageName(block.Attachment)).ToList();
        var suffix = imageNames.Count == 0 ? "" : $" [图片: {string.Join(", ", imageNames)}]";
        Append($"\n❯ {text}{suffix}\n");
    }

    /** 只显示文件名, 绝不渲染包含账户目录的完整路径。 */
    private static string ImageName(ImageAttachmentRef attachment)
        => string.IsNullOrWhiteSpace(attachment.Name) ? "image" : attachment.Name;

    /** replay=true 用于会话切换后的历史回放: 用户消息平时由输入回显渲染, 只在回放时从事件补渲染。 */
    public void AppendSessionEvent(SessionEvent sessionEvent, bool replay = false, bool foldToolResult = true)
    {
        switch (sessionEvent.Data)
        {
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
                _pendingToolCalls[call.CallId] = (call.Name, call.Arguments);
                Append($"⚙ {call.Name} {Preview(call.Arguments, ToolArgumentsPreviewChars)}\n");
                break;
            case ToolResultPayload result:
                {
                    var text = MessageText.Flatten(result.Message.Content);
                    _pendingToolCalls.Remove(result.Message.Block.ToolCallId, out var pending);
                    var card = result.Error is null
                        ? DiffCardExtractor.TryExtract(pending.Name, pending.Arguments, result.Meta, text)
                        : null;
                    if (card is not null)
                    {
                        AppendDiffCard(card, foldToolResult);
                        break;
                    }
                    var label = result.Error is not null ? $"✗ {result.Error.Code} " : "↳ ";
                    var start = _buffer.Length;
                    Append($"  {label}{text}\n");
                    if (foldToolResult)
                    {
                        _folds.Add(new TranscriptFold
                        {
                            Start = start,
                            End = _buffer.Length,
                            Label = "tool result",
                            Preview = $"  {label}{Preview(text, ToolResultPreviewChars)}",
                        });
                    }

                    break;
                }
            case TurnEndPayload { Reason: TurnEndReason.Error error }:
                _assistantStreamed = false;
                Append($"  ✗ turn failed: {error.Failure.Code}: {error.Failure.Message}\n");
                break;
            case TurnEndPayload:
                _assistantStreamed = false;
                Append("\n");
                break;
        }
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
        var numberWidth = LineNumberWidth(card.Lines);
        foreach (var line in card.Lines)
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
            });
        }
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
        return flat.Length <= limit ? flat : $"{flat[..limit]}…";
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

