using Dsh.Llm;

namespace Dsh.Core;

public static class SessionRepair
{
    public const string ToolNotStarted = "TOOL_NOT_STARTED";
    public const string ToolOutcomeUnknown = "TOOL_OUTCOME_UNKNOWN";

    private const string OutcomeUnknownText =
        "The tool call was interrupted after it was recorded, but no result was durably recorded. Its outcome is unknown. Decide whether to retry from the tool semantics: retry only if the operation is read-only or idempotent; if it may have side effects, first verify external state or ask the user. Do not retry blindly.";

    private const string NotStartedText =
        "The tool call was interrupted before the Harness recorded it as started. Retry it if it is still needed.";

    public static IReadOnlyList<SessionEvent> InterruptedTurnClosers(IReadOnlyList<SessionEvent> events)
    {
        int? openTurn = null;
        int? openStep = null;
        var pendingCalls = new Dictionary<ToolCallId, (int Step, long? CallSeq)>();
        foreach (var sessionEvent in events)
        {
            switch (sessionEvent.Data)
            {
                case TurnStartPayload turnStart:
                    openTurn = turnStart.Turn;
                    openStep = null;
                    pendingCalls.Clear();
                    break;
                case TurnEndPayload:
                    openTurn = null;
                    openStep = null;
                    pendingCalls.Clear();
                    break;
                case StepStartPayload stepStart:
                    openStep = stepStart.Step;
                    break;
                case StepEndPayload:
                    pendingCalls.Clear();
                    openStep = null;
                    break;
                case AssistantMessagePayload assistantMessage:
                    foreach (var block in assistantMessage.Message.Content.OfType<ToolCallBlock>())
                        pendingCalls[block.Id] = (assistantMessage.Step, null);
                    break;
                case ToolCallPayload toolCall:
                    if (pendingCalls.TryGetValue(toolCall.CallId, out var entry))
                        pendingCalls[toolCall.CallId] = entry with { CallSeq = sessionEvent.Seq };
                    break;
                case ToolResultPayload toolResult:
                    pendingCalls.Remove(toolResult.Message.ToolSource.CallId);
                    break;
            }
        }
        var last = events.Count > 0 ? events[^1] : null;
        if (openTurn is null || last is null)
            return [];
        var seq = last.Seq + 1;
        var time = last.Time;
        var closers = new List<SessionEvent>();
        foreach (var (callId, (step, callSeq)) in pendingCalls)
        {
            var started = callSeq is not null;
            var block = new ToolResultBlock(callId, [new TextBlock(started ? OutcomeUnknownText : NotStartedText)], true);
            var message = new ToolResultMessage
            {
                Id = MessageId.Create($"interrupted-tool-result-{callId}-{seq}"),
                Content = [block],
                ToolSource = new ToolMessageSource(callId),
            };
            closers.Add(new SessionEvent
            {
                Type = SessionEventTypes.ToolResult,
                Seq = seq++,
                Time = time,
                Data = new ToolResultPayload(
                    openTurn.Value,
                    step,
                    message,
                    new ToolResultErrorInfo(started ? "ToolOutcomeUnknownError" : "ToolNotStartedError", started ? ToolOutcomeUnknown : ToolNotStarted)),
                SurfaceOp = new SurfaceOp.Append(),
                SourceEventSeqs = started ? [callSeq!.Value] : null,
            });
        }
        if (openStep is not null)
        {
            closers.Add(new SessionEvent
            {
                Type = SessionEventTypes.StepEnd,
                Seq = seq++,
                Time = time,
                Data = new StepEndPayload(openTurn.Value, openStep.Value),
            });
        }
        closers.Add(new SessionEvent
        {
            Type = SessionEventTypes.TurnEnd,
            Seq = seq++,
            Time = time,
            Data = new TurnEndPayload(openTurn.Value, new TurnEndReason.Interrupted()),
        });
        return closers;
    }
}
