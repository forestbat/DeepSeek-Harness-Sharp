using Dsh.Runtime;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Subagent;

public sealed partial class SubagentRuntime : Service, ISubagentService
{
    public const string ServiceName = ISubagentService.ServiceName;
    public const string DelegationContextName = "subagent:delegation";

    private readonly Dictionary<string, ISubagentProvider> _providers = [];
    private readonly List<string> _providerNames = [];
    private readonly Dictionary<SessionId, ISubagentRun> _liveRuns = [];
    private readonly HashSet<ScopeKey> _childScopes = [];
    private readonly Lock _sync = new();

    public SubagentRuntime(Context ctx) : base(ctx, ServiceName)
    {
        ctx.Get<SystemPrompt>(SystemPrompt.ServiceName, false)?.Context(new PromptContext(
            DelegationContextName,
            PromptOrders.ContextSubagentDelegation,
            context => context.Scope is { } scope && IsChildScope(scope)
                ? ChildCompositionSupport.DelegationContextText
                : ""));
    }

    public static SubagentRuntime Register(Context ctx) => new(ctx);

    public IDisposable RegisterProvider(ISubagentProvider provider)
    {
        if (!_providers.TryAdd(provider.Name, provider))
        {
            throw new SubagentException(
                $"subagent provider \"{provider.Name}\" is already registered", SubagentErrorCodes.DuplicateProvider);
        }
        _providerNames.Add(provider.Name);
        Ctx.Emit(new SubagentProviderAddedNotification(provider));
        return new DisposeAction(() =>
        {
            if (!_providers.Remove(provider.Name))
                return;
            _providerNames.Remove(provider.Name);
            Ctx.Emit(new SubagentProviderRemovedNotification(provider.Name));
        });
    }

    public ISubagentProvider? GetProvider(string name) => _providers.GetValueOrDefault(name);

    public SubagentProviderInfo? GetProviderInfo(string name)
        => GetProvider(name) is { } provider
            ? new SubagentProviderInfo(provider.Capabilities, provider.InheritsParentContext)
            : null;

    public IReadOnlyList<string> List() => _providerNames.ToList();

    public IAgent? GetLive(SessionId id)
    {
        lock (_sync)
            return _liveRuns.GetValueOrDefault(id)?.LocalAgent;
    }

    /** 控制面：中断存活的直系子 agent（任意档位）。未知或非直系 id 返回 false。 */
    public bool CancelChild(SessionId parentId, SessionId childId)
    {
        if (GetLiveChild(parentId, childId) is not { } child)
            return false;
        child.Cancel(new AgentCancelCause.User());
        return true;
    }

    /** 控制面：向存活的直系子 agent 注入提示词，在下一个 step 边界消费（Steer 语义）。已完成的 run 拒绝注入。 */
    public bool InjectChild(SessionId parentId, SessionId childId, IReadOnlyList<ContentBlock> content)
    {
        ISubagentRun? run;
        IAgent? child;
        lock (_sync)
        {
            run = _liveRuns.GetValueOrDefault(childId);
            child = run?.LocalAgent;
            if (child is null || child.Session.Header.ParentSession != parentId)
                return false;
        }
        if (run!.Result.IsCompleted)
            return false;
        // 换挡规则：注入是写操作，无头子代先升档（入 SessionStore，从此持久化与 UI 可见）；中断不算写操作，不升档。
        AttachChild(parentId, childId);
        child.Steer(MessageFactory.CreateUserMessage(content));
        return true;
    }

    /** 升档：把纯内存的无头子会话接入 SessionStore（Announce 触发持久化全量回填）。已在店内的子代为 no-op。 */
    public bool AttachChild(SessionId parentId, SessionId childId)
    {
        if (GetLiveChild(parentId, childId) is not { } child)
            return false;
        if (Ctx.Get<SessionStore>(SessionStore.ServiceName, false) is not { } sessions)
            return false;
        if (sessions.Get(childId) is not null)
            return true;
        sessions.Enter(child.Session, Ctx);
        sessions.Announce(child.Session);
        return true;
    }

    private IAgent? GetLiveChild(SessionId parentId, SessionId childId)
    {
        lock (_sync)
        {
            var child = _liveRuns.GetValueOrDefault(childId)?.LocalAgent;
            return child is not null && child.Session.Header.ParentSession == parentId ? child : null;
        }
    }

    public async Task<ISubagentRun> StartAsync(string name, SubagentStartRequest request)
    {
        var provider = ExpectProvider(name);
        AssertCapabilities(provider, request);
        DelegationDepth.AssertSubagentMaxDepth(request.MaxDepth);
        if (request.OutputSchema is { } schema)
            AssertObjectSchema(schema);
        var resolved = new ResolvedSubagentStartRequest(
            request, SubagentDescriptorPayload.OneShot(name, request.Label));
        return ObserveRun(name, request.Parent, await provider.StartAsync(resolved));
    }

