using Dsh.Llm;

namespace Dsh.Core;

/**
 * 安全决策与设置变更的审计载荷: 它们作为会话事件写入日志, 属于会话内核的载荷定义,
 * 因此留在 Dsh.Core, 而不是随 safety 运行时搬到 Dsh.Safety。
 */
public sealed record SafetyDecisionAuditPayload(string? RuleId, string RuleKind, bool AutoApprove,
    string Decision, string? Tool, string? Dialect, IReadOnlyList<string> MatchedField, IReadOnlyList<string> MatchedPattern,
    string? Path, string? Intent, string? SessionId, string? CallId, long Timestamp,
    string Namespace = "safety", string? LifecycleOwner = null, string? Outcome = null) : SessionEventPayload
{
    public const string EventType = "safety/decision";
    public override string Type => EventType;
}

public sealed record SettingsMutationAuditPayload(
    string Id, ToolCallId CallId, ToolCallId RootCallId, string ToolName,
    string Path, string Diff, string BeforeHash, string AfterHash, string Outcome) : SessionEventPayload
{
    public const string EventType = "settings/mutation";
    public override string Type => EventType;
}
