using System.Net;
using System.Text;
using System.Text.Json;
using Dsh.Agent;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Persistence;
using Dsh.Runtime;
using Dsh.Schedule;
using Dsh.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Dsh.Tests;

public sealed class ReviewRound1CoreAgentTests : IDisposable
{
    private static readonly DateTimeOffset BaseTime = new(2026, 10, 15, 8, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan AsyncTimeout = TimeSpan.FromSeconds(10);

    private readonly string _root = TempTree.CreateDirectory("review-r1-core-agent");

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

        private async Task Serve(HttpContext context)
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(context.RequestAborted);
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
            _ = new AgentExecutionRouter(Ctx);
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

    private sealed class StubAgent(SessionId id, AgentId agentId, Session session) : IAgent
    {
        public SessionId Id { get; } = id;
        public AgentId AgentId { get; } = agentId;
        public AgentOptions Options { get; } = new();
        public Session Session { get; } = session;
        public ScopeKey ScopeKey { get; } = new();
        public Context Ctx { get; } = new();
        public Inbox Inbox { get; } = new(session, new InboxNotifications());
        public AgentStatus Status => AgentStatus.Idle;
        public void Send(UserMessage message, string target, bool wakeup) => Inbox.Splice(target, int.MaxValue, 0, [message]);
        public void Followup(UserMessage message) => Send(message, InboxTargets.NextTurn, true);
        public void Steer(UserMessage message) => Send(message, InboxTargets.NextStep, true);
        public void Inject(UserMessage message) => Send(message, InboxTargets.NextStep, false);
        public void Cancel(AgentCancelCause cause, bool keepInbox = false) { }
        public Task<T> RunMaintenance<T>(Func<CancellationToken, Task<T>> job) => job(CancellationToken.None);
        public Task WhenIdle() => Task.CompletedTask;
    }

    private sealed class FlakyWakeManager(IAgent targetAgent, AgentProfile profile, int failuresBeforeSuccess)
        : IPersistentAgentManager
    {
        private int _attempts;

        public Task<AgentProfile> CreateAsync(AgentProfileRegistration registration, CancellationToken cancellationToken = default)
            => Task.FromResult(profile);

        public AgentProfile? Get(AgentId id) => id == profile.AgentId ? profile : null;

        public IReadOnlyList<AgentProfile> List() => [profile];

        public Task<IAgent> WakeAsync(AgentId id, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _attempts) <= failuresBeforeSuccess)
                throw new OperationCanceledException("simulated transient wake cancellation");
            return Task.FromResult(targetAgent);
        }

