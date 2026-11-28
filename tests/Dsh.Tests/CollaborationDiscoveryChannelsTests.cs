using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Agent;
using Dsh.Channels;
using Dsh.Collab;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Runtime;
using Microsoft.Extensions.Time.Testing;

namespace Dsh.Tests;

public sealed class CollaborationDiscoveryChannelsTests
{
    private sealed class TempHome : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"dsh-collab-test-{Guid.NewGuid():N}");

        public TempHome() => Directory.CreateDirectory(Root);

        public Context CreateContext()
        {
            var ctx = new Context();
            ctx.SetOwn("dshHomePath", Root);
            return ctx;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // Ignore temp cleanup races.
            }
        }
    }

    private sealed class TestAgentDirectoryStore : IAgentDirectoryStore
    {
        private readonly Dictionary<AgentId, AgentProfile> _profiles = [];

        public TestAgentDirectoryStore(Context ctx) => ctx.Provide(IAgentDirectoryStore.ServiceName, this);

        public AgentProfile? Get(AgentId id) => _profiles.GetValueOrDefault(id);

        public IReadOnlyList<AgentProfile> List() => _profiles.Values.ToList();

        public void Save(AgentProfile profile) => _profiles[profile.AgentId] = profile;

        public bool Delete(AgentId id) => _profiles.Remove(id);
    }

    private sealed class TestPersistentAgentManager : IPersistentAgentManager
    {
        private readonly IAgentDirectoryStore _directory;

        public List<AgentId> WokenAgents { get; } = [];

        public TestPersistentAgentManager(Context ctx, IAgentDirectoryStore directory)
        {
            _directory = directory;
            ctx.Provide(IPersistentAgentManager.ServiceName, this);
        }

        public Task<AgentProfile> CreateAsync(AgentProfileRegistration registration, CancellationToken cancellationToken = default)
        {
            var agentId = registration.AgentId ?? AgentId.New();
            var profile = new AgentProfile(
                agentId,
                registration.Name,
                registration.Description,
                registration.BackendKind,
                registration.InitialSessionId,
                registration.InitialSessionId is { } sid ? [sid] : [],
                AgentLifecycleState.Suspended,
                registration.TargetMachine,
                registration.TargetUri,
                registration.Cwd,
                registration.Options,
                registration.AgentPreset,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                registration.Capabilities,
                registration.Metadata);
            _directory.Save(profile);
            return Task.FromResult(profile);
        }

        public AgentProfile? Get(AgentId id) => _directory.Get(id);

        public IReadOnlyList<AgentProfile> List() => _directory.List();

        public Task<IAgent> WakeAsync(AgentId id, CancellationToken cancellationToken = default)
        {
            if (_directory.Get(id) is { } profile)
            {
                WokenAgents.Add(id);
                _directory.Save(profile with { State = AgentLifecycleState.Idle });
            }
            return Task.FromResult<IAgent>(null!);
        }

        public Task SuspendAsync(AgentId id, CancellationToken cancellationToken = default)
        {
            if (_directory.Get(id) is { } profile)
                _directory.Save(profile with { State = AgentLifecycleState.Suspended });
            return Task.CompletedTask;
        }

        public Task<AgentProfile> AttachSessionAsync(AgentId id, SessionId sessionId, CancellationToken cancellationToken = default)
            => Task.FromResult(_directory.Get(id)!);

        public Task<bool> DeleteAsync(AgentId id, CancellationToken cancellationToken = default)
            => Task.FromResult(_directory.Delete(id));
    }

    private sealed class TestAgentMessageBus : IAgentMessageBus
    {
        private readonly IPersistentAgentManager? _agents;
        private readonly Lock _gate = new();
        private readonly List<(AgentEventSubscription Sub, Func<AgentRuntimeEvent, ValueTask>? Handler)> _subscriptions = [];

        public ConcurrentQueue<AgentEnvelope> SentEnvelopes { get; } = new();

        public TestAgentMessageBus(Context ctx, IPersistentAgentManager? agents = null)
        {
            _agents = agents;
            ctx.Provide(IAgentMessageBus.ServiceName, this);
        }

        public async Task<AgentDeliveryResult> SendAsync(AgentEnvelope envelope, CancellationToken cancellationToken = default)
        {
            SentEnvelopes.Enqueue(envelope);
            var status = AgentDeliveryStatus.DeliveredLive;
            if (_agents?.Get(envelope.TargetAgentId) is { State: AgentLifecycleState.Suspended } && envelope.WakeIfSuspended)
            {
                await _agents.WakeAsync(envelope.TargetAgentId, cancellationToken).ConfigureAwait(false);
                status = AgentDeliveryStatus.WokeAndDelivered;
            }
            return new AgentDeliveryResult(envelope.EnvelopeId, status, envelope.TargetAgentId);
        }

        public Task<int> FlushOfflineAsync(AgentId agentId, CancellationToken cancellationToken = default)
            => Task.FromResult(0);

        public IReadOnlyList<AgentEnvelope> PeekOffline(AgentId agentId) => [];

        public IDisposable Subscribe(AgentEventSubscription subscription, Func<AgentRuntimeEvent, ValueTask>? handler = null)
        {
            var entry = (subscription, handler);
            lock (_gate)
                _subscriptions.Add(entry);
            return new Unsubscribe(() =>
            {
                lock (_gate)
                    _subscriptions.Remove(entry);
            });
        }

        public IReadOnlyList<AgentEventSubscription> ListSubscriptions(AgentId? publisher = null)
        {
            lock (_gate)
                return _subscriptions.Select(s => s.Sub).Where(s => publisher is null || s.PublisherAgentId == publisher).ToList();
        }

        public async Task PublishEventAsync(AgentRuntimeEvent runtimeEvent, CancellationToken cancellationToken = default)
        {
            List<(AgentEventSubscription Sub, Func<AgentRuntimeEvent, ValueTask>? Handler)> snapshot;
            lock (_gate)
                snapshot = [.. _subscriptions];

            foreach (var (sub, handler) in snapshot)
            {
                if (handler is null)
                    continue;
                if (!string.IsNullOrEmpty(sub.PublisherAgentId.Value) && sub.PublisherAgentId != runtimeEvent.AgentId)
                    continue;
                if (sub.Kinds is { Count: > 0 } && !sub.Kinds.Contains(runtimeEvent.Kind))
                    continue;
                await handler(runtimeEvent).ConfigureAwait(false);
            }
        }

        private sealed class Unsubscribe(Action remove) : IDisposable
        {
            public void Dispose() => remove();
        }
    }

    private sealed class TestDiscoveryProvider(string name, IReadOnlyList<DiscoveredAgentCard> cards) : IAgentDiscoveryProvider
    {
        public string Name => name;

        public Task<IReadOnlyList<DiscoveredAgentCard>> DiscoverAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(cards);
    }

    private sealed class LoopbackWebhookCapture : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptTask;

        public ConcurrentQueue<(string Path, string? Authorization, string Body)> Received { get; } = new();
        public int Port { get; }
        public string UrlFor(string path) => $"http://127.0.0.1:{Port}{path}";

        public LoopbackWebhookCapture()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptTask = AcceptAsync();
        }

        private async Task AcceptAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                }
                catch
                {
                    break;
                }
                _ = HandleAsync(client);
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            await using (var stream = client.GetStream())
            {
                var buffer = new byte[16 * 1024];
                var total = 0;
                while (total < buffer.Length)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(total), _cts.Token).ConfigureAwait(false);
                    if (read == 0)
                        break;
                    total += read;
                    var text = Encoding.UTF8.GetString(buffer, 0, total);
                    var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (headerEnd < 0)
                        continue;

                    var headerBlock = text[..headerEnd];
                    var lines = headerBlock.Split("\r\n");
                    var path = lines[0].Split(' ', 3)[1];
                    string? auth = null;
                    var contentLength = 0;
                    foreach (var line in lines.Skip(1))
                    {
                        var idx = line.IndexOf(':');
                        if (idx <= 0)
                            continue;
                        var key = line[..idx].Trim();
                        var val = line[(idx + 1)..].Trim();
                        if (string.Equals(key, "Authorization", StringComparison.OrdinalIgnoreCase))
                            auth = val;
                        else if (string.Equals(key, "Content-Length", StringComparison.OrdinalIgnoreCase))
                            int.TryParse(val, out contentLength);
                    }

                    var headerByteCount = Encoding.ASCII.GetByteCount(headerBlock) + 4;
                    while (total < headerByteCount + contentLength)
                    {
                        var more = await stream.ReadAsync(buffer.AsMemory(total), _cts.Token).ConfigureAwait(false);
                        if (more == 0)
                            break;
                        total += more;
                    }
                    var body = Encoding.UTF8.GetString(buffer, headerByteCount, Math.Min(contentLength, total - headerByteCount));
                    Received.Enqueue((path, auth, body));
                    var ok = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}");
                    await stream.WriteAsync(ok, _cts.Token).ConfigureAwait(false);
                    break;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            _listener.Stop();
            try
            {
                await _acceptTask.ConfigureAwait(false);
            }
            catch
            {
                // Ignore shutdown cancellation.
            }
            _cts.Dispose();
        }
    }

    [Fact]
    public async Task Rooms_PersistAndEnforceRoleMatrixAndInviteConstraints()
    {
        using var home = new TempHome();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 28, 12, 0, 0, TimeSpan.Zero));
        var ctx = home.CreateContext();
        using var rooms = new CollaborationRoomService(ctx, clock);

        var owner = new MessagePrincipal(PrincipalKind.Human, "owner-1", "Owner");
        var approver = new MessagePrincipal(PrincipalKind.RemoteUser, "approver-1", "Approver");
        var collab = new MessagePrincipal(PrincipalKind.RemoteUser, "collab-1", "Collaborator");
        var observer = new MessagePrincipal(PrincipalKind.RemoteUser, "observer-1", "Observer");

        var room = await rooms.CreateRoomAsync(
            "Incident War Room",
            owner,
            new RoomPolicy(AllowCollaboratorPrompts: true, AllowCollaboratorApprovals: false, AllowObserverRead: true),
            TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(home.Root, "collab", "rooms.json")));

        var invApprover = await rooms.CreateInviteAsync(room.Id, owner, RoomRole.Approver, TimeSpan.FromMinutes(10), maxUses: 1, TestContext.Current.CancellationToken);
        var invCollab = await rooms.CreateInviteAsync(room.Id, owner, RoomRole.Collaborator, TimeSpan.FromMinutes(10), maxUses: 1, TestContext.Current.CancellationToken);
        var invObserver = await rooms.CreateInviteAsync(room.Id, owner, RoomRole.Observer, TimeSpan.FromMinutes(10), maxUses: 1, TestContext.Current.CancellationToken);
        var invExpired = await rooms.CreateInviteAsync(room.Id, owner, RoomRole.Collaborator, TimeSpan.FromSeconds(5), maxUses: 2, TestContext.Current.CancellationToken);
        var invRevoked = await rooms.CreateInviteAsync(room.Id, owner, RoomRole.Collaborator, TimeSpan.FromMinutes(10), maxUses: 2, TestContext.Current.CancellationToken);

        Assert.NotEqual(invApprover.Token, invCollab.Token);

        await rooms.RedeemInviteAsync(invApprover.Token, approver, cancellationToken: TestContext.Current.CancellationToken);
        await rooms.RedeemInviteAsync(invCollab.Token, collab, cancellationToken: TestContext.Current.CancellationToken);
        await rooms.RedeemInviteAsync(invObserver.Token, observer, cancellationToken: TestContext.Current.CancellationToken);

        // MaxUses = 1 enforced.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rooms.RedeemInviteAsync(invCollab.Token, new MessagePrincipal(PrincipalKind.RemoteUser, "extra"), cancellationToken: TestContext.Current.CancellationToken));

        // Revocation enforced.
        Assert.True(await rooms.RevokeInviteAsync(room.Id, invRevoked.InviteId, owner, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rooms.RedeemInviteAsync(invRevoked.Token, new MessagePrincipal(PrincipalKind.RemoteUser, "extra"), cancellationToken: TestContext.Current.CancellationToken));

        // TTL expiry enforced.
        clock.Advance(TimeSpan.FromSeconds(6));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rooms.RedeemInviteAsync(invExpired.Token, new MessagePrincipal(PrincipalKind.RemoteUser, "extra"), cancellationToken: TestContext.Current.CancellationToken));

        // Role matrix verification.
        Assert.True(rooms.CanPerform(room.Id, owner.Id, RoomActions.ManageMembers));
        Assert.True(rooms.CanPerform(room.Id, owner.Id, RoomActions.Invite));
        Assert.True(rooms.CanPerform(room.Id, approver.Id, RoomActions.Approve));
        Assert.False(rooms.CanPerform(room.Id, approver.Id, RoomActions.Invite));
        Assert.True(rooms.CanPerform(room.Id, collab.Id, RoomActions.Post));
        Assert.True(rooms.CanPerform(room.Id, collab.Id, RoomActions.PromptAgent));
        Assert.False(rooms.CanPerform(room.Id, collab.Id, RoomActions.Approve));
        Assert.True(rooms.CanPerform(room.Id, observer.Id, RoomActions.Read));
        Assert.False(rooms.CanPerform(room.Id, observer.Id, RoomActions.Post));
        Assert.False(rooms.CanPerform(room.Id, observer.Id, RoomActions.PromptAgent));
        Assert.False(rooms.CanPerform(room.Id, observer.Id, RoomActions.Approve));

        // Observer denied write/invite/manage-members at operation level too.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            rooms.PostMessageAsync(room.Id, observer, "should be denied", cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            rooms.CreateInviteAsync(room.Id, observer, RoomRole.Observer, cancellationToken: TestContext.Current.CancellationToken));

        await rooms.PostMessageAsync(room.Id, collab, "Investigating issue", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(File.Exists(Path.Combine(home.Root, "collab", "messages", $"{room.Id.Value}.json")));

        // Verify persistence reload across service restart.
        var secondCtx = home.CreateContext();
        using var reloadedRooms = new CollaborationRoomService(secondCtx, clock);
        var loadedRoom = reloadedRooms.GetRoom(room.Id);
        Assert.NotNull(loadedRoom);
        Assert.Equal(4, loadedRoom.Participants.Count);
        var loadedMessages = reloadedRooms.ReadMessages(room.Id);
        Assert.Single(loadedMessages);
        Assert.Equal("Investigating issue", loadedMessages[0].Body);
    }

    [Fact]
    public async Task Rooms_RouteMentionedAgentMessagesThroughBusAndProjectReply()
    {
        using var home = new TempHome();
        var ctx = home.CreateContext();
        var directory = new TestAgentDirectoryStore(ctx);
        var persistent = new TestPersistentAgentManager(ctx, directory);
        var bus = new TestAgentMessageBus(ctx, persistent);
        using var rooms = new CollaborationRoomService(ctx);

        var coderId = AgentId.Create("agent-coder");
        await persistent.CreateAsync(new AgentProfileRegistration("Coder", AgentId: coderId), TestContext.Current.CancellationToken);

        var owner = new MessagePrincipal(PrincipalKind.Human, "owner-1", "Owner");
        var collab = new MessagePrincipal(PrincipalKind.RemoteUser, "collab-1", "Collaborator");
        var room = await rooms.CreateRoomAsync("Code Review", owner, cancellationToken: TestContext.Current.CancellationToken);
        var invite = await rooms.CreateInviteAsync(room.Id, owner, RoomRole.Collaborator, cancellationToken: TestContext.Current.CancellationToken);
        await rooms.RedeemInviteAsync(invite.Token, collab, cancellationToken: TestContext.Current.CancellationToken);
        await rooms.AddAgentAsync(room.Id, owner, coderId, "Coder", TestContext.Current.CancellationToken);

        // Unmentioned message does not trigger agent when RequireExplicitAgentMention=true.
        await rooms.PostMessageAsync(room.Id, owner, "Just a human side note", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(bus.SentEnvelopes);

        // @mention by Owner -> OwnerHuman trust, wakes suspended agent.
        var ownerPrompt = await rooms.PostMessageAsync(room.Id, owner, "@Coder please audit PR #42", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(bus.SentEnvelopes.TryDequeue(out var ownerEnvelope));
        Assert.Equal(coderId, ownerEnvelope.TargetAgentId);
        Assert.Equal(MessageTrustLevel.OwnerHuman, ownerEnvelope.TrustLevel);
        Assert.Contains(coderId, persistent.WokenAgents);

        // @mention by Collaborator -> DelegatedHuman trust.
        await rooms.PostMessageAsync(room.Id, collab, "@agent-coder run unit tests", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(bus.SentEnvelopes.TryDequeue(out var collabEnvelope));
        Assert.Equal(MessageTrustLevel.DelegatedHuman, collabEnvelope.TrustLevel);

        // Simulate agent turn completion -> reply projected back as RoomMessage without leaking private logs.
        await bus.PublishEventAsync(new AgentRuntimeEvent(
            "evt-1",
            coderId,
            null,
            AgentRuntimeEventKind.TurnCompleted,
            "Turn completed",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ownerEnvelope.CorrelationId,
            "PR #42 looks clean; all invariants hold."), TestContext.Current.CancellationToken);

        var timeline = rooms.ReadMessages(room.Id);
        var reply = Assert.Single(timeline, msg => msg.SenderKind == PrincipalKind.Agent);
        Assert.Equal("PR #42 looks clean; all invariants hold.", reply.Body);
        Assert.Equal(ownerPrompt.MessageId, reply.ReplyToMessageId);
        Assert.Equal(coderId.Value, reply.SenderPrincipalId);
    }

    [Fact]
    public async Task Discovery_NeverGrantsTrustAutomaticallyAndSupportsGrantRevoke()
    {
        using var home = new TempHome();
        var ctx = home.CreateContext();
        var directory = new TestAgentDirectoryStore(ctx);
        var localId = AgentId.Create("agent-local");
        directory.Save(new AgentProfile(
            localId,
            "Local Researcher",
            "Researches codebases",
            AgentBackendKind.Local,
            null,
            [],
            AgentLifecycleState.Idle,
            Capabilities: ["search", "read"]));

        using var discovery = new AgentDiscoveryService(ctx);
        using var providerReg = discovery.RegisterProvider(new TestDiscoveryProvider(
            "lan-mdns",
            [
                new DiscoveredAgentCard(
                    "remote:mac-studio:planner",
                    "Remote Planner",
                    "External planning agent",
                    AgentBackendKind.A2a,
                    "http://10.0.0.12:8090",
                    ["plan", "code"],
                    OwnerPrincipalId: "alice"),
            ]));

        var cards = await discovery.DiscoverAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, cards.Count);

        // Strict invariant: discovered card has NO permissions granted.
        var remoteTrust = discovery.GetTrust("remote:mac-studio:planner");
        Assert.NotNull(remoteTrust);
        Assert.Equal(AgentTrustStatus.Discovered, remoteTrust.Status);
        Assert.False(remoteTrust.Permissions.AllowMessaging);
        Assert.False(remoteTrust.Permissions.AllowTaskDelegation);
        Assert.False(remoteTrust.Permissions.AllowEventSubscription);
        Assert.False(remoteTrust.Permissions.AllowRoomParticipation);
        Assert.False(remoteTrust.Permissions.AllowFileAccess);
        Assert.False(remoteTrust.Permissions.AllowToolControl);

        // Export and import card JSON -> PendingApproval with all permissions false.
        var exportedJson = discovery.ExportLocalCardJson(localId);
        var foreignJson = exportedJson.Replace("local:agent-local", "shared:bob:helper", StringComparison.Ordinal);
        var imported = discovery.ImportCard(foreignJson, "shared-file");
        Assert.NotNull(imported);
        var importedTrust = discovery.GetTrust("shared:bob:helper");
        Assert.NotNull(importedTrust);
        Assert.Equal(AgentTrustStatus.PendingApproval, importedTrust.Status);
        Assert.False(importedTrust.Permissions.AllowMessaging);
        Assert.False(importedTrust.Permissions.AllowEventSubscription);

        // Explicit GrantTrust enables requested permissions; RevokeTrust clears them immediately.
        var granted = discovery.GrantTrust(
            "remote:mac-studio:planner",
            new AgentTrustPermissions(AllowMessaging: true, AllowTaskDelegation: true, AllowEventSubscription: false),
            "owner-1",
            "Approved for planning");
        Assert.Equal(AgentTrustStatus.Trusted, granted.Status);
        Assert.True(discovery.IsTrustedFor("remote:mac-studio:planner", p => p.AllowTaskDelegation));

        var revoked = discovery.RevokeTrust("remote:mac-studio:planner", "owner-1", "No longer needed");
        Assert.Equal(AgentTrustStatus.Revoked, revoked.Status);
        Assert.False(revoked.Permissions.AllowMessaging);
        Assert.False(revoked.Permissions.AllowTaskDelegation);
        Assert.False(discovery.IsTrustedFor("remote:mac-studio:planner", p => p.AllowMessaging));
    }

    [Fact]
    public async Task Channels_HttpWebhooksVerifySignaturesWakeSuspendedAgentsAndDeliverOutbound()
    {
        using var home = new TempHome();
        var ctx = home.CreateContext();
        var directory = new TestAgentDirectoryStore(ctx);
        var persistent = new TestPersistentAgentManager(ctx, directory);
        var bus = new TestAgentMessageBus(ctx, persistent);

        var ciAgentId = AgentId.Create("agent-ci");
        var slackAgentId = AgentId.Create("agent-slack");
        var discordAgentId = AgentId.Create("agent-discord");
        var watchdogAgentId = AgentId.Create("agent-watchdog");
        foreach (var id in new[] { ciAgentId, slackAgentId, discordAgentId, watchdogAgentId })
            await persistent.CreateAsync(new AgentProfileRegistration(id.Value, AgentId: id), TestContext.Current.CancellationToken);

        await using var outboundSink = new LoopbackWebhookCapture();
        using var router = new ExternalChannelRouter(ctx);
        var githubAdapter = new WebhookChannelAdapter(router, () => new HttpClient());
        var slackAdapter = new SlackChannelAdapter(router, () => new HttpClient());
        var discordAdapter = new DiscordChannelAdapter(router, () => new HttpClient());
        using var r1 = router.RegisterAdapter(githubAdapter);
        using var r2 = router.RegisterAdapter(slackAdapter);
        using var r3 = router.RegisterAdapter(discordAdapter);

        await using var listener = new ChannelWebhookListener(githubAdapter, slackAdapter, discordAdapter);
        var port = await listener.StartListenerAsync(0, cancellationToken: TestContext.Current.CancellationToken);
        var baseUrl = $"http://127.0.0.1:{port}";

        router.SetSecret("gh-secret-ref", "super-github-secret");
        router.SetSecret("slack-secret-ref", "super-slack-secret");
        router.SetSecret("slack-bind:botToken", "xoxb-test-token");

        router.Bind(new ChannelBinding("gh-bind", "github", "org/repo", ciAgentId, SecretRef: "gh-secret-ref"));
        router.Bind(new ChannelBinding("slack-bind", "slack", "C12345", slackAgentId, SecretRef: "slack-secret-ref", OutboundUrl: outboundSink.UrlFor("/slack-post")));
        router.Bind(new ChannelBinding("discord-bind", "discord", "D999", discordAgentId, OutboundUrl: outboundSink.UrlFor("/discord-post")));

        router.AddTrigger(new AutomationTriggerRule(
            "ci-fail-trigger",
            "Wake watchdog on CI failure",
            "github.ci",
            "workflow_*",
            watchdogAgentId,
            "Investigate CI event {event}: {summary}"));

        using var http = new HttpClient();

        // 1. GitHub: unsigned request rejected with 401.
        var ghBody = """{"action":"completed","repository":{"full_name":"org/repo"},"sender":{"login":"octocat"},"workflow_run":{"name":"CI","conclusion":"failure"}}""";
        var unsignedResp = await http.PostAsync(
            $"{baseUrl}/webhooks/github",
            new StringContent(ghBody, Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, unsignedResp.StatusCode);

        // 2. GitHub: signed request wakes ciAgent + triggers watchdogAgent.
        var ghSig = "sha256=" + ComputeHmacHex("super-github-secret", ghBody);
        using (var ghReq = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/webhooks/github")
        {
            Content = new StringContent(ghBody, Encoding.UTF8, "application/json"),
        })
        {
            ghReq.Headers.Add("X-GitHub-Event", "workflow_run");
            ghReq.Headers.Add("X-GitHub-Delivery", "deliv-100");
            ghReq.Headers.Add("X-Hub-Signature-256", ghSig);
            var ghResp = await http.SendAsync(ghReq, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, ghResp.StatusCode);
        }

        Assert.Contains(ciAgentId, persistent.WokenAgents);
        Assert.Contains(watchdogAgentId, persistent.WokenAgents);

        // 3. Slack: url_verification + signed event_callback + outbound HTTP reply.
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var slackChallengeBody = """{"type":"url_verification","challenge":"challenge-xyz"}""";
        var slackChallengeSig = "v0=" + ComputeHmacHex("super-slack-secret", $"v0:{ts}:{slackChallengeBody}");
        using (var challengeReq = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/webhooks/slack")
        {
            Content = new StringContent(slackChallengeBody, Encoding.UTF8, "application/json"),
        })
        {
            challengeReq.Headers.Add("X-Slack-Request-Timestamp", ts);
            challengeReq.Headers.Add("X-Slack-Signature", slackChallengeSig);
            var challengeResp = await http.SendAsync(challengeReq, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, challengeResp.StatusCode);
            var challengeJson = await challengeResp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains("challenge-xyz", challengeJson);
        }

        var slackEventBody = """{"type":"event_callback","event_id":"Ev001","event":{"type":"app_mention","user":"U777","text":"deploy status?","channel":"C12345","ts":"1700000.100"}}""";
        var slackEventSig = "v0=" + ComputeHmacHex("super-slack-secret", $"v0:{ts}:{slackEventBody}");
        using (var eventReq = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/webhooks/slack")
        {
            Content = new StringContent(slackEventBody, Encoding.UTF8, "application/json"),
        })
        {
            eventReq.Headers.Add("X-Slack-Request-Timestamp", ts);
            eventReq.Headers.Add("X-Slack-Signature", slackEventSig);
            var eventResp = await http.SendAsync(eventReq, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, eventResp.StatusCode);
        }

        var slackEnvelope = bus.SentEnvelopes.First(e => e.TargetAgentId == slackAgentId);
        Assert.Equal(PrincipalKind.ExternalChannel, slackEnvelope.Sender.Kind);
        Assert.Equal(MessageTrustLevel.ExternalAgent, slackEnvelope.TrustLevel);
        Assert.Contains(slackAgentId, persistent.WokenAgents);

        // Agent completes turn -> ExternalChannelRouter automatically posts outbound Slack HTTP reply.
        await bus.PublishEventAsync(new AgentRuntimeEvent(
            "evt-slack-done",
            slackAgentId,
            null,
            AgentRuntimeEventKind.TurnCompleted,
            "Deploy is green",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            slackEnvelope.CorrelationId,
            "Deploy v0.5.0 succeeded on all nodes."), TestContext.Current.CancellationToken);

        Assert.True(outboundSink.Received.TryDequeue(out var slackOutbound));
        Assert.Equal("/slack-post", slackOutbound.Path);
        Assert.Equal("Bearer xoxb-test-token", slackOutbound.Authorization);
        Assert.Contains("C12345", slackOutbound.Body);
        Assert.Contains("1700000.100", slackOutbound.Body);
        Assert.Contains("Deploy v0.5.0 succeeded on all nodes.", slackOutbound.Body);

        // 4. Slack stale timestamp (>300s) rejected with 401.
        var staleTs = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds().ToString();
        var staleSig = "v0=" + ComputeHmacHex("super-slack-secret", $"v0:{staleTs}:{slackEventBody}");
        using (var staleReq = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/webhooks/slack")
        {
            Content = new StringContent(slackEventBody, Encoding.UTF8, "application/json"),
        })
        {
            staleReq.Headers.Add("X-Slack-Request-Timestamp", staleTs);
            staleReq.Headers.Add("X-Slack-Signature", staleSig);
            var staleResp = await http.SendAsync(staleReq, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, staleResp.StatusCode);
        }

        // Duplicate Slack event_id is deduplicated.
        var dupResult = await router.DispatchInboundAsync(new ChannelInboundMessage(
            "slack-bind",
            "slack",
            "C12345",
            "Ev001",
            "U777",
            "U777",
            "deploy status?",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), TestContext.Current.CancellationToken);
        Assert.Equal(AgentDeliveryStatus.Deduplicated, dupResult.Status);

        // 5. Discord: PING + message webhook + outbound reply.
        var pingResp = await http.PostAsync(
            $"{baseUrl}/webhooks/discord",
            new StringContent("""{"type":1}""", Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, pingResp.StatusCode);
        Assert.Contains("\"type\":1", await pingResp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var discordResp = await http.PostAsync(
            $"{baseUrl}/webhooks/discord",
            new StringContent("""{"id":"dmsg-1","channel_id":"D999","content":"ping agent","author":{"id":"du1","username":"bob"}}""", Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, discordResp.StatusCode);
        Assert.Contains(discordAgentId, persistent.WokenAgents);

        var discordEnvelope = bus.SentEnvelopes.First(e => e.TargetAgentId == discordAgentId);
        await bus.PublishEventAsync(new AgentRuntimeEvent(
            "evt-discord-done",
            discordAgentId,
            null,
            AgentRuntimeEventKind.TurnCompleted,
            "Pong",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            discordEnvelope.CorrelationId,
            "Hello Discord!"), TestContext.Current.CancellationToken);

        Assert.True(outboundSink.Received.TryDequeue(out var discordOutbound));
        Assert.Equal("/discord-post", discordOutbound.Path);
        Assert.Contains("Hello Discord!", discordOutbound.Body);

        // 6. AutomationTriggerEngine subscribes to TurnFailed runtime events and wakes bound agent.
        await persistent.SuspendAsync(watchdogAgentId, TestContext.Current.CancellationToken);
        persistent.WokenAgents.Clear();
        router.AddTrigger(new AutomationTriggerRule(
            "agent-fail-trigger",
            "Wake watchdog on agent turn failure",
            "agent",
            "turn.failed",
            watchdogAgentId,
            "Recover failed turn: {summary}"));
        await bus.PublishEventAsync(new AgentRuntimeEvent(
            "evt-turn-fail-1",
            discordAgentId,
            null,
            AgentRuntimeEventKind.TurnFailed,
            "Tool execution timed out",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), TestContext.Current.CancellationToken);
        Assert.Contains(watchdogAgentId, persistent.WokenAgents);
    }

    [Fact]
    public async Task ToolsAndCommands_RegisterAndExecuteForCollabAndChannels()
    {
        using var home = new TempHome();
        var ctx = home.CreateContext();
        _ = new SessionStore(ctx);
        _ = new SystemPrompt(ctx, new SystemPromptConfig());
        var tools = new ToolRuntime(ctx);
        var commands = CommandsService.Register(ctx);
        var agents = new AgentRegistry(ctx);
        _ = new AgentLoop(ctx);
        var directory = new TestAgentDirectoryStore(ctx);
        var persistent = new TestPersistentAgentManager(ctx, directory);
        _ = new TestAgentMessageBus(ctx, persistent);

        var handle = await agents.Create(new CreateAgentOptions(SessionId.Create("main-session")), TestContext.Current.CancellationToken);
        using var ownership = handle.Dispose;

        using var rooms = new CollaborationRoomService(ctx);
        using var discovery = new AgentDiscoveryService(ctx);
        using var router = new ExternalChannelRouter(ctx);
        var gh = new WebhookChannelAdapter(router, () => new HttpClient());
        var sl = new SlackChannelAdapter(router, () => new HttpClient());
        var dc = new DiscordChannelAdapter(router, () => new HttpClient());
        using var listener = new ChannelWebhookListener(gh, sl, dc);

        using var collabToolsReg = CollabTools.Apply(ctx, rooms, discovery);
        using var collabCmdReg = CollabCommands.Apply(ctx, rooms, discovery);
        using var channelToolsReg = ChannelTools.Apply(ctx, router);
        using var channelCmdReg = ChannelCommands.Apply(ctx, router, listener);

        var roomResult = await tools.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create("c1"),
            Name = CollabTools.RoomCreateTool,
            Arguments = JsonDocument.Parse("""{"name":"Tools Room"}""").RootElement,
            Agent = handle.Agent,
            Signal = TestContext.Current.CancellationToken,
        });
        Assert.IsType<ToolExecutionResult.Success>(roomResult);

        var cmdExec = await commands.Execute(handle.Agent, "/room list", TestContext.Current.CancellationToken);
        Assert.NotNull(cmdExec);
        var success = Assert.IsType<CommandResult.Success>(cmdExec.Result);
        Assert.Contains("Tools Room", success.Text);

        var channelCmd = await commands.Execute(handle.Agent, "/channel listener status", TestContext.Current.CancellationToken);
        Assert.NotNull(channelCmd);
        Assert.IsType<CommandResult.Success>(channelCmd.Result);

        // Also verify plugin entrypoints and ConfigType == null.
        using var pluginHome = new TempHome();
        var pluginCtx = pluginHome.CreateContext();
        _ = new SessionStore(pluginCtx);
        _ = new SystemPrompt(pluginCtx, new SystemPromptConfig());
        _ = new ToolRuntime(pluginCtx);
        _ = CommandsService.Register(pluginCtx);
        var collabPlugin = new Dsh.Collab.Plugin("@deepseek-ai/dsh-collab");
        var channelsPlugin = new Dsh.Channels.Plugin("@deepseek-ai/dsh-channels");
        Assert.Null(((Dsh.Plugins.IDshPlugin)collabPlugin).ConfigType);
        Assert.Null(((Dsh.Plugins.IDshPlugin)channelsPlugin).ConfigType);
        using var collabApplied = collabPlugin.Apply(pluginCtx, null);
        using var channelsApplied = await channelsPlugin.ApplyAsync(pluginCtx, null, TestContext.Current.CancellationToken);
        Assert.NotNull(pluginCtx.Get<ICollaborationRoomService>(ICollaborationRoomService.ServiceName, false));
        Assert.NotNull(pluginCtx.Get<IAgentDiscoveryService>(IAgentDiscoveryService.ServiceName, false));
        Assert.NotNull(pluginCtx.Get<IExternalChannelRouter>(IExternalChannelRouter.ServiceName, false));
    }

    private static string ComputeHmacHex(string secret, string payload)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexStringLower(hash);
    }
}
