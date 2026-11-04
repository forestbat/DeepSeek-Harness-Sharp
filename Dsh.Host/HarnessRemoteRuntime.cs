using System.Collections.Concurrent;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.RemoteHost;
using Dsh.Runtime;

namespace Dsh.Host;

/** Shared harness resources and ownership, not a remote domain implementation. */
internal sealed class HarnessRemoteRuntime(HarnessApp app, HostSessionRegistry sessions, HostPendingDecisions pending, HarnessSessionTracker tracker)
{
    private readonly ConcurrentDictionary<string, AgentHandle> _handles = new(StringComparer.Ordinal);

    internal HarnessApp App => app;
    internal AgentOptions DefaultAgentOptions { get; } = new(
        app.Provider, app.Model, app.ReasoningEffort is null ? null : ReasoningEffortId.Create(app.ReasoningEffort));
    internal HostSessionRegistry Sessions => sessions;
    internal HostPendingDecisions Pending => pending;
    internal AgentRegistry Registry => app.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)
        ?? throw new InvalidOperationException("AgentRegistry 未注册");
    internal ISessionPersistence? Persistence => app.Ctx.Get<ISessionPersistence>(ISessionPersistence.ServiceName, strict: false);
    internal SessionStore Store => app.Ctx.Get<SessionStore>(SessionStore.ServiceName)
        ?? throw new InvalidOperationException("SessionStore 未注册");

    internal IAgent Require(string sessionId)
        => Registry.Get(SessionId.Create(sessionId)) ?? throw new InvalidOperationException($"unknown session: {sessionId}");

    internal void Own(AgentHandle handle) => _handles[handle.Agent.Id.Value] = handle;

    internal void Track(IAgent agent) => tracker.Track(agent);

    internal void Release(string sessionId)
    {
        if (_handles.TryRemove(sessionId, out var handle))
            handle.Dispose.Dispose();
    }

    internal void DisposeHandles()
    {
        foreach (var handle in _handles.Values)
            handle.Dispose.Dispose();
        _handles.Clear();
    }


}
