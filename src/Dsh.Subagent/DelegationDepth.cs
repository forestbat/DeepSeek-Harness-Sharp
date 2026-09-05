using Dsh.Core;

namespace Dsh.Subagent;

public static class DelegationDepth
{
    public static int DelegationDepthOf(IAgent agent) => agent.Session.Header.DelegationDepth ?? 0;

    public static void AssertSubagentMaxDepth(int? maxDepth)
    {
        if (maxDepth is < 0)
            throw new ArgumentException("subagent maxDepth must be a non-negative safe integer");
    }

    public static int ResolveChildDepth(IAgent parent, int? maxDepth)
    {
        var parentDepth = DelegationDepthOf(parent);
        if (parentDepth == int.MaxValue)
            throw new OverflowException("subagent child depth exceeds the safe integer range");
        var childDepth = parentDepth + 1;
        if (maxDepth is not null && childDepth > maxDepth)
            throw new SubagentDepthError($"subagent maxDepth {maxDepth} exceeded: child would be depth {childDepth}");
        return childDepth;
    }
}
