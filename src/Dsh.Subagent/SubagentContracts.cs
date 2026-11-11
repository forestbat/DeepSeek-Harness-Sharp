using Dsh.Core;
using Dsh.Llm;
using Dsh.Sdk;

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
