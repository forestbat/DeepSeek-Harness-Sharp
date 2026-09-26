using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Subagent;

public sealed record ResolvedSubagentStartRequest(SubagentStartRequest Request, SubagentDescriptorPayload Descriptor)
{
    public IAgent Parent => Request.Parent;
}

public interface ISubagentProvider
{
    string Name { get; }
    SubagentCapabilities Capabilities { get; }
    bool InheritsParentContext { get; }
    Task<ISubagentRun> StartAsync(ResolvedSubagentStartRequest request);
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
