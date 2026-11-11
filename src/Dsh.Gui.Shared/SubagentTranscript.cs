using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;

namespace Dsh.Gui.ViewModels;

/**
 * 子会话事件到消息的最小渲染器: 复用主会话的可见语义(用户/正文/思考/工具/结果/上下文注入),
 * 不涉及轨迹与统计, 也不写入会话; 主会话渲染行为不受影响。
 * 每条消息都带上产生它的事件序号与时间, 手机端的时刻显示与时间线定位因此不需要另存事件列表。
 */
public sealed class SubagentTranscript
{
    /** Source plugin name of the system messages that carry an ACP agent's plan (see the host's ACP transcript). */
    private const string AcpPlanSource = "acp-plan";

    private MessageViewModel? _openAssistant;
    private MessageViewModel? _openReasoning;
    private string _model = "助手";

    /** 从窗口起点重新渲染时, 起点之前已经切过的模型名要先继承下来, 否则助手气泡会退回泛称。 */
    public SubagentTranscript(string? model = null)
    {
        if (model is { Length: > 0 })
            _model = model;
    }

    /**
     * 是否把增量合并交给计时器。
     * 历史重放不需要: 那批增量会在同一帧里全部到达, 每条排一个计时器只是把上百个回调堆到 UI 线程上,
     * 反正最后由 Flush() 一次性合并文本。
     */
    public bool Coalesce { get; set; } = true;

    /** 事件序列里截止 limit 之前最后一次切换到的模型; 用于从中间某处开始渲染时还原气泡标签。 */
    public static string? ModelBefore(IReadOnlyList<SessionEvent> events, int limit)
    {
        for (var index = Math.Min(limit, events.Count) - 1; index >= 0; index--)
        {
            if (events[index].Data is RequestHeaderPayload { Header.Config.Model: { Length: > 0 } model })
                return model;
        }
        return null;
    }

    /** 消息被窗口裁掉时同步丢弃它的流式引用, 免得增量文本继续堆进一条不在列表里的消息。 */
    public void Forget(MessageViewModel message)
    {
        if (ReferenceEquals(_openAssistant, message))
            _openAssistant = null;
        if (ReferenceEquals(_openReasoning, message))
            _openReasoning = null;
    }

    public IReadOnlyList<MessageViewModel> Render(IReadOnlyList<SessionEvent> events)
    {
        var messages = new List<MessageViewModel>();
        foreach (var sessionEvent in events)
            Apply(sessionEvent, messages);
        Flush();
        return messages;
    }

    /** 子代理卡片的内联摘要: 只取子会话的工具调用/结果流。 */
    public static IReadOnlyList<MessageViewModel> Summarize(IReadOnlyList<SessionEvent> events)
        => [.. new SubagentTranscript().Render(events)
            .Where(message => message.Kind is MessageKind.Tool or MessageKind.Result)];

