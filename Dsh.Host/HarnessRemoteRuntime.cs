using System.Collections.Concurrent;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.RemoteHost;
using Dsh.Runtime;
using Dsh.Agent;

namespace Dsh.Host;

/** Shared harness resources and ownership, not a remote domain implementation. */
internal sealed class HarnessRemoteRuntime(HarnessApp app, HostSessionRegistry sessions, HostPendingDecisions pending, HarnessSessionTracker tracker)
{
    private readonly ConcurrentDictionary<string, AgentHandle> _handles = new(StringComparer.Ordinal);
    private IDisposable? _acpExecutionRegistration;

    internal HarnessApp App => app;

    /**
     * 新建/恢复会话用的默认模型在需要时才读 settings.yaml。
     * 启动时算一次存成字段的话, 之后用 /model 或 App 里换了模型, 新会话仍旧按老模型开。
     * 命令行显式给了 provider/model 时以它为准, 那是用户当次的意图。
     */
    internal AgentOptions DefaultAgentOptions
    {
        get
        {
            var configured = HarnessSettings.Load(app.Home).ResolveDefaultModel();
            var provider = app.Provider.Length > 0 ? app.Provider : configured?.Provider ?? "";
            var model = app.Model.Length > 0 ? app.Model : configured?.Model ?? "";
            return new AgentOptions(
                provider,
                model,
                app.ReasoningEffort is null ? null : ReasoningEffortId.Create(app.ReasoningEffort));
        }
    }
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

    internal void RegisterAcpExecutionBackend(ExternalAgentSessions agents)
    {
        if (_acpExecutionRegistration is not null)
            return;
        if (app.Ctx.Get<IAgentExecutionRouter>(IAgentExecutionRouter.ServiceName, strict: false) is { } router)
            _acpExecutionRegistration = router.RegisterBackend(new AcpAgentExecutionBackend(agents, sessions));
    }

    internal void DisposeHandles()
    {
        _acpExecutionRegistration?.Dispose();
        _acpExecutionRegistration = null;
        foreach (var handle in _handles.Values)
            handle.Dispose.Dispose();
        _handles.Clear();
    }


}
