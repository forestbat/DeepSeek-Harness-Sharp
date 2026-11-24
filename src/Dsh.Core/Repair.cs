using Dsh.Llm;

namespace Dsh.Core;

public static class SessionRepair
{
    public const string ToolNotStarted = "TOOL_NOT_STARTED";
    public const string ToolOutcomeUnknown = "TOOL_OUTCOME_UNKNOWN";

    public const string OutcomeUnknownText =
        "The tool call was interrupted after it was recorded, but no result was durably recorded. Its outcome is unknown. Decide whether to retry from the tool semantics: retry only if the operation is read-only or idempotent; if it may have side effects, first verify external state or ask the user. Do not retry blindly.";

    public const string NotStartedText =
        "The tool call was interrupted before the Harness recorded it as started. Retry it if it is still needed.";

    /**
     * 中断轮的收尾事件: 只统计**本会话自有**的事件(inheritedEventCount 之后)。
     * 种子(fork 复制的父会话前缀)是冻结的历史, 它的未闭合 turn / 悬空工具调用不该在这里补合成事件——
     * 那些合成文本("请判断是否重试")会让模型以为要继续, fork 必须保持"只复制、不触发推理"。
     *
     * 窗口化恢复时 `turn/start` 可能落在常驻尾窗之外(一个回合能超过两万条事件): 那**不代表**这个回合已闭合。
     * 所以判据只用窗口里能看到的事实——末尾不是 `turn/end`、且有悬空工具调用或在窗口里看到了未闭合的 `turn/start`;
     * 收尾事件里的 Turn/Step 取自各自事件自带字段, 不依赖 `turn/start` 是否在窗口内。
     */
    public static IReadOnlyList<SessionEvent> InterruptedTurnClosers(
        IReadOnlyList<SessionEvent> events,
        long inheritedEventCount = 0)
    {
        var scan = Scan(events, inheritedEventCount);
        if (scan.Last is not { } last || scan.Turn is not { } turn)
            return [];
        // 末尾就是 turn/end: 该回合已闭合。
        if (last.Data is TurnEndPayload)
            return [];
        // 窗口里既没有未闭合的 turn/start、也没有悬空的步或调用: 无从判断, 不凭空造事件。
        if (!scan.TurnOpen && scan.OpenStep is null && scan.Pending.Count == 0)
            return [];
        var closers = new List<SessionEvent>();
        var seq = last.Seq + 1;
        foreach (var (callId, call) in scan.Pending)
            closers.Add(ToolCloser(last, callId, call, seq++));
        if (scan is { OpenStep: { } openStep, OpenStepTurn: { } stepTurn })
            closers.Add(StepCloser(last, stepTurn, openStep, seq++));
        closers.Add(TurnCloser(last, turn, seq));
        return closers;
    }

    private static SessionEvent ToolCloser(SessionEvent last, ToolCallId callId, PendingCall call, long seq)
    {
        var started = call.CallSeq is not null;
        var block = new ToolResultBlock(callId, [new TextBlock(started ? OutcomeUnknownText : NotStartedText)], true);
        var message = new ToolResultMessage
        {
            Id = MessageId.Create($"interrupted-tool-result-{callId}-{seq}"),
            Content = [block],
            ToolSource = new ToolMessageSource(callId),
        };
        return new SessionEvent
        {
            Type = SessionEventTypes.ToolResult,
            Seq = seq,
            Time = last.Time,
            Data = new ToolResultPayload(
                call.Turn,
                call.Step,
                message,
                new ToolResultErrorInfo(started ? "ToolOutcomeUnknownError" : "ToolNotStartedError", started ? ToolOutcomeUnknown : ToolNotStarted)),
            SurfaceOp = new SurfaceOp.Append(),
            SourceEventSeqs = call.CallSeq is { } callSeq ? [callSeq] : null,
        };
    }

    private static SessionEvent StepCloser(SessionEvent last, int turn, int step, long seq) => new()
    {
        Type = SessionEventTypes.StepEnd,
        Seq = seq,
        Time = last.Time,
        Data = new StepEndPayload(turn, step),
    };

