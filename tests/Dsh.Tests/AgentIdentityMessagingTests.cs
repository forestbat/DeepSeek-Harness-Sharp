using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Agent;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Persistence;
using Dsh.Runtime;
using Dsh.Schedule;
using Dsh.Subprocess;
using Dsh.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Dsh.Tests;

public sealed class AgentIdentityMessagingTests : IDisposable
{
    private static readonly DateTimeOffset BaseTime = new(2026, 10, 15, 8, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan AsyncTimeout = TimeSpan.FromSeconds(10);

    private readonly string _root = TempTree.CreateDirectory("agent-identity-messaging");

    public void Dispose() => TempTree.Delete(_root);

    private sealed class MockDeepSeekServer : IAsyncDisposable
    {
        private readonly WebApplication _application;
        private readonly Func<string, IReadOnlyList<string>> _script;

        private MockDeepSeekServer(Func<string, IReadOnlyList<string>> script)
        {
            _script = script;
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            _application = builder.Build();
            _application.Run(Serve);
        }

        public static async Task<MockDeepSeekServer> StartAsync(Func<string, IReadOnlyList<string>> script)
        {
            var server = new MockDeepSeekServer(script);
            try
            {
                await server._application.StartAsync(TestContext.Current.CancellationToken);
                server.Port = new Uri(server._application.Urls.Single()).Port;
                return server;
            }
            catch
            {
                await server.DisposeAsync();
                throw;
            }
        }

        public int Port { get; private set; }
        public string BaseUrl => $"http://127.0.0.1:{Port}";
        public List<string> RequestBodies { get; } = [];

        private async Task Serve(HttpContext context)
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(context.RequestAborted);
            lock (RequestBodies)
                RequestBodies.Add(body);
            context.Response.ContentType = "text/event-stream";
            foreach (var line in _script(body))
            {
                var bytes = Encoding.UTF8.GetBytes($"{line}\n");
                await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
            }
        }

        public ValueTask DisposeAsync() => _application.DisposeAsync();
    }

    private sealed class RuntimeHarness : IDisposable
    {
        private readonly List<IDisposable> _disposables = [];

        public Context Ctx { get; } = new();
        public HarnessHome Home { get; }
        public DshPaths Paths { get; }
        public SessionStore Sessions { get; }
        public ToolRuntime Tools { get; }
        public AgentRegistry Agents { get; }
        public JsonlSessionPersistence SessionPersistence { get; }
        public JsonAgentProfileStore ProfileStore { get; }
        public JsonOfflineMailboxStore MailboxStore { get; }
        public PersistentAgentManager Manager { get; }
        public AgentMessageBus Bus { get; }
        public AgentExecutionRouter Router { get; }
        public string WorkspaceDir { get; }

        public RuntimeHarness(string root, string? llmBaseUrl = null)
        {
            Home = HarnessHome.Resolve(Path.Combine(root, "home"));
            WorkspaceDir = Path.Combine(root, "workspace");
            Directory.CreateDirectory(Home.Root);
            Directory.CreateDirectory(WorkspaceDir);
            Paths = DshPaths.Default(Home.Root);
            Ctx.SetOwn(DshPaths.PropName, Paths);
            Ctx.SetOwn("harnessOptions", new HarnessOptions(Home));

            Sessions = new SessionStore(Ctx);
            _ = new SessionProjectionRegistry(Ctx);
            _ = new SystemPrompt(Ctx, new SystemPromptConfig());
            Tools = new ToolRuntime(Ctx);
            var llm = new LlmRuntime(Ctx);
            Agents = new AgentRegistry(Ctx);

            SessionPersistence = new JsonlSessionPersistence(Paths.SessionsPath);
            Ctx.Provide(ISessionPersistence.ServiceName, SessionPersistence);
            _disposables.Add(SessionPersistence);
            _disposables.Add(Dsh.Persistence.Plugin.WirePersistence(Ctx, SessionPersistence));

            var agentsRoot = Paths.Path("agents");
            ProfileStore = new JsonAgentProfileStore(Ctx, agentsRoot);
            MailboxStore = new JsonOfflineMailboxStore(Ctx, agentsRoot);

            _ = new AgentLoop(Ctx, persistenceFor: _ => SessionPersistence);
            Manager = new PersistentAgentManager(Ctx);
            _disposables.Add(Manager);
            Bus = new AgentMessageBus(Ctx);
            Router = new AgentExecutionRouter(Ctx);
            _disposables.Add(AgentCoordinationTools.Register(Ctx));

            if (llmBaseUrl is not null)
            {
                var connection = new Dsh.Llm.DeepSeek.DeepSeekConnectionOptions(
                    llmBaseUrl,
                    "TEST_KEY",
                    new Dsh.Llm.DeepSeek.RequestDefaults(),
                    Dsh.Llm.DeepSeek.DeepSeekConnectionOptions.DefaultMaxTokens,
                    Dsh.Llm.DeepSeek.DeepSeekConnectionOptions.DefaultContextWindowValue,
                    [new ProviderModelSpec("test-model", SystemPromptUpdate: SystemPromptUpdateModes.InHistory)],
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
            }
        }

        public void Track(IDisposable disposable) => _disposables.Add(disposable);

        public void Dispose()
        {
            for (var index = _disposables.Count - 1; index >= 0; index--)
                _disposables[index].Dispose();
        }
    }

