using System.Text;
using System.Text.Json;
using Dsh.Acp;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;

namespace Dsh.Host;

/**
 * The conversation of one ACP session as harness session events, so clients render it like a harness session (host.session.subscribe).
 * Mapping: the prompt becomes turn/start + user/message; message and thought chunks become assistant/chunk deltas and the text is closed
 * into one assistant/message before each tool call and at the end of the turn; tool_call becomes tool/call, a completed or failed
 * tool_call_update becomes tool/result; plan entries become a system/message; the stop reason becomes turn/end.
 * Storage, sequence numbers, history and subscriptions belong to the same Session used by harness conversations.
 */
internal sealed class AcpTranscript : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly SessionStore _store;
    public Session Session { get; }
    private readonly StringBuilder _text = new();
    private readonly string _agentKind;
    private int _turn;
    private int _step;

    private AcpTranscript(Session session, string agentKind, SessionStore store)
    {
        Session = session;
        _agentKind = agentKind;
        _store = store;
        _turn = session.SnapshotEvents().Count(sessionEvent => sessionEvent.Data is TurnStartPayload);
    }

    /** Only running ACP processes own a writable Session; stopped histories are read directly from persistence. */
    public static async Task<AcpTranscript> OpenAsync(Context ctx, RemoteHost.RemoteSessionSummary record)
    {
        var store = ctx.Get<SessionStore>(SessionStore.ServiceName) ?? throw new InvalidOperationException("session store is not registered");
        var persistence = ctx.Get<ISessionPersistence>(ISessionPersistence.ServiceName)
            ?? throw new InvalidOperationException("ACP conversations require session persistence");
        var id = SessionId.Create(record.SessionId);
        Session session;
        if (persistence.Stat(id) is not null)
        {
            using var handle = persistence.Open(id, SessionAccess.Read);
            session = store.Create(id, handle.Read(), handle.Header, handle.InheritedEventCount);
        }
        else
            session = store.Create(id, header: new SessionHeader
            {
                Version = SessionHeader.SessionFormatVersion, Id = id, CreatedAt = record.CreatedAt,
                Cwd = record.ProjectPath, Title = record.Title, IsSeeded = false,
            });
        try
        {
            await store.Flush(session).ConfigureAwait(false);
            return new AcpTranscript(session, record.AgentKind!, store);
        }
        catch
        {
            store.Release(id);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _store.Flush(Session).ConfigureAwait(false);
        }
        finally
        {
            _store.Release(Session.Id);
        }
    }

    public void BeginTurn(string text)
    {
        lock (_gate)
        {
            _step = 0;
            Append(new TurnStartPayload(++_turn));
            Append(new UserMessagePayload(MessageFactory.CreateUserText(text)), new SurfaceOp.Append());
        }
    }

    public void Apply(AcpSessionUpdate update)
    {
        lock (_gate)
        {
            switch (update.Kind)
            {
                case AcpMethods.UpdateAgentMessageChunk when update.Text is { Length: > 0 } text:
                    _text.Append(text);
                    Append(new AssistantChunkPayload(_turn, _step, new StreamChunk.TextDelta(0, text)));
                    break;
                case AcpMethods.UpdateAgentThoughtChunk when update.Text is { Length: > 0 } thought:
                    Append(new AssistantChunkPayload(_turn, _step, new StreamChunk.ReasoningDelta(0, thought)));
                    break;
                case AcpMethods.UpdateToolCall:
                    CloseText();
                    _step++;
                    Append(new ToolCallPayload(_turn, _step, ToolCallId.Create(update.ToolCallId ?? ""), ToolName(update), RawInput(update.Update)));
                    break;
                case AcpMethods.UpdateToolCallResult when update.Status is "completed" or "failed":
                    AppendToolResult(update);
                    break;
                case AcpMethods.UpdatePlan:
                    Append(new SystemMessagePayload(_turn, _step, MessageFactory.CreateSystemMessage([new TextBlock(PlanText(update.Update))], new PluginMessageSource(SessionEventSources.AcpPlan))), new SurfaceOp.Append());
                    break;
            }
        }
    }

    /** Maps the ACP stop reason to the harness turn end reasons. */
    public void EndTurn(string stopReason)
    {
        TurnEndReason reason = stopReason switch
        {
            AcpMethods.StopEndTurn => new TurnEndReason.Completed(),
            AcpMethods.StopCancelled => new TurnEndReason.Interrupted(),
            AcpMethods.StopMaxTokens => new TurnEndReason.MaxTokens(),
            AcpMethods.StopRefusal => new TurnEndReason.Blocked(),
            _ => new TurnEndReason.Error(new LlmFailure($"the agent stopped: {stopReason}", stopReason)),
        };
        Finish(reason);
    }

    public void FailTurn(string message, string code) => Finish(new TurnEndReason.Error(new LlmFailure(message, code)));


    private void Finish(TurnEndReason reason)
    {
        lock (_gate)
        {
            CloseText();
            Append(new TurnEndPayload(_turn, reason));
        }
    }

    private void AppendToolResult(AcpSessionUpdate update)
    {
        var failed = update.Status == "failed";
        var content = ToolContentText(update.Update);
        var message = MessageFactory.CreateToolResultMessage(ToolCallId.Create(update.ToolCallId ?? ""), [new TextBlock(content)], failed);
        Append(new ToolResultPayload(_turn, _step, message, failed ? new ToolResultErrorInfo(ToolName(update), update.Status!) : null), new SurfaceOp.Append());
    }

    /** Called under the lock: the streamed text so far becomes one assistant message. */
    private void CloseText()
    {
        if (_text.Length == 0)
            return;
        var message = MessageFactory.CreateAssistantMessage([new TextBlock(_text.ToString())], _agentKind, _agentKind);
        _text.Clear();
        Append(new AssistantMessagePayload(_turn, _step, message), new SurfaceOp.Append());
    }

    private void Append(SessionEventPayload payload, SurfaceOp? surfaceOp = null)
        => Session.Append(payload, surfaceOp);

    private static string ToolName(AcpSessionUpdate update) => update.Title ?? StringOf(update.Update, "kind") ?? "tool";

    private static string? StringOf(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string RawInput(JsonElement update)
        => update.TryGetProperty("rawInput", out var input) ? input.GetRawText() : "{}";

    /** Text blocks of the tool call content (`[{type:"content", content:{type:"text", text}}]`), else the raw output. */
    private static string ToolContentText(JsonElement update)
    {
        var texts = update.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
            ? content.EnumerateArray()
                .Select(item => item.TryGetProperty("content", out var inner) ? inner : item)
                .Select(inner => StringOf(inner, "text"))
                .OfType<string>()
                .ToList()
            : [];
        if (texts.Count > 0)
            return string.Join('\n', texts);
        return update.TryGetProperty("rawOutput", out var output) ? output.GetRawText() : "";
    }

    /** One line per plan entry: `[status] content`. */
    private static string PlanText(JsonElement update)
        => update.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array
            ? string.Join('\n', entries.EnumerateArray().Select(entry => $"[{StringOf(entry, "status")}] {StringOf(entry, "content")}"))
            : "";

}