    private ISubagentProvider ExpectProvider(string name)
        => GetProvider(name)
            ?? throw new SubagentException(
                $"subagent provider \"{name}\" is not registered", SubagentErrorCodes.NoProvider);

    private static void AssertCapabilities(ISubagentProvider provider, SubagentStartRequest request)
    {
        AssertCapability(request.AgentOptions is not null, provider.Capabilities.AgentOptions, provider.Name, "child agentOptions");
        AssertCapability(request.OutputSchema is not null, provider.Capabilities.OutputSchema, provider.Name, "structured output (outputSchema)");
        AssertCapability(request.MaxDepth is not null, provider.Capabilities.DepthLimit, provider.Name, "depthLimit (maxDepth)");
        AssertCapability(request.ToolFilter is not null, provider.Capabilities.ToolFilter, provider.Name, "toolFilter");
        AssertCapability(request.Persona is not null, provider.Capabilities.Persona, provider.Name, "persona");
    }

    private static void AssertCapability(bool requested, bool supported, string providerName, string feature)
    {
        if (requested && !supported)
        {
            throw new SubagentException(
                $"subagent provider \"{providerName}\" does not support {feature}",
                SubagentErrorCodes.UnsupportedCapability);
        }
    }

    private static void AssertObjectSchema(System.Text.Json.Nodes.JsonObject schema)
    {
        JsonSchemaValidator.AssertSupported(schema);
        if (schema["type"] is not System.Text.Json.Nodes.JsonValue type
            || !type.TryGetValue<string>(out var typeText)
            || typeText != "object")
        {
            throw new HarnessException(
                "schema.type must be \"object\" (structured output is object-rooted)", "INVALID_JSON_SCHEMA");
        }
    }

    private ISubagentRun ObserveRun(string providerName, IAgent parent, ISubagentRun run)
    {
        var info = new SubagentRunInfo(Guid.NewGuid().ToString(), providerName, run.Id, run.LocalAgent is not null);
        var observed = new ObservedRun(run, this);
        if (run.LocalAgent is { } local)
            Track(local.Id, observed);
        _ = ObserveEndAsync(info, observed, parent.ScopeKey);
        Ctx.Events.Emit(DshScope.ScopeTarget(Ctx, parent.ScopeKey), new SubagentStartNotification(info));
        return observed;
    }

    private async Task ObserveEndAsync(SubagentRunInfo info, ISubagentRun run, ScopeKey parentScope)
    {
        try
        {
            var result = await run.Result;
            EmitEnd(parentScope, info, result.StopReason, result.Output.Count > 0 ? result.Output : null, result.Interruption);
        }
        catch
        {
            EmitEnd(parentScope, info, SubagentStopReason.Error, null, null);
        }
    }

    private void EmitEnd(
        ScopeKey parentScope,
        SubagentRunInfo info,
        SubagentStopReason stopReason,
        IReadOnlyList<ContentBlock>? output,
        SubagentInterruptionSnapshot? interruption)
        => Ctx.Events.Emit(
            DshScope.ScopeTarget(Ctx, parentScope),
            new SubagentEndNotification(
                new SubagentRunEndInfo(info.RunId, info.Provider, info.Id, info.Local, stopReason, output, interruption)));

    private void Track(SessionId id, ISubagentRun run)
    {
        lock (_sync)
            _liveRuns[id] = run;
    }

    private void Untrack(SessionId id)
    {
        lock (_sync)
            _liveRuns.Remove(id);
    }

    // 驱动器在子 agent 构造后、首个 prompt 装配前调用；scope 一旦属于子会话即永久成立（该会话始终是 subagent）。
    internal void NoteChildScope(ScopeKey scope)
    {
        lock (_sync)
            _childScopes.Add(scope);
    }

    private bool IsChildScope(ScopeKey scope)
    {
        lock (_sync)
            return _childScopes.Contains(scope);
    }

    private sealed class ObservedRun(ISubagentRun inner, SubagentRuntime owner) : ISubagentRun
    {
        public SessionId Id => inner.Id;

        public IAgent? LocalAgent => inner.LocalAgent;

        public Task<SubagentResult> Result => inner.Result;

        public async Task DisposeAsync()
        {
            try
            {
                await inner.DisposeAsync();
            }
            finally
            {
                if (inner.LocalAgent is { } agent)
                    owner.Untrack(agent.Id);
            }
        }
    }

    private sealed class DisposeAction(Action dispose) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            dispose();
        }
    }
}
