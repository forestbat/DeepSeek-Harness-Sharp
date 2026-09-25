using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Gui.ViewModels;

/**
 * 子会话事件到消息的最小渲染器: 复用主会话的可见语义(用户/正文/思考/工具/结果/上下文注入),
 * 不涉及轨迹与统计, 也不写入会话; 主会话渲染行为不受影响。
 */
internal sealed class SubagentTranscript
{
    private MessageViewModel? _openAssistant;
    private MessageViewModel? _openReasoning;
    private string _model = "助手";

    public IReadOnlyList<MessageViewModel> Render(IReadOnlyList<SessionEvent> events)
    {
        var messages = new List<MessageViewModel>();
        foreach (var sessionEvent in events)
            Apply(sessionEvent, messages);
        Flush();
        return messages;
    }

    /** 子代理卡片的内联摘要: 只取子会话的工具调用/结果流。 */
    public static IReadOnlyList<MessageViewModel> Summarize(Session session)
        => [.. new SubagentTranscript().Render(session.SnapshotEvents())
            .Where(message => message.Kind is MessageKind.Tool or MessageKind.Result)];

    public void Apply(SessionEvent sessionEvent, IList<MessageViewModel> messages)
    {
        switch (sessionEvent.Data)
        {
            case TurnEndPayload turn:
                _openAssistant = null;
                _openReasoning = null;
                if (turn.Reason is TurnEndReason.Error error)
                    Add(new MessageViewModel("错误", $"{error.Failure.Code}: {error.Failure.Message}", MessageKind.System, false), messages);
                break;
            case UserMessagePayload user:
                ApplyUserMessage(user, messages);
                break;
            case RequestHeaderPayload header when header.Header.Config.Model is { Length: > 0 } model:
                _model = model;
                break;
            case AssistantChunkPayload chunk:
                ApplyChunk(chunk.Chunk, messages);
                break;
            case AssistantMessagePayload assistant:
                ApplyAssistant(SessionText.AssistantText(assistant.Message.Content), messages);
                break;
            case ToolCallPayload call:
                _openAssistant = null;
                _openReasoning = null;
                var callMessage = Add(new MessageViewModel("工具", $"{call.Name} {call.Arguments}", MessageKind.Tool, false), messages);
                callMessage.Detail = call.Arguments;
                break;
            case ToolResultPayload result:
                var resultText = SessionText.ContentText(result.Message.Content);
                var resultMessage = Add(
                    new MessageViewModel(result.Error is null ? "结果" : $"错误 {result.Error.Code}", resultText, MessageKind.Result, false),
                    messages);
                resultMessage.Detail = resultText;
                break;
        }
    }

    /** 合并刷新由流式增量挂起的文本, 让只读视图在不依赖计时器的情况下拿到最新内容。 */
    public void Flush()
    {
        _openAssistant?.FlushPending();
        _openReasoning?.FlushPending();
    }

    private void ApplyUserMessage(UserMessagePayload payload, IList<MessageViewModel> messages)
    {
        var text = SessionText.ContentText(payload.Message.Content);
        switch (payload.Message.Source)
        {
            case UserMessageSource:
                Add(new MessageViewModel("你", text, MessageKind.User, false), messages);
                break;
            case PluginMessageSource plugin:
                var injected = Add(new MessageViewModel("", $"上下文注入 · {plugin.Plugin}", MessageKind.Context, false), messages);
                injected.Detail = plugin.Summary ?? text;
                break;
            default:
                Add(new MessageViewModel(payload.Message.Source.Kind, text, MessageKind.System, false), messages);
                break;
        }
    }

    private void ApplyChunk(StreamChunk chunk, IList<MessageViewModel> messages)
    {
        switch (chunk)
        {
            case StreamChunk.BlockStart { BlockType: "reasoning" }:
                _openReasoning = null;
                break;
            case StreamChunk.ReasoningDelta delta when delta.Text.Length > 0:
                _openReasoning ??= Add(new MessageViewModel("思考", "", MessageKind.Reasoning, true), messages);
                _openReasoning.Append(delta.Text);
                break;
            case StreamChunk.BlockStart { BlockType: "text" }:
                _openAssistant = null;
                break;
            case StreamChunk.TextDelta delta when delta.Text.Length > 0:
                _openAssistant ??= Add(new MessageViewModel(_model, "", MessageKind.Assistant, true), messages);
                _openAssistant.Append(delta.Text);
                break;
        }
    }

    private void ApplyAssistant(string text, IList<MessageViewModel> messages)
    {
        if (_openAssistant is not null)
        {
            _openAssistant.SetText(text);
            _openAssistant.IsStreaming = false;
            _openAssistant = null;
            return;
        }
        if (text.Length > 0)
            Add(new MessageViewModel(_model, text, MessageKind.Assistant, false), messages);
    }

    private static MessageViewModel Add(MessageViewModel message, IList<MessageViewModel> messages)
    {
        messages.Add(message);
        return message;
    }
}