    private static SessionEvent TurnCloser(SessionEvent last, int turn, long seq) => new()
    {
        Type = SessionEventTypes.TurnEnd,
        Seq = seq,
        Time = last.Time,
        Data = new TurnEndPayload(turn, new TurnEndReason.Interrupted()),
    };

    /**
     * 扫一遍窗口, 只记"能据此判断未闭合"的东西: 见过的最大回合/步号、窗口内是否还有未闭合的 turn、悬空调用。
     * 回合/步边界会清空悬空调用(与旧行为一致): 边界之后还欠着的调用不算这一轮的中断。
     */
    private static ScanResult Scan(IReadOnlyList<SessionEvent> events, long inheritedEventCount)
    {
        int? turn = null;
        var turnOpen = false;
        int? openStep = null;
        int? openStepTurn = null;
        var pending = new Dictionary<ToolCallId, PendingCall>();
        SessionEvent? last = null;
        foreach (var sessionEvent in events.Skip((int)Math.Clamp(inheritedEventCount, 0, events.Count)))
        {
            last = sessionEvent;
            switch (sessionEvent.Data)
            {
                case TurnStartPayload turnStart:
                    turn = Pick(turn, turnStart.Turn);
                    turnOpen = true;
                    openStep = null;
                    pending.Clear();
                    break;
                case TurnEndPayload turnEnd:
                    turn = Pick(turn, turnEnd.Turn);
                    turnOpen = false;
                    openStep = null;
                    pending.Clear();
                    break;
                case StepStartPayload stepStart:
                    turn = Pick(turn, stepStart.Turn);
                    openStep = stepStart.Step;
                    openStepTurn = stepStart.Turn;
                    break;
                case StepEndPayload:
                    openStep = null;
                    pending.Clear();
                    break;
                case AssistantMessagePayload assistantMessage:
                    turn = Pick(turn, assistantMessage.Turn);
                    foreach (var block in assistantMessage.Message.Content.OfType<ToolCallBlock>())
                        pending[block.Id] = new PendingCall(assistantMessage.Turn, assistantMessage.Step, null);
                    break;
                case ToolCallPayload toolCall:
                    turn = Pick(turn, toolCall.Turn);
                    if (pending.TryGetValue(toolCall.CallId, out var entry))
                        pending[toolCall.CallId] = entry with { CallSeq = sessionEvent.Seq };
                    break;
                case ToolResultPayload toolResult:
                    turn = Pick(turn, toolResult.Turn);
                    pending.Remove(toolResult.Message.ToolSource.CallId);
                    break;
            }
        }
        return new ScanResult(turn, turnOpen, openStep, openStepTurn, pending, last);
    }

    private static int Pick(int? current, int value) => current is { } existing ? Math.Max(existing, value) : value;

    /**
     * 恢复出来的会话从哪个回合接着数: 只看常驻尾窗, 否则窗口化打开等于把整份日志按页读回来。
     *
     * 取"窗口里最后一个带回合号的事件"而不是"最后一个 turn/start": 一个回合可以超过两万条事件,
     * 那样 turn/start 会落在窗口之外, 只认 turn/start 的写法会把回合号误判成 0(下一回合与历史撞号)。
     */
    public static int LastTurnOf(Session session)
    {
        var last = session.LastResidentEventWhere(sessionEvent => sessionEvent.Data switch
        {
            TurnStartPayload => true,
            StepStartPayload => true,
            AssistantMessagePayload => true,
            ToolCallPayload => true,
            ToolResultPayload => true,
            _ => false,
        });
        return last?.Data switch
        {
            TurnStartPayload turnStart => turnStart.Turn,
            StepStartPayload stepStart => stepStart.Turn,
            AssistantMessagePayload assistantMessage => assistantMessage.Turn,
            ToolCallPayload toolCall => toolCall.Turn,
            ToolResultPayload toolResult => toolResult.Turn,
            _ => 0,
        };
    }

    private readonly record struct PendingCall(int Turn, int Step, long? CallSeq);

    private readonly record struct ScanResult(
        int? Turn,
        bool TurnOpen,
        int? OpenStep,
        int? OpenStepTurn,
        Dictionary<ToolCallId, PendingCall> Pending,
        SessionEvent? Last);
}
