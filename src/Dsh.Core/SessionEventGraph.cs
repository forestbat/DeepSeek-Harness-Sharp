using Dsh.Llm;

namespace Dsh.Core;

/** 事件在模型上下文里的状态: 仍在 surface 上、被某次 replace 遮蔽、或本就不是 surface 事件。 */
public enum SessionEventSurface
{
    Current,
    Shadowed,
    LogOnly,
}

/** 事件图节点: 日志里的一条事件, 附带 surface 状态与是否属于 fork 继承区。 */
public readonly record struct SessionEventNode(SessionEvent Event, SessionEventSurface Surface, bool Inherited)
{
    public long Seq => Event.Seq;
    public string Type => Event.Type;
    public long Time => Event.Time;
}

public enum SessionEventEdgeKind
{
    /** 替换事件 → 被它遮蔽的 surface 事件。 */
    Replaces,
    /** 事件 → 它在 sourceEventSeqs 里引用、但没有遮蔽的事件。 */
    Cites,
    /** 工具结果 → 同一 callId 的工具调用。 */
    ResultOf,
    /** 审批询问 → 它拦下的工具调用。 */
    Approves,
    /** 收尾事件 → 同一配对 id 的开始事件(命令、审批决定、压缩、工作流、程序内派发等)。 */
    Completes,
}

/** 同一会话内的一条派生边, 方向为 From → To。 */
public readonly record struct SessionEventEdge(SessionEventEdgeKind Kind, long From, long To);

public enum SessionLineageKind
{
    /** 复制父会话前缀得到的会话(/timestamp fork、/checkpoints restore)。 */
    Fork,
    /** 子代理会话(header 的 origin 为 subagent)。 */
    Subagent,
}

/**
 * 会话血缘: Session 由 Parent 派生。
 * ParentSeq 是父会话里的派生点: fork 为最后一条继承事件, 子代理为日志里能定位到的派生事件, 定位不到时为 null。
 */
public readonly record struct SessionLineage(SessionId Session, SessionLineageKind Kind, SessionId Parent, long? ParentSeq);
