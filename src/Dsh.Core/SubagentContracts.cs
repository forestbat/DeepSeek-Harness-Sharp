using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Llm;

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
    public required CancellationToken Signal { get; init; }
    public AgentOptions? AgentOptions { get; init; }
    public JsonObject? OutputSchema { get; init; }
    public int? MaxDepth { get; init; }
    public ToolRestriction? ToolFilter { get; init; }
    public string? Persona { get; init; }
}

/** 子代理 provider 的能力视图:供消费方在不接触 provider SPI 的情况下探测能力。 */
public sealed record SubagentProviderInfo(SubagentCapabilities Capabilities, bool InheritsParentContext);

/** 子代理启动服务的消费侧契约:实现由 subagent 插件的 SubagentRuntime 提供,工作流等模块只面向本接口。 */
public interface ISubagentService
{
    public const string ServiceName = "subagents";

    Task<ISubagentRun> StartAsync(string name, SubagentStartRequest request);

    SubagentProviderInfo? GetProviderInfo(string name);
}
