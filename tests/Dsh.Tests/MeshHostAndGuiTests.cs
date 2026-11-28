using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dsh.Agent;
using Dsh.Collab;
using Dsh.Core;
using Dsh.Gui;
using Dsh.Gui.ViewModels;
using Dsh.Gui.Views;
using Dsh.Host;
using Dsh.Llm;
using Dsh.RemoteHost;
using Dsh.RemoteHost.Rpc;
using Dsh.Runtime;
using Dsh.Transport;
using Dsh.WebBridge;
using Dsh.Workspace;
using Nerdbank.Streams;
using StreamJsonRpc;

namespace Dsh.Tests;

public sealed class MeshHostAndGuiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task HarnessRemoteMeshBackend_RoundTripAndNotifications_WorkOverFullDuplexStream()
    {
        var harness = MeshTestHarness.Create();
        await using var host = await MeshServerFixture.StartAsync(harness.Ctx, token: null, Ct);
        await using var client = await host.ConnectAsync("owner-gui", cancellationToken: Ct);

        Assert.True(client.Hello.Ok);
        Assert.Equal(nameof(RoomRole.Owner), client.Hello.Role);
        Assert.True(client.Supports(MeshProtocol.CapabilityMesh));
        Assert.True(client.Supports(MeshProtocol.CapabilityRooms));
        Assert.True(client.Supports(MeshProtocol.CapabilityDiscovery));
        Assert.True(client.Supports(MeshProtocol.CapabilityChannels));

        var mesh = client.GetCapability<IWorkspaceMesh>();
        Assert.NotNull(mesh);

        using var updatesCts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var updates = mesh.MeshChangesAsync(updatesCts.Token).GetAsyncEnumerator(updatesCts.Token);
        Assert.True(await updates.MoveNextAsync());
        Assert.Equal("snapshot", updates.Current.Area);

        var targets = await mesh.ListTargetsAsync(Ct);
        Assert.Contains(targets, item => item.TargetId == "local:default");

        var createdAgent = await mesh.CreateAgentAsync(new AgentProfileRegistration("planner", "Plans work", AgentBackendKind.Local), Ct);
        Assert.Equal("planner", createdAgent.Name);
        Assert.Single(await mesh.ListAgentsAsync(Ct));

        var woken = await mesh.WakeAgentAsync(createdAgent.AgentId.Value, Ct);
        Assert.Equal(AgentLifecycleState.Idle, woken.State);

        var delivery = await mesh.SendToAgentAsync(new RemoteMeshSendRequest(createdAgent.AgentId.Value, "run check"), Ct);
        Assert.Equal(AgentDeliveryStatus.DeliveredLive, delivery.Status);

        Assert.True(await mesh.SuspendAgentAsync(createdAgent.AgentId.Value, Ct));

        var room = await mesh.CreateRoomAsync("design-room", new RoomPolicy(), Ct);
        Assert.Single(await mesh.ListRoomsAsync(Ct));
        var rooms = harness.Ctx.Get<ICollaborationRoomService>(ICollaborationRoomService.ServiceName)!;
        await rooms.AddAgentAsync(room.Id, new MessagePrincipal(PrincipalKind.Human, room.OwnerPrincipalId, "Owner"), createdAgent.AgentId, createdAgent.Name, Ct);
        var invite = await mesh.CreateInviteAsync(room.Id.Value, RoomRole.Collaborator, cancellationToken: Ct);
        var posted = await mesh.PostRoomMessageAsync(room.Id.Value, "hello mesh", createdAgent.AgentId.Value, cancellationToken: Ct);
        Assert.Equal("hello mesh", posted.Body);
        var messages = await mesh.ReadRoomMessagesAsync(room.Id.Value, cancellationToken: Ct);
        Assert.Single(messages);

        var cards = await mesh.DiscoverAgentsAsync(cancellationToken: Ct);
        Assert.Single(cards);
        var granted = await mesh.GrantTrustAsync(cards[0].CardId, new AgentTrustPermissions(AllowMessaging: true, AllowTaskDelegation: true), "approved", Ct);
        Assert.Equal(AgentTrustStatus.Trusted, granted.Status);
        Assert.Single(await mesh.ListTrustAsync(Ct));
        var revoked = await mesh.RevokeTrustAsync(cards[0].CardId, "expired", Ct);
        Assert.Equal(AgentTrustStatus.Revoked, revoked.Status);

        var binding = await mesh.BindChannelAsync(new ChannelBinding("b1", "webhook", "github:pr", createdAgent.AgentId, room.Id), Ct);
        Assert.Equal("b1", binding.BindingId);
        Assert.Single(await mesh.ListChannelsAsync(Ct));
        Assert.Single(await mesh.ListTriggersAsync(Ct));

        Assert.True(await updates.MoveNextAsync());
        Assert.NotNull(updates.Current.Snapshot);
        await updates.DisposeAsync();
    }

    [Fact]
    public async Task RemoteHostServer_MultiUserRoles_EnforcePermissionsAndPendingAttribution()
    {
        var harness = MeshTestHarness.Create();
        await using var host = await MeshServerFixture.StartAsync(harness.Ctx, token: "host-master-token", Ct);

        await using var owner = await host.ConnectAsync("owner-app", token: "host-master-token", cancellationToken: Ct);
        Assert.Equal(nameof(RoomRole.Owner), owner.Hello.Role);
        var ownerMesh = owner.GetCapability<IWorkspaceMesh>()!;
        var session = await owner.CreateSessionAsync(host.Directory, Ct);
        var unrelatedSession = await owner.CreateSessionAsync(host.Directory, Ct);
        var agent = await ownerMesh.CreateAgentAsync(new AgentProfileRegistration("worker", InitialSessionId: SessionId.Create(session.SessionId)), Ct);
        var rooms = harness.Ctx.Get<ICollaborationRoomService>(ICollaborationRoomService.ServiceName)!;

        var restrictedRoom = await ownerMesh.CreateRoomAsync("restricted", new RoomPolicy(AllowCollaboratorApprovals: false), Ct);
        await rooms.AddAgentAsync(restrictedRoom.Id, new MessagePrincipal(PrincipalKind.Human, restrictedRoom.OwnerPrincipalId, "Owner"), agent.AgentId, agent.Name, Ct);
        var observerInvite = await ownerMesh.CreateInviteAsync(restrictedRoom.Id.Value, RoomRole.Observer, cancellationToken: Ct);
        var collabInvite = await ownerMesh.CreateInviteAsync(restrictedRoom.Id.Value, RoomRole.Collaborator, cancellationToken: Ct);
        var approverInvite = await ownerMesh.CreateInviteAsync(restrictedRoom.Id.Value, RoomRole.Approver, cancellationToken: Ct);

        var approvalPolicyRoom = await ownerMesh.CreateRoomAsync("collab-approvals", new RoomPolicy(AllowCollaboratorApprovals: true), Ct);
        await rooms.AddAgentAsync(approvalPolicyRoom.Id, new MessagePrincipal(PrincipalKind.Human, approvalPolicyRoom.OwnerPrincipalId, "Owner"), agent.AgentId, agent.Name, Ct);
        var collabApproverInvite = await ownerMesh.CreateInviteAsync(approvalPolicyRoom.Id.Value, RoomRole.Collaborator, cancellationToken: Ct);

        await Assert.ThrowsAsync<IOException>(() =>
            host.ConnectAsync("bad-invite", principalId: "bad", inviteToken: "nonexistent-invite", cancellationToken: Ct));

        await using var observer = await host.ConnectAsync("obs-client", principalId: "obs-1", inviteToken: observerInvite.Token, cancellationToken: Ct);
        Assert.Equal(nameof(RoomRole.Observer), observer.Hello.Role);
        var obsMesh = observer.GetCapability<IWorkspaceMesh>()!;

        // Observer reads succeed.
        Assert.NotEmpty(await observer.ListSessionsAsync(Ct));
        Assert.NotNull(await observer.GetSessionSnapshotAsync(session.SessionId, Ct));
        Assert.NotNull(await observer.ListPendingAsync(Ct));
        Assert.NotEmpty(await obsMesh.ListAgentsAsync(Ct));
        Assert.NotEmpty(await obsMesh.ListRoomsAsync(Ct));
        await obsMesh.ReadRoomMessagesAsync(restrictedRoom.Id.Value, cancellationToken: Ct);

        // Observer mutations are denied with -32003.
        await AssertUnauthorizedAsync(() => observer.SendMessageAsync(session.SessionId, "blocked", [], Ct));
        await AssertUnauthorizedAsync(() => observer.CreateSessionAsync(host.Directory, Ct));
        await AssertUnauthorizedAsync(() => observer.InterruptAsync(session.SessionId, Ct));
        await AssertUnauthorizedAsync(() => observer.RespondApprovalAsync("req-none", true, cancellationToken: Ct));
        await AssertUnauthorizedAsync(() => observer.RespondQuestionAsync("req-none", [], cancellationToken: Ct));
        await AssertUnauthorizedAsync(() => observer.Agent.StartAgentAsync(new RemoteAgentStartRequest(AgentKinds.Terminal, host.Directory), Ct));
        await AssertUnauthorizedAsync(() => observer.Agent.PromptAgentAsync(session.SessionId, "hi", Ct));
        await AssertUnauthorizedAsync(() => observer.Agent.CancelAgentAsync(session.SessionId, Ct));
        await AssertUnauthorizedAsync(() => observer.Agent.StopAgentAsync(session.SessionId, Ct));
        await AssertUnauthorizedAsync(() => observer.Files.WriteFileAsync(Path.Combine(host.Directory, "blocked.txt"), new byte[] { 1 }, Ct));
        await AssertUnauthorizedAsync(() => observer.Files.DeleteAsync(Path.Combine(host.Directory, "blocked.txt"), cancellationToken: Ct));
        await AssertUnauthorizedAsync(() => observer.Files.BeginUploadAsync(Path.Combine(host.Directory, "blocked.txt"), cancellationToken: Ct));
        await AssertUnauthorizedAsync(() => observer.Pty.StartPtyAsync(new RemotePtyStartRequest("sh", []), Ct));
        await AssertUnauthorizedAsync(() => observer.ForwardAsync(
            PtyProtocol.MethodPtyWrite,
            System.Text.Json.JsonSerializer.SerializeToElement(new RemotePtyInput("pty-1", "AA=="), PtyProtocolJsonContext.Default.RemotePtyInput),
            Ct));
        await AssertUnauthorizedAsync(() => observer.Pty.StopPtyAsync("pty-1", Ct));
        await AssertUnauthorizedAsync(() => obsMesh.CreateAgentAsync(new AgentProfileRegistration("blocked"), Ct));
        await AssertUnauthorizedAsync(() => obsMesh.WakeAgentAsync(agent.AgentId.Value, Ct));
        await AssertUnauthorizedAsync(() => obsMesh.SuspendAgentAsync(agent.AgentId.Value, Ct));
        await AssertUnauthorizedAsync(() => obsMesh.SendToAgentAsync(new RemoteMeshSendRequest(agent.AgentId.Value, "blocked"), Ct));
        await AssertUnauthorizedAsync(() => obsMesh.CreateInviteAsync(restrictedRoom.Id.Value, RoomRole.Observer, cancellationToken: Ct));
        await AssertUnauthorizedAsync(() => obsMesh.GrantTrustAsync("card-1", new AgentTrustPermissions(), cancellationToken: Ct));
        await AssertUnauthorizedAsync(() => obsMesh.RevokeTrustAsync("card-1", cancellationToken: Ct));
        await AssertUnauthorizedAsync(() => obsMesh.BindChannelAsync(new ChannelBinding("b", "webhook", "k", agent.AgentId), Ct));

        // Collaborator can read and message room session, cannot touch unrelated sessions, PTY, files, or host-wide admin/mutations.
        await using var collab = await host.ConnectAsync("collab-client", principalId: "collab-1", inviteToken: collabInvite.Token, cancellationToken: Ct);
        Assert.Equal(nameof(RoomRole.Collaborator), collab.Hello.Role);
        var collabMesh = collab.GetCapability<IWorkspaceMesh>()!;
        await collab.SendMessageAsync(session.SessionId, "from collaborator", [], Ct);
        var collabMsg = await collabMesh.PostRoomMessageAsync(restrictedRoom.Id.Value, "collab post", cancellationToken: Ct);
        Assert.Equal("collab post", collabMsg.Body);
        await AssertUnauthorizedAsync(() => collab.SendMessageAsync(unrelatedSession.SessionId, "blocked", [], Ct));
        await AssertUnauthorizedAsync(() => collab.InterruptAsync(unrelatedSession.SessionId, Ct));
        await AssertUnauthorizedAsync(() => collab.CreateSessionAsync(host.Directory, Ct));
        await AssertUnauthorizedAsync(() => collab.Files.WriteFileAsync(Path.Combine(host.Directory, "collab-blocked.txt"), new byte[] { 1 }, Ct));
        await AssertUnauthorizedAsync(() => collab.Pty.StartPtyAsync(new RemotePtyStartRequest("sh", []), Ct));
        await AssertUnauthorizedAsync(() => collab.RespondApprovalAsync("req-1", true, cancellationToken: Ct));
        await AssertUnauthorizedAsync(() => collab.RespondQuestionAsync("req-1", [], cancellationToken: Ct));
        await AssertUnauthorizedAsync(() => collabMesh.CreateInviteAsync(restrictedRoom.Id.Value, RoomRole.Observer, cancellationToken: Ct));
        await AssertUnauthorizedAsync(() => collabMesh.GrantTrustAsync("card-1", new AgentTrustPermissions(), cancellationToken: Ct));
        await AssertUnauthorizedAsync(() => collabMesh.RevokeTrustAsync("card-1", cancellationToken: Ct));
        await AssertUnauthorizedAsync(() => collabMesh.BindChannelAsync(new ChannelBinding("b2", "webhook", "k2", agent.AgentId), Ct));
        await AssertUnauthorizedAsync(() => collabMesh.ListChannelsAsync(Ct));
        await AssertUnauthorizedAsync(() => collabMesh.ListTriggersAsync(Ct));
        await AssertUnauthorizedAsync(() => collabMesh.ListTrustAsync(Ct));
        await AssertUnauthorizedAsync(() => collabMesh.DiscoverAgentsAsync(cancellationToken: Ct));
        await AssertUnauthorizedAsync(() => collabMesh.ListTargetsAsync(Ct));

        // Approver can approve room session, cannot approve unrelated sessions, and records PrincipalId + Role in ResolvedBy.
        await using var approver = await host.ConnectAsync("approver-app", principalId: "approver-1", inviteToken: approverInvite.Token, cancellationToken: Ct);
        Assert.Equal(nameof(RoomRole.Approver), approver.Hello.Role);
        var unrelatedApprovalTask = host.Backend.Pending.AskApprovalAsync(new RemoteApprovalRequest("req-unrelated", unrelatedSession.SessionId, "bash", "ls", "check"), cancellationToken: Ct);
        await AssertUnauthorizedAsync(() => approver.RespondApprovalAsync("req-unrelated", true, cancellationToken: Ct));
        Assert.True(await owner.RespondApprovalAsync("req-unrelated", false, cancellationToken: Ct));
        Assert.False((await unrelatedApprovalTask)?.Allow);
        await using var ownerPendingEnumerator = owner.PendingChangesAsync(Ct).GetAsyncEnumerator(Ct);
        Assert.True(await ownerPendingEnumerator.MoveNextAsync());
        var approvalTask = host.Backend.Pending.AskApprovalAsync(new RemoteApprovalRequest("req-approver", session.SessionId, "bash", "ls", "check"), cancellationToken: Ct);
        while (await ownerPendingEnumerator.MoveNextAsync())
        {
            if (ownerPendingEnumerator.Current.Pending.Approvals.Count > 0)
                break;
        }
        Assert.True(await approver.RespondApprovalAsync("req-approver", true, cancellationToken: Ct));
        Assert.True((await approvalTask)?.Allow);
        RemotePendingResolved? resolvedByApprover = null;
        while (await ownerPendingEnumerator.MoveNextAsync())
        {
            if (ownerPendingEnumerator.Current.Resolution is { RequestId: "req-approver" } hit)
            {
                resolvedByApprover = hit;
                break;
            }
        }
        Assert.NotNull(resolvedByApprover);
        Assert.Contains("approver-app", resolvedByApprover.ResolvedBy, StringComparison.Ordinal);
        Assert.Contains("approver-1", resolvedByApprover.ResolvedBy, StringComparison.Ordinal);
        Assert.Contains(nameof(RoomRole.Approver), resolvedByApprover.ResolvedBy, StringComparison.Ordinal);

        // Collaborator in a room with AllowCollaboratorApprovals = true is permitted to approve.
        await using var collabApprover = await host.ConnectAsync("collab-approver", principalId: "collab-2", inviteToken: collabApproverInvite.Token, cancellationToken: Ct);
        var collabApprovalTask = host.Backend.Pending.AskApprovalAsync(new RemoteApprovalRequest("req-collab-allow", session.SessionId, "read", "a", "check"), cancellationToken: Ct);
        Assert.True(await collabApprover.RespondApprovalAsync("req-collab-allow", true, cancellationToken: Ct));
        Assert.True((await collabApprovalTask)?.Allow);

        // Backwards-compatible default Owner preserves exact ClientName attribution without invite token.
        var ownerApprovalTask = host.Backend.Pending.AskApprovalAsync(new RemoteApprovalRequest("req-owner-legacy", session.SessionId, "read", "b", "check"), cancellationToken: Ct);
        Assert.True(await owner.RespondApprovalAsync("req-owner-legacy", true, cancellationToken: Ct));
        Assert.True((await ownerApprovalTask)?.Allow);
        RemotePendingResolved? resolvedByOwner = null;
        while (await ownerPendingEnumerator.MoveNextAsync())
        {
            if (ownerPendingEnumerator.Current.Resolution is { RequestId: "req-owner-legacy" } hit)
            {
                resolvedByOwner = hit;
                break;
            }
        }
        Assert.Equal("owner-app", resolvedByOwner?.ResolvedBy);
    }

    [Fact]
    public async Task RemoteHostServer_IgnoresRemoteInvocationException_WhenOldClientLacksMeshChangedPush()
    {
        var harness = MeshTestHarness.Create();
        await using var host = await MeshServerFixture.StartAsync(harness.Ctx, token: null, Ct);
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();
        var serverTask = host.Server.ServeAsync(serverStream, Ct);

        // Connect a legacy RPC peer that only registers core push handlers without host.mesh.changed.
        using var legacyChannel = JsonRpcChannel.ServeMethods(
            clientStream,
            HostRpcFormatter.CreateJson(),
            (HostProtocol.NotificationPendingChanged, (Action<RemotePendingChange>)(_ => { })),
            (HostProtocol.NotificationSessionsChanged, (Action<RemoteSessionChange>)(_ => { })));
        var rpc = legacyChannel.AttachGenerated<IHostRpc>();
        var meshRpc = legacyChannel.AttachGenerated<IMeshRpc>();
        var hello = await rpc.HelloAsync(new HostHelloRequest(null, HostProtocol.Version, "legacy"), Ct);
        Assert.True(hello.Ok);

        // Trigger host.mesh.changed push towards the legacy peer; the server must ignore MethodNotFound and keep serving.
        await meshRpc.CreateAgentAsync(new AgentProfileRegistration("after-legacy-push"), Ct);
        var agents = await meshRpc.ListAgentsAsync(Ct);
        Assert.Single(agents);

        legacyChannel.Dispose();
        clientStream.Dispose();
        await serverTask;
    }

    [Fact]
    public async Task AcpAgentExecutionBackend_AdaptsExternalSessionsAndRegistersInRouter()
    {
        await using var environment = await GuiTestEnvironment.CreateIsolatedAsync();
        var router = environment.App.Ctx.Get<IAgentExecutionRouter>(IAgentExecutionRouter.ServiceName)!;

        await using var backend = await HarnessRemoteHostBackend.CreateAsync(environment.App, HostPtyDaemonForTests.Unused, Ct);
        backend.Sessions.Register(new RemoteSessionSummary(
            "acp-live-1",
            HostSessionKinds.Acp,
            AgentKinds.Acp,
            environment.App.Home.Root,
            "Custom ACP",
            HostSessionStatuses.Idle,
            10,
            10,
            0,
            null));

        var targets = await router.ListTargetsAsync(Ct);
        Assert.Contains(targets, target => target.Kind == AgentBackendKind.Acp && target.TargetId == "acp:session:acp-live-1");
        Assert.Contains(targets, target => target.Kind == AgentBackendKind.Acp && target.TargetId == $"acp:{AgentKinds.Acp}");

        var attached = await router.AttachAsync(AgentId.Create("acp-live-1"), AgentBackendKind.Acp, "acp:session:acp-live-1", Ct);
        Assert.NotNull(attached);
        var snapshot = await attached.GetStatusAsync(Ct);
        Assert.Equal(AgentBackendKind.Acp, snapshot.BackendKind);
        Assert.Equal(AgentLifecycleState.Idle, snapshot.State);
    }

    [Fact]
    public async Task BrowserProtocol_ExportsMeshProtocolAndCapabilities()
    {
        var root = TempTree.CreateSocketDirectory("wb-mesh");
        try
        {
            await using var bridge = WebBridgeServer.Start(new WebBridgeOptions(root, 0));
            using var http = new HttpClient();
            var script = await http.GetStringAsync($"http://127.0.0.1:{bridge.Port}/protocol.js", Ct);

            Assert.Contains("export const MeshProtocol", script, StringComparison.Ordinal);
            Assert.Contains(MeshProtocol.CapabilityMesh, script, StringComparison.Ordinal);
            Assert.Contains(MeshProtocol.CapabilityRooms, script, StringComparison.Ordinal);
            Assert.Contains(MeshProtocol.CapabilityDiscovery, script, StringComparison.Ordinal);
            Assert.Contains(MeshProtocol.CapabilityChannels, script, StringComparison.Ordinal);
            Assert.Contains(MeshProtocol.MethodAgentsList, script, StringComparison.Ordinal);
            Assert.Contains(MeshProtocol.MethodRoomsCreate, script, StringComparison.Ordinal);
            Assert.Contains(MeshProtocol.MethodDiscoveryTrust, script, StringComparison.Ordinal);
            Assert.Contains(MeshProtocol.MethodChannelsBind, script, StringComparison.Ordinal);
            Assert.Contains(MeshProtocol.NotificationMeshChanged, script, StringComparison.Ordinal);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    private static async Task AssertUnauthorizedAsync(Func<Task> action)
    {
        var error = await Assert.ThrowsAnyAsync<RemoteRpcException>(action);
        Assert.Equal(RemoteHostServer.Unauthorized, (int?)error.ErrorCode);
    }

    internal sealed class MeshTestHarness
    {
        public Context Ctx { get; } = new();
        public InMemoryRouter Router { get; } = new();

        public static MeshTestHarness Create()
        {
            var harness = new MeshTestHarness();
            var bus = new InMemoryBus();
            var agents = new InMemoryPersistentAgents(bus);
            bus.Agents = agents;
            harness.Ctx.Provide(IPersistentAgentManager.ServiceName, agents);
            harness.Ctx.Provide(IAgentMessageBus.ServiceName, bus);
            harness.Ctx.Provide(IAgentExecutionRouter.ServiceName, harness.Router);
            _ = new CollaborationRoomService(harness.Ctx);
            harness.Ctx.Provide(IAgentDiscoveryService.ServiceName, new InMemoryDiscoveryService());
            harness.Ctx.Provide(IExternalChannelRouter.ServiceName, new InMemoryChannelRouter());
            harness.Router.RegisterBackend(new StubLocalBackend());
            return harness;
        }
    }

    internal sealed class MeshServerFixture : IAsyncDisposable
    {
        private readonly string _directory;
        private readonly TestHostBackend _backend;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly List<Task> _servers = [];
        private readonly string? _token;

        private MeshServerFixture(string directory, TestHostBackend backend, string? token)
        {
            _directory = directory;
            _backend = backend;
            _token = token;
            Server = new RemoteHostServer(new RemoteHostServerOptions(token), backend);
        }

        public string Directory => _directory;
        public TestHostBackend Backend => _backend;
        public RemoteHostServer Server { get; }

        public static async Task<MeshServerFixture> StartAsync(Context ctx, string? token, CancellationToken cancellationToken)
        {
            var directory = TempTree.CreateDirectory("mesh-host");
            var pending = new HostPendingDecisions();
            var sessions = await HostSessionRegistry.OpenAsync(Path.Combine(directory, "sessions.json"), pending, cancellationToken);
            var notifications = await HostNotificationCenter.OpenAsync(Path.Combine(directory, HostNotificationCenter.FileName), sessions, pending, cancellationToken);
            var mesh = new HarnessRemoteMeshBackend(ctx);
            var backend = new TestHostBackend(directory, pending, sessions, notifications, mesh);
            return new MeshServerFixture(directory, backend, token);
        }

        public async Task<RemoteHostClient> ConnectAsync(
            string? clientName,
            string? token = null,
            string? principalId = null,
            string? inviteToken = null,
            CancellationToken cancellationToken = default)
        {
            var (clientSide, serverSide) = FullDuplexStream.CreatePair();
            _servers.Add(Server.ServeAsync(serverSide, _lifetime.Token));
            return await RemoteHostClient.ConnectAsync(clientSide, token ?? (inviteToken is null ? _token : null), clientName, principalId, inviteToken, cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await _lifetime.CancelAsync();
            try
            {
                await Task.WhenAll(_servers);
            }
            catch (OperationCanceledException)
            {
            }
            await _backend.DisposeAsync();
            _lifetime.Dispose();
            TempTree.Delete(_directory);
        }
    }

    internal sealed class TestHostBackend : IRemoteHostBackend, IRemoteSessionBackend, IRemoteAgentBackend, IRemotePtyBackend, IAsyncDisposable
    {
        private readonly string _directory;
        private readonly HarnessRemoteMeshBackend _mesh;
        private readonly ConcurrentDictionary<string, RemoteSessionSummary> _sessions = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, Channel<RemoteSessionEvent>> _events = new(StringComparer.Ordinal);

        public TestHostBackend(string directory, HostPendingDecisions pending, HostSessionRegistry sessions, HostNotificationCenter notifications, HarnessRemoteMeshBackend mesh)
        {
            _directory = directory;
            Pending = pending;
            Sessions = sessions;
            Notifications = notifications;
            _mesh = mesh;
            Files = new HostFileSystem(new HostPathPolicy([directory], () => []));
        }

        public HostPendingDecisions Pending { get; }
        public HostSessionRegistry Sessions { get; }
        public IRemoteSessionBackend Session => this;
        public IRemoteAgentBackend Agent => this;
        public IRemotePtyBackend Pty => this;
        public HostFileSystem Files { get; }
        public HostGitService? Git => null;
        public HostNotificationCenter Notifications { get; }
        public IRemoteSessionEditingBackend? SessionEditing => null;
        public IRemotePluginsBackend? Plugins => null;
        public IRemoteNetworkBackend? Network => null;
        public IRemoteMeshBackend Mesh => _mesh;

        public Task<HostInfo> InfoAsync(CancellationToken cancellationToken)
            => Task.FromResult(new HostInfo(HostProtocol.Version, HostProtocol.ReleaseVersion, "linux", _directory));

        public Task<IReadOnlyList<RemoteSessionSummary>> ListSessionsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<RemoteSessionSummary>>([.. _sessions.Values]);

        public Task<RemoteSessionSummary> CreateSessionAsync(string cwd, CancellationToken cancellationToken)
        {
            var summary = new RemoteSessionSummary($"session-{_sessions.Count + 1}", cwd, HostSessionStatuses.Idle, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "Test", null, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _sessions[summary.Id] = summary;
            _events[summary.Id] = Channel.CreateUnbounded<RemoteSessionEvent>();
            Sessions.Register(summary);
            return Task.FromResult(summary);
        }

        public Task<RemoteSessionSummary> ResumeSessionAsync(string sessionId, CancellationToken cancellationToken)
            => Task.FromResult(_sessions[sessionId]);

        public Task<RemoteSessionSnapshot> GetSessionSnapshotAsync(string sessionId, long fromEvent, int maxEvents, CancellationToken cancellationToken)
        {
            var summary = _sessions[sessionId];
            return Task.FromResult(new RemoteSessionSnapshot(summary.Header, [], 0));
        }

        public Task SendMessageAsync(string sessionId, string text, IReadOnlyList<RemoteImageBlock> images, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task InterruptAsync(string sessionId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<string>> ListToolsAsync(string sessionId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<RemoteHostStream<RemoteSessionEvent>> SubscribeAsync(string sessionId, long fromSeq, CancellationToken cancellationToken)
        {
            var channel = _events[sessionId];
            return Task.FromResult(new RemoteHostStream<RemoteSessionEvent>(channel.Reader.ReadAllAsync(cancellationToken)));
        }

        public Task<IReadOnlyList<RemoteAgentCatalogEntry>> AgentCatalogAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<RemoteAgentCatalogEntry>>([]);

        public Task<RemoteSessionSummary> StartAgentAsync(RemoteAgentStartRequest request, CancellationToken cancellationToken)
            => CreateSessionAsync(request.ProjectPath, cancellationToken);

        public Task PromptAgentAsync(string sessionId, string text, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CancelAgentAsync(string sessionId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAgentAsync(string sessionId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<RemoteSessionSummary> ResumeAgentAsync(string sessionId, CancellationToken cancellationToken) => ResumeSessionAsync(sessionId, cancellationToken);
        public Task<string> StartPtyAsync(RemotePtyStartRequest request, CancellationToken cancellationToken) => Task.FromResult($"pty-{Guid.NewGuid():N}");
        public Task WritePtyAsync(string ptyId, byte[] data, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ResizePtyAsync(string ptyId, int columns, int rows, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopPtyAsync(string ptyId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<RemoteHostStream<RemotePtyOutput>> AttachPtyAsync(string ptyId, CancellationToken cancellationToken)
            => Task.FromResult(new RemoteHostStream<RemotePtyOutput>(Channel.CreateUnbounded<RemotePtyOutput>().Reader.ReadAllAsync(cancellationToken)));

        public async ValueTask DisposeAsync()
        {
            _mesh.Dispose();
            await Notifications.DisposeAsync();
            await Sessions.DisposeAsync();
            Pending.Dispose();
        }
    }

    private sealed class InMemoryPersistentAgents(IAgentMessageBus bus) : IPersistentAgentManager
    {
        private readonly ConcurrentDictionary<AgentId, AgentProfile> _profiles = new();

        public Task<AgentProfile> CreateAsync(AgentProfileRegistration registration, CancellationToken cancellationToken = default)
        {
            var id = registration.AgentId ?? AgentId.New();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var profile = new AgentProfile(
                id,
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
                now,
                now,
                registration.Capabilities,
                registration.Metadata);
            _profiles[id] = profile;
            return Task.FromResult(profile);
        }

        public AgentProfile? Get(AgentId id) => _profiles.GetValueOrDefault(id);

        public IReadOnlyList<AgentProfile> List() => [.. _profiles.Values];

        public async Task<IAgent> WakeAsync(AgentId id, CancellationToken cancellationToken = default)
        {
            var profile = _profiles[id];
            var sessionId = profile.ActiveSessionId ?? SessionId.Create($"s-{Guid.NewGuid():N}");
            _profiles[id] = profile with
            {
                ActiveSessionId = sessionId,
                SessionIds = profile.SessionIds.Contains(sessionId) ? profile.SessionIds : [.. profile.SessionIds, sessionId],
                State = AgentLifecycleState.Idle,
                UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            await bus.PublishEventAsync(new AgentRuntimeEvent(Guid.NewGuid().ToString("N"), id, sessionId, AgentRuntimeEventKind.AgentWoken, "woken", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), cancellationToken);
            return null!;
        }

        public async Task SuspendAsync(AgentId id, CancellationToken cancellationToken = default)
        {
            var profile = _profiles[id];
            _profiles[id] = profile with { State = AgentLifecycleState.Suspended, UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            await bus.PublishEventAsync(new AgentRuntimeEvent(Guid.NewGuid().ToString("N"), id, profile.ActiveSessionId, AgentRuntimeEventKind.AgentSuspended, "suspended", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), cancellationToken);
        }

        public Task<AgentProfile> AttachSessionAsync(AgentId id, SessionId sessionId, CancellationToken cancellationToken = default)
        {
            var profile = _profiles[id];
            var updated = profile with { ActiveSessionId = sessionId, SessionIds = [.. profile.SessionIds, sessionId] };
            _profiles[id] = updated;
            return Task.FromResult(updated);
        }

        public Task<bool> DeleteAsync(AgentId id, CancellationToken cancellationToken = default)
            => Task.FromResult(_profiles.TryRemove(id, out _));
    }

    private sealed class InMemoryBus : IAgentMessageBus
    {
        private readonly ConcurrentDictionary<string, Func<AgentRuntimeEvent, ValueTask>> _handlers = new(StringComparer.Ordinal);
        public IPersistentAgentManager? Agents { get; set; }

        public Task<AgentDeliveryResult> SendAsync(AgentEnvelope envelope, CancellationToken cancellationToken = default)
        {
            var sessionId = Agents?.Get(envelope.TargetAgentId)?.ActiveSessionId;
            return Task.FromResult(new AgentDeliveryResult(envelope.EnvelopeId, AgentDeliveryStatus.DeliveredLive, envelope.TargetAgentId, sessionId));
        }

        public Task<int> FlushOfflineAsync(AgentId agentId, CancellationToken cancellationToken = default) => Task.FromResult(0);
        public IReadOnlyList<AgentEnvelope> PeekOffline(AgentId agentId) => [];

        public IDisposable Subscribe(AgentEventSubscription subscription, Func<AgentRuntimeEvent, ValueTask>? handler = null)
        {
            if (handler is not null)
                _handlers[subscription.SubscriptionId] = handler;
            return new SubscriptionHandle(() => _handlers.TryRemove(subscription.SubscriptionId, out _));
        }

        public IReadOnlyList<AgentEventSubscription> ListSubscriptions(AgentId? publisher = null) => [];

        public async Task PublishEventAsync(AgentRuntimeEvent runtimeEvent, CancellationToken cancellationToken = default)
        {
            foreach (var handler in _handlers.Values)
                await handler(runtimeEvent);
        }

        private sealed class SubscriptionHandle(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }

    internal sealed class InMemoryRouter : IAgentExecutionRouter
    {
        private readonly List<IAgentExecutionBackend> _backends = [];

        public IDisposable RegisterBackend(IAgentExecutionBackend backend)
        {
            _backends.Add(backend);
            return new Handle(() => _backends.Remove(backend));
        }

        public async Task<IReadOnlyList<RemoteExecutionTarget>> ListTargetsAsync(CancellationToken cancellationToken = default)
        {
            List<RemoteExecutionTarget> results = [];
            foreach (var backend in _backends)
                results.AddRange(await backend.ListTargetsAsync(cancellationToken));
            return results;
        }

        public Task<IAgentExecutionHandle> DispatchAsync(AgentDispatchRequest request, CancellationToken cancellationToken = default)
            => _backends.First(backend => backend.CanHandle(request)).DispatchAsync(request, cancellationToken);

        public async Task<IAgentExecutionHandle?> AttachAsync(AgentId agentId, AgentBackendKind? preferredBackend = null, string? targetId = null, CancellationToken cancellationToken = default)
        {
            foreach (var backend in _backends.Where(b => preferredBackend is null || b.Kind == preferredBackend))
            {
                if (await backend.AttachAsync(agentId, targetId, cancellationToken) is { } handle)
                    return handle;
            }
            return null;
        }

        private sealed class Handle(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }

    private sealed class StubLocalBackend : IAgentExecutionBackend
    {
        public AgentBackendKind Kind => AgentBackendKind.Local;

        public Task<IReadOnlyList<RemoteExecutionTarget>> ListTargetsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RemoteExecutionTarget>>([new RemoteExecutionTarget("local:default", AgentBackendKind.Local, "Local Harness")]);

        public bool CanHandle(AgentDispatchRequest request) => false;
        public Task<IAgentExecutionHandle> DispatchAsync(AgentDispatchRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IAgentExecutionHandle?> AttachAsync(AgentId agentId, string? targetId = null, CancellationToken cancellationToken = default) => Task.FromResult<IAgentExecutionHandle?>(null);
    }

    private sealed class InMemoryDiscoveryService : IAgentDiscoveryService
    {
        private readonly List<DiscoveredAgentCard> _cards =
        [
            new("card-peer-1", "RemoteReviewer", "Code review peer", AgentBackendKind.A2a, "https://peer.local/a2a", ["review", "diff"]),
        ];
        private readonly ConcurrentDictionary<string, AgentTrustRecord> _trust = new(StringComparer.Ordinal);

        public IDisposable RegisterProvider(IAgentDiscoveryProvider provider) => new Noop();
        public Task<IReadOnlyList<DiscoveredAgentCard>> DiscoverAsync(string? query = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DiscoveredAgentCard>>(
                string.IsNullOrWhiteSpace(query)
                    ? _cards
                    : [.. _cards.Where(c => c.Name.Contains(query, StringComparison.OrdinalIgnoreCase))]);
        public DiscoveredAgentCard? ImportCard(string cardJson, string? source = null) => null;
        public string ExportLocalCardJson(AgentId agentId) => "{}";
        public IReadOnlyList<AgentTrustRecord> ListTrust() => [.. _trust.Values];
        public AgentTrustRecord? GetTrust(string cardId) => _trust.GetValueOrDefault(cardId);

        public AgentTrustRecord GrantTrust(string cardId, AgentTrustPermissions permissions, string actorPrincipalId, string? note = null)
        {
            var card = _cards.First(c => c.CardId == cardId);
            var record = new AgentTrustRecord(cardId, card.Name, card.Endpoint, AgentTrustStatus.Trusted, permissions, actorPrincipalId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), note);
            _trust[cardId] = record;
            return record;
        }

        public AgentTrustRecord RevokeTrust(string cardId, string actorPrincipalId, string? reason = null)
        {
            var card = _cards.First(c => c.CardId == cardId);
            var record = new AgentTrustRecord(cardId, card.Name, card.Endpoint, AgentTrustStatus.Revoked, new AgentTrustPermissions(false, false, false, false, false, false), actorPrincipalId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), reason);
            _trust[cardId] = record;
            return record;
        }

        private sealed class Noop : IDisposable { public void Dispose() { } }
    }

    private sealed class InMemoryChannelRouter : IExternalChannelRouter
    {
        private readonly List<ChannelBinding> _bindings = [];
        private readonly List<AutomationTriggerRule> _triggers =
        [
            new("trig-1", "CI Failure", "webhook", "ci.failed", AgentId.Create("agent-ci"), "Investigate failure"),
        ];

        public IDisposable RegisterAdapter(IExternalChannelAdapter adapter) => new Noop();
        public ChannelBinding Bind(ChannelBinding binding) { _bindings.Add(binding); return binding; }
        public bool Unbind(string bindingId) => _bindings.RemoveAll(b => b.BindingId == bindingId) > 0;
        public IReadOnlyList<ChannelBinding> ListBindings() => [.. _bindings];
        public Task<AgentDeliveryResult> DispatchInboundAsync(ChannelInboundMessage message, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SendOutboundAsync(ChannelOutboundMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public AutomationTriggerRule AddTrigger(AutomationTriggerRule rule) { _triggers.Add(rule); return rule; }
        public bool RemoveTrigger(string ruleId) => _triggers.RemoveAll(t => t.RuleId == ruleId) > 0;
        public IReadOnlyList<AutomationTriggerRule> ListTriggers() => [.. _triggers];
        public Task<int> FireTriggerAsync(string sourceKind, string eventName, string payloadSummary, CancellationToken cancellationToken = default) => Task.FromResult(0);
        private sealed class Noop : IDisposable { public void Dispose() { } }
    }
}

[Collection(GuiSerialCollection.CollectionName)]
public sealed class MeshGuiHeadlessTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AgentMeshViewModelAndView_BindAllFourSections_InHeadlessAvalonia() => await HeadlessGui.RunAsync(async () =>
    {
        var harness = MeshHostAndGuiTests.MeshTestHarness.Create();
        await using var host = await MeshHostAndGuiTests.MeshServerFixture.StartAsync(harness.Ctx, token: null, Ct);
        await using var client = await host.ConnectAsync("mesh-gui-test", cancellationToken: Ct);
        var mesh = client.GetCapability<IWorkspaceMesh>()!;

        using var model = new AgentMeshViewModel();
        model.AttachMesh(mesh, "local", client);
        var view = new AgentMeshView { DataContext = model };
        var window = new Window { Content = view, Width = 1200, Height = 800 };
        try
        {
            window.Show();
            await model.RefreshCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();

            Assert.True(model.IsConnected);
            Assert.Contains(model.Targets, target => target.TargetId == "local:default");
            Assert.Single(model.DiscoveredAgents);
            Assert.False(model.DiscoveredAgents[0].IsTrusted);
            Assert.Contains("未信任", model.DiscoveredAgents[0].TrustBadge, StringComparison.Ordinal);
            Assert.Single(model.Triggers);

            // Create agent via view model command.
            model.NewAgentName = "gui-agent";
            await model.CreateAgentCommand.ExecuteAsync(null);
            Assert.Single(model.Agents);

            // Grant trust and verify trusted badge updates.
            model.SelectedDiscoveredAgent = model.DiscoveredAgents[0];
            await model.GrantTrustCommand.ExecuteAsync(model.SelectedDiscoveredAgent);
            Assert.True(model.DiscoveredAgents[0].IsTrusted);
            Assert.Contains("Trusted", model.DiscoveredAgents[0].TrustBadge, StringComparison.Ordinal);

            // Render each of the 4 tabs cleanly without stealing focus.
            var tabs = view.GetVisualDescendants().OfType<TabControl>().Single();
            for (var index = 0; index < 4; index++)
            {
                tabs.SelectedIndex = index;
                window.Measure(new Size(1200, 800));
                window.Arrange(new Rect(0, 0, 1200, 800));
                Dispatcher.UIThread.RunJobs();
            }
            Assert.Equal(["Agents", "Rooms", "Discovery & Trust", "Channels & Triggers"],
                tabs.Items.OfType<TabItem>().Select(item => Assert.IsType<string>(item.Header)));
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });

    [Fact]
    public async Task WorkspacePages_AndGuiWorkbench_ExposeMeshPageAndWorkspaceClient() => await HeadlessGui.RunAsync(async () =>
    {
        var directory = TempTree.CreateDirectory("mesh-ws");
        try
        {
            var home = new Dsh.Boot.HarnessHome(directory);
            var services = new Dsh.Fleet.FleetWorkspaceServices(
                new Dsh.Fleet.MachineStore(home),
                new Dsh.Fleet.FleetOptions { AutoReconnect = false, StartLocalHost = null },
                () => new Dsh.Fleet.FleetClient([], new Dsh.Fleet.FleetOptions { AutoReconnect = false }));
            await using var fleet = new Dsh.Gui.Services.FleetWorkspace(services);
            await using var workspace = new WorkspaceViewModel(fleet, (_, _, _) => { });
            var icon = Geometry.Parse("M0,0 L10,10");

            workspace.RegisterHostPages(icon, icon, icon);
            workspace.RegisterMeshPage(icon);

            Assert.Equal([WorkspacePages.Files, WorkspacePages.Git, WorkspacePages.Machines, WorkspacePages.Mesh],
                workspace.Pages.Available.Select(page => page.Id));
            var meshEntry = Assert.Single(workspace.Pages.Available, page => page.Id == WorkspacePages.Mesh);
            Assert.Equal("智能体网络", meshEntry.Label);

            workspace.OpenPage(meshEntry);
            var meshModel = Assert.IsType<AgentMeshViewModel>(workspace.PageContent);

            var mainWindow = new MainWindow();
            try
            {
                var pageHost = mainWindow.FindControl<ContentControl>("WorkspacePageHost")!;
                Assert.Contains(pageHost.DataTemplates, template => template.Match(meshModel));
            }
            finally
            {
                mainWindow.Close();
                Dispatcher.UIThread.RunJobs();
            }

            using var workbench = new GuiWorkbench();
            Assert.Null(workbench.Workspace);
            Assert.Null(workbench.TryGetWorkspace());
        }
        finally
        {
            TempTree.Delete(directory);
        }
    });
}