    public void Apply(SessionEvent sessionEvent, IList<MessageViewModel> messages)
    {
        switch (sessionEvent.Data)
        {
            case TurnEndPayload turn:
                _openAssistant = null;
                _openReasoning = null;
                if (turn.Reason is TurnEndReason.Error error)
                    Add(New(sessionEvent, "错误", $"{error.Failure.Code}: {error.Failure.Message}", MessageKind.System), messages);
                break;
            case UserMessagePayload user:
                ApplyUserMessage(sessionEvent, user, messages);
                break;
            case SystemMessagePayload system:
                _openAssistant = null;
                _openReasoning = null;
                Add(New(sessionEvent,
                    SystemLabel(system.Message.Source),
                    UserFacingText.StripSystemReminders(SessionText.ContentText(system.Message.Content)),
                    MessageKind.System), messages);
                break;
            case RequestHeaderPayload header when header.Header.Config.Model is { Length: > 0 } model:
                _model = model;
                break;
            case AssistantChunkPayload chunk:
                ApplyChunk(sessionEvent, chunk.Chunk, messages);
                break;
            case AssistantMessagePayload assistant:
                ApplyAssistant(sessionEvent, SessionText.AssistantText(assistant.Message.Content), messages);
                break;
            case ToolCallPayload call:
                _openAssistant = null;
                _openReasoning = null;
                var arguments = ToolPresentation.FormatArguments(call.Arguments);
                var callMessage = Add(New(sessionEvent, "工具", call.Name, MessageKind.Tool, toolName: call.Name, argumentText: arguments), messages);
                callMessage.Detail = arguments;
                break;
            case ToolResultPayload result:
                var resultText = ToolPresentation.FormatResult(SessionText.ContentText(result.Message.Content));
                var resultMessage = Add(
                    New(sessionEvent, result.Error is null ? "结果" : $"错误 {result.Error.Code}", resultText, MessageKind.Result),
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

    private static void ApplyUserMessage(SessionEvent sessionEvent, UserMessagePayload payload, IList<MessageViewModel> messages)
    {
        var text = UserFacingText.StripSystemReminders(SessionText.ContentText(payload.Message.Content));
        switch (payload.Message.Source)
        {
            case UserMessageSource:
                if (text.Length == 0)
                    break;
                Add(New(sessionEvent, "你", text, MessageKind.User), messages);
                break;
            case PluginMessageSource plugin:
                var injected = Add(New(sessionEvent, "", $"上下文注入 · {plugin.Plugin}", MessageKind.Context), messages);
                injected.Detail = plugin.Summary ?? text;
                break;
            default:
                Add(New(sessionEvent, payload.Message.Source.Kind, text, MessageKind.System), messages);
                break;
        }
    }

    /** ACP agents publish their plan as a system message from the "acp-plan" source; other plugin messages show the plugin's name. */
    private static string SystemLabel(MessageSource source) => source switch
    {
        PluginMessageSource { Plugin: AcpPlanSource } => "计划",
        PluginMessageSource plugin => plugin.Plugin,
        _ => "系统",
    };

    private void ApplyChunk(SessionEvent sessionEvent, StreamChunk chunk, IList<MessageViewModel> messages)
    {
        switch (chunk)
        {
            case StreamChunk.BlockStart { BlockType: "reasoning" }:
                _openReasoning = null;
                break;
            case StreamChunk.ReasoningDelta delta when delta.Text.Length > 0:
                _openReasoning ??= Add(New(sessionEvent, "思考", "", MessageKind.Reasoning, streaming: true), messages);
                _openReasoning.Append(delta.Text, Coalesce);
                break;
            case StreamChunk.BlockStart { BlockType: "text" }:
                _openAssistant = null;
                break;
            case StreamChunk.TextDelta delta when delta.Text.Length > 0:
                _openAssistant ??= Add(New(sessionEvent, _model, "", MessageKind.Assistant, streaming: true), messages);
                _openAssistant.Append(delta.Text, Coalesce);
                break;
        }
    }

    private void ApplyAssistant(SessionEvent sessionEvent, string text, IList<MessageViewModel> messages)
    {
        if (_openAssistant is not null)
        {
            _openAssistant.SetText(text);
            _openAssistant.IsStreaming = false;
            _openAssistant = null;
            return;
        }
        if (text.Length > 0)
            Add(New(sessionEvent, _model, text, MessageKind.Assistant), messages);
    }

    /** 一条由事件产生的消息: 序号与时间都取自该事件, 手机端据此显示时刻并做时间线定位。 */
    private static MessageViewModel New(
        SessionEvent sessionEvent,
        string role,
        string text,
        MessageKind kind,
        bool streaming = false,
        string toolName = "",
        string argumentText = "")
        => new(role, text, kind, streaming)
        {
            Seq = sessionEvent.Seq,
            Time = sessionEvent.Time,
            ToolName = toolName,
            ArgumentText = argumentText,
        };

    private static MessageViewModel Add(MessageViewModel message, IList<MessageViewModel> messages)
    {
        messages.Add(message);
        return message;
    }
}