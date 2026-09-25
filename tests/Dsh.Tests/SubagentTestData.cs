using Dsh.Core;
using Dsh.Llm;
using Dsh.Subagent;

namespace Dsh.Tests;

/** GUI 子代理测试的输入装配: 用真实 SessionStore 直接把一个带 descriptor 的子会话放进存储, 不重写运行时逻辑。 */
internal static class SubagentTestData
{
    public static Session AddChild(
        SessionStore store,
        SessionId parent,
        string label,
        long createdAt,
        int depth = 1,
        string mode = SubagentDescriptorPayload.OneShotMode,
        bool withToolCall = false)
    {
        var id = SessionId.Create($"session-sub-{Guid.NewGuid():N}");
        var header = new SessionHeader
        {
            Version = SessionHeader.SessionFormatVersion,
            Id = id,
            CreatedAt = createdAt,
            ParentSession = parent,
            IsSeeded = false,
            Origin = "subagent",
            DelegationDepth = depth,
        };
        var session = store.Create(id, header: header);
        session.Append(new SubagentDescriptorPayload(SubagentDescriptorPayload.CurrentVersion, mode, "spawn", label));
        if (withToolCall)
        {
            var callId = ToolCallId.Create($"call-{id.Value}");
            session.Append(new TurnStartPayload(1));
            var callSeq = session.Seq;
            session.Append(new ToolCallPayload(1, 1, callId, "bash", """{"command":"echo child"}"""));
            session.Append(
                new ToolResultPayload(1, 1, MessageFactory.CreateToolResultMessage(callId, [new TextBlock("child-out")], false)),
                new SurfaceOp.Append(),
                [callSeq]);
            session.Append(new TurnEndPayload(1, new TurnEndReason.Completed()));
        }
        return session;
    }
}