    private static string[] TextSse(string text) =>
    [
        """data: {"choices":[{"delta":{"role":"assistant","content":null,"reasoning_content":""}}]}""",
        "",
        $$$"""data: {"choices":[{"delta":{"content":{{{JsonSerializer.Serialize(text)}}}}}]}""",
        "",
        """data: {"choices":[{"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":2,"total_tokens":12}}""",
        "",
        "data: [DONE]",
        "",
    ];

    private static string[] ToolCallSse(string callId, string toolName, string argumentsJson) =>
    [
        """data: {"choices":[{"delta":{"role":"assistant","content":null,"reasoning_content":""}}]}""",
        "",
        $$$$"""data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":{{{{JsonSerializer.Serialize(callId)}}}},"type":"function","function":{"name":{{{{JsonSerializer.Serialize(toolName)}}}},"arguments":{{{{JsonSerializer.Serialize(argumentsJson)}}}}}}]}}]}""",
        "",
        """data: {"choices":[{"delta":{},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":10,"completion_tokens":4,"total_tokens":14}}""",
        "",
        "data: [DONE]",
        "",
    ];

    [Fact]
    public async Task MultiSessionPerAgentId_RoutesTargetedAndActiveSessions()
    {
        using var harness = new RuntimeHarness(_root);
        var ct = TestContext.Current.CancellationToken;

        var profile = await harness.Manager.CreateAsync(
            new AgentProfileRegistration("multi-session-agent", Cwd: harness.WorkspaceDir),
            ct);
        var first = await harness.Manager.WakeAsync(profile.AgentId, ct);
        var secondHandle = await harness.Agents.Create(
            new CreateAgentOptions(Cwd: harness.WorkspaceDir, AgentId: profile.AgentId),
            ct);
        var second = secondHandle.Agent;
        var updatedProfile = await harness.Manager.AttachSessionAsync(profile.AgentId, second.Id, ct);

        Assert.Equal([first.Id, second.Id], updatedProfile.SessionIds);
        Assert.Equal(second.Id, updatedProfile.ActiveSessionId);
        Assert.Equal(2, harness.Agents.ListByAgent(profile.AgentId).Count);
        Assert.NotNull(harness.Agents.GetByAgent(profile.AgentId));

        var sender = new MessagePrincipal(PrincipalKind.Agent, "caller");
        var targeted = await harness.Bus.SendAsync(
            new AgentEnvelope(
                "env-s1",
                profile.AgentId,
                first.Id,
                sender,
                MessageTrustLevel.TrustedPeerAgent,
                AgentMessageIntent.Task,
                "for session 1",
                WakeIfSuspended: false),
            ct);
        var active = await harness.Bus.SendAsync(
            new AgentEnvelope(
                "env-active",
                profile.AgentId,
                null,
                sender,
                MessageTrustLevel.TrustedPeerAgent,
                AgentMessageIntent.Task,
                "for active session",
                WakeIfSuspended: false),
            ct);

        Assert.Equal(AgentDeliveryStatus.DeliveredLive, targeted.Status);
        Assert.Equal(first.Id, targeted.DeliveredSessionId);
        Assert.Equal(AgentDeliveryStatus.DeliveredLive, active.Status);
        Assert.Equal(second.Id, active.DeliveredSessionId);
        Assert.Contains(first.Session.SnapshotEvents(), e => e.Data is AgentEnvelopePayload { Envelope.EnvelopeId: "env-s1" });
        Assert.Contains(second.Session.SnapshotEvents(), e => e.Data is AgentEnvelopePayload { Envelope.EnvelopeId: "env-active" });
    }

