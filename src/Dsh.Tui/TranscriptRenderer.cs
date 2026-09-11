using System.Text;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Tui;

public sealed class TranscriptRenderer
{
    public const int ToolArgumentsPreviewChars = 120;
    public const int ToolResultPreviewChars = 300;

    private readonly StringBuilder _buffer = new();
    private readonly List<TranscriptFold> _folds = [];
    private int _renderedLength;
    private bool _reasoningOpen;
    private bool _assistantOpen;
    private int _reasoningFoldStart;
    private int? _codeFenceStart;
    private string? _codeFenceKind;

    public string FullText => _buffer.ToString();

    public IReadOnlyList<TranscriptFold> Folds => _folds;

    public void AppendRaw(string text) => Append(text);

    public string TakeDelta()
    {
        lock (_buffer)
        {
            var delta = _buffer.ToString(_renderedLength, _buffer.Length - _renderedLength);
            _renderedLength = _buffer.Length;
            return delta;
        }
    }

    public void AppendUserMessage(UserMessage message)
    {
        var text = string.Concat(message.Content.OfType<TextBlock>().Select(block => block.Text));
        Append($"\n❯ {text}\n");
    }

    public void AppendSessionEvent(SessionEvent sessionEvent)
    {
        switch (sessionEvent.Data)
        {
            case AssistantChunkPayload chunk:
                AppendChunk(chunk.Chunk);
                break;
            case ToolCallPayload call:
                CloseReasoning();
                CloseAssistant();
                Append($"⚙ {call.Name} {Preview(call.Arguments, ToolArgumentsPreviewChars)}\n");
                break;
            case ToolResultPayload result:
            {
                var text = string.Concat(result.Message.Content.OfType<TextBlock>().Select(block => block.Text));
                var label = result.Error is not null ? $"✗ {result.Error.Code} " : "↳ ";
                var start = _buffer.Length;
                Append($"  {label}{text}\n");
                _folds.Add(new TranscriptFold
                {
                    Start = start,
                    End = _buffer.Length,
                    Label = "tool result",
                    Preview = $"  {label}{Preview(text, ToolResultPreviewChars)}",
                });
                break;
            }
            case TurnEndPayload { Reason: TurnEndReason.Error error }:
                Append($"  ✗ turn failed: {error.Failure.Code}: {error.Failure.Message}\n");
                break;
            case TurnEndPayload:
                Append("\n");
                break;
        }
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

    private static string Preview(string text, int limit)
    {
        var flat = text.Replace("\r\n", " ").Replace('\n', ' ').Trim();
        return flat.Length <= limit ? flat : $"{flat[..limit]}…";
    }

    private void Append(string text)
    {
        lock (_buffer)
            _buffer.Append(text);
    }
}
