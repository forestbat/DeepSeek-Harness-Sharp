using System.Collections.Concurrent;
using Dsh.Llm;

namespace Dsh.Core;

/** 同一日志里事件之间共享的配对 id(如工具调用 id、压缩 id), Scope 区分发放 id 的事件族。 */
public readonly record struct SessionEventCorrelation(string Scope, string Id);

/** 事件类型声明的一条关系, 事件图据此派生边。 */
public abstract record SessionEventRelation
{
    /** 事件开启配对 id: 之后引用同一 id 的事件指回它。 */
    public sealed record Opens(SessionEventCorrelation Key) : SessionEventRelation;

    /** 事件指向开启该配对 id 的事件, 边的种类为 Kind。 */
    public sealed record RefersTo(SessionEventCorrelation Key, SessionEventEdgeKind Kind) : SessionEventRelation;

    /** 事件启动了子会话 Child。 */
    public sealed record Spawns(SessionId Child) : SessionEventRelation;
}

/**
 * 事件类型 → 关系声明的登记表: 各插件在注册事件 codec 的同一处登记自己事件的关系。
 * 事件图只读这张表, 不认识任何具体插件的载荷类型, 第三方插件的事件同样可以登记。
 */
public static class SessionEventRelations
{
    private static readonly ConcurrentDictionary<string, Func<SessionEventPayload, IReadOnlyList<SessionEventRelation>>> Declarations = new(StringComparer.Ordinal);

    static SessionEventRelations()
    {
        Register<ToolCallPayload>(SessionEventTypes.ToolCall, payload => [new SessionEventRelation.Opens(ToolCall(payload.CallId))]);
        Register<ToolResultPayload>(
            SessionEventTypes.ToolResult,
            payload => [new SessionEventRelation.RefersTo(ToolCall(payload.Message.ToolSource.CallId), SessionEventEdgeKind.ResultOf)]);
        Register<SettingsMutationAuditPayload>(SettingsMutationAuditPayload.EventType,
            payload => [new SessionEventRelation.RefersTo(ToolCall(payload.CallId), SessionEventEdgeKind.ResultOf)]);
    }

    /** 工具调用的配对 id: 工具结果、审批询问等按它指回 tool/call。 */
    public static SessionEventCorrelation ToolCall(ToolCallId callId) => new(SessionEventTypes.ToolCall, callId.Value);

    public static void Register<T>(string type, Func<T, IReadOnlyList<SessionEventRelation>> relations) where T : SessionEventPayload
        => Declarations[type] = payload => payload is T typed ? relations(typed) : [];

    /** 载荷声明的关系; 未登记的类型(包括未知事件)没有关系。 */
    public static IReadOnlyList<SessionEventRelation> Of(SessionEventPayload payload)
        => Declarations.TryGetValue(payload.Type, out var relations) ? relations(payload) : [];
}