    [Fact]
    public async Task SuspendOfflineMailboxAndWakeFlush_PersistsAndResumesAcrossReload()
    {
        var agentId = AgentId.Create("agent-persistent-1");
        SessionId initialSessionId;
        {
            using var harness = new RuntimeHarness(_root);
            var ct = TestContext.Current.CancellationToken;
            var profile = await harness.Manager.CreateAsync(
                new AgentProfileRegistration("worker", AgentId: agentId, Cwd: harness.WorkspaceDir),
                ct);
            var live = await harness.Manager.WakeAsync(agentId, ct);
            initialSessionId = live.Id;

            live.Session.Append(new UserMessagePayload(MessageFactory.CreateUserText("seed history")), new SurfaceOp.Append());
            await harness.Sessions.Flush(live.Session);
            await harness.Manager.SuspendAsync(agentId, ct);

            Assert.Null(harness.Agents.Get(initialSessionId));
            Assert.Null(harness.Agents.GetByAgent(agentId));
            var suspended = harness.Manager.Get(agentId)!;
            Assert.Equal(AgentLifecycleState.Suspended, suspended.State);
            Assert.Equal(initialSessionId, suspended.ActiveSessionId);
            Assert.True(File.Exists(Path.Combine(harness.Paths.Path("agents"), "profiles.json")));

            var sender = new MessagePrincipal(PrincipalKind.Agent, "supervisor");
            var firstQueued = await harness.Bus.SendAsync(
                new AgentEnvelope(
                    "env-off-1",
                    agentId,
                    null,
                    sender,
                    MessageTrustLevel.TrustedPeerAgent,
                    AgentMessageIntent.Task,
                    "offline task 1",
                    IdempotencyKey: "k-1",
                    WakeIfSuspended: false),
                ct);
            var duplicate = await harness.Bus.SendAsync(
                new AgentEnvelope(
                    "env-off-1-dup",
                    agentId,
                    null,
                    sender,
                    MessageTrustLevel.TrustedPeerAgent,
                    AgentMessageIntent.Task,
                    "offline task 1 dup",
                    IdempotencyKey: "k-1",
                    WakeIfSuspended: false),
                ct);
            var secondQueued = await harness.Bus.SendAsync(
                new AgentEnvelope(
                    "env-off-2",
                    agentId,
                    null,
                    sender,
                    MessageTrustLevel.TrustedPeerAgent,
                    AgentMessageIntent.Notify,
                    "offline notice 2",
                    IdempotencyKey: "k-2",
                    WakeIfSuspended: false),
                ct);

            Assert.Equal(AgentDeliveryStatus.QueuedOffline, firstQueued.Status);
            Assert.Equal(AgentDeliveryStatus.Deduplicated, duplicate.Status);
            Assert.Equal(AgentDeliveryStatus.QueuedOffline, secondQueued.Status);
            Assert.Equal(2, harness.Bus.PeekOffline(agentId).Count);
        }

        // Reload from disk with fresh stores and manager, then wake and verify flush.
        {
            using var reloaded = new RuntimeHarness(_root);
            var ct = TestContext.Current.CancellationToken;
            var wokenEvents = new List<AgentRuntimeEvent>();
            using var sub = reloaded.Bus.Subscribe(
                new AgentEventSubscription("sub-woken", agentId, null, [AgentRuntimeEventKind.AgentWoken], WakeSubscriber: false),
                evt =>
                {
                    wokenEvents.Add(evt);
                    return ValueTask.CompletedTask;
                });

            Assert.Equal(2, reloaded.Bus.PeekOffline(agentId).Count);
            var duplicateAfterReload = await reloaded.Bus.SendAsync(
                new AgentEnvelope(
                    "env-off-after-reload",
                    agentId,
                    null,
                    new MessagePrincipal(PrincipalKind.Agent, "supervisor"),
                    MessageTrustLevel.TrustedPeerAgent,
                    AgentMessageIntent.Task,
                    "dup",
                    IdempotencyKey: "k-1",
                    WakeIfSuspended: false),
                ct);
            Assert.Equal(AgentDeliveryStatus.Deduplicated, duplicateAfterReload.Status);

            var woken = await reloaded.Manager.WakeAsync(agentId, ct);
            Assert.Equal(initialSessionId, woken.Id);
            Assert.Equal(agentId, woken.AgentId);
            Assert.Empty(reloaded.Bus.PeekOffline(agentId));
            Assert.Equal(AgentLifecycleState.Idle, reloaded.Manager.Get(agentId)!.State);
            Assert.Single(wokenEvents);

            var loopAgent = Assert.IsType<AgentLoopAgent>(woken);
            Assert.Equal(2, loopAgent.Inbox.NextTurn.Count);
            Assert.Contains(woken.Session.SnapshotEvents(), e => e.Data is UserMessagePayload { Message.Content: [TextBlock { Text: "seed history" }] });
            Assert.Contains(woken.Session.SnapshotEvents(), e => e.Data is AgentEnvelopePayload { Envelope.EnvelopeId: "env-off-1", Status: AgentDeliveryStatus.WokeAndDelivered });
            Assert.Contains(woken.Session.SnapshotEvents(), e => e.Data is AgentEnvelopePayload { Envelope.EnvelopeId: "env-off-2", Status: AgentDeliveryStatus.WokeAndDelivered });
        }
    }

