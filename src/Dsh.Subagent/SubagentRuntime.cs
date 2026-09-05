using Cordis;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Subagent;

public sealed partial class SubagentRuntime : Service
{
    public const string ServiceName = "subagents";
    public const string ProviderAddedEvent = "subagent/provider-added";
    public const string ProviderRemovedEvent = "subagent/provider-removed";
    public const string StartEvent = "subagent/start";
    public const string EndEvent = "subagent/end";
    public const string DelegationContextName = "subagent:delegation";

    private readonly Dictionary<string, ISubagentProvider> _providers = [];
    private readonly List<string> _providerNames = [];
    private readonly Dictionary<SessionId, IAgent> _liveChildren = [];
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
        if (_providers.ContainsKey(provider.Name))
        {
            throw new SubagentException(
                $"subagent provider \"{provider.Name}\" is already registered", SubagentErrorCodes.DuplicateProvider);
        }
        _providers[provider.Name] = provider;
        _providerNames.Add(provider.Name);
        Ctx.Emit(ProviderAddedEvent, provider);
        return new DisposeAction(() =>
        {
            if (!_providers.Remove(provider.Name))
                return;
            _providerNames.Remove(provider.Name);
            Ctx.Emit(ProviderRemovedEvent, provider.Name);
        });
    }

    public ISubagentProvider? GetProvider(string name) => _providers.GetValueOrDefault(name);

    public IReadOnlyList<string> List() => _providerNames.ToList();

    public IAgent? GetLive(SessionId id)
    {
        lock (_sync)
            return _liveChildren.GetValueOrDefault(id);
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
        Dsh.Core.JsonSchemaValidator.AssertSupported(schema);
        if (schema["type"] is not System.Text.Json.Nodes.JsonValue type
            || !type.TryGetValue<string>(out var typeText)
            || typeText != "object")
        {
            throw new Dsh.Llm.HarnessException(
                "schema.type must be \"object\" (structured output is object-rooted)", "INVALID_JSON_SCHEMA");
        }
    }

    private ISubagentRun ObserveRun(string providerName, IAgent parent, ISubagentRun run)
    {
        var info = new SubagentRunInfo(Guid.NewGuid().ToString(), providerName, run.Id, run.LocalAgent is not null);
        if (run.LocalAgent is { } local)
            Track(local);
        var observed = new ObservedRun(run, this);
        _ = ObserveEndAsync(info, observed, parent.ScopeKey);
        EmitScoped(parent, StartEvent, info);
        return observed;
    }

    private async Task ObserveEndAsync(SubagentRunInfo info, ISubagentRun run, ScopeKey parentScope)
    {
        try
        {
            var result = await run.Result;
            EmitEnd(parentScope, info, result.StopReason, result.Output.Count > 0 ? result.Output : null);
        }
        catch
        {
            EmitEnd(parentScope, info, SubagentStopReason.Error, null);
        }
    }

    private void EmitEnd(ScopeKey parentScope, SubagentRunInfo info, SubagentStopReason stopReason, IReadOnlyList<Dsh.Llm.ContentBlock>? output)
        => Ctx.Events.Emit(
            DshScope.ScopeTarget(Ctx, parentScope),
            EndEvent,
            new SubagentRunEndInfo(info.RunId, info.Provider, info.Id, info.Local, stopReason, output));

    private void EmitScoped(IAgent parent, string name, object payload)
        => Ctx.Events.Emit(DshScope.ScopeTarget(Ctx, parent.ScopeKey), name, payload);

    private void Track(IAgent child)
    {
        lock (_sync)
            _liveChildren[child.Id] = child;
    }

    private void Untrack(SessionId id)
    {
        lock (_sync)
            _liveChildren.Remove(id);
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
