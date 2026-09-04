using System.Text;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Tui;

public sealed class TranscriptRenderer
{
    public const int ToolArgumentsPreviewChars = 120;
    public const int ToolResultPreviewChars = 300;

    private readonly StringBuilder _buffer = new();
    private bool _reasoningOpen;
    private bool _assistantOpen;

    public string Text
    {
        get
        {
            lock (_buffer)
                return _buffer.ToString();
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
                Append($"  {label}{Preview(text, ToolResultPreviewChars)}\n");
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
                Append(text.Text);
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
        Append("\n[thinking] ");
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
        Append("\n");
        _reasoningOpen = false;
    }

    private void CloseAssistant()
    {
        if (!_assistantOpen)
            return;
        Append("\n");
        _assistantOpen = false;
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