    [Fact]
    public async Task LiveMidRunSteerAndFollowup_InjectsWithoutStoppingSession()
    {
        var callCount = 0;
        await using var server = await MockDeepSeekServer.StartAsync(_ =>
        {
            var current = Interlocked.Increment(ref callCount);
            return current switch
            {
                1 => ToolCallSse("call-hold", "hold_step", "{}"),
                2 => TextSse("steered ack"),
                _ => TextSse("followup ack"),
            };
        });

        using var harness = new RuntimeHarness(_root, server.BaseUrl);
        var ct = TestContext.Current.CancellationToken;
        var toolEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTool = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Track(harness.Tools.Register(new ToolDefinition
        {
            Name = "hold_step",
            Description = "Blocks mid-step until released by test.",
            Parameters = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            Output = new ToolOutputDefinition(
                new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["ok"] = new JsonObject { ["type"] = "boolean" } } },
                (_, value) => [new TextBlock(value.GetRawText())]),
            Execute = async (_, _) =>
            {
                toolEntered.TrySetResult();
                await releaseTool.Task.WaitAsync(AsyncTimeout, ct);
                return JsonSerializer.SerializeToElement(new { ok = true }, DshJson.Options);
            },
        }));

        var profile = await harness.Manager.CreateAsync(
            new AgentProfileRegistration(
                "live-steer-agent",
                Cwd: harness.WorkspaceDir,
                Options: new AgentOptions("test-provider", "test-model")),
            ct);
        var agent = await harness.Manager.WakeAsync(profile.AgentId, ct);
        var originalSessionId = agent.Id;

        var taskDelivery = await harness.Bus.SendAsync(
            new AgentEnvelope(
                "env-initial-task",
                profile.AgentId,
                null,
                new MessagePrincipal(PrincipalKind.Agent, "coord"),
                MessageTrustLevel.TrustedPeerAgent,
                AgentMessageIntent.Task,
                "start holding"),
            ct);
        Assert.Equal(AgentDeliveryStatus.DeliveredLive, taskDelivery.Status);

        await toolEntered.Task.WaitAsync(AsyncTimeout, ct);
        Assert.Equal(AgentStatus.Running, agent.Status);

        var steerDelivery = await harness.Bus.SendAsync(
            new AgentEnvelope(
                "env-steer-1",
                profile.AgentId,
                null,
                new MessagePrincipal(PrincipalKind.Agent, "coord"),
                MessageTrustLevel.TrustedPeerAgent,
                AgentMessageIntent.Steer,
                "steer mid-run instruction"),
            ct);
        Assert.Equal(AgentDeliveryStatus.DeliveredLive, steerDelivery.Status);
        Assert.Equal(originalSessionId, steerDelivery.DeliveredSessionId);

        releaseTool.TrySetResult();
        await agent.WhenIdle().WaitAsync(AsyncTimeout, ct);

        Assert.Same(agent, harness.Agents.Get(originalSessionId));
        Assert.Equal(2, callCount);
        Assert.Contains("steer mid-run instruction", server.RequestBodies[1]);

