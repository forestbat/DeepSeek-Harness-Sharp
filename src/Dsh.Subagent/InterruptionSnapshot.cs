using System.Text;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Subagent;

/** 从子会话事件推导中断快照：相位、已完成轮次、未完成轮次的部分输出、悬而未决的工具调用。 */
public static class InterruptionSnapshotFold
{
    public const int ArgsPreviewMaxChars = 200;

    public static SubagentInterruptionSnapshot? Fold(
        IReadOnlyList<SessionEvent> events, SessionId id, SubagentStopReason stopReason)
    {
        if (stopReason == SubagentStopReason.Completed)
            return null;
        var lastTurn = -1;
        var completedTurns = 0;
        foreach (var sessionEvent in events)
        {
            switch (sessionEvent.Data)
            {
                case TurnStartPayload turnStart:
                    lastTurn = turnStart.Turn;
                    break;
                case TurnEndPayload { Reason: TurnEndReason.Completed }:
                    completedTurns++;
                    break;
            }
        }
        if (lastTurn < 0)
            return new SubagentInterruptionSnapshot(
                SubagentInterruptionPhase.TurnBoundary, 0, "", null, true, null);

        AssistantMessage? interruptedMessage = null;
        var trailingText = new StringBuilder();
        var calls = new List<ToolCallPayload>();
        var results = new Dictionary<ToolCallId, ToolResultPayload>();
        foreach (var sessionEvent in events)
        {
            switch (sessionEvent.Data)
            {
                case ToolCallPayload call when call.Turn == lastTurn:
                    calls.Add(call);
                    break;
                case ToolResultPayload result when result.Turn == lastTurn:
                    results[result.Message.Block.ToolCallId] = result;
                    break;
                case AssistantMessagePayload message when message.Turn == lastTurn:
                    interruptedMessage = message.Interrupted ? message.Message : null;
                    trailingText.Clear();
                    break;
                case AssistantChunkPayload { Chunk: StreamChunk.TextDelta delta } chunk when chunk.Turn == lastTurn:
                    trailingText.Append(delta.Text);
                    break;
            }
        }

        var pending = calls.LastOrDefault(call => !results.ContainsKey(call.CallId));
        // 工具体被中途取消时会记录 ABORTED 合成结果：调用已派发、副作用未知，同样视为工具相位中断。
        // ABORTED_BEFORE_DISPATCH 表示从未派发（无副作用），不构成工具相位中断。
        var abortedInFlight = pending is null
            ? calls.LastOrDefault(call =>
                results.TryGetValue(call.CallId, out var result) && result.Error?.Code == ToolErrorCodes.Aborted)
            : null;
        var interruptedCall = pending ?? abortedInFlight;
        if (interruptedCall is not null)
        {
            return new SubagentInterruptionSnapshot(
                SubagentInterruptionPhase.ToolExecution,
                completedTurns,
                "",
                new SubagentPendingToolCall(interruptedCall.Name, interruptedCall.CallId.Value, Preview(interruptedCall.Arguments)),
                false,
                Checkpoint(id, lastTurn));
        }
        if (interruptedMessage is not null || trailingText.Length > 0)
        {
            var partial = interruptedMessage is not null
                ? MessageText.Flatten(interruptedMessage.Content)
                : trailingText.ToString();
            return new SubagentInterruptionSnapshot(
                SubagentInterruptionPhase.LlmStream, completedTurns, partial, null, true, Checkpoint(id, lastTurn));
        }
        return new SubagentInterruptionSnapshot(
            SubagentInterruptionPhase.TurnBoundary, completedTurns, "", null, true, Checkpoint(id, lastTurn));
    }

    private static string Preview(string arguments)
        => arguments.Length <= ArgsPreviewMaxChars ? arguments : $"{arguments[..ArgsPreviewMaxChars]}…";

    private static string Checkpoint(SessionId id, int turn) => $"{id.Value}@{turn}";
}
