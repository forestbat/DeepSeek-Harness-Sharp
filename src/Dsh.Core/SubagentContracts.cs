using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Llm;
using Dsh.Runtime.Events;

namespace Dsh.Core;

public enum SubagentStopReason
{
    Completed,
    Aborted,
    Error,
    MaxTokens,
    Refusal,
}

public static class SubagentStopReasonWire
{
    public static string Of(SubagentStopReason reason) => reason switch
    {
        SubagentStopReason.Completed => "completed",
        SubagentStopReason.Aborted => "aborted",
        SubagentStopReason.Error => "error",
        SubagentStopReason.MaxTokens => "max-tokens",
        SubagentStopReason.Refusal => "refusal",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
    };
}

public sealed record SubagentCapabilities(
    bool AgentOptions = false,
    bool OutputSchema = false,
    bool DepthLimit = false,
    bool ToolFilter = false,
    bool Persona = false);

/** 中断发生的相位：turn 边界 / LLM 流式输出中途 / 工具执行中途。 */
public enum SubagentInterruptionPhase
{
    TurnBoundary,
    LlmStream,
    ToolExecution,
}

/** 中断时尚未拿到结果的工具调用；其副作用是否发生不可知。 */
public sealed record SubagentPendingToolCall(string ToolName, string CallId, string ArgsPreview)
{
    public const string UnknownOutcome = "unknown";

    public string Outcome { get; init; } = UnknownOutcome;
}

/**
 * 非 Completed 终态的自包含快照：不依赖子会话日志即可向主会话说明断在哪里、能否续跑。
 * Resumable 表达数据完好性（断在工具执行中途时副作用未知，不可续跑）；续跑通道由 provider 能力另行决定。
 */
public sealed record SubagentInterruptionSnapshot(
    SubagentInterruptionPhase Phase,
    int CompletedTurns,
    string PartialOutput,
    SubagentPendingToolCall? PendingToolCall,
    bool Resumable,
    string? CheckpointToken);

public sealed record SubagentResult
{
    public required IReadOnlyList<ContentBlock> Output { get; init; }
    public required SubagentStopReason StopReason { get; init; }
    public JsonElement? Structured { get; init; }
    public string? Diagnostic { get; init; }
    public SubagentInterruptionSnapshot? Interruption { get; init; }
}

public interface ISubagentRun
{
    SessionId Id { get; }
    IAgent? LocalAgent { get; }
    Task<SubagentResult> Result { get; }
    Task DisposeAsync();
}

public sealed record SubagentStartRequest
{
    public string? Label { get; init; }
    public required IReadOnlyList<ContentBlock> Prompt { get; init; }
    public required IAgent Parent { get; init; }
    /** 发起本次委派的父会话工具调用；写入子会话 header 的 parentCallId。 */
    public ToolCallId? ParentCallId { get; init; }
    public required CancellationToken Signal { get; init; }
    public AgentOptions? AgentOptions { get; init; }
    public JsonObject? OutputSchema { get; init; }
    public int? MaxDepth { get; init; }
    public ToolRestriction? ToolFilter { get; init; }
    public string? Persona { get; init; }

    /**
     * 后台运行: 子代理不再随父回合的取消信号一起终止, 完成后由调用方唤醒父会话。
     * 父会话按停只应结束父自己的回合, 子代理继续跑完 —— 否则用户一按停, 跑了一半的子代理成果就全丢了。
     */
    public bool Background { get; init; }
}

/** 子代理 provider 的能力视图:供消费方在不接触 provider SPI 的情况下探测能力。 */
public sealed record SubagentProviderInfo(SubagentCapabilities Capabilities, bool InheritsParentContext);

/** 子代理服务的消费侧契约:实现由 subagent 插件的 SubagentRuntime 提供,工作流与 TUI/GUI 等模块只面向本接口。 */
public interface ISubagentService
{
    public const string ServiceName = "subagents";

    /** 子代理委派工具的缺省名:界面据此把工具调用渲染成子代理卡片。 */
    public const string DefaultToolName = "subagent";

    Task<ISubagentRun> StartAsync(string name, SubagentStartRequest request);

    SubagentProviderInfo? GetProviderInfo(string name);

    /** 存活子代理的本地 agent;已结束或非本地运行返回 null。 */
    IAgent? GetLive(SessionId id);

    Task<IReadOnlyList<SubagentListEntry>> ListChildrenAsync(SessionId parentSessionId, CancellationToken signal = default);

    Task<IReadOnlyList<SubagentListEntry>> ListDescendantsAsync(
        SessionId parentSessionId, int depth = int.MaxValue, CancellationToken signal = default);
}

public abstract record SubagentListEntry
{
    public required SessionId Id { get; init; }
    public SessionId? Parent { get; init; }
    public int? Depth { get; init; }

    public sealed record Child : SubagentListEntry
    {
        public required string Mode { get; init; }
        public string? Label { get; init; }
        public bool HasChildren { get; init; }
    }

    public sealed record Diagnostic : SubagentListEntry
    {
        public required string Reason { get; init; }
    }
}

public sealed record SubagentRunInfo(string RunId, string Provider, SessionId Id, bool Local);

public sealed record SubagentRunEndInfo(
    string RunId,
    string Provider,
    SessionId Id,
    bool Local,
    SubagentStopReason StopReason,
    IReadOnlyList<ContentBlock>? LastAssistantMessage = null,
    SubagentInterruptionSnapshot? Interruption = null);

public sealed record SubagentStartNotification(SubagentRunInfo Info) : INotification
{
    public static string EventName => "subagent/start";
}

public sealed record SubagentEndNotification(SubagentRunEndInfo Info) : INotification
{
    public static string EventName => "subagent/end";
}

/** 子会话身份事件:由 subagent 插件写入并注册编解码,界面只读地据此识别子代理会话。 */
public sealed record SubagentDescriptorPayload(int Version, string Mode, string Provider, string? Label = null)
    : SessionEventPayload
{
    public const string EventType = "subagent/descriptor";
    public const int CurrentVersion = 3;
    public const string OneShotMode = "one-shot";
    public const string ContinuableMode = "continuable";

    public override string Type => EventType;

    public static SubagentDescriptorPayload OneShot(string provider, string? label)
        => new(CurrentVersion, OneShotMode, provider, label);

    // 提供者每次激活只向子会话 own 后缀追加一个 descriptor；own 后缀里首个即为身份（fork 种子里的祖先 descriptor 不参与）。
    public static SubagentDescriptorPayload? IdentityOf(Session session)
    {
        foreach (var sessionEvent in session.OwnEvents())
        {
            if (sessionEvent.Data is SubagentDescriptorPayload payload)
                return payload.Version == CurrentVersion ? payload : null;
        }
        return null;
    }
}