        var followupDelivery = await harness.Bus.SendAsync(
            new AgentEnvelope(
                "env-followup-1",
                profile.AgentId,
                null,
                new MessagePrincipal(PrincipalKind.Agent, "coord"),
                MessageTrustLevel.TrustedPeerAgent,
                AgentMessageIntent.Task,
                "followup task"),
            ct);
        Assert.Equal(AgentDeliveryStatus.DeliveredLive, followupDelivery.Status);
        await agent.WhenIdle().WaitAsync(AsyncTimeout, ct);

        Assert.Equal(3, callCount);
        Assert.Contains("followup task", server.RequestBodies[2]);
    }

    [Fact]
    public async Task TrustBoundary_RejectsExternalControlAndBlocksAutoApproveEscalation()
    {
        var outsideDir = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outsideDir);
        var ownerTarget = Path.Combine(outsideDir, "owner-ok.txt");
        var externalTarget = Path.Combine(_root, "workspace", "external-blocked.txt");

        var llmTurn = 0;
        await using var server = await MockDeepSeekServer.StartAsync(_ =>
        {
            var current = Interlocked.Increment(ref llmTurn);
            if (current == 1)
                return ToolCallSse("call-write-ext", WriteTool.ToolName, JsonSerializer.Serialize(new { file_path = externalTarget, content = "pwned" }));
            return TextSse("done");
        });

        using var harness = new RuntimeHarness(_root, server.BaseUrl);
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(harness.Home.SettingsFile, "safety:\n  autoApprove: true\n", ct);
        _ = new SafetyPolicySource(harness.Ctx, HarnessSettings.CreatePolicyReader(harness.Home, HarnessSettings.Load(harness.Home)));
        _ = ApprovalService.Register(harness.Ctx);
        harness.Track(WriteTool.Register(harness.Ctx));

        var targetProfile = await harness.Manager.CreateAsync(
            new AgentProfileRegistration(
                "guarded-agent",
                Cwd: harness.WorkspaceDir,
                Options: new AgentOptions("test-provider", "test-model")),
            ct);
        var agent = await harness.Manager.WakeAsync(targetProfile.AgentId, ct);

        // 1. ExternalAgent Control intent is rejected at the bus boundary.
        var externalSender = new MessagePrincipal(PrincipalKind.ExternalChannel, "ext-bot-1", "Untrusted Bot");
        var rejectedControl = await harness.Bus.SendAsync(
            new AgentEnvelope(
                "env-ctrl-ext",
                targetProfile.AgentId,
                null,
                externalSender,
                MessageTrustLevel.ExternalAgent,
                AgentMessageIntent.Control,
                "shutdown"),
            ct);
        Assert.Equal(AgentDeliveryStatus.Rejected, rejectedControl.Status);

        // 2. OwnerHuman with autoApprove: true can still write outside root without asking.
        agent.Session.Append(new TurnStartPayload(1));
        var ownerResult = await harness.Tools.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create("call-owner"),
            Name = WriteTool.ToolName,
            Arguments = JsonSerializer.SerializeToElement(new { file_path = ownerTarget, content = "ok" }),
            Agent = agent,
            Signal = ct,
        });
        agent.Session.Append(new TurnEndPayload(1, new TurnEndReason.Completed()));
        Assert.False(ownerResult.IsError);
        Assert.True(File.Exists(ownerTarget));

        // 3. ExternalAgent Task triggers real turn -> AutoApprove is suppressed, explicit approval is required (and denied).
        using var deny = ApprovalAnswerers.DenyAll(harness.Ctx);
        var observedEvents = new List<AgentRuntimeEvent>();
        using var sub = harness.Bus.Subscribe(
            new AgentEventSubscription("sub-events", targetProfile.AgentId, null, WakeSubscriber: false),
            evt =>
            {
                observedEvents.Add(evt);
                return ValueTask.CompletedTask;
            });

        var delivered = await harness.Bus.SendAsync(
            new AgentEnvelope(
                "env-task-ext",
                targetProfile.AgentId,
                null,
                externalSender,
                MessageTrustLevel.ExternalAgent,
                AgentMessageIntent.Task,
                $"write to {externalTarget}",
                CorrelationId: "corr-ext-1"),
            ct);
        Assert.Equal(AgentDeliveryStatus.DeliveredLive, delivered.Status);
        await agent.WhenIdle().WaitAsync(AsyncTimeout, ct);

        Assert.False(File.Exists(externalTarget));
        var asked = Assert.Single(agent.Session.SnapshotEvents().Select(e => e.Data).OfType<ApprovalAskedPayload>());
        Assert.Contains("[ExternalAgent: Untrusted Bot]", asked.Reason);
        Assert.Contains(
            agent.Session.SnapshotEvents().Select(e => e.Data).OfType<SafetyDecisionAuditPayload>(),
            audit => audit.LifecycleOwner == "ExternalAgent:ExternalChannel:ext-bot-1" && !audit.AutoApprove);
        Assert.Contains(observedEvents, evt => evt.Kind == AgentRuntimeEventKind.ApprovalRequested);
        Assert.Contains(observedEvents, evt => evt.Kind == AgentRuntimeEventKind.TurnCompleted && evt.CorrelationId == "corr-ext-1");
    }

    [Fact]
    public async Task EventSubscription_WakesSuspendedSupervisorAgent()
    {
        await using var server = await MockDeepSeekServer.StartAsync(_ => TextSse("worker finished"));
        using var harness = new RuntimeHarness(_root, server.BaseUrl);
        var ct = TestContext.Current.CancellationToken;

        var supervisorProfile = await harness.Manager.CreateAsync(
            new AgentProfileRegistration(
                "supervisor",
                Cwd: harness.WorkspaceDir,
                Options: new AgentOptions("test-provider", "test-model")),
            ct);
        var supervisor = await harness.Manager.WakeAsync(supervisorProfile.AgentId, ct);
        supervisor.Session.Append(new UserMessagePayload(MessageFactory.CreateUserText("supervisor booted")), new SurfaceOp.Append());
        await harness.Sessions.Flush(supervisor.Session);
        await harness.Manager.SuspendAsync(supervisorProfile.AgentId, ct);
        Assert.Null(harness.Agents.GetByAgent(supervisorProfile.AgentId));

        var workerProfile = await harness.Manager.CreateAsync(
            new AgentProfileRegistration(
                "worker",
                Cwd: harness.WorkspaceDir,
                Options: new AgentOptions("test-provider", "test-model")),
            ct);
        var worker = await harness.Manager.WakeAsync(workerProfile.AgentId, ct);

        using var sub = harness.Bus.Subscribe(
            new AgentEventSubscription(
                "sub-sup",
                workerProfile.AgentId,
                supervisorProfile.AgentId,
                [AgentRuntimeEventKind.TurnCompleted],
                WakeSubscriber: true));

        await harness.Bus.SendAsync(
            new AgentEnvelope(
                "env-work-1",
                workerProfile.AgentId,
                null,
                new MessagePrincipal(PrincipalKind.Agent, supervisorProfile.AgentId.Value),
                MessageTrustLevel.TrustedPeerAgent,
                AgentMessageIntent.Task,
                "do work",
                CorrelationId: "corr-work-1"),
            ct);

        await worker.WhenIdle().WaitAsync(AsyncTimeout, ct);

        var wokenSupervisor = harness.Agents.GetByAgent(supervisorProfile.AgentId);
        Assert.NotNull(wokenSupervisor);
        await wokenSupervisor.WhenIdle().WaitAsync(AsyncTimeout, ct);

        Assert.Equal(AgentLifecycleState.Idle, harness.Manager.Get(supervisorProfile.AgentId)!.State);
        Assert.Contains(
            wokenSupervisor.Session.SnapshotEvents().Select(e => e.Data).OfType<AgentEnvelopePayload>(),
            payload => payload.Envelope.Intent == AgentMessageIntent.Notify
                && payload.Envelope.Body.Contains("TurnCompleted", StringComparison.Ordinal)
                && payload.Status == AgentDeliveryStatus.WokeAndDelivered);
    }

    [Fact]
    public async Task ScheduleWakeIndex_WakesSuspendedPersistentAgentWhenDue()
    {
        using var harness = new RuntimeHarness(_root);
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider(BaseTime);
        using var schedules = new ScheduleService(harness.Ctx, time);

        var profile = await harness.Manager.CreateAsync(
            new AgentProfileRegistration("scheduled-agent", Cwd: harness.WorkspaceDir),
            ct);
        var agent = await harness.Manager.WakeAsync(profile.AgentId, ct);
        var sessionId = agent.Id;
        var created = schedules.Create(agent, "check status", "wake up and check", new ScheduleTiming.After(60));
        await harness.Sessions.Flush(agent.Session);

        await harness.Manager.SuspendAsync(profile.AgentId, ct);
        Assert.Null(harness.Agents.GetByAgent(profile.AgentId));

        var indexPath = Path.Combine(harness.Paths.Path("schedules"), "wake-index.json");
        Assert.True(File.Exists(indexPath));
        var reloadedIndex = new ScheduleWakeIndex(indexPath);
        var indexedEntry = Assert.Single(reloadedIndex.List());
        Assert.Equal(profile.AgentId, indexedEntry.AgentId);
        Assert.Equal(sessionId, indexedEntry.SessionId);

        time.Advance(TimeSpan.FromSeconds(60));

        // Wait briefly for the async index wake task to resume and dispatch the reminder.
        var deadline = DateTimeOffset.UtcNow + AsyncTimeout;
        IAgent? woken = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            woken = harness.Agents.GetByAgent(profile.AgentId);
            if (woken is not null && woken.Session.SnapshotEvents().Any(e => e.Data is UserMessagePayload))
                break;
            await Task.Delay(20, ct);
        }

        Assert.NotNull(woken);
        await woken.WhenIdle().WaitAsync(AsyncTimeout, ct);
        var reminder = Assert.Single(
            woken.Session.SnapshotEvents().Select(e => e.Data).OfType<UserMessagePayload>(),
            payload => payload.Message.Source is PluginMessageSource { Plugin: ScheduleService.MessagePlugin });
        Assert.Contains(created.Id, string.Concat(reminder.Message.Content.OfType<TextBlock>().Select(b => b.Text)));
        Assert.Empty(schedules.WakeIndex.List());
    }

    [Fact]
    public async Task ExecutionRouterAndCoordinationTools_DispatchAndValidateSchemas()
    {
        using var harness = new RuntimeHarness(_root);
        var ct = TestContext.Current.CancellationToken;

        var targetsResult = await ExecuteTool(harness, "agent_targets", "{}");
        var targets = targetsResult.GetProperty("targets").EnumerateArray().ToList();
        Assert.Contains(targets, target => target.GetProperty("targetId").GetString() == LocalAgentExecutionBackend.LocalTargetId);

        var spawned = await ExecuteTool(harness, "agent_spawn", """{"name":"tool-worker","wait_for_completion":false}""");
        var spawnedId = spawned.GetProperty("agentId").GetString()!;
        Assert.NotEmpty(spawnedId);

        var suspended = await ExecuteTool(harness, "agent_suspend", $$"""{"agent_id":{{JsonSerializer.Serialize(spawnedId)}}}""");
        Assert.True(suspended.GetProperty("suspended").GetBoolean());
        Assert.Null(harness.Agents.GetByAgent(AgentId.Create(spawnedId)));

        var delivered = await ExecuteTool(harness, "agent_message", $$"""{"target_agent_id":{{JsonSerializer.Serialize(spawnedId)}},"body":"wake via message","intent":"task","wake_if_suspended":true}""");
        Assert.Equal("WokeAndDelivered", delivered.GetProperty("status").GetString());

        var woken = await ExecuteTool(harness, "agent_wake", $$"""{"agent_id":{{JsonSerializer.Serialize(spawnedId)}}}""");
        Assert.Equal(spawnedId, woken.GetProperty("agentId").GetString());

        var subscribed = await ExecuteTool(harness, "agent_subscribe", $$"""{"publisher_agent_id":{{JsonSerializer.Serialize(spawnedId)}},"kinds":["TurnCompleted"]}""");
        Assert.Equal(spawnedId, subscribed.GetProperty("publisherAgentId").GetString());
    }

    private static async Task<JsonElement> ExecuteTool(RuntimeHarness harness, string name, string argumentsJson)
    {
        var result = await harness.Tools.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create($"call-{Guid.NewGuid():N}"),
            Name = name,
            Arguments = JsonDocument.Parse(argumentsJson).RootElement,
            RawArguments = argumentsJson,
            Signal = TestContext.Current.CancellationToken,
        });
        var success = Assert.IsType<ToolExecutionResult.Success>(result);
        return success.Value;
    }
}