        public Task SuspendAsync(AgentId id, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<AgentProfile> AttachSessionAsync(AgentId id, SessionId sessionId, CancellationToken cancellationToken = default)
            => Task.FromResult(profile);

        public Task<bool> DeleteAsync(AgentId id, CancellationToken cancellationToken = default) => Task.FromResult(false);
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
    public async Task F9_QueuedExternalAttribution_PersistsAcrossRestartAndSuppressesAutoApprove()
    {
        var externalTarget = Path.Combine(_root, "workspace", "restored-external-blocked.txt");
        var llmTurn = 0;
        await using var server = await MockDeepSeekServer.StartAsync(_ =>
        {
            var current = Interlocked.Increment(ref llmTurn);
            if (current == 1)
                return ToolCallSse("call-write-restored", WriteTool.ToolName, JsonSerializer.Serialize(new { file_path = externalTarget, content = "pwned" }));
            return TextSse("done");
        });

        var agentId = AgentId.Create("agent-f9-restart");
        SessionId sessionId;
        {
            using var harness = new RuntimeHarness(_root);
            var ct = TestContext.Current.CancellationToken;
            await harness.Manager.CreateAsync(
                new AgentProfileRegistration(
                    "f9-agent",
                    AgentId: agentId,
                    Cwd: harness.WorkspaceDir,
                    Options: new AgentOptions("test-provider", "test-model")),
                ct);
            var live = await harness.Manager.WakeAsync(agentId, ct);
            sessionId = live.Id;

            var delivery = await harness.Bus.SendAsync(
                new AgentEnvelope(
                    "env-ext-queued",
                    agentId,
                    sessionId,
                    new MessagePrincipal(PrincipalKind.ExternalChannel, "ext-1", "External User"),
                    MessageTrustLevel.ExternalAgent,
                    AgentMessageIntent.Task,
                    $"write {externalTarget}",
                    CorrelationId: "corr-f9",
                    WakeIfSuspended: false),
                ct);
            Assert.Equal(AgentDeliveryStatus.DeliveredLive, delivery.Status);

            var loopAgent = Assert.IsType<AgentLoopAgent>(live);
            var queued = Assert.Single(loopAgent.Inbox.NextTurn);
            Assert.Equal(MessageTrustLevel.ExternalAgent, loopAgent.Inbox.PeekAttribution(queued.Id)?.TrustLevel);

            // Cancel with keepInbox: false should still preserve retained BackgroundResultNotice attributions when Clear keeps items.
            var notice = BackgroundResultNotice.Create("background notice");
            var noticeAttribution = new MessageAttribution(
                new MessagePrincipal(PrincipalKind.ExternalChannel, "ext-2", "External Notice"),
                MessageTrustLevel.ExternalAgent,
                AgentMessageIntent.Notify,
                "env-notice");
            loopAgent.Inbox.Append(
                InboxTargets.NextStep,
                notice,
                new Dictionary<MessageId, MessageAttribution> { [notice.Id] = noticeAttribution });
            loopAgent.Inbox.Clear(BackgroundResultNotice.IsNotice);
            Assert.Equal(MessageTrustLevel.ExternalAgent, loopAgent.Inbox.PeekAttribution(notice.Id)?.TrustLevel);
            loopAgent.Inbox.Remove(notice.Id);
            Assert.Null(loopAgent.Inbox.PeekAttribution(notice.Id));

            // Re-enqueue the external message cleanly before flushing and restarting.
            var delivery2 = await harness.Bus.SendAsync(
                new AgentEnvelope(
                    "env-ext-queued-2",
                    agentId,
                    sessionId,
                    new MessagePrincipal(PrincipalKind.ExternalChannel, "ext-1", "External User"),
                    MessageTrustLevel.ExternalAgent,
                    AgentMessageIntent.Task,
                    $"write {externalTarget}",
                    CorrelationId: "corr-f9-2",
                    WakeIfSuspended: false),
                ct);
            Assert.Equal(AgentDeliveryStatus.DeliveredLive, delivery2.Status);
            await harness.Sessions.Flush(live.Session);
        }

        // Restart runtime with autoApprove: true and verify restored pending message keeps ExternalAgent restriction.
        {
            using var restarted = new RuntimeHarness(_root, server.BaseUrl);
            var ct = TestContext.Current.CancellationToken;
            await File.WriteAllTextAsync(restarted.Home.SettingsFile, "safety:\n  autoApprove: true\n", ct);
            _ = new SafetyPolicySource(restarted.Ctx, HarnessSettings.CreatePolicyReader(restarted.Home, HarnessSettings.Load(restarted.Home)));
            _ = ApprovalService.Register(restarted.Ctx);
            restarted.Track(WriteTool.Register(restarted.Ctx));
            using var deny = ApprovalAnswerers.DenyAll(restarted.Ctx);

            var resumed = await restarted.Agents.Resume(new ResumeAgentOptions(sessionId, new AgentOptions("test-provider", "test-model"), AgentId: agentId), ct);
            var loopAgent = Assert.IsType<AgentLoopAgent>(resumed.Agent);
            var restoredMsg = Assert.Single(loopAgent.Inbox.NextTurn);
            Assert.Equal(MessageTrustLevel.ExternalAgent, loopAgent.Inbox.PeekAttribution(restoredMsg.Id)?.TrustLevel);

            // Trigger driver execution on the queued NextTurn item (which runs Turn 1 before any followup).
            loopAgent.Followup(MessageFactory.CreateUserText("after"));
            await loopAgent.WhenIdle().WaitAsync(AsyncTimeout, ct);

            Assert.False(File.Exists(externalTarget));
            var asked = Assert.Single(loopAgent.Session.SnapshotEvents().Select(e => e.Data).OfType<ApprovalAskedPayload>());
            Assert.Contains("[ExternalAgent: External User]", asked.Reason);
            Assert.Contains(
                loopAgent.Session.SnapshotEvents().Select(e => e.Data).OfType<SafetyDecisionAuditPayload>(),
                audit => audit.LifecycleOwner == "ExternalAgent:ExternalChannel:ext-1" && !audit.AutoApprove);
        }
    }

    [Fact]
    public async Task F14_ReciprocalSubscriptions_DoNotCascadeAndDefaultKindsExcludeDeliveryEvents()
    {
        using var harness = new RuntimeHarness(_root);
        var ct = TestContext.Current.CancellationToken;

        var profileA = await harness.Manager.CreateAsync(new AgentProfileRegistration("agent-a", Cwd: harness.WorkspaceDir), ct);
        var profileB = await harness.Manager.CreateAsync(new AgentProfileRegistration("agent-b", Cwd: harness.WorkspaceDir), ct);
        var agentA = await harness.Manager.WakeAsync(profileA.AgentId, ct);
        var agentB = await harness.Manager.WakeAsync(profileB.AgentId, ct);

        // 1. Default SubscribeTool kinds when wake_subscriber is true (or omitted) excludes MessageDelivered/AgentWoken.
        var profileC = await harness.Manager.CreateAsync(new AgentProfileRegistration("agent-c", Cwd: harness.WorkspaceDir), ct);
        var defaultSubResult = await ExecuteTool(
            harness,
            "agent_subscribe",
            $$"""{"publisher_agent_id":{{JsonSerializer.Serialize(profileC.AgentId.Value)}},"subscriber_agent_id":{{JsonSerializer.Serialize(profileB.AgentId.Value)}}}""");
        var defaultKinds = defaultSubResult.GetProperty("kinds").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(["TurnCompleted", "TurnFailed", "ApprovalRequested"], defaultKinds);

        // Explicit kinds array and wake_subscriber: false still behave as requested.
        var explicitSubResult = await ExecuteTool(
            harness,
            "agent_subscribe",
            $$"""{"publisher_agent_id":{{JsonSerializer.Serialize(profileC.AgentId.Value)}},"subscriber_agent_id":{{JsonSerializer.Serialize(profileB.AgentId.Value)}},"kinds":["MessageDelivered"],"wake_subscriber":true}""");
        Assert.Equal(["MessageDelivered"], explicitSubResult.GetProperty("kinds").EnumerateArray().Select(e => e.GetString()).ToList());

        var observeOnlyResult = await ExecuteTool(
            harness,
            "agent_subscribe",
            $$"""{"publisher_agent_id":{{JsonSerializer.Serialize(profileC.AgentId.Value)}},"wake_subscriber":false}""");
        Assert.False(observeOnlyResult.TryGetProperty("kinds", out _));

        // 2. Reciprocal wildcard subscriptions (Kinds = null, WakeSubscriber = true) must not loop infinitely.
        using var subAB = harness.Bus.Subscribe(new AgentEventSubscription("sub-ab", profileA.AgentId, profileB.AgentId, null, WakeSubscriber: true));
        using var subBA = harness.Bus.Subscribe(new AgentEventSubscription("sub-ba", profileB.AgentId, profileA.AgentId, null, WakeSubscriber: true));

        var delivery = await harness.Bus.SendAsync(
            new AgentEnvelope(
                "env-ping",
                profileA.AgentId,
                null,
                new MessagePrincipal(PrincipalKind.Human, "owner"),
                MessageTrustLevel.OwnerHuman,
                AgentMessageIntent.Notify,
                "ping",
                WakeIfSuspended: false),
            ct);
        Assert.Equal(AgentDeliveryStatus.DeliveredLive, delivery.Status);

        // Delivery to A triggers a single notification to B (no MessageDelivered cascade back to A).
        var envelopesB = agentB.Session.SnapshotEvents().Select(e => e.Data).OfType<AgentEnvelopePayload>().ToList();
        var envelopesA = agentA.Session.SnapshotEvents().Select(e => e.Data).OfType<AgentEnvelopePayload>().ToList();
        Assert.Single(envelopesB);
        Assert.Single(envelopesA);

        // Direct PublishEventAsync with a causal cycle A -> B -> A is also stopped by the AsyncLocal causal guard.
        await harness.Bus.PublishEventAsync(
            new AgentRuntimeEvent(
                "evt-cycle-1",
                profileA.AgentId,
                agentA.Id,
                AgentRuntimeEventKind.TurnCompleted,
                "done",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            ct);
        await agentB.WhenIdle().WaitAsync(AsyncTimeout, ct);
        await agentA.WhenIdle().WaitAsync(AsyncTimeout, ct);

        Assert.True(agentA.Session.SnapshotEvents().Count(e => e.Data is AgentEnvelopePayload) <= 3);
        Assert.True(agentB.Session.SnapshotEvents().Count(e => e.Data is AgentEnvelopePayload) <= 3);
    }

    [Fact]
    public async Task F15_InboxConcurrentSpliceAndClaim_PreservesValidReplayLog()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await MockDeepSeekServer.StartAsync(_ => TextSse("ok"));
        using var harness = new RuntimeHarness(_root, server.BaseUrl);
        var profile = await harness.Manager.CreateAsync(
            new AgentProfileRegistration(
                "f15-agent",
                Cwd: harness.WorkspaceDir,
                Options: new AgentOptions("test-provider", "test-model")),
            ct);
        var agent = Assert.IsType<AgentLoopAgent>(await harness.Manager.WakeAsync(profile.AgentId, ct));

        const int appendCount = 40;
        var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();

        var appender1 = Task.Run(() =>
        {
            for (var i = 0; i < appendCount; i++)
            {
                try
                {
                    agent.Followup(MessageFactory.CreateUserText($"t-{i}"));
                }
                catch (Exception ex)
                {
                    errors.Enqueue(ex);
                }
            }
        }, ct);
        var appender2 = Task.Run(() =>
        {
            for (var i = 0; i < appendCount; i++)
            {
                try
                {
                    agent.Steer(MessageFactory.CreateUserText($"s-{i}"));
                    _ = agent.Inbox.NextTurn;
                    _ = agent.Inbox.NextStep;
                    _ = agent.Inbox.HasPending;
                }
                catch (Exception ex)
                {
                    errors.Enqueue(ex);
                }
            }
        }, ct);

        await Task.WhenAll(appender1, appender2).WaitAsync(AsyncTimeout, ct);
        await agent.WhenIdle().WaitAsync(AsyncTimeout, ct);
        Assert.Empty(errors);

        // Fresh Inbox replaying the same session log must succeed with identical pending state.
        using var replayed = new Inbox(agent.Session, new InboxNotifications());
        var liveNextTurn = agent.Inbox.NextTurn.Select(m => m.Id).ToList();
        var replayedNextTurn = replayed.NextTurn.Select(m => m.Id).ToList();
        Assert.True(
            liveNextTurn.SequenceEqual(replayedNextTurn),
            $"NextTurn mismatch: live={liveNextTurn.Count} replayed={replayedNextTurn.Count} status={agent.Status} sessionSeq={agent.Session.Seq}");
        Assert.Equal(agent.Inbox.NextStep.Select(m => m.Id), replayed.NextStep.Select(m => m.Id));
        Assert.False(replayed.HasPending);

        // Also stress concurrent Inbox.Append / Splice / Clear without driver consumption, then verify replay.
        var rawSession = Session.Create(SessionId.Create(Guid.NewGuid().ToString("N")));
        using var rawInbox = new Inbox(rawSession, new InboxNotifications());
        var splicer1 = Task.Run(() =>
        {
            for (var i = 0; i < appendCount; i++)
                rawInbox.Append(InboxTargets.NextTurn, MessageFactory.CreateUserText($"raw-t-{i}"));
        }, ct);
        var splicer2 = Task.Run(() =>
        {
            for (var i = 0; i < appendCount; i++)
            {
                rawInbox.Append(InboxTargets.NextStep, MessageFactory.CreateUserText($"raw-s-{i}"));
                rawInbox.Splice(InboxTargets.NextStep, 0, 1, []);
            }
        }, ct);
        await Task.WhenAll(splicer1, splicer2).WaitAsync(AsyncTimeout, ct);

        using var replayedRaw = new Inbox(rawSession, new InboxNotifications());
        Assert.Equal(rawInbox.NextTurn.Select(m => m.Id), replayedRaw.NextTurn.Select(m => m.Id));
        Assert.Equal(rawInbox.NextStep.Select(m => m.Id), replayedRaw.NextStep.Select(m => m.Id));
        Assert.Equal(appendCount, replayedRaw.NextTurn.Count);
    }

    [Fact]
    public async Task F16_SelfSuspendAndSelfDelete_FailImmediatelyWithoutDeadlock()
    {
        using var harness = new RuntimeHarness(_root);
        var ct = TestContext.Current.CancellationToken;

        var profile = await harness.Manager.CreateAsync(new AgentProfileRegistration("self-guard", Cwd: harness.WorkspaceDir), ct);
        var agent = await harness.Manager.WakeAsync(profile.AgentId, ct);

        // 1. Direct PersistentAgentManager guard under Initiator context.
        await harness.Agents.WithInitiatorAsync<object?>(agent, async () =>
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Manager.SuspendAsync(profile.AgentId, ct))
                .WaitAsync(AsyncTimeout, ct);
            await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Manager.DeleteAsync(profile.AgentId, ct))
                .WaitAsync(AsyncTimeout, ct);
            return null;
        });

        // 2. AgentCoordinationTools agent_suspend rejects self-suspend cleanly.
        var selfToolResult = await harness.Tools.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create("call-self-suspend"),
            Name = "agent_suspend",
            Arguments = JsonDocument.Parse($$"""{"agent_id":{{JsonSerializer.Serialize(profile.AgentId.Value)}}}""").RootElement,
            Agent = agent,
            Signal = ct,
        }).WaitAsync(AsyncTimeout, ct);
        Assert.IsType<ToolExecutionResult.Failure>(selfToolResult);
        Assert.True(selfToolResult.IsError);

        // Lock was never stuck; an external caller can still suspend cleanly.
        await harness.Manager.SuspendAsync(profile.AgentId, ct).WaitAsync(AsyncTimeout, ct);
        Assert.Equal(AgentLifecycleState.Suspended, harness.Manager.Get(profile.AgentId)!.State);
    }

    [Fact]
    public async Task F17_PersistenceStores_CoordinateAcrossConcurrentInstancesWithoutOverwriting()
    {
        var agentsRoot = Path.Combine(_root, "shared-agents");
        var ctx1 = new Context();
        var ctx2 = new Context();
        var profiles1 = new JsonAgentProfileStore(ctx1, agentsRoot);
        var profiles2 = new JsonAgentProfileStore(ctx2, agentsRoot);

        var id1 = AgentId.Create("shared-agent-1");
        var id2 = AgentId.Create("shared-agent-2");
        profiles1.Save(new AgentProfile(id1, "one", null, AgentBackendKind.Local, null, [], AgentLifecycleState.Suspended, CreatedAt: 1, UpdatedAt: 1));
        profiles2.Save(new AgentProfile(id2, "two", null, AgentBackendKind.Local, null, [], AgentLifecycleState.Suspended, CreatedAt: 2, UpdatedAt: 2));

        Assert.NotNull(profiles1.Get(id2));
        Assert.NotNull(profiles2.Get(id1));
        Assert.Equal(2, profiles1.List().Count);
        Assert.Equal(2, profiles2.List().Count);

        var mailbox1 = new JsonOfflineMailboxStore(ctx1, agentsRoot);
        var mailbox2 = new JsonOfflineMailboxStore(ctx2, agentsRoot);
        var targetId = AgentId.Create("shared-mailbox-agent");
        var sender = new MessagePrincipal(PrincipalKind.Agent, "peer");

        Assert.True(mailbox1.Enqueue(new AgentEnvelope("env-1", targetId, null, sender, MessageTrustLevel.TrustedPeerAgent, AgentMessageIntent.Task, "m1")));
        Assert.True(mailbox2.Enqueue(new AgentEnvelope("env-2", targetId, null, sender, MessageTrustLevel.TrustedPeerAgent, AgentMessageIntent.Task, "m2")));

        var peek1 = mailbox1.Peek(targetId);
        var peek2 = mailbox2.Peek(targetId);
        Assert.Equal(["env-1", "env-2"], peek1.Select(e => e.EnvelopeId).ToArray());
        Assert.Equal(["env-1", "env-2"], peek2.Select(e => e.EnvelopeId).ToArray());
        Assert.True(mailbox1.Remove(targetId, "env-1"));
        Assert.Equal(["env-2"], mailbox2.Peek(targetId).Select(e => e.EnvelopeId).ToArray());
    }

    [Fact]
    public async Task F18_FlushOfflineAsync_CancelledMidBatch_KeepsUndeliveredMessagesOnDisk()
    {
        using var harness = new RuntimeHarness(_root);
        var ct = TestContext.Current.CancellationToken;

        var profile = await harness.Manager.CreateAsync(new AgentProfileRegistration("flush-cancel-agent", Cwd: harness.WorkspaceDir), ct);
        var agent = await harness.Manager.WakeAsync(profile.AgentId, ct);
        var sender = new MessagePrincipal(PrincipalKind.Agent, "coord");

        for (var i = 1; i <= 3; i++)
        {
            Assert.True(harness.MailboxStore.Enqueue(
                new AgentEnvelope($"env-batch-{i}", profile.AgentId, null, sender, MessageTrustLevel.TrustedPeerAgent, AgentMessageIntent.Notify, $"msg-{i}", WakeIfSuspended: false)));
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var deliveredEvents = 0;
        using var sub = harness.Bus.Subscribe(
            new AgentEventSubscription("sub-cancel-mid", profile.AgentId, null, [AgentRuntimeEventKind.MessageDelivered], WakeSubscriber: false),
            _ =>
            {
                if (Interlocked.Increment(ref deliveredEvents) == 1)
                    cts.Cancel();
                return ValueTask.CompletedTask;
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Bus.FlushOfflineAsync(profile.AgentId, cts.Token));

        var remaining = harness.Bus.PeekOffline(profile.AgentId).Select(e => e.EnvelopeId).ToArray();
        Assert.Equal(["env-batch-2", "env-batch-3"], remaining);

        var flushed = await harness.Bus.FlushOfflineAsync(profile.AgentId, ct);
        Assert.Equal(2, flushed);
        Assert.Empty(harness.Bus.PeekOffline(profile.AgentId));

        var deliveredIds = agent.Session.SnapshotEvents()
            .Select(e => e.Data)
            .OfType<AgentEnvelopePayload>()
            .Select(p => p.Envelope.EnvelopeId)
            .ToArray();
        Assert.Equal(["env-batch-1", "env-batch-2", "env-batch-3"], deliveredIds);
    }

    [Fact]
    public async Task F24_SendAsync_FailedWake_DoesNotPoisonIdempotencyRetry()
    {
        var ctx = new Context();
        var agents = new AgentRegistry(ctx);
        var agentId = AgentId.Create("agent-flaky-wake");
        var sessionId = SessionId.Create(Guid.NewGuid().ToString("N"));
        var stub = new StubAgent(sessionId, agentId, Session.Create(sessionId));
        var profile = new AgentProfile(agentId, "flaky", null, AgentBackendKind.Local, sessionId, [sessionId], AgentLifecycleState.Suspended);
        var manager = new FlakyWakeManager(stub, profile, failuresBeforeSuccess: 1);
        ctx.Provide(IPersistentAgentManager.ServiceName, manager);
        var bus = new AgentMessageBus(ctx);

        var envelope = new AgentEnvelope(
            "env-retry-1",
            agentId,
            null,
            new MessagePrincipal(PrincipalKind.ExternalChannel, "gh", "GitHub"),
            MessageTrustLevel.ExternalAgent,
            AgentMessageIntent.Task,
            "retry me",
            IdempotencyKey: "gh-delivery-42",
            WakeIfSuspended: true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bus.SendAsync(envelope, TestContext.Current.CancellationToken));

        var retryResult = await bus.SendAsync(envelope, TestContext.Current.CancellationToken);
        Assert.Equal(AgentDeliveryStatus.WokeAndDelivered, retryResult.Status);
        Assert.Equal(sessionId, retryResult.DeliveredSessionId);

        var dupResult = await bus.SendAsync(envelope, TestContext.Current.CancellationToken);
        Assert.Equal(AgentDeliveryStatus.Deduplicated, dupResult.Status);
    }

    [Fact]
    public async Task F22_ScheduleWakeIndex_WakesExactDueSessionWhenAnotherSessionIsActive()
    {
        using var harness = new RuntimeHarness(_root);
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider(BaseTime);
        using var schedules = new ScheduleService(harness.Ctx, time);

        var profile = await harness.Manager.CreateAsync(new AgentProfileRegistration("multi-session-scheduled", Cwd: harness.WorkspaceDir), ct);
        var sessionAHandle = await harness.Manager.WakeAsync(profile.AgentId, ct);
        var sessionAId = sessionAHandle.Id;
        var reminder = schedules.Create(sessionAHandle, "session A reminder", "check session A", new ScheduleTiming.After(60));
        await harness.Sessions.Flush(sessionAHandle.Session);
        await harness.Manager.SuspendAsync(profile.AgentId, ct);

        // Create and keep session B live as ActiveSessionId while session A stays suspended with the pending reminder.
        var sessionBHandle = await harness.Agents.Create(new CreateAgentOptions(Cwd: harness.WorkspaceDir, AgentId: profile.AgentId), ct);
        var sessionB = sessionBHandle.Agent;
        await harness.Manager.AttachSessionAsync(profile.AgentId, sessionB.Id, ct);
        Assert.Equal(sessionB.Id, harness.Manager.Get(profile.AgentId)!.ActiveSessionId);

        time.Advance(TimeSpan.FromSeconds(60));

        var deadline = DateTimeOffset.UtcNow + AsyncTimeout;
        IAgent? wokenA = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            wokenA = harness.Agents.Get(sessionAId);
            if (wokenA is not null && wokenA.Session.SnapshotEvents().Any(e => e.Data is UserMessagePayload))
                break;
            await Task.Delay(20, ct);
        }

        Assert.NotNull(wokenA);
        await wokenA.WhenIdle().WaitAsync(AsyncTimeout, ct);

        var reminderPayload = Assert.Single(
            wokenA.Session.SnapshotEvents().Select(e => e.Data).OfType<UserMessagePayload>(),
            payload => payload.Message.Source is PluginMessageSource { Plugin: ScheduleService.MessagePlugin });
        Assert.Contains(reminder.Id, string.Concat(reminderPayload.Message.Content.OfType<TextBlock>().Select(b => b.Text)));
        Assert.DoesNotContain(sessionB.Session.SnapshotEvents(), e => e.Data is UserMessagePayload);
        Assert.Equal(sessionB.Id, harness.Manager.Get(profile.AgentId)!.ActiveSessionId);
        Assert.Empty(schedules.WakeIndex.List());
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
