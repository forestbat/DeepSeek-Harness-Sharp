using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using A2A;
using Dsh.A2A;
using Dsh.Agent;
using Dsh.Board;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Fleet;
using Dsh.GitInspect;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Notifications;
using Dsh.RemoteHost;
using Dsh.Runtime;
using Dsh.Subagent;
using Dsh.Subprocess;
using Dsh.Workflow;
using Dsh.Workspace;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using A2AClient = global::A2A.A2AClient;
using A2AMessage = global::A2A.Message;
using A2APart = global::A2A.Part;
using A2ARole = global::A2A.Role;
using SendMessageRequest = global::A2A.SendMessageRequest;
using TaskState = global::A2A.TaskState;

namespace Dsh.Tests;

public sealed class AgentExecutionOrchestrationTests
{
    private sealed class TestRouter : IAgentExecutionRouter
    {
        private readonly List<IAgentExecutionBackend> _backends = [];
        private readonly Lock _sync = new();

        public IDisposable RegisterBackend(IAgentExecutionBackend backend)
        {
            lock (_sync)
                _backends.Add(backend);
            return new RemoveAction(() =>
            {
                lock (_sync)
                    _backends.Remove(backend);
            });
        }

        public async Task<IReadOnlyList<RemoteExecutionTarget>> ListTargetsAsync(CancellationToken cancellationToken = default)
        {
            IAgentExecutionBackend[] snapshot;
            lock (_sync)
                snapshot = [.. _backends];
            var all = new List<RemoteExecutionTarget>();
            foreach (var backend in snapshot)
                all.AddRange(await backend.ListTargetsAsync(cancellationToken));
            return all;
        }

        public Task<IAgentExecutionHandle> DispatchAsync(AgentDispatchRequest request, CancellationToken cancellationToken = default)
        {
            IAgentExecutionBackend? match;
            lock (_sync)
                match = _backends.FirstOrDefault(backend => backend.CanHandle(request));
            return match is null
                ? throw new InvalidOperationException($"No execution backend matched target={request.TargetId} backend={request.PreferredBackend}.")
                : match.DispatchAsync(request, cancellationToken);
        }

        public async Task<IAgentExecutionHandle?> AttachAsync(
            AgentId agentId,
            AgentBackendKind? preferredBackend = null,
            string? targetId = null,
            CancellationToken cancellationToken = default)
        {
            IAgentExecutionBackend[] snapshot;
            lock (_sync)
                snapshot = [.. _backends];
            foreach (var backend in snapshot)
            {
                if (preferredBackend is { } kind && backend.Kind != kind)
                    continue;
                var handle = await backend.AttachAsync(agentId, targetId, cancellationToken);
                if (handle is not null)
                    return handle;
            }
            return null;
        }

        private sealed class RemoveAction(Action remove) : IDisposable
        {
            public void Dispose() => remove();
        }
    }

    private sealed class TestMessageBus(Context ctx, IPersistentAgentManager? persistent = null) : IAgentMessageBus
    {
        private readonly ConcurrentDictionary<AgentId, List<AgentEnvelope>> _offline = new();
        private readonly HashSet<string> _seenKeys = [];
        private readonly Lock _sync = new();

        public List<AgentEnvelope> DeliveredEnvelopes { get; } = [];

        public async Task<AgentDeliveryResult> SendAsync(AgentEnvelope envelope, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                DeliveredEnvelopes.Add(envelope);
                if (envelope.IdempotencyKey is { Length: > 0 } key && !_seenKeys.Add(key))
                    return new AgentDeliveryResult(envelope.EnvelopeId, AgentDeliveryStatus.Deduplicated, envelope.TargetAgentId);
            }

            if (envelope.Sender.Kind == PrincipalKind.Agent
                && envelope.TrustLevel == MessageTrustLevel.ExternalAgent
                && envelope.Intent == AgentMessageIntent.Control)
            {
                return new AgentDeliveryResult(
                    envelope.EnvelopeId,
                    AgentDeliveryStatus.Rejected,
                    envelope.TargetAgentId,
                    Reason: "ExternalAgent cannot send Control intent");
            }

            var agents = ctx.Get<AgentRegistry>(AgentRegistry.ServiceName, false);
            var target = ResolveLive(agents, envelope.TargetAgentId, envelope.TargetSessionId);
            var woke = false;
            if (target is null && envelope.WakeIfSuspended && persistent is not null)
            {
                target = await persistent.WakeAsync(envelope.TargetAgentId, cancellationToken);
                woke = true;
            }
            if (target is null)
            {
                var list = _offline.GetOrAdd(envelope.TargetAgentId, _ => []);
                lock (list)
                    list.Add(envelope);
                return new AgentDeliveryResult(envelope.EnvelopeId, AgentDeliveryStatus.QueuedOffline, envelope.TargetAgentId);
            }

            target.Session.Append(new AgentEnvelopePayload(
                envelope,
                woke ? AgentDeliveryStatus.WokeAndDelivered : AgentDeliveryStatus.DeliveredLive));
            var msg = MessageFactory.CreateUserText(envelope.Body);
            if (envelope.Intent == AgentMessageIntent.Steer)
                target.Steer(msg);
            else
                target.Followup(msg);
            return new AgentDeliveryResult(
                envelope.EnvelopeId,
                woke ? AgentDeliveryStatus.WokeAndDelivered : AgentDeliveryStatus.DeliveredLive,
                target.AgentId,
                target.Id);
        }

        public Task<int> FlushOfflineAsync(AgentId agentId, CancellationToken cancellationToken = default)
        {
            if (!_offline.TryRemove(agentId, out var queued))
                return Task.FromResult(0);
            var agents = ctx.Get<AgentRegistry>(AgentRegistry.ServiceName, false);
            var target = ResolveLive(agents, agentId, null);
            if (target is null)
                return Task.FromResult(0);
            foreach (var envelope in queued)
                target.Followup(MessageFactory.CreateUserText(envelope.Body));
            return Task.FromResult(queued.Count);
        }

