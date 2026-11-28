using Dsh.Agent;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Runtime.Events;
using Dsh.Sdk;

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
            context => context.Scope is { } scope && IsChildScope(scope)
                ? ChildCompositionSupport.DelegationContextText
                : ""));
    }

    public static SubagentRuntime Register(Context ctx) => new(ctx);

    public IDisposable RegisterProvider(ISubagentProvider provider)
    {
        lock (_sync)
        {
            if (!_providers.TryAdd(provider.Name, provider))
            {
                throw new SubagentException(
                    $"subagent provider \"{provider.Name}\" is already registered", SubagentErrorCodes.DuplicateProvider);
            }
            _providerNames.Add(provider.Name);
        }
        Ctx.Emit(new SubagentProviderAddedNotification(provider));
        return new DisposeAction(() =>
        {
            lock (_sync)
            {
                if (!_providers.Remove(provider.Name))
                    return;
                _providerNames.Remove(provider.Name);
            }
            Ctx.Emit(new SubagentProviderRemovedNotification(provider.Name));
        });
    }

    public ISubagentProvider? GetProvider(string name)
    {
        lock (_sync)
            return _providers.GetValueOrDefault(name);
    }

    public SubagentProviderInfo? GetProviderInfo(string name)
        => GetProvider(name) is { } provider
            ? new SubagentProviderInfo(provider.Capabilities, provider.InheritsParentContext)
            : null;

    public IReadOnlyList<string> List()
    {
        lock (_sync)
            return _providerNames.ToList();
    }

    public IAgent? GetLive(SessionId id)
    {
        lock (_sync)
            return _liveRuns.GetValueOrDefault(id)?.LocalAgent;
    }

    /** 控制面：中断存活的直系子 agent（任意档位或远端）。未知或非直系 id 返回 false。 */
    public bool CancelChild(SessionId parentId, SessionId childId)
    {
        ISubagentRun? run;
        lock (_sync)
            run = _liveRuns.GetValueOrDefault(childId);
        if (run is RemoteSubagentRun remote && remote.ParentId == parentId)
        {
            remote.Cancel();
            return true;
        }
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
        }
        if (run is RemoteSubagentRun remote && remote.ParentId == parentId && !remote.Result.IsCompleted)
        {
            _ = remote.SendSteerAsync(PlainText(content));
            return true;
        }
        if (child is null || child.Session.Header.ParentSession != parentId)
            return false;
        if (run!.Result.IsCompleted)
            return false;
        // 换挡规则：注入是写操作，无头子代先升档（入 SessionStore，从此持久化与 UI 可见）；中断不算写操作，不升档。
        AttachChild(parentId, childId);
        child.Steer(MessageFactory.CreateUserMessage(content));
        return true;
    }

    /** 跨 agent 投递消息：支持直系子代、直接父会话、或同级/独立 peer AgentId/SessionId。 */
    public async Task<bool> SendMessageAsync(
        IAgent sender,
        string target,
        string? childId,
        IReadOnlyList<ContentBlock> content,
        AgentMessageIntent intent = AgentMessageIntent.Task,
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(target, "child", StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(childId))
                throw new SubagentException("send_message with target \"child\" requires childId", SubagentErrorCodes.NoProvider);
            if (!InjectChild(sender.Id, SessionId.Create(childId), content))
            {
                throw new SubagentException(
                    $"child subagent \"{childId}\" is not live (already finished or unknown)",
                    SubagentErrorCodes.ContinuationUnavailable);
            }
            return true;
        }

        if (string.Equals(target, "parent", StringComparison.Ordinal))
            return await SendToParentAsync(sender, content, intent, cancellationToken).ConfigureAwait(false);

        var peerId = string.Equals(target, "peer", StringComparison.Ordinal) ? childId : target;
        if (string.IsNullOrWhiteSpace(peerId))
            throw new SubagentException("send_message requires a target agent or session id", SubagentErrorCodes.NoProvider);
        return await SendToPeerAsync(sender, peerId, content, intent, cancellationToken).ConfigureAwait(false);
    }

    /** 升档：把纯内存的无头子会话接入 SessionStore（Announce 触发持久化全量回填）。已在店内的子代为 no-op。 */
    public bool AttachChild(SessionId parentId, SessionId childId)
    {
        if (GetLiveChild(parentId, childId) is not { } child)
            return false;
        if (Ctx.Get<SessionStore>(SessionStore.ServiceName, false) is not { } sessions)
            return false;
        // TryEnter 把"已在店里"与"登记成功"合进同一把锁: 并发注入/升档同一子会话时不会撞 already live。
        _ = sessions.TryEnter(child.Session);
        return true;
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
        return ObserveRun(name, request.Parent, await provider.StartAsync(resolved).ConfigureAwait(false));
    }

    public Task<ISubagentRun> StartAsync(
        string name,
        SubagentStartRequest request,
        string? targetId,
        AgentBackendKind? backend = null)
    {
        var isLocalTarget = (string.IsNullOrWhiteSpace(targetId) || string.Equals(targetId, "local", StringComparison.OrdinalIgnoreCase))
            && (backend is null || backend == AgentBackendKind.Local);
        if (isLocalTarget)
            return StartAsync(name, request);

        DelegationDepth.AssertSubagentMaxDepth(request.MaxDepth);
        var router = Ctx.Get<IAgentExecutionRouter>(IAgentExecutionRouter.ServiceName, false)
            ?? throw new SubagentException(
                "remote subagent execution requires the agentExecutionRouter service",
                SubagentErrorCodes.NoProvider);
        var remote = new RemoteSubagentRun(router, request, targetId, backend);
        return Task.FromResult(ObserveRun(name, request.Parent, remote));
    }

    private async Task<bool> SendToParentAsync(
        IAgent sender,
        IReadOnlyList<ContentBlock> content,
        AgentMessageIntent intent,
        CancellationToken cancellationToken)
    {
        var parentSessionId = sender.Session.Header.ParentSession
            ?? throw new SubagentException("current session has no parent session", SubagentErrorCodes.ContinuationUnavailable);
        var agents = Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName, false);
        var parentAgent = agents?.Get(parentSessionId);
        var bus = Ctx.Get<IAgentMessageBus>(IAgentMessageBus.ServiceName, false);
        var text = PlainText(content);

        if (bus is not null)
        {
            var trust = AgentTurnAttribution.Current?.TrustLevel == MessageTrustLevel.ExternalAgent
                ? MessageTrustLevel.ExternalAgent
                : MessageTrustLevel.TrustedPeerAgent;
            var targetAgentId = parentAgent?.AgentId ?? AgentId.FromSession(parentSessionId);
            var envelope = new AgentEnvelope(
                EnvelopeId: $"subagent-parent-{Guid.NewGuid():N}",
                TargetAgentId: targetAgentId,
                TargetSessionId: parentSessionId,
                Sender: new MessagePrincipal(
                    PrincipalKind.Agent,
                    sender.AgentId.Value,
                    sender.Session.Header.SubagentLabel ?? sender.Id.Value),
                TrustLevel: trust,
                Intent: intent == AgentMessageIntent.Control ? AgentMessageIntent.Task : intent,
                Body: text,
                CreatedAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                WakeIfSuspended: true);
            var delivery = await bus.SendAsync(envelope, cancellationToken).ConfigureAwait(false);
            if (delivery.Status == AgentDeliveryStatus.Rejected)
            {
                throw new SubagentException(
                    delivery.Reason ?? "message to parent was rejected",
                    SubagentErrorCodes.ContinuationUnavailable);
            }
            return true;
        }

        if (parentAgent is null)
        {
            throw new SubagentException(
                $"parent session \"{parentSessionId}\" is not live",
                SubagentErrorCodes.ContinuationUnavailable);
        }
        DeliverDirect(parentAgent, content, intent);
        return true;
    }

    private async Task<bool> SendToPeerAsync(
        IAgent sender,
        string peerKey,
        IReadOnlyList<ContentBlock> content,
        AgentMessageIntent intent,
        CancellationToken cancellationToken)
    {
        var agents = Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName, false);
        var persistent = Ctx.Get<IPersistentAgentManager>(IPersistentAgentManager.ServiceName, false);
        var bus = Ctx.Get<IAgentMessageBus>(IAgentMessageBus.ServiceName, false);
        var peerSessionId = SessionId.Create(peerKey);
        var peerAgentId = AgentId.Create(peerKey);

        var liveInRegistry = agents?.Get(peerSessionId)
            ?? agents?.List().FirstOrDefault(agent =>
                agent.AgentId == peerAgentId || agent.Session.Header.AgentId == peerAgentId);
        var headlessPeer = liveInRegistry is null ? GetLive(peerSessionId) : null;

        if (headlessPeer is not null)
        {
            DeliverDirect(headlessPeer, content, intent);
            return true;
        }

        if (bus is not null)
        {
            var profile = persistent?.Get(peerAgentId)
                ?? persistent?.List().FirstOrDefault(item =>
                    item.ActiveSessionId == peerSessionId || item.SessionIds.Contains(peerSessionId));
            if (liveInRegistry is null && profile is null)
            {
                throw new SubagentException(
                    $"target agent \"{peerKey}\" is unknown or not reachable",
                    SubagentErrorCodes.ContinuationUnavailable);
            }
            var trust = AgentTurnAttribution.Current?.TrustLevel == MessageTrustLevel.ExternalAgent
                ? MessageTrustLevel.ExternalAgent
                : MessageTrustLevel.TrustedPeerAgent;
            var targetAgentId = liveInRegistry?.AgentId ?? profile?.AgentId ?? peerAgentId;
            var targetSessionId = liveInRegistry?.Id ?? profile?.ActiveSessionId;
            var envelope = new AgentEnvelope(
                EnvelopeId: $"subagent-peer-{Guid.NewGuid():N}",
                TargetAgentId: targetAgentId,
                TargetSessionId: targetSessionId,
                Sender: new MessagePrincipal(
                    PrincipalKind.Agent,
                    sender.AgentId.Value,
                    sender.Session.Header.SubagentLabel ?? sender.Id.Value),
                TrustLevel: trust,
                Intent: intent == AgentMessageIntent.Control ? AgentMessageIntent.Task : intent,
                Body: PlainText(content),
                CreatedAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                WakeIfSuspended: true);
            var delivery = await bus.SendAsync(envelope, cancellationToken).ConfigureAwait(false);
            if (delivery.Status == AgentDeliveryStatus.Rejected)
            {
                throw new SubagentException(
                    delivery.Reason ?? $"message to peer \"{peerKey}\" was rejected",
                    SubagentErrorCodes.ContinuationUnavailable);
            }
            return true;
        }

        if (liveInRegistry is null)
        {
            throw new SubagentException(
                $"peer agent \"{peerKey}\" is not live",
                SubagentErrorCodes.ContinuationUnavailable);
        }
        DeliverDirect(liveInRegistry, content, intent);
        return true;
    }

    private static void DeliverDirect(IAgent target, IReadOnlyList<ContentBlock> content, AgentMessageIntent intent)
    {
        var message = MessageFactory.CreateUserMessage(content);
        if (intent == AgentMessageIntent.Steer)
            target.Steer(message);
        else
            target.Followup(message);
    }

    private static string PlainText(IReadOnlyList<ContentBlock> content)
        => string.Concat(content.OfType<TextBlock>().Select(block => block.Text));

    private IAgent? GetLiveChild(SessionId parentId, SessionId childId)
    {
        lock (_sync)
        {
            var child = _liveRuns.GetValueOrDefault(childId)?.LocalAgent;
            return child is not null && child.Session.Header.ParentSession == parentId ? child : null;
        }
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
        Track(run.Id, observed);
        Ctx.Events.Emit(DshScope.ScopeTarget(Ctx, parent.ScopeKey), new SubagentStartNotification(info));
        // Callers await the observed result, so the end notification is out before the delegating tool returns.
        observed.Result = ObserveEndAsync(info, run, parent.ScopeKey);
        return observed;
    }

    private async Task<SubagentResult> ObserveEndAsync(SubagentRunInfo info, ISubagentRun run, ScopeKey parentScope)
    {
        SubagentResult result;
        try
        {
            result = await run.Result.ConfigureAwait(false);
        }
        catch
        {
            EmitEnd(parentScope, info, SubagentStopReason.Error, null, null);
            throw;
        }

        EmitEnd(parentScope, info, result.StopReason, result.Output.Count > 0 ? result.Output : null, result.Interruption);
        return result;
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

    // 驱动器在子 agent 构造后、首个 prompt 装配前调用；子 agent 存续期间 scope 一直属于子会话，run 处置时移除。
    internal void NoteChildScope(ScopeKey scope)
    {
        lock (_sync)
            _childScopes.Add(scope);
    }

    private void ForgetChildScope(ScopeKey scope)
    {
        lock (_sync)
            _childScopes.Remove(scope);
    }

    private bool IsChildScope(ScopeKey scope)
    {
        lock (_sync)
            return _childScopes.Contains(scope);
    }

    private sealed class RemoteSubagentRun : ISubagentRun
    {
        private readonly IAgentExecutionRouter _router;
        private readonly CancellationTokenSource _ownAbort;
        private readonly CancellationTokenRegistration _parentRegistration;
        private IAgentExecutionHandle? _handle;
        private volatile bool _cancelled;

        public RemoteSubagentRun(
            IAgentExecutionRouter router,
            SubagentStartRequest request,
            string? targetId,
            AgentBackendKind? backend)
        {
            _router = router;
            ParentId = request.Parent.Id;
            Id = SessionId.Create($"remote-{Guid.NewGuid():N}");
            _ownAbort = new CancellationTokenSource();
            if (!request.Background)
            {
                _parentRegistration = request.Signal.Register(() =>
                {
                    _cancelled = true;
                    _ownAbort.Cancel();
                });
            }
            Result = DriveAsync(request, targetId, backend);
        }

        public SessionId ParentId { get; }
        public SessionId Id { get; }
        public IAgent? LocalAgent => null;
        public Task<SubagentResult> Result { get; }

        public void Cancel()
        {
            _cancelled = true;
            _ownAbort.Cancel();
            if (_handle is { } handle)
                _ = handle.CancelAsync(CancellationToken.None);
        }

        public async Task SendSteerAsync(string message)
        {
            if (_handle is { } handle)
                _ = await handle.SendAsync(message, AgentMessageIntent.Steer, cancellationToken: _ownAbort.Token).ConfigureAwait(false);
        }

        private async Task<SubagentResult> DriveAsync(
            SubagentStartRequest request,
            string? targetId,
            AgentBackendKind? backend)
        {
            try
            {
                var dispatch = new AgentDispatchRequest(
                    TargetId: string.IsNullOrWhiteSpace(targetId) ? null : targetId,
                    PreferredBackend: backend,
                    Prompt: PlainText(request.Prompt),
                    Cwd: request.Parent.Session.WorkingDirectory,
                    Name: request.Label,
                    WaitForCompletion: true,
                    Sender: new MessagePrincipal(
                        PrincipalKind.Agent,
                        request.Parent.AgentId.Value,
                        request.Parent.Id.Value),
                    AgentPreset: request.Persona);
                _handle = await _router.DispatchAsync(dispatch, _ownAbort.Token).ConfigureAwait(false);
                var snapshot = await _handle.GetStatusAsync(_ownAbort.Token).ConfigureAwait(false);
                if (_cancelled)
                {
                    return new SubagentResult
                    {
                        Output = [],
                        StopReason = SubagentStopReason.Aborted,
                    };
                }
                if (snapshot.Error is { Length: > 0 } error)
                {
                    return new SubagentResult
                    {
                        Output = snapshot.Output is { Length: > 0 } partial ? [new TextBlock(partial)] : [],
                        StopReason = SubagentStopReason.Error,
                        Diagnostic = error,
                    };
                }
                var text = snapshot.Output ?? snapshot.Summary ?? "";
                return new SubagentResult
                {
                    Output = text.Length > 0 ? [new TextBlock(text)] : [],
                    StopReason = SubagentStopReason.Completed,
                };
            }
            catch (OperationCanceledException) when (_cancelled || _ownAbort.IsCancellationRequested)
            {
                return new SubagentResult
                {
                    Output = [],
                    StopReason = SubagentStopReason.Aborted,
                };
            }
            catch (Exception error)
            {
                return new SubagentResult
                {
                    Output = [],
                    StopReason = SubagentStopReason.Error,
                    Diagnostic = error.Message,
                };
            }
        }

        public async Task DisposeAsync()
        {
            _cancelled = true;
            await _ownAbort.CancelAsync().ConfigureAwait(false);
            await _parentRegistration.DisposeAsync().ConfigureAwait(false);
            if (_handle is { } handle)
                await handle.DisposeAsync().ConfigureAwait(false);
            _ownAbort.Dispose();
        }
    }

    private sealed class ObservedRun(ISubagentRun inner, SubagentRuntime owner) : ISubagentRun
    {
        public SessionId Id => inner.Id;

        public IAgent? LocalAgent => inner.LocalAgent;

        /** Completes after the run's end notification has been emitted (set by ObserveRun). */
        public Task<SubagentResult> Result { get; set; } = inner.Result;

        public async Task DisposeAsync()
        {
            try
            {
                await inner.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                owner.Untrack(inner.Id);
                if (inner.LocalAgent is { } agent)
                    owner.ForgetChildScope(agent.ScopeKey);
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
