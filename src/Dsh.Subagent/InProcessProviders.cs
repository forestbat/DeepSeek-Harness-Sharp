using Cordis;
using Dsh.Core;

namespace Dsh.Subagent;

public sealed class SpawnInProcessProvider(string? providerName = null) : ISubagentProvider
{
    public const string DefaultProviderName = "spawn";

    public string Name => providerName ?? DefaultProviderName;

    public SubagentCapabilities Capabilities { get; } = new(
        AgentOptions: true,
        OutputSchema: true,
        DepthLimit: true,
        ToolFilter: true,
        Persona: true);

    public bool InheritsParentContext => false;

    public Task<ISubagentRun> StartAsync(ResolvedSubagentStartRequest request)
        => InProcessDriver.StartAsync(request, seed: null);
}

public sealed class ForkInProcessProvider(string? providerName = null) : ISubagentProvider
{
    public const string DefaultProviderName = "fork";

    public string Name => providerName ?? DefaultProviderName;

    public SubagentCapabilities Capabilities { get; } = new(
        AgentOptions: true,
        OutputSchema: true,
        DepthLimit: true,
        ToolFilter: true,
        Persona: true);

    public bool InheritsParentContext => true;

    public Task<ISubagentRun> StartAsync(ResolvedSubagentStartRequest request)
        => InProcessDriver.StartAsync(request, CompletedTurnPrefix(request.Parent));

    // fork 种子 = 父会话最近完成 turn 之前缀；最后一个 turn/end 之后的进行中内容不进子会话。
    private static IReadOnlyList<SessionEvent>? CompletedTurnPrefix(IAgent parent)
    {
        var events = parent.Session.SnapshotEvents();
        SessionEvent? lastEnd = null;
        foreach (var sessionEvent in events)
        {
            if (sessionEvent.Data is TurnEndPayload)
                lastEnd = sessionEvent;
        }
        if (lastEnd is null)
            return null;
        return events.Take((int)(lastEnd.Seq + 1)).ToList();
    }
}

public static class SubagentInProcessProviders
{
    public static IDisposable RegisterSpawn(Context ctx, string? providerName = null)
        => ctx.Get<SubagentRuntime>(SubagentRuntime.ServiceName)!.RegisterProvider(new SpawnInProcessProvider(providerName));

    public static IDisposable RegisterFork(Context ctx, string? providerName = null)
        => ctx.Get<SubagentRuntime>(SubagentRuntime.ServiceName)!.RegisterProvider(new ForkInProcessProvider(providerName));
}