        public IReadOnlyList<AgentEnvelope> PeekOffline(AgentId agentId)
            => _offline.TryGetValue(agentId, out var queued) ? [.. queued] : [];

        public IDisposable Subscribe(AgentEventSubscription subscription, Func<AgentRuntimeEvent, ValueTask>? handler = null)
            => new NoopDisposable();

        public IReadOnlyList<AgentEventSubscription> ListSubscriptions(AgentId? publisher = null) => [];

        public Task PublishEventAsync(AgentRuntimeEvent runtimeEvent, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        private static IAgent? ResolveLive(AgentRegistry? agents, AgentId agentId, SessionId? sessionId)
        {
            if (agents is null)
                return null;
            if (sessionId is { } sid && agents.Get(sid) is { } bySession)
                return bySession;
            return agents.List().FirstOrDefault(agent =>
                agent.AgentId == agentId || agent.Session.Header.AgentId == agentId || agent.Id.Value == agentId.Value);
        }

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    private sealed class TestPersistentManager(Context ctx) : IPersistentAgentManager
    {
        private readonly Dictionary<AgentId, AgentProfile> _profiles = [];
        private readonly Lock _sync = new();

        public async Task<AgentProfile> CreateAsync(AgentProfileRegistration registration, CancellationToken cancellationToken = default)
        {
            var agentId = registration.AgentId ?? AgentId.New();
            var sessionId = registration.InitialSessionId ?? SessionId.Create($"sess-{Guid.NewGuid():N}");
            var sessions = ctx.Get<SessionStore>(SessionStore.ServiceName)!;
            var session = sessions.Create(sessionId, header: new SessionHeader
            {
                Version = SessionHeader.SessionFormatVersion,
                Id = sessionId,
                AgentId = agentId,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Cwd = registration.Cwd,
                IsSeeded = false,
            });
            var profile = new AgentProfile(
                agentId,
                registration.Name,
                registration.Description,
                registration.BackendKind,
                sessionId,
                [sessionId],
                AgentLifecycleState.Suspended,
                Cwd: registration.Cwd,
                Options: registration.Options);
            lock (_sync)
                _profiles[agentId] = profile;
            _ = session;
            await Task.CompletedTask;
            return profile;
        }

        public AgentProfile? Get(AgentId id)
        {
            lock (_sync)
                return _profiles.GetValueOrDefault(id);
        }

        public IReadOnlyList<AgentProfile> List()
        {
            lock (_sync)
                return [.. _profiles.Values];
        }

        public Task<IAgent> WakeAsync(AgentId id, CancellationToken cancellationToken = default)
        {
            AgentProfile profile;
            lock (_sync)
                profile = _profiles[id];
            var agents = ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
            var sessions = ctx.Get<SessionStore>(SessionStore.ServiceName)!;
            var sessionId = profile.ActiveSessionId ?? SessionId.Create($"sess-{Guid.NewGuid():N}");
            if (agents.Get(sessionId) is { } alreadyLive)
                return Task.FromResult(alreadyLive);
            var session = sessions.Get(sessionId) ?? sessions.Create(sessionId, header: new SessionHeader
            {
                Version = SessionHeader.SessionFormatVersion,
                Id = sessionId,
                AgentId = profile.AgentId,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Cwd = profile.Cwd,
                IsSeeded = false,
            });
            var woken = new AgentLoopAgent(
                ctx.Root,
                sessionId,
                profile.Options ?? new AgentOptions("test-provider", "test-model"),
                session,
                SessionRepair.LastTurnOf);
            agents.Register(woken);
            lock (_sync)
                _profiles[id] = profile with { State = AgentLifecycleState.Idle, ActiveSessionId = sessionId };
            return Task.FromResult<IAgent>(woken);
        }

        public Task SuspendAsync(AgentId id, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<AgentProfile> AttachSessionAsync(AgentId id, SessionId sessionId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> DeleteAsync(AgentId id, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class ScriptedAdapter : LlmAdapter
    {
        private readonly Queue<Func<GenerateOptions, IReadOnlyList<StreamChunk>>> _script;

        public ScriptedAdapter(string provider, IEnumerable<Func<GenerateOptions, IReadOnlyList<StreamChunk>>> script)
        {
            _script = new Queue<Func<GenerateOptions, IReadOnlyList<StreamChunk>>>(script);
            ProviderInfo = new LlmProviderInfo(provider, provider);
        }

        public override LlmProviderInfo ProviderInfo { get; }
        public override ResolvedRetryPolicy ProviderRetryPolicy => ResolvedRetryPolicy.Resolve(null, "test");

        public override IAsyncEnumerable<StreamChunk> Stream(GenerateOptions options, CancellationToken cancellationToken)
        {
            if (_script.Count == 0)
                throw new InvalidOperationException("scripted adapter: no scripted response left");
            return Yield(_script.Dequeue()(options), cancellationToken);
        }

        private static async IAsyncEnumerable<StreamChunk> Yield(
            IEnumerable<StreamChunk> chunks,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var chunk in chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return chunk;
                await Task.Yield();
            }
        }
    }

    private sealed class MockLlmServer : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly Queue<string> _replies;

        private MockLlmServer(IEnumerable<string> replies)
        {
            _replies = new Queue<string>(replies);
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            _app = builder.Build();
            _app.Run(Serve);
        }

        public static async Task<MockLlmServer> StartAsync(params string[] replies)
        {
            var server = new MockLlmServer(replies);
            await server._app.StartAsync(TestContext.Current.CancellationToken);
            server.Port = new Uri(server._app.Urls.Single()).Port;
            return server;
        }

        public int Port { get; private set; }
        public string BaseUrl => $"http://127.0.0.1:{Port}";

        private async Task Serve(HttpContext context)
        {
            using var reader = new StreamReader(context.Request.Body);
            _ = await reader.ReadToEndAsync(context.RequestAborted);
            string reply;
            lock (_replies)
                reply = _replies.Count > 0 ? _replies.Dequeue() : "default-reply";

            string[] lines =
            [
                """data: {"choices":[{"delta":{"role":"assistant","content":null}}]}""",
                "",
                $"data: {{\"choices\":[{{\"delta\":{{\"content\":{JsonSerializer.Serialize(reply)}}}}}]}}",
                "",
                """data: {"choices":[{"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":2,"total_tokens":12}}""",
                "",
                "data: [DONE]",
                "",
            ];
            context.Response.ContentType = "text/event-stream";
            foreach (var line in lines)
            {
                await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes($"{line}\n"), context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
            }
        }

        public ValueTask DisposeAsync() => _app.DisposeAsync();
    }

    private sealed class FakeMachineCatalog(params MachineSpec[] machines) : IMachineCatalog
    {
        private readonly FleetConfig _config = new([.. machines], new DeploySettings());

        public string FilePath => "memory://fleet";
        public FleetConfig Load() => _config;
        public void Save(FleetConfig config) { }
        public bool Remove(string name) => false;
        public void Validate(IReadOnlyList<MachineSpec> list) { }
    }

    private sealed class FakeFleetClient(params FakeFleetMachine[] machines) : IWorkspaceClient
    {
        private readonly List<FakeFleetMachine> _machines = [.. machines];

        public Task WhenDisposed => Task.CompletedTask;
        public event Action? Changed;
        public IReadOnlyList<IWorkspaceMachine> Machines => _machines;

        public IWorkspaceMachine? Find(string name)
            => _machines.FirstOrDefault(m => string.Equals(m.Machine.Name, name, StringComparison.Ordinal));

        public void Start() => Changed?.Invoke();
        public Task WaitSettledAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public IReadOnlyList<FleetMachineStatus> Statuses() => [];
        public IReadOnlyList<FleetSession> Sessions() => [];
        public IReadOnlyList<FleetMachineGroup> Groups() => [];
        public IReadOnlyList<FleetPendingItem> Pending() => [];
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeFleetMachine(MachineSpec spec, FakeFleetHost host) : IWorkspaceMachine
    {
        public MachineSpec Machine { get; } = spec;
        public MachineConnectionState State => MachineConnectionState.Connected;
        public string? Detail => null;
        public HostInfo? Info => new(HostProtocol.Version, "1.0.0", "linux", "/tmp/remote-home");
        public HostHelloResponse? Hello => host.Hello;
        public IReadOnlyList<RemoteSessionSummary> Sessions => host.Summaries;
        public RemotePendingList Pending => new([], []);
        public RemoteNotifyPage? Notifications => null;
        public int UnreadNotifications => 0;
        public IWorkspaceMachineNetwork Network => throw new NotSupportedException();

        public event Action<IWorkspaceMachine>? StateChanged;
        public event Action<IWorkspaceMachine>? SessionsChanged;
        public event Action<IWorkspaceMachine>? PendingChanged;
        public event Action<IWorkspaceMachine, RemoteNotificationChange?>? NotificationsChanged;

        public void Start()
        {
            _ = StateChanged;
            _ = SessionsChanged;
            _ = PendingChanged;
            _ = NotificationsChanged;
        }

        public Task WaitSettledAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<WorkspaceHostLease> WaitConnectedAsync(CancellationToken cancellationToken)
            => Task.FromResult(new WorkspaceHostLease(host, cancellationToken, Task.CompletedTask));

        public IAsyncEnumerable<RemoteSessionEvent> SubscribeAsync(string sessionId, long fromSeq, CancellationToken cancellationToken)
            => host.SubscribeAsync(sessionId, fromSeq, cancellationToken);

        public Task AttachPtyAsync(string ptyId, Stream input, Stream output, CancellationToken cancellationToken,
            Func<IWorkspaceHost, CancellationToken, Task>? prepare = null)
            => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeFleetHost : IWorkspaceHost
    {
        private readonly Dictionary<string, List<SessionEvent>> _events = new(StringComparer.Ordinal);
        private readonly List<RemoteSessionSummary> _summaries = [];

        public List<(string SessionId, string Text)> SentMessages { get; } = [];
        public List<string> InterruptedSessions { get; } = [];
        public IReadOnlyList<RemoteSessionSummary> Summaries => _summaries;
        public HostHelloResponse Hello { get; } = new(true, HostProtocol.Version, "1.0.0", null, []);
        public bool Supports(string capability) => false;
        public Task<long> PingAsync(CancellationToken cancellationToken = default) => Task.FromResult(1L);
        public Task<HostInfo> InfoAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new HostInfo(HostProtocol.Version, "1.0.0", "linux", "/tmp/remote-home"));
        public IWorkspaceAgents Agent => throw new NotSupportedException();
        public T? GetCapability<T>() where T : class => null;
        public IWorkspaceFiles Files => throw new NotSupportedException();
        public IWorkspaceGit Git => throw new NotSupportedException();
        public IWorkspaceNotifications Notifications => throw new NotSupportedException();
        public IWorkspaceSessionEditing SessionEditing => throw new NotSupportedException();
        public IWorkspacePlugins Plugins => throw new NotSupportedException();
        public IWorkspaceNetwork Network => throw new NotSupportedException();
        public IWorkspaceTerminals Pty => throw new NotSupportedException();

        public Task<IReadOnlyList<RemoteSessionSummary>> ListSessionsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RemoteSessionSummary>>([.. _summaries]);

        public Task<RemoteSessionSummary> CreateSessionAsync(string cwd, CancellationToken cancellationToken = default)
        {
            var id = $"fleet-sess-{Guid.NewGuid():N}";
            var summary = new RemoteSessionSummary(id, cwd, HostSessionStatuses.Idle, 0, id, null, 0);
            _summaries.Add(summary);
            _events[id] = [];
            return Task.FromResult(summary);
        }

        public Task<RemoteSessionSummary> ResumeSessionAsync(string sessionId, CancellationToken cancellationToken = default)
            => Task.FromResult(_summaries.First(s => s.SessionId == sessionId));

        public Task<RemoteSessionSnapshot> GetSessionSnapshotAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            var list = _events[sessionId];
            return Task.FromResult(new RemoteSessionSnapshot(
                new SessionHeader { Version = 0, Id = SessionId.Create(sessionId), CreatedAt = 0, IsSeeded = false },
                list,
                list.Count));
        }

        public async IAsyncEnumerable<RemoteSessionEvent> SubscribeAsync(
            string sessionId,
            long fromSeq,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (var sessionEvent in _events[sessionId].Where(ev => ev.Seq >= fromSeq))
                yield return new RemoteSessionEvent(sessionId, sessionEvent);
        }

        public Task SendMessageAsync(string sessionId, string text, IReadOnlyList<RemoteImageBlock> images, CancellationToken cancellationToken = default)
        {
            SentMessages.Add((sessionId, text));
            var list = _events[sessionId];
            var seq = list.Count;
            var assistantPayload = new AssistantMessagePayload(
                1, 1, MessageFactory.CreateAssistantMessage([new TextBlock($"fleet-reply:{text}")], "p", "m"));
            var turnEndPayload = new TurnEndPayload(1, new TurnEndReason.Completed());
            list.Add(new SessionEvent { Type = assistantPayload.Type, Seq = seq, Time = 0, Data = assistantPayload });
            list.Add(new SessionEvent { Type = turnEndPayload.Type, Seq = seq + 1, Time = 0, Data = turnEndPayload });
            return Task.CompletedTask;
        }

        public Task InterruptAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            InterruptedSessions.Add(sessionId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListToolsAsync(string sessionId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>([]);
        public IAsyncEnumerable<RemotePendingChange> PendingChangesAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<bool> RespondApprovalAsync(string requestId, bool allow, string? reason = null, CancellationToken cancellationToken = default, bool rememberForSession = false, bool cancelled = false)
            => Task.FromResult(false);
        public Task<bool> RespondQuestionAsync(string requestId, IReadOnlyList<RemoteQuestionAnswerItem> answers, CancellationToken cancellationToken = default, string status = "answered", string? reason = null)
            => Task.FromResult(false);
        public Task<RemotePendingList> ListPendingAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new RemotePendingList([], []));
        public Task<IReadOnlyList<RemoteSessionSummary>> ListHostSessionsAsync(CancellationToken cancellationToken = default)
            => ListSessionsAsync(cancellationToken);
        public IAsyncEnumerable<RemoteSessionChange> HostSessionChangesAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class MockExecutionBackend(AgentBackendKind kind, params RemoteExecutionTarget[] targets) : IAgentExecutionBackend
    {
        public AgentBackendKind Kind { get; } = kind;
        public List<AgentDispatchRequest> Dispatches { get; } = [];

        public Task<IReadOnlyList<RemoteExecutionTarget>> ListTargetsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RemoteExecutionTarget>>(targets);

        public bool CanHandle(AgentDispatchRequest request)
            => request.TargetId is { Length: > 0 } tid
                ? targets.Any(t => string.Equals(t.TargetId, tid, StringComparison.OrdinalIgnoreCase))
                : request.PreferredBackend == Kind;

        public Task<IAgentExecutionHandle> DispatchAsync(AgentDispatchRequest request, CancellationToken cancellationToken = default)
        {
            Dispatches.Add(request);
            var targetId = request.TargetId ?? targets[0].TargetId;
            var agentId = request.AgentId ?? AgentId.New();
            var sessionId = request.SessionId ?? SessionId.Create($"mock-{Guid.NewGuid():N}");
            return Task.FromResult<IAgentExecutionHandle>(new MockHandle(agentId, sessionId, Kind, targetId, $"mock:{request.Prompt}"));
        }

        public Task<IAgentExecutionHandle?> AttachAsync(AgentId agentId, string? targetId = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IAgentExecutionHandle?>(null);

        private sealed class MockHandle(
            AgentId agentId,
            SessionId sessionId,
            AgentBackendKind kind,
            string targetId,
            string initialOutput) : IAgentExecutionHandle
        {
            private string _output = initialOutput;

            public AgentId AgentId { get; } = agentId;
            public SessionId? SessionId { get; } = sessionId;
            public AgentBackendKind BackendKind { get; } = kind;
            public string TargetId { get; } = targetId;

            public Task<AgentExecutionSnapshot> SendAsync(
                string message,
                AgentMessageIntent intent = AgentMessageIntent.Task,
                MessagePrincipal? sender = null,
                CancellationToken cancellationToken = default)
            {
                _output = $"mock:{message}";
                return GetStatusAsync(cancellationToken);
            }

            public Task<AgentExecutionSnapshot> GetStatusAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(new AgentExecutionSnapshot(
                    AgentId, SessionId, BackendKind, TargetId, AgentLifecycleState.Idle,
                    Summary: _output, Completed: true, Output: _output));

            public Task CancelAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Router_DispatchesAcrossFleetA2aAndMockBackends()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var llmServer = await MockLlmServer.StartAsync("a2a-one", "a2a-two");
        var a2aCtx = CreateLlmContext(llmServer.BaseUrl);
        using var a2aHandler = new DshAgentHandler(a2aCtx, "test-provider", "test-model");
        using var a2aHost = new A2aHost(a2aHandler, new A2aHostOptions());
        a2aHost.Start();

        var machineSpec = new MachineSpec("gpu-box", MachineKinds.Ssh, "10.0.0.5", 22, "ubuntu", SshAuth.Agent);
        var fleetHost = new FakeFleetHost();
        using var fleetBackend = new FleetAgentExecutionBackend(
            new FakeMachineCatalog(machineSpec),
            () => new FakeFleetClient(new FakeFleetMachine(machineSpec, fleetHost)));

        var remotes = new Dictionary<string, A2aRemoteSettings>
        {
            ["reviewer"] = new() { Url = a2aHost.Endpoint },
        };
        var a2aBackend = new A2aAgentExecutionBackend(remotes);
        var acpBackend = new MockExecutionBackend(
            AgentBackendKind.Acp,
            new RemoteExecutionTarget("acp:codex", AgentBackendKind.Acp, "Codex"));

        var router = new TestRouter();
        using var r1 = router.RegisterBackend(fleetBackend);
        using var r2 = router.RegisterBackend(a2aBackend);
        using var r3 = router.RegisterBackend(acpBackend);

        var targets = await router.ListTargetsAsync(ct);
        Assert.Contains(targets, t => t.TargetId == "fleet:gpu-box" && t.Kind == AgentBackendKind.Fleet);
        Assert.Contains(targets, t => t.TargetId == "a2a:reviewer" && t.Kind == AgentBackendKind.A2a);
        Assert.Contains(targets, t => t.TargetId == "acp:codex" && t.Kind == AgentBackendKind.Acp);

        await using var fleetHandle = await router.DispatchAsync(
            new AgentDispatchRequest(TargetId: "fleet:gpu-box", Prompt: "run tests"), ct);
        var fleetStatus = await fleetHandle.GetStatusAsync(ct);
        Assert.Equal("fleet-reply:run tests", fleetStatus.Output);

        var attachedFleet = await router.AttachAsync(fleetHandle.AgentId, AgentBackendKind.Fleet, "fleet:gpu-box", ct);
        Assert.NotNull(attachedFleet);
        var followFleet = await attachedFleet!.SendAsync("follow-up", cancellationToken: ct);
        Assert.Equal("fleet-reply:follow-up", followFleet.Output);
        await attachedFleet.CancelAsync(ct);
        Assert.Single(fleetHost.InterruptedSessions);

        await using var a2aHandle = await router.DispatchAsync(
            new AgentDispatchRequest(TargetId: "a2a:reviewer", Prompt: "turn1"), ct);
        var a2aSnap1 = await a2aHandle.GetStatusAsync(ct);
        Assert.Equal("a2a-one", a2aSnap1.Output);
        var a2aSnap2 = await a2aHandle.SendAsync("turn2", cancellationToken: ct);
        Assert.Equal("a2a-two", a2aSnap2.Output);

        await using var acpHandle = await router.DispatchAsync(
            new AgentDispatchRequest(PreferredBackend: AgentBackendKind.Acp, Prompt: "inspect"), ct);
        Assert.Equal("mock:inspect", (await acpHandle.GetStatusAsync(ct)).Output);
    }

    [Fact]
    public async Task A2aRemoteClient_CachesAgentCardWithTtl_AndReusesContextId()
    {
        var ct = TestContext.Current.CancellationToken;
        var cardFetches = 0;
        var observedContexts = new List<string?>();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        string? baseUrl = null;
        app.Run(async http =>
        {
            if (http.Request.Path == "/.well-known/agent-card.json")
            {
                Interlocked.Increment(ref cardFetches);
                http.Response.ContentType = "application/json";
                await http.Response.WriteAsync($$"""
                    {
                      "name": "cached-agent",
                      "description": "test",
                      "version": "1.0",
                      "supportedInterfaces": [{"url":"{{baseUrl}}","protocolBinding":"JSONRPC","protocolVersion":"1.0"}],
                      "capabilities": {"streaming": false},
                      "defaultInputModes": ["text/plain"],
                      "defaultOutputModes": ["text/plain"],
                      "skills": [{"id":"s1","name":"Skill","description":"desc","tags":["a"]}]
                    }
                    """, ct);
                return;
            }
            using var reader = new StreamReader(http.Request.Body);
            var body = await reader.ReadToEndAsync(ct);
            var doc = JsonNode.Parse(body)!.AsObject();
            var id = doc["id"]!.DeepClone();
            var contextId = doc["params"]?["message"]?["contextId"]?.GetValue<string>();
            observedContexts.Add(contextId);
            var assignedContext = contextId ?? "ctx-generated-1";
            var response = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["result"] = new JsonObject
                {
                    ["task"] = new JsonObject
                    {
                        ["id"] = $"task-{observedContexts.Count}",
                        ["contextId"] = assignedContext,
                        ["status"] = new JsonObject
                        {
                            ["state"] = "TASK_STATE_COMPLETED",
                            ["message"] = new JsonObject
                            {
                                ["role"] = "ROLE_AGENT",
                                ["messageId"] = $"msg-{observedContexts.Count}",
                                ["parts"] = new JsonArray { new JsonObject { ["text"] = $"reply:{assignedContext}" } },
                            },
                        },
                    },
                },
            };
            http.Response.ContentType = "application/json";
            await http.Response.WriteAsync(response.ToJsonString(), ct);
        });
        await app.StartAsync(ct);
        baseUrl = app.Urls.Single();

        var clock = new ManualClock();
        var client = new A2aRemoteClient(
            new Dictionary<string, A2aRemoteSettings> { ["peer"] = new() { Url = baseUrl } },
            cardCacheTtl: TimeSpan.FromMinutes(5),
            clock: clock);

        _ = await client.CardAsync("peer", ct);
        var turn1 = await client.SendAsync("peer", "hello", wait: true, timeoutSeconds: 10, contextId: null, ct);
        var turn2 = await client.SendAsync("peer", "again", wait: true, timeoutSeconds: 10, contextId: turn1.Task!.ContextId, ct);
        Assert.Equal(1, cardFetches);
        Assert.Equal("ctx-generated-1", turn2.Task!.ContextId);
        Assert.Equal([null, "ctx-generated-1"], observedContexts);

        clock.Advance(TimeSpan.FromMinutes(6));
        _ = await client.CardAsync("peer", ct);
        Assert.Equal(2, cardFetches);
    }

    [Fact]
    public async Task DshAgentHandler_InjectsIntoExistingLiveAndSuspendedSessions_WithExternalAgentTrust()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var llmServer = await MockLlmServer.StartAsync("live-reply", "woken-reply", "fresh-reply");
        var ctx = CreateLlmContext(llmServer.BaseUrl);
        var agents = ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
        var persistent = new TestPersistentManager(ctx);
        ctx.Provide(IPersistentAgentManager.ServiceName, persistent);
        var bus = new TestMessageBus(ctx, persistent);
        ctx.Provide(IAgentMessageBus.ServiceName, bus);

        var liveHandle = await agents.Create(new CreateAgentOptions(
            SessionId.Create("existing-live-session"),
            null,
            new AgentOptions("test-provider", "test-model")), ct);
        var suspendedProfile = await persistent.CreateAsync(new AgentProfileRegistration(
            "sleeper",
            AgentId: AgentId.Create("agent-suspended-1"),
            Options: new AgentOptions("test-provider", "test-model")), ct);

        using var handler = new DshAgentHandler(ctx, "test-provider", "test-model");
        using var host = new A2aHost(handler, new A2aHostOptions());
        host.Start();
        using var client = new A2AClient(new Uri(host.Endpoint));

        // 1. Inject into existing live session without stopping or recreating it.
        var r1 = await client.SendMessageAsync(new SendMessageRequest
        {
            Message = new A2AMessage
            {
                MessageId = Guid.NewGuid().ToString("N"),
                ContextId = liveHandle.Agent.Id.Value,
                Role = A2ARole.User,
                Parts = [A2APart.FromText("ping existing")],
            },
        }, ct);
        Assert.Equal(TaskState.Completed, r1.Task!.Status.State);
        Assert.Equal("live-reply", r1.Task.Status.Message!.Parts[0].Text);
        Assert.Same(liveHandle.Agent, agents.Get(liveHandle.Agent.Id));
        var firstEnvelope = bus.DeliveredEnvelopes[0];
        Assert.Equal(PrincipalKind.Agent, firstEnvelope.Sender.Kind);
        Assert.Equal(MessageTrustLevel.ExternalAgent, firstEnvelope.TrustLevel);
        Assert.Equal(AgentMessageIntent.Task, firstEnvelope.Intent);

        // 2. Inject into suspended persistent agent via AgentId contextId -> wakes and delivers.
        var r2 = await client.SendMessageAsync(new SendMessageRequest
        {
            Message = new A2AMessage
            {
                MessageId = Guid.NewGuid().ToString("N"),
                ContextId = suspendedProfile.AgentId.Value,
                Role = A2ARole.User,
                Parts = [A2APart.FromText("wake sleeper")],
            },
        }, ct);
        Assert.Equal(TaskState.Completed, r2.Task!.Status.State);
        Assert.Equal("woken-reply", r2.Task.Status.Message!.Parts[0].Text);

        // 3. Brand new contextId still creates a new session.
        var r3 = await client.SendMessageAsync(new SendMessageRequest
        {
            Message = new A2AMessage
            {
                MessageId = Guid.NewGuid().ToString("N"),
                ContextId = "brand-new-a2a-session",
                Role = A2ARole.User,
                Parts = [A2APart.FromText("hello new")],
            },
        }, ct);
        Assert.Equal(TaskState.Completed, r3.Task!.Status.State);
        Assert.Equal("fresh-reply", r3.Task.Status.Message!.Parts[0].Text);
        Assert.NotNull(agents.Get(SessionId.Create("brand-new-a2a-session")));
    }

    [Fact]
    public async Task Subagent_SupportsParentMessaging_PeerMessaging_AndRemoteTargetDispatch()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new Context();
        var sessions = new SessionStore(ctx);
        _ = new SystemPrompt(ctx, new SystemPromptConfig());
        var tools = new ToolRuntime(ctx);
        var llm = new LlmRuntime(ctx);
        var agents = new AgentRegistry(ctx);
        _ = new AgentLoop(ctx);
        var subagents = new SubagentRuntime(ctx);
        using var spawnReg = SubagentInProcessProviders.RegisterSpawn(ctx);
        using var controlReg = SubagentControlTools.Apply(ctx);
        using var subagentToolReg = SubagentTool.Apply(ctx, new SubagentToolConfig { Provider = "spawn" });

        llm.RegisterAdapter(["test-provider"], new ScriptedAdapter("test-provider",
        [
            _ => TextAnswer("parent-got-msg"),
            _ => TextAnswer("peer-got-msg"),
        ]));

        var router = new TestRouter();
        var remoteMock = new MockExecutionBackend(
            AgentBackendKind.Fleet,
            new RemoteExecutionTarget("fleet:worker-1", AgentBackendKind.Fleet, "worker-1"));
        using var routerBackend = router.RegisterBackend(remoteMock);
        ctx.Provide(IAgentExecutionRouter.ServiceName, router);

        var parentHandle = await agents.Create(new CreateAgentOptions(
            SessionId.Create("subagent-parent-1"),
            null,
            new AgentOptions("test-provider", "test-model")), ct);
        var peerHandle = await agents.Create(new CreateAgentOptions(
            SessionId.Create("subagent-peer-1"),
            null,
            new AgentOptions("test-provider", "test-model")), ct);

        var childSessionId = SessionId.Create("subagent-child-1");
        var childSession = sessions.Create(childSessionId, header: new SessionHeader
        {
            Version = SessionHeader.SessionFormatVersion,
            Id = childSessionId,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ParentSession = parentHandle.Agent.Id,
            RootSession = parentHandle.Agent.Id,
            Origin = "subagent",
            DelegationDepth = 1,
            IsSeeded = false,
            SubagentProvider = "spawn",
            SubagentMode = "one-shot",
        });
        var childAgent = new AgentLoopAgent(
            ctx.Root,
            childSessionId,
            new AgentOptions("test-provider", "test-model"),
            childSession,
            SessionRepair.LastTurnOf);

        // Fallback without IAgentMessageBus delivers directly to parent.
        var directParentResult = await ExecuteTool(tools, childAgent, SubagentControlTools.SendMessageToolName,
            new JsonObject { ["target"] = "parent", ["content"] = "direct hello parent" }, ct);
        var directSuccess = Assert.IsType<ToolExecutionResult.Success>(directParentResult);
        Assert.True(directSuccess.Value.GetProperty("delivered").GetBoolean());
        await parentHandle.Agent.WhenIdle();

        // Now attach IAgentMessageBus and test both target:"parent" and peer-to-peer messaging.
        var bus = new TestMessageBus(ctx);
        ctx.Provide(IAgentMessageBus.ServiceName, bus);

        var busParentResult = await ExecuteTool(tools, childAgent, SubagentControlTools.SendMessageToolName,
            new JsonObject { ["target"] = "parent", ["content"] = "bus hello parent" }, ct);
        Assert.True(Assert.IsType<ToolExecutionResult.Success>(busParentResult).Value.GetProperty("delivered").GetBoolean());

        var peerResult = await ExecuteTool(tools, childAgent, SubagentControlTools.SendMessageToolName,
            new JsonObject { ["target"] = peerHandle.Agent.Id.Value, ["content"] = "hello peer" }, ct);
        Assert.True(Assert.IsType<ToolExecutionResult.Success>(peerResult).Value.GetProperty("delivered").GetBoolean());
        await peerHandle.Agent.WhenIdle();

        Assert.Equal(2, bus.DeliveredEnvelopes.Count);
        Assert.Equal(parentHandle.Agent.AgentId, bus.DeliveredEnvelopes[0].TargetAgentId);
        Assert.Equal(peerHandle.Agent.AgentId, bus.DeliveredEnvelopes[1].TargetAgentId);
        Assert.Equal(MessageTrustLevel.TrustedPeerAgent, bus.DeliveredEnvelopes[1].TrustLevel);

        // Remote target via SubagentTool routes through IAgentExecutionRouter.
        var remoteDelegation = await ExecuteTool(tools, parentHandle.Agent, SubagentTool.DefaultToolName,
            new JsonObject
            {
                ["description"] = "remote worker task",
                ["prompt"] = "analyze remote logs",
                ["target"] = "fleet:worker-1",
            }, ct);
        var remoteSuccess = Assert.IsType<ToolExecutionResult.Success>(remoteDelegation);
        Assert.Equal("foreground", remoteSuccess.Value.GetProperty("kind").GetString());
        Assert.Contains("mock:analyze remote logs", remoteSuccess.Value.GetProperty("output")[0].GetProperty("text").GetString());
        Assert.Single(remoteMock.Dispatches);
    }

    [Fact]
    public async Task Workflow_HybridLocalAndRemoteDispatchInParallel()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new Context();
        _ = new SessionStore(ctx);
        _ = new SystemPrompt(ctx, new SystemPromptConfig());
        _ = new ToolRuntime(ctx);
        var llm = new LlmRuntime(ctx);
        _ = new AgentRegistry(ctx);
        _ = new AgentLoop(ctx);
        _ = ApprovalService.Register(ctx);
        _ = new SubagentRuntime(ctx);
        using var subprocess = new SubprocessService(ctx);
        using var spawn = SubagentInProcessProviders.RegisterSpawn(ctx);
        llm.RegisterAdapter(["test-provider"], new ScriptedAdapter("test-provider",
        [
            _ => TextAnswer("local-result"),
        ]));

        var router = new TestRouter();
        using var fleetBackendReg = router.RegisterBackend(new MockExecutionBackend(
            AgentBackendKind.Fleet,
            new RemoteExecutionTarget("fleet:gpu-node", AgentBackendKind.Fleet, "gpu-node")));
        using var a2aBackendReg = router.RegisterBackend(new MockExecutionBackend(
            AgentBackendKind.A2a,
            new RemoteExecutionTarget("a2a:reviewer", AgentBackendKind.A2a, "reviewer")));
        ctx.Provide(IAgentExecutionRouter.ServiceName, router);

        var workflow = new WorkerThreadWorkflowEngine(ctx, null);
        var parentHandle = await ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!.Create(new CreateAgentOptions(
            SessionId.Create("workflow-hybrid-parent"),
            null,
            new AgentOptions("test-provider", "test-model")), ct);

        var run = workflow.Start(new WorkflowStartRequest
        {
            Script = """
                var results = await parallel(new Func<Task<JsonNode?>>[]
                {
                    () => agent("run locally"),
                    () => agent("run fleet target", new JsonObject { ["target"] = "fleet:gpu-node" }),
                    () => agent("run a2a backend", new JsonObject { ["backend"] = "A2a" })
                });
                return results;
                """,
            Meta = new Dictionary<string, object?> { ["name"] = "hybrid-test", ["description"] = "hybrid local+remote" },
            Parent = parentHandle.Agent,
        });

        var result = await run.Result;
        Assert.Equal(WorkflowStopReason.Completed, result.StopReason);
        Assert.Equal(3, result.AgentsStarted);
        var items = Assert.IsAssignableFrom<List<object?>>(result.Value);
        Assert.Equal("local-result", items[0]);
        Assert.Equal("mock:run fleet target", items[1]);
        Assert.Equal("mock:run a2a backend", items[2]);
    }

    [Fact]
    public async Task SwarmBoard_ExplicitTeamBoardId_SharesAcrossIndependentRootAgents()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new Context();
        _ = new SessionStore(ctx);
        _ = new SystemPrompt(ctx, new SystemPromptConfig());
        var tools = new ToolRuntime(ctx);
        _ = new AgentRegistry(ctx);
        _ = new AgentLoop(ctx);
        var board = SwarmBoard.Register(ctx, new ManualClock());
        using var boardReg = BoardTools.Apply(ctx);
        tools.Register(new ToolDefinition
        {
            Name = "echo",
            Description = "echo",
            Parameters = new JsonObject(),
            Output = new ToolOutputDefinition(new JsonObject(), (_, v) => [new TextBlock(v.ToString())]),
            IsConcurrencySafe = _ => true,
            Execute = (_, _) => Task.FromResult<object?>(new { ok = true }),
        });

        var agents = ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
        var rootA = (await agents.Create(new CreateAgentOptions(
            SessionId.Create("team-root-a"), null, new AgentOptions("test-provider", "test-model")), ct)).Agent;
        var rootB = (await agents.Create(new CreateAgentOptions(
            SessionId.Create("team-root-b"), null, new AgentOptions("test-provider", "test-model")), ct)).Agent;

        // Default lineage boards stay isolated between independent roots.
        _ = Assert.IsType<ToolExecutionResult.Success>(await ExecuteTool(tools, rootA, BoardTools.PostToolName,
            new JsonObject { ["to"] = "ALL", ["type"] = "INFO", ["body"] = "private to rootA" }, ct));
        var defaultReadB = Assert.IsType<ToolExecutionResult.Success>(await ExecuteTool(tools, rootB, BoardTools.ReadToolName,
            new JsonObject(), ct));
        Assert.Empty(defaultReadB.Value.GetProperty("messages").EnumerateArray());

        // Explicit team board_id connects independent roots.
        var teamPost = Assert.IsType<ToolExecutionResult.Success>(await ExecuteTool(tools, rootA, BoardTools.PostToolName,
            new JsonObject
            {
                ["board_id"] = "team-alpha",
                ["to"] = rootB.Id.Value,
                ["type"] = "ASK",
                ["body"] = "can rootB verify parser?",
            }, ct));
        Assert.Equal(1, teamPost.Value.GetProperty("seq").GetInt64());

        var teamReadB = Assert.IsType<ToolExecutionResult.Success>(await ExecuteTool(tools, rootB, BoardTools.ReadToolName,
            new JsonObject { ["board_id"] = "team-alpha" }, ct));
        var msg = Assert.Single(teamReadB.Value.GetProperty("messages").EnumerateArray());
        Assert.Equal(rootA.Id.Value, msg.GetProperty("from").GetString());
        Assert.Equal(rootB.Id.Value, msg.GetProperty("to").GetString());
        Assert.Equal("can rootB verify parser?", msg.GetProperty("body").GetString());

        var participants = teamReadB.Value.GetProperty("participants").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains(rootA.Id.Value, participants);
        Assert.Contains(rootB.Id.Value, participants);

        // Claims on explicit team board detect cross-root conflicts.
        _ = Assert.IsType<ToolExecutionResult.Success>(await ExecuteTool(tools, rootA, BoardTools.ClaimToolName,
            new JsonObject
            {
                ["board_id"] = "team-alpha",
                ["path"] = "src/shared.cs",
                ["start_line"] = 10,
                ["end_line"] = 25,
                ["intent"] = "refactor",
            }, ct));
        var claimB = Assert.IsType<ToolExecutionResult.Success>(await ExecuteTool(tools, rootB, BoardTools.ClaimToolName,
            new JsonObject
            {
                ["board_id"] = "team-alpha",
                ["path"] = "src/shared.cs",
                ["start_line"] = 20,
                ["end_line"] = 30,
                ["intent"] = "fix bug",
            }, ct));
        Assert.Single(claimB.Value.GetProperty("conflicts").EnumerateArray());

        // Subsequent post on team-alpha triggers BoardNotifier for rootB.
        _ = Assert.IsType<ToolExecutionResult.Success>(await ExecuteTool(tools, rootA, BoardTools.PostToolName,
            new JsonObject
            {
                ["board_id"] = "team-alpha",
                ["to"] = "ALL",
                ["type"] = "INFO",
                ["body"] = "done with lines 10-25",
            }, ct));
        var echoNotice = Assert.IsType<ToolExecutionResult.Success>(await ExecuteTool(tools, rootB, "echo", new JsonObject(), ct));
        Assert.Contains(echoNotice.Content.OfType<TextBlock>(), block => block.Text.Contains("shared-board-notice"));
        _ = board;
    }

    private static Context CreateLlmContext(string llmBaseUrl)
    {
        var ctx = new Context();
        _ = new SessionStore(ctx);
        _ = new SystemPrompt(ctx, new SystemPromptConfig());
        _ = new ToolRuntime(ctx);
        var llm = new LlmRuntime(ctx);
        _ = new AgentRegistry(ctx);
        _ = new AgentLoop(ctx);
        var connection = new Dsh.Llm.DeepSeek.DeepSeekConnectionOptions(
            llmBaseUrl,
            "TEST_KEY",
            new Dsh.Llm.DeepSeek.RequestDefaults(),
            Dsh.Llm.DeepSeek.DeepSeekConnectionOptions.DefaultMaxTokens,
            Dsh.Llm.DeepSeek.DeepSeekConnectionOptions.DefaultContextWindowValue,
            [new ProviderModelSpec("test-model")],
            new Dsh.Llm.DeepSeek.DeepSeekModelListing([new Dsh.Llm.DeepSeek.DeepSeekListedModel("test-model")]),
            Dsh.Llm.DeepSeek.DeepSeekConnectionOptions.DefaultStreamIdleTimeoutMs,
            ResolvedRetryPolicy.Resolve(null, "test"));
        var adapter = new Dsh.Llm.DeepSeek.DeepSeekAdapter("test-provider", new Dsh.Llm.DeepSeek.DeepSeekAdapterOptions
        {
            Options = () => connection,
            ResolveCredential = (_, _) => Task.FromResult(new LlmCredential("sk-test", LlmCredentialScheme.Bearer)),
            ResolveUserId = () => "test-user",
        });
        llm.RegisterAdapter(["test-provider"], adapter);
        return ctx;
    }

    private static IReadOnlyList<StreamChunk> TextAnswer(string text) =>
    [
        new StreamChunk.BlockStart(0, "text"),
        new StreamChunk.TextDelta(0, text),
        new StreamChunk.BlockEnd(0, new TextBlock(text)),
        new StreamChunk.Finish(new FinishReason.Stop()),
    ];

    private static Task<ToolExecutionResult> ExecuteTool(
        ToolRuntime tools,
        IAgent agent,
        string toolName,
        JsonObject args,
        CancellationToken ct)
        => tools.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create($"call-{Guid.NewGuid():N}"),
            Name = toolName,
            Arguments = JsonDocument.Parse(args.ToJsonString()).RootElement,
            Agent = agent,
            Signal = ct,
        });
}
