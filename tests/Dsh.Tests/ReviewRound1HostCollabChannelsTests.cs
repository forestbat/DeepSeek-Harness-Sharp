using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dsh.Agent;
using Dsh.Channels;
using Dsh.Collab;
using Dsh.Core;
using Dsh.Host;
using Dsh.Ide;
using Dsh.Ide.Bridge;
using Dsh.Llm;
using Dsh.RemoteHost;
using Dsh.RemoteHost.Rpc;
using Dsh.Runtime;
using Dsh.Workspace;
using Nerdbank.Streams;
using StreamJsonRpc;

namespace Dsh.Tests;

public sealed class ReviewRound1HostCollabChannelsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task F1_InviteScopedOwnerAndApprover_AreBlockedFromHostMutationsAndUnrelatedSessions()
    {
        var harness = MeshHostAndGuiTests.MeshTestHarness.Create();
        await using var host = await MeshHostAndGuiTests.MeshServerFixture.StartAsync(harness.Ctx, token: "host-secret", Ct);
        await using var owner = await host.ConnectAsync("owner", token: "host-secret", cancellationToken: Ct);
        var ownerMesh = owner.GetCapability<IWorkspaceMesh>()!;

        var roomSession = await owner.CreateSessionAsync(host.Directory, Ct);
        var otherSession = await owner.CreateSessionAsync(host.Directory, Ct);
        var roomAgent = await ownerMesh.CreateAgentAsync(
            new AgentProfileRegistration("room-agent", InitialSessionId: SessionId.Create(roomSession.SessionId)),
            Ct);
        var otherAgent = await ownerMesh.CreateAgentAsync(
            new AgentProfileRegistration("other-agent", InitialSessionId: SessionId.Create(otherSession.SessionId)),
            Ct);

        var rooms = harness.Ctx.Get<ICollaborationRoomService>(ICollaborationRoomService.ServiceName)!;
        var room = await ownerMesh.CreateRoomAsync("scoped-room", new RoomPolicy(), Ct);
        await rooms.AddAgentAsync(room.Id, new MessagePrincipal(PrincipalKind.Human, room.OwnerPrincipalId, "Owner"), roomAgent.AgentId, roomAgent.Name, Ct);

        // Even an invite with RoomRole.Owner must NOT gain HostAction.Admin or host-wide mutation capabilities.
        var ownerInvite = await ownerMesh.CreateInviteAsync(room.Id.Value, RoomRole.Owner, cancellationToken: Ct);
        await using var invitedOwner = await host.ConnectAsync("invited-owner", principalId: "guest-owner", inviteToken: ownerInvite.Token, cancellationToken: Ct);
        Assert.Equal(nameof(RoomRole.Owner), invitedOwner.Hello.Role);
        var invitedOwnerMesh = invitedOwner.GetCapability<IWorkspaceMesh>()!;

        // Admin RPCs denied.
        await AssertUnauthorizedAsync(() => invitedOwnerMesh.CreateInviteAsync(room.Id.Value, RoomRole.Observer, cancellationToken: Ct));
        await AssertUnauthorizedAsync(() => invitedOwnerMesh.GrantTrustAsync("card-peer-1", new AgentTrustPermissions(AllowMessaging: true), cancellationToken: Ct));
        await AssertUnauthorizedAsync(() => invitedOwnerMesh.RevokeTrustAsync("card-peer-1", cancellationToken: Ct));
        await AssertUnauthorizedAsync(() => invitedOwnerMesh.BindChannelAsync(new ChannelBinding("b-inv", "webhook", "k", roomAgent.AgentId), Ct));
        await AssertUnauthorizedAsync(() => invitedOwnerMesh.ListChannelsAsync(Ct));
        await AssertUnauthorizedAsync(() => invitedOwnerMesh.ListTriggersAsync(Ct));
        await AssertUnauthorizedAsync(() => invitedOwnerMesh.ListTrustAsync(Ct));
        await AssertUnauthorizedAsync(() => invitedOwnerMesh.DiscoverAgentsAsync(cancellationToken: Ct));
        await AssertUnauthorizedAsync(() => invitedOwnerMesh.ListTargetsAsync(Ct));

        // Host-wide OS/machine/session/mesh mutations denied.
        await AssertUnauthorizedAsync(() => invitedOwner.CreateSessionAsync(host.Directory, Ct));
        await AssertUnauthorizedAsync(() => invitedOwner.ResumeSessionAsync(roomSession.SessionId, Ct));
        await AssertUnauthorizedAsync(() => invitedOwner.Pty.StartPtyAsync(new RemotePtyStartRequest("sh", []), Ct));
        await AssertUnauthorizedAsync(() => invitedOwner.ForwardAsync(
            PtyProtocol.MethodPtyWrite,
            JsonSerializer.SerializeToElement(new RemotePtyInput("pty-1", "AA=="), PtyProtocolJsonContext.Default.RemotePtyInput),
            Ct));
        await AssertUnauthorizedAsync(() => invitedOwner.ForwardAsync(
            PtyProtocol.MethodPtyResize,
            JsonSerializer.SerializeToElement(new RemotePtyResize("pty-1", 80, 24), PtyProtocolJsonContext.Default.RemotePtyResize),
            Ct));
        await AssertUnauthorizedAsync(() => invitedOwner.Pty.StopPtyAsync("pty-1", Ct));
        await AssertUnauthorizedAsync(() => invitedOwner.ForwardAsync(
            PtyProtocol.MethodPtyAttach,
            JsonSerializer.SerializeToElement(new RemotePtyAttachRequest("pty-1", "sub-1"), PtyProtocolJsonContext.Default.RemotePtyAttachRequest),
            Ct));
        await AssertUnauthorizedAsync(() => invitedOwner.Files.WriteFileAsync(Path.Combine(host.Directory, "pwn.txt"), new byte[] { 1 }, Ct));
        await AssertUnauthorizedAsync(() => invitedOwner.Files.DeleteAsync(Path.Combine(host.Directory, "pwn.txt"), cancellationToken: Ct));
        await AssertUnauthorizedAsync(() => invitedOwner.Files.CreateDirectoryAsync(Path.Combine(host.Directory, "sub"), Ct));
        await AssertUnauthorizedAsync(() => invitedOwner.Files.BeginUploadAsync(Path.Combine(host.Directory, "up.txt"), cancellationToken: Ct));
        await AssertUnauthorizedAsync(() => invitedOwner.Agent.StartAgentAsync(new RemoteAgentStartRequest(AgentKinds.Terminal, host.Directory), Ct));
        await AssertUnauthorizedAsync(() => invitedOwner.Agent.PromptAgentAsync(roomSession.SessionId, "hi", Ct));
        await AssertUnauthorizedAsync(() => invitedOwner.Agent.CancelAgentAsync(roomSession.SessionId, Ct));
        await AssertUnauthorizedAsync(() => invitedOwner.Agent.StopAgentAsync(roomSession.SessionId, Ct));
        await AssertUnauthorizedAsync(() => invitedOwnerMesh.CreateAgentAsync(new AgentProfileRegistration("bad"), Ct));
        await AssertUnauthorizedAsync(() => invitedOwnerMesh.WakeAgentAsync(roomAgent.AgentId.Value, Ct));
        await AssertUnauthorizedAsync(() => invitedOwnerMesh.SuspendAgentAsync(roomAgent.AgentId.Value, Ct));
        await AssertUnauthorizedAsync(() => invitedOwnerMesh.CreateRoomAsync("rogue-room", new RoomPolicy(), Ct));

        // Scoped room session actions succeed; unrelated session actions fail with -32003.
        await invitedOwner.SendMessageAsync(roomSession.SessionId, "allowed in room", [], Ct);
        await invitedOwner.InterruptAsync(roomSession.SessionId, Ct);
        var roomSend = await invitedOwnerMesh.SendToAgentAsync(new RemoteMeshSendRequest(roomAgent.AgentId.Value, "allowed mesh"), Ct);
        Assert.Equal(AgentDeliveryStatus.DeliveredLive, roomSend.Status);

        await AssertUnauthorizedAsync(() => invitedOwner.SendMessageAsync(otherSession.SessionId, "blocked", [], Ct));
        await AssertUnauthorizedAsync(() => invitedOwner.InterruptAsync(otherSession.SessionId, Ct));
        await AssertUnauthorizedAsync(() => invitedOwnerMesh.SendToAgentAsync(new RemoteMeshSendRequest(otherAgent.AgentId.Value, "blocked"), Ct));
        await AssertUnauthorizedAsync(() => invitedOwnerMesh.SendToAgentAsync(
            new RemoteMeshSendRequest(roomAgent.AgentId.Value, "control blocked", AgentMessageIntent.Control),
            Ct));

        // Pending decisions (approval & question) are strictly scoped to the room's sessions.
        var approverInvite = await ownerMesh.CreateInviteAsync(room.Id.Value, RoomRole.Approver, cancellationToken: Ct);
        await using var approver = await host.ConnectAsync("approver", principalId: "app-1", inviteToken: approverInvite.Token, cancellationToken: Ct);

        var unrelatedApproval = host.Backend.Pending.AskApprovalAsync(
            new RemoteApprovalRequest("req-other-app", otherSession.SessionId, "bash", "id", "check"),
            cancellationToken: Ct);
        var unrelatedQuestion = host.Backend.Pending.AskQuestionAsync(
            new RemoteQuestionRequest("req-other-q", otherSession.SessionId, [new RemoteQuestionItem("q1", "Pick", null, "Pick", [new RemoteQuestionOption("A", "A")], false, false)]),
            cancellationToken: Ct);

        await AssertUnauthorizedAsync(() => approver.RespondApprovalAsync("req-other-app", true, cancellationToken: Ct));
        await AssertUnauthorizedAsync(() => approver.RespondQuestionAsync("req-other-q", [new RemoteQuestionAnswerItem("q1", ["A"], null)], Ct));

        Assert.True(await owner.RespondApprovalAsync("req-other-app", false, cancellationToken: Ct));
        Assert.True(await owner.RespondQuestionAsync("req-other-q", [new RemoteQuestionAnswerItem("q1", ["A"], null)], Ct));
        Assert.False((await unrelatedApproval)?.Allow);
        Assert.Equal("answered", (await unrelatedQuestion)?.Status);

        var roomApproval = host.Backend.Pending.AskApprovalAsync(
            new RemoteApprovalRequest("req-room-app", roomSession.SessionId, "read", "f", "check"),
            cancellationToken: Ct);
        Assert.True(await approver.RespondApprovalAsync("req-room-app", true, cancellationToken: Ct));
        Assert.True((await roomApproval)?.Allow);
    }

    [Fact]
    public async Task F1_HostPendingDecisions_CanResolveSessionPredicateBlocksMismatchedSession()
    {
        using var pending = new HostPendingDecisions();
        var approvalTask = pending.AskApprovalAsync(
            new RemoteApprovalRequest("req-p1", "session-b", "bash", "ls", "check"),
            cancellationToken: Ct);

        Assert.Equal("session-b", pending.GetSessionId("req-p1", PendingKinds.Approval));
        Assert.False(pending.RespondApproval(
            new RemoteApprovalResponse("req-p1", true, null),
            PendingResolver.Host,
            sid => sid == "session-a"));
        Assert.Equal(1, pending.CountFor("session-b"));

        Assert.True(pending.RespondApproval(
            new RemoteApprovalResponse("req-p1", true, null),
            PendingResolver.Host,
            sid => sid == "session-b"));
        Assert.True((await approvalTask)?.Allow);
    }

    [Fact]
    public async Task F1_GitAndSessionEditingMutations_RejectInviteScopedConnections()
    {
        var dir = TempTree.CreateDirectory("r1-git-edit");
        try
        {
            var ctx = new Context();
            var bus = new StubBus();
            var agents = new StubAgents();
            ctx.Provide(IPersistentAgentManager.ServiceName, agents);
            ctx.Provide(IAgentMessageBus.ServiceName, bus);
            var rooms = new CollaborationRoomService(ctx);

            var ownerPrincipal = new MessagePrincipal(PrincipalKind.Human, "owner", "Owner");
            var room = await rooms.CreateRoomAsync("git-edit-room", ownerPrincipal, cancellationToken: Ct);
            var invite = await rooms.CreateInviteAsync(room.Id, ownerPrincipal, RoomRole.Owner, cancellationToken: Ct);

            using var pending = new HostPendingDecisions();
            await using var sessions = await HostSessionRegistry.OpenAsync(Path.Combine(dir, "sessions.json"), pending, Ct);
            await using var notifications = await HostNotificationCenter.OpenAsync(Path.Combine(dir, HostNotificationCenter.FileName), sessions, pending, Ct);
            using var mesh = new HarnessRemoteMeshBackend(ctx);
            var backend = new FullDomainStubBackend(dir, pending, sessions, notifications, mesh);

            var server = new RemoteHostServer(new RemoteHostServerOptions(Token: "master"), backend);
            var (clientStream, serverStream) = FullDuplexStream.CreatePair();
            var serverTask = server.ServeAsync(serverStream, Ct);
            await using var guest = await RemoteHostClient.ConnectAsync(clientStream, token: null, "guest", "guest-label", invite.Token, Ct);

            await AssertUnauthorizedAsync(() => guest.Git.AddWorktreeAsync(new RemoteGitWorktreeAdd(dir, Path.Combine(dir, "wt"), "feat/x"), Ct));
            await AssertUnauthorizedAsync(() => guest.Git.RemoveWorktreeAsync(new RemoteGitWorktreeRemove(dir, Path.Combine(dir, "wt"), false), Ct));
            await AssertUnauthorizedAsync(() => guest.SessionEditing.ExecuteSessionCommandAsync("s1", "/help", Ct));
            await AssertUnauthorizedAsync(() => guest.SessionEditing.RenameSessionAsync("s1", "new-name", Ct));
            await AssertUnauthorizedAsync(() => guest.SessionEditing.DeleteSessionAsync("s1", Ct));
            await AssertUnauthorizedAsync(() => guest.SessionEditing.SetSessionApprovalPolicyAsync("s1", "auto", Ct));
            await AssertUnauthorizedAsync(() => guest.ForwardAsync(
                EditorProtocol.MethodOpen,
                JsonSerializer.SerializeToElement(
                    new RemoteEditorOpen(Guid.NewGuid(), new EditorRequest { WorkspaceRoot = dir, Path = Path.Combine(dir, "a.cs") }),
                    EditorProtocolJsonContext.Default.RemoteEditorOpen),
                Ct));

            clientStream.Dispose();
            await serverTask;
        }
        finally
        {
            TempTree.Delete(dir);
        }
    }

    [Fact]
    public async Task F2_InviteRedemption_NeverImpersonatesOwnerOrOverwritesExistingParticipants()
    {
        var dir = TempTree.CreateDirectory("r1-f2");
        try
        {
            var ctx = new Context();
            ctx.SetOwn("dshHomePath", dir);
            var bus = new StubBus();
            var agents = new StubAgents();
            ctx.Provide(IPersistentAgentManager.ServiceName, agents);
            ctx.Provide(IAgentMessageBus.ServiceName, bus);
            using var rooms = new CollaborationRoomService(ctx);
            using var mesh = new HarnessRemoteMeshBackend(ctx);

            var owner = new MessagePrincipal(PrincipalKind.Human, "owner-real", "Real Owner");
            var existingMember = new MessagePrincipal(PrincipalKind.RemoteUser, "member-1", "Existing Member");
            var room = await rooms.CreateRoomAsync("f2-room", owner, cancellationToken: Ct);
            var memberInvite = await rooms.CreateInviteAsync(room.Id, owner, RoomRole.Approver, maxUses: 1, cancellationToken: Ct);
            await rooms.RedeemInviteAsync(memberInvite.Token, existingMember, cancellationToken: Ct);

            var guestInvite = await rooms.CreateInviteAsync(room.Id, owner, RoomRole.Observer, maxUses: 2, cancellationToken: Ct);

            // Direct service redemption must reject owner or existing participant IDs BEFORE incrementing UsedCount.
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                rooms.RedeemInviteAsync(guestInvite.Token, owner, cancellationToken: Ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                rooms.RedeemInviteAsync(guestInvite.Token, new MessagePrincipal(PrincipalKind.RemoteUser, "member-1", "Spoof"), cancellationToken: Ct));
            Assert.Equal(0, rooms.ListInvites(room.Id).Single(i => i.InviteId == guestInvite.InviteId).UsedCount);

            // Host invite resolution must assign a server-prefixed guest principal ID even if caller claims "owner-real".
            var identity = await mesh.ResolveInviteAsync(guestInvite.Token, "owner-real", null, Ct);
            Assert.NotNull(identity);
            Assert.StartsWith("guest:", identity.PrincipalId, StringComparison.Ordinal);
            Assert.NotEqual("owner-real", identity.PrincipalId);
            Assert.Equal(RoomRole.Observer, identity.Role);

            var updatedRoom = rooms.GetRoom(room.Id)!;
            Assert.Equal(RoomRole.Approver, updatedRoom.Participants.Single(p => p.PrincipalId == "member-1").Role);
            Assert.Equal(RoomRole.Owner, updatedRoom.Participants.Single(p => p.PrincipalId == "owner-real").Role);
        }
        finally
        {
            TempTree.Delete(dir);
        }
    }

    [Fact]
    public async Task F3_MeshSnapshotsAndListAgents_AreStrictlyScopedPerSubscriberCaller()
    {
        var harness = MeshHostAndGuiTests.MeshTestHarness.Create();
        await using var host = await MeshHostAndGuiTests.MeshServerFixture.StartAsync(harness.Ctx, token: "host-secret", Ct);
        await using var owner = await host.ConnectAsync("owner", token: "host-secret", cancellationToken: Ct);
        var ownerMesh = owner.GetCapability<IWorkspaceMesh>()!;

        var agentA = await ownerMesh.CreateAgentAsync(new AgentProfileRegistration("agent-room-a"), Ct);
        var agentB = await ownerMesh.CreateAgentAsync(new AgentProfileRegistration("agent-room-b"), Ct);
        var rooms = harness.Ctx.Get<ICollaborationRoomService>(ICollaborationRoomService.ServiceName)!;

        var roomA = await ownerMesh.CreateRoomAsync("room-a", new RoomPolicy(), Ct);
        var roomB = await ownerMesh.CreateRoomAsync("room-b", new RoomPolicy(), Ct);
        await rooms.AddAgentAsync(roomA.Id, new MessagePrincipal(PrincipalKind.Human, roomA.OwnerPrincipalId, "Owner"), agentA.AgentId, agentA.Name, Ct);
        await rooms.AddAgentAsync(roomB.Id, new MessagePrincipal(PrincipalKind.Human, roomB.OwnerPrincipalId, "Owner"), agentB.AgentId, agentB.Name, Ct);

        var cards = await ownerMesh.DiscoverAgentsAsync(cancellationToken: Ct);
        await ownerMesh.GrantTrustAsync(cards[0].CardId, new AgentTrustPermissions(AllowMessaging: true), "ok", Ct);
        await ownerMesh.BindChannelAsync(new ChannelBinding("secret-hook", "webhook", "org/secret", agentB.AgentId, roomB.Id, OutboundUrl: "https://hooks.example.invalid/token=secret"), Ct);

        var inviteA = await ownerMesh.CreateInviteAsync(roomA.Id.Value, RoomRole.Collaborator, cancellationToken: Ct);
        await using var guestA = await host.ConnectAsync("guest-a", principalId: "guest-a", inviteToken: inviteA.Token, cancellationToken: Ct);
        var guestMesh = guestA.GetCapability<IWorkspaceMesh>()!;

        // ListAgents is filtered to agentA only.
        var visibleAgents = await guestMesh.ListAgentsAsync(Ct);
        Assert.Single(visibleAgents);
        Assert.Equal(agentA.AgentId, visibleAgents[0].AgentId);

        // Initial snapshot pushed to guestA excludes roomB, agentB, targets, discovered, trust, channels, and triggers.
        using var guestUpdatesCts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var guestUpdates = guestMesh.MeshChangesAsync(guestUpdatesCts.Token).GetAsyncEnumerator(guestUpdatesCts.Token);
        Assert.True(await guestUpdates.MoveNextAsync());
        var initial = guestUpdates.Current.Snapshot;
        Assert.NotNull(initial);
        Assert.Single(initial.Rooms);
        Assert.Equal(roomA.Id, initial.Rooms[0].Id);
        Assert.Single(initial.Agents);
        Assert.Equal(agentA.AgentId, initial.Agents[0].AgentId);
        Assert.Empty(initial.Targets);
        Assert.Empty(initial.Discovered);
        Assert.Empty(initial.TrustRecords);
        Assert.Empty(initial.Channels);
        Assert.Empty(initial.Triggers);

        // Subsequent mutations in roomB / channels must not leak in live pushes to guestA.
        await ownerMesh.BindChannelAsync(new ChannelBinding("secret-hook-2", "slack", "C_SECRET", agentB.AgentId, roomB.Id), Ct);
        Assert.True(await guestUpdates.MoveNextAsync());
        var livePush = guestUpdates.Current.Snapshot;
        Assert.NotNull(livePush);
        Assert.Single(livePush.Rooms);
        Assert.Equal(roomA.Id, livePush.Rooms[0].Id);
        Assert.Single(livePush.Agents);
        Assert.Equal(agentA.AgentId, livePush.Agents[0].AgentId);
        Assert.Empty(livePush.Channels);
        Assert.Empty(livePush.Triggers);
        Assert.Empty(livePush.TrustRecords);

        // Meanwhile non-invite Owner still receives full snapshot (both on live push and fresh connect).
        await using var owner2 = await host.ConnectAsync("owner-2", token: "host-secret", cancellationToken: Ct);
        var owner2Mesh = owner2.GetCapability<IWorkspaceMesh>()!;
        using var ownerUpdatesCts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var ownerUpdates = owner2Mesh.MeshChangesAsync(ownerUpdatesCts.Token).GetAsyncEnumerator(ownerUpdatesCts.Token);
        Assert.True(await ownerUpdates.MoveNextAsync());
        var ownerSnapshot = ownerUpdates.Current.Snapshot!;
        Assert.Equal(2, ownerSnapshot.Rooms.Count);
        Assert.Equal(2, ownerSnapshot.Agents.Count);
        Assert.NotEmpty(ownerSnapshot.Channels);
        Assert.NotEmpty(ownerSnapshot.Triggers);
        Assert.NotEmpty(ownerSnapshot.TrustRecords);

        await guestUpdates.DisposeAsync();
        await ownerUpdates.DisposeAsync();
    }

    [Fact]
    public void F4_HostPathPolicy_CanonicalizesHomeRelativeTraversal()
    {
        var dir = TempTree.CreateDirectory("r1-f4");
        try
        {
            var policy = new HostPathPolicy([dir], () => []);
            var expanded = HostPathPolicy.Expand("~/../../etc/passwd");
            Assert.DoesNotContain("..", expanded, StringComparison.Ordinal);

            Assert.Throws<UnauthorizedAccessException>(() => policy.Resolve("~/../../etc/passwd"));
            Assert.Throws<UnauthorizedAccessException>(() => policy.Resolve("~/subdir/../../../etc/passwd"));
            Assert.Throws<UnauthorizedAccessException>(() => policy.ResolveEntry("~/../../etc/passwd"));
            Assert.False(policy.IsAllowed(Path.Combine(dir, "..", "outside.txt")));

            var allowed = policy.Resolve(Path.Combine(dir, "sub", "..", "ok.txt"));
            Assert.Equal(Path.GetFullPath(Path.Combine(dir, "ok.txt")), allowed);
        }
        finally
        {
            TempTree.Delete(dir);
        }
    }

    [Fact]
    public async Task F11_And_F12_CollaborationRoomServiceAndTools_EnforceParticipantAndReadAccess()
    {
        var dir = TempTree.CreateDirectory("r1-f11-f12");
        try
        {
            var ctx = new Context();
            ctx.SetOwn("dshHomePath", dir);
            _ = new SessionStore(ctx);
            _ = new SystemPrompt(ctx, new SystemPromptConfig());
            var tools = new ToolRuntime(ctx);
            var bus = new StubBus();
            var agents = new StubAgents();
            ctx.Provide(IPersistentAgentManager.ServiceName, agents);
            ctx.Provide(IAgentMessageBus.ServiceName, bus);

            var inRoomAgent = AgentId.Create("agent-in-room");
            var outsideAgent = AgentId.Create("agent-outside");
            await agents.CreateAsync(new AgentProfileRegistration("InRoom", AgentId: inRoomAgent), Ct);
            await agents.CreateAsync(new AgentProfileRegistration("Outside", AgentId: outsideAgent), Ct);

            using var rooms = new CollaborationRoomService(ctx);
            using var discovery = new AgentDiscoveryService(ctx);
            using var toolReg = CollabTools.Apply(ctx, rooms, discovery);

            var owner = new MessagePrincipal(PrincipalKind.Human, "owner-1", "Owner");
            var room = await rooms.CreateRoomAsync("private-room", owner, new RoomPolicy(AllowObserverRead: false), Ct);
            await rooms.AddAgentAsync(room.Id, owner, inRoomAgent, "InRoom", Ct);
            await rooms.PostMessageAsync(room.Id, owner, "secret room message", cancellationToken: Ct);

            // F11: Prompting an outside agent explicitly must fail before saving or dispatching.
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                rooms.PostMessageAsync(room.Id, owner, "hijack", targetAgentId: outsideAgent, cancellationToken: Ct));
            Assert.Single(rooms.ReadMessages(room.Id));
            Assert.Empty(bus.Sent);

            // Prompting the participant agent succeeds.
            await rooms.PostMessageAsync(room.Id, owner, "run check", targetAgentId: inRoomAgent, cancellationToken: Ct);
            Assert.Single(bus.Sent);

            // F12: room_read must reject principals who cannot read the room.
            using (AgentTurnAttribution.Enter(new MessageAttribution(
                new MessagePrincipal(PrincipalKind.RemoteUser, "outsider-1", "Outsider"),
                MessageTrustLevel.DelegatedHuman,
                AgentMessageIntent.Task)))
            {
                var denied = await tools.Execute(new ToolExecutionInput
                {
                    CallId = ToolCallId.Create("read-denied"),
                    Name = CollabTools.RoomReadTool,
                    Arguments = JsonDocument.Parse($$"""{"room_id":"{{room.Id.Value}}"}""").RootElement,
                    Signal = Ct,
                });
                var err = Assert.IsType<ToolExecutionResult.Failure>(denied);
                Assert.Contains("not permitted", err.Error.Message, StringComparison.OrdinalIgnoreCase);
            }

            // Authorized owner can read via room_read.
            using (AgentTurnAttribution.Enter(new MessageAttribution(owner, MessageTrustLevel.OwnerHuman, AgentMessageIntent.Task)))
            {
                var allowed = await tools.Execute(new ToolExecutionInput
                {
                    CallId = ToolCallId.Create("read-allowed"),
                    Name = CollabTools.RoomReadTool,
                    Arguments = JsonDocument.Parse($$"""{"room_id":"{{room.Id.Value}}"}""").RootElement,
                    Signal = Ct,
                });
                Assert.IsType<ToolExecutionResult.Success>(allowed);
            }
        }
        finally
        {
            TempTree.Delete(dir);
        }
    }

    [Fact]
    public async Task F13_F23_F24_F25_F34_ChannelsRouterAdaptersAndIpv6Listener_WorkEndToEnd()
    {
        var dir = TempTree.CreateDirectory("r1-channels");
        try
        {
            var ctx = new Context();
            ctx.SetOwn("dshHomePath", dir);
            var bus = new StubBus();
            var agents = new StubAgents();
            ctx.Provide(IPersistentAgentManager.ServiceName, agents);
            ctx.Provide(IAgentMessageBus.ServiceName, bus);

            var agentA = AgentId.Create("agent-a");
            var agentB = AgentId.Create("agent-b");
            var agentWatch = AgentId.Create("agent-watch");
            await agents.CreateAsync(new AgentProfileRegistration("A", AgentId: agentA), Ct);
            await agents.CreateAsync(new AgentProfileRegistration("B", AgentId: agentB), Ct);
            await agents.CreateAsync(new AgentProfileRegistration("Watch", AgentId: agentWatch), Ct);

            using var router = new ExternalChannelRouter(ctx);
            var outboundPosts = new ConcurrentQueue<ChannelOutboundMessage>();
            var githubAdapter = new WebhookChannelAdapter(router, () => new HttpClient());
            var slackAdapter = new SlackChannelAdapter(router, () => new HttpClient());
            var discordAdapter = new DiscordChannelAdapter(router, () => new HttpClient());
            using var r1 = router.RegisterAdapter(githubAdapter);
            using var r2 = router.RegisterAdapter(new RecordingSlackAdapter(outboundPosts));
            using var r3 = router.RegisterAdapter(discordAdapter);

            router.SetSecret("shared-secret", "top-secret-value");
            router.Bind(new ChannelBinding("slack-a", "slack", "C_A", agentA, SecretRef: "shared-secret", OutboundUrl: "https://slack.example.invalid/post"));
            router.Bind(new ChannelBinding("slack-b", "slack", "C_B", agentB, SecretRef: "shared-secret", OutboundUrl: "https://slack.example.invalid/post"));
            router.Bind(new ChannelBinding("gh-a", "github", "org/repo-a", agentA, SecretRef: "shared-secret"));
            router.AddTrigger(new AutomationTriggerRule("trig-gh", "GH", "github", "push", agentWatch, "Push: {summary}"));

            // F13: explicit bindingId must still match adapterKind and channelKey.
            Assert.Empty(router.FindBindings("github", "org/repo-a", "slack-a"));
            Assert.Empty(router.FindBindings("slack", "C_B", "slack-a"));
            var mismatchedDispatch = await router.DispatchInboundAsync(new ChannelInboundMessage(
                "slack-a", "slack", "C_B", "m-mismatch", "u1", "u1", "hi", 1000), Ct);
            Assert.Equal(AgentDeliveryStatus.Rejected, mismatchedDispatch.Status);

            // Signed Slack event for channel C_B claiming X-Dsh-Binding-Id: slack-a must get 401.
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var crossChannelBody = """{"type":"event_callback","event_id":"EvCross1","event":{"type":"message","channel":"C_B","user":"U1","text":"hi","ts":"1700.1"}}""";
            var crossSig = "v0=" + ComputeHmacHex("top-secret-value", $"v0:{ts}:{crossChannelBody}");
            var crossRes = await slackAdapter.HandleInboundAsync(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["x-slack-request-timestamp"] = ts,
                    ["x-slack-signature"] = crossSig,
                    ["x-dsh-binding-id"] = "slack-a",
                },
                Encoding.UTF8.GetBytes(crossChannelBody),
                Ct);
            Assert.Equal(401, crossRes.StatusCode);

            // F23: GitHub retry with same X-GitHub-Delivery fires trigger only once.
            var ghBody = """{"repository":{"full_name":"org/repo-a"},"sender":{"login":"octo"},"head_commit":{"message":"fix"}}""";
            var ghSig = "sha256=" + ComputeHmacHex("top-secret-value", ghBody);
            var ghHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["x-github-event"] = "push",
                ["x-github-delivery"] = "deliv-f23",
                ["x-hub-signature-256"] = ghSig,
            };
            var firstGh = await githubAdapter.HandleInboundAsync(ghHeaders, Encoding.UTF8.GetBytes(ghBody), Ct);
            Assert.Equal(200, firstGh.StatusCode);
            Assert.Contains("\"triggersFired\":1", firstGh.JsonBody, StringComparison.Ordinal);

            var triggerEnvelope = bus.Sent.Last(e => e.TargetAgentId == agentWatch);
            Assert.Equal("trigger:trig-gh:deliv-f23", triggerEnvelope.IdempotencyKey);

            var countBeforeRetry = bus.Sent.Count;
            var retryGh = await githubAdapter.HandleInboundAsync(ghHeaders, Encoding.UTF8.GetBytes(ghBody), Ct);
            Assert.Equal(200, retryGh.StatusCode);
            Assert.Contains("\"status\":\"Deduplicated\"", retryGh.JsonBody, StringComparison.Ordinal);
            Assert.Contains("\"triggersFired\":0", retryGh.JsonBody, StringComparison.Ordinal);
            Assert.Equal(countBeforeRetry, bus.Sent.Count);

            // F24: Cancelled/rejected bus delivery must not persist dedup, allowing legitimate retry.
            bus.NextException = new OperationCanceledException("simulated cancel");
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                router.DispatchInboundAsync(new ChannelInboundMessage("slack-a", "slack", "C_A", "EvRetry1", "U1", "U1", "first try", 1000, "thread-f24"), Ct));
            bus.NextStatus = AgentDeliveryStatus.Rejected;
            var rejectedAttempt = await router.DispatchInboundAsync(
                new ChannelInboundMessage("slack-a", "slack", "C_A", "EvRetry1", "U1", "U1", "second try", 1000, "thread-f24"),
                Ct);
            Assert.Equal(AgentDeliveryStatus.Rejected, rejectedAttempt.Status);
            bus.NextStatus = null;

            var acceptedRetry = await router.DispatchInboundAsync(
                new ChannelInboundMessage("slack-a", "slack", "C_A", "EvRetry1", "U1", "U1", "third try", 1000, "thread-f24"),
                Ct);
            Assert.Equal(AgentDeliveryStatus.DeliveredLive, acceptedRetry.Status);

            var dupAfterAccept = await router.DispatchInboundAsync(
                new ChannelInboundMessage("slack-a", "slack", "C_A", "EvRetry1", "U1", "U1", "fourth try", 1000, "thread-f24"),
                Ct);
            Assert.Equal(AgentDeliveryStatus.Deduplicated, dupAfterAccept.Status);

            // F25: Two distinct messages in the same Slack thread each get their own automatic reply.
            var msg1 = await router.DispatchInboundAsync(
                new ChannelInboundMessage("slack-a", "slack", "C_A", "EvThread1", "U1", "U1", "q1", 1000, "1700.77"),
                Ct);
            var msg2 = await router.DispatchInboundAsync(
                new ChannelInboundMessage("slack-a", "slack", "C_A", "EvThread2", "U1", "U1", "q2", 1000, "1700.77"),
                Ct);
            var env1 = bus.Sent.Last(e => e.EnvelopeId == msg1.EnvelopeId);
            var env2 = bus.Sent.Last(e => e.EnvelopeId == msg2.EnvelopeId);
            Assert.NotEqual(env1.CorrelationId, env2.CorrelationId);

            await bus.PublishEventAsync(new AgentRuntimeEvent("e1", agentA, null, AgentRuntimeEventKind.TurnCompleted, "d1", 1001, env1.CorrelationId, "reply 1"), Ct);
            await bus.PublishEventAsync(new AgentRuntimeEvent("e2", agentA, null, AgentRuntimeEventKind.TurnCompleted, "d2", 1002, env2.CorrelationId, "reply 2"), Ct);

            Assert.Equal(2, outboundPosts.Count);
            Assert.Contains(outboundPosts, item => item.Body == "reply 1" && item.ThreadId == "1700.77");
            Assert.Contains(outboundPosts, item => item.Body == "reply 2" && item.ThreadId == "1700.77");

            // F34: IPv6 listener constructs valid bracketed BaseUri and serves /health.
            if (Socket.OSSupportsIPv6)
            {
                await using var listener = new ChannelWebhookListener(githubAdapter, slackAdapter, discordAdapter);
                var v6Port = await listener.StartListenerAsync(0, "::1", Ct);
                Assert.True(v6Port > 0);
                Assert.True(listener.IsListening);
                Assert.Equal($"http://[::1]:{v6Port}/", listener.BaseUri?.ToString());
                using var http = new HttpClient();
                var health = await http.GetAsync($"{listener.BaseUri}health", Ct);
                Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            }
        }
        finally
        {
            TempTree.Delete(dir);
        }
    }

    private static async Task AssertUnauthorizedAsync(Func<Task> action)
    {
        var error = await Assert.ThrowsAnyAsync<RemoteRpcException>(action);
        Assert.Equal(RemoteHostServer.Unauthorized, (int?)error.ErrorCode);
    }

    private static string ComputeHmacHex(string secret, string payload)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexStringLower(hash);
    }

    private sealed class RecordingSlackAdapter(ConcurrentQueue<ChannelOutboundMessage> posts) : IExternalChannelAdapter
    {
        public string AdapterKind => SlackChannelAdapter.Kind;

        public Task SendAsync(ChannelBinding binding, ChannelOutboundMessage message, CancellationToken cancellationToken = default)
        {
            posts.Enqueue(message);
            return Task.CompletedTask;
        }
    }

    private sealed class StubAgents : IPersistentAgentManager
    {
        private readonly ConcurrentDictionary<AgentId, AgentProfile> _profiles = new();

        public Task<AgentProfile> CreateAsync(AgentProfileRegistration registration, CancellationToken cancellationToken = default)
        {
            var id = registration.AgentId ?? AgentId.New();
            var profile = new AgentProfile(
                id,
                registration.Name,
                registration.Description,
                registration.BackendKind,
                registration.InitialSessionId,
                registration.InitialSessionId is { } sid ? [sid] : [],
                AgentLifecycleState.Idle);
            _profiles[id] = profile;
            return Task.FromResult(profile);
        }

        public AgentProfile? Get(AgentId id) => _profiles.GetValueOrDefault(id);
        public IReadOnlyList<AgentProfile> List() => [.. _profiles.Values];
        public Task<IAgent> WakeAsync(AgentId id, CancellationToken cancellationToken = default) => Task.FromResult<IAgent>(null!);
        public Task SuspendAsync(AgentId id, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AgentProfile> AttachSessionAsync(AgentId id, SessionId sessionId, CancellationToken cancellationToken = default)
            => Task.FromResult(_profiles[id]);
        public Task<bool> DeleteAsync(AgentId id, CancellationToken cancellationToken = default)
            => Task.FromResult(_profiles.TryRemove(id, out _));
    }

    private sealed class StubBus : IAgentMessageBus
    {
        private readonly Lock _gate = new();
        private readonly List<Func<AgentRuntimeEvent, ValueTask>> _handlers = [];

        public ConcurrentQueue<AgentEnvelope> Sent { get; } = new();
        public Exception? NextException { get; set; }
        public AgentDeliveryStatus? NextStatus { get; set; }

        public Task<AgentDeliveryResult> SendAsync(AgentEnvelope envelope, CancellationToken cancellationToken = default)
        {
            if (NextException is { } error)
            {
                NextException = null;
                throw error;
            }
            if (NextStatus is { } status)
                return Task.FromResult(new AgentDeliveryResult(envelope.EnvelopeId, status, envelope.TargetAgentId));
            Sent.Enqueue(envelope);
            return Task.FromResult(new AgentDeliveryResult(envelope.EnvelopeId, AgentDeliveryStatus.DeliveredLive, envelope.TargetAgentId));
        }

        public Task<int> FlushOfflineAsync(AgentId agentId, CancellationToken cancellationToken = default) => Task.FromResult(0);
        public IReadOnlyList<AgentEnvelope> PeekOffline(AgentId agentId) => [];

        public IDisposable Subscribe(AgentEventSubscription subscription, Func<AgentRuntimeEvent, ValueTask>? handler = null)
        {
            if (handler is null)
                return new Unsub(() => { });
            lock (_gate)
                _handlers.Add(handler);
            return new Unsub(() =>
            {
                lock (_gate)
                    _handlers.Remove(handler);
            });
        }

        public IReadOnlyList<AgentEventSubscription> ListSubscriptions(AgentId? publisher = null) => [];

        public async Task PublishEventAsync(AgentRuntimeEvent runtimeEvent, CancellationToken cancellationToken = default)
        {
            List<Func<AgentRuntimeEvent, ValueTask>> copy;
            lock (_gate)
                copy = [.. _handlers];
            foreach (var handler in copy)
                await handler(runtimeEvent).ConfigureAwait(false);
        }

        private sealed class Unsub(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }

    private sealed class FullDomainStubBackend(
        string directory,
        HostPendingDecisions pending,
        HostSessionRegistry sessions,
        HostNotificationCenter notifications,
        IRemoteMeshBackend mesh)
        : IRemoteHostBackend, IRemoteSessionBackend, IRemoteSessionEditingBackend, IRemoteEditorBackend
    {
        public HostPendingDecisions Pending => pending;
        public HostSessionRegistry Sessions => sessions;
        public IRemoteSessionBackend Session => this;
        public IRemoteAgentBackend? Agent => null;
        public IRemotePtyBackend? Pty => null;
        public HostFileSystem? Files => null;
        public HostGitService? Git { get; } = new(new HostPathPolicy([directory], () => []), new StubGitService());
        public HostNotificationCenter Notifications => notifications;
        public IRemoteSessionEditingBackend SessionEditing => this;
        public IRemotePluginsBackend? Plugins => null;
        public IRemoteNetworkBackend? Network => null;
        public IRemoteEditorBackend Editor => this;
        public IRemoteMeshBackend Mesh => mesh;
        public IEditorService Service { get; } = new StubEditorService();
        public Dsh.Ide.Bridge.EditorClientDispatcher? Clients => null;

        public Task<HostInfo> InfoAsync(CancellationToken cancellationToken)
            => Task.FromResult(new HostInfo(HostProtocol.Version, HostProtocol.ReleaseVersion, "linux", directory));
        public Task<IReadOnlyList<RemoteSessionSummary>> ListSessionsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<RemoteSessionSummary>>([]);
        public Task<RemoteSessionSummary> CreateSessionAsync(string cwd, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<RemoteSessionSummary> ResumeSessionAsync(string sessionId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<RemoteSessionSnapshot> GetSessionSnapshotAsync(string sessionId, long fromEvent, int maxEvents, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task SendMessageAsync(string sessionId, string text, IReadOnlyList<RemoteImageBlock> images, CancellationToken cancellationToken)
            => Task.CompletedTask;
        public Task InterruptAsync(string sessionId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> ListToolsAsync(string sessionId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<RemoteHostStream<RemoteSessionEvent>> SubscribeAsync(string sessionId, long fromSeq, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<RemoteCommandResult> ExecuteSessionCommandAsync(string sessionId, string line, CancellationToken cancellationToken)
            => Task.FromResult(new RemoteCommandResult(true, "ok", null, true));
        public Task<IReadOnlyList<RemoteCommandDescriptor>> ListSessionCommandsAsync(string sessionId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<RemoteCommandDescriptor>>([]);
        public Task RenameSessionAsync(string sessionId, string title, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetSessionApprovalPolicyAsync(string sessionId, string policy, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<RemoteSubagentInfo>> ListSubagentsAsync(string rootSessionId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<RemoteSubagentInfo>>([]);
        public Task<IReadOnlyList<RemoteTimelineEntry>> ListSessionTimelineAsync(string sessionId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<RemoteTimelineEntry>>([]);
    }

    private sealed class StubGitService : IGitService
    {
        public Task<Dsh.GitInspect.GitStatus> StatusAsync(string projectPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Dsh.GitInspect.GitDiff> DiffAsync(string projectPath, Dsh.GitInspect.GitDiffTarget target, Dsh.GitInspect.GitDiffLimits? limits = null, IReadOnlyList<string>? paths = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Dsh.GitInspect.GitDiffSummary> DiffSummaryAsync(string projectPath, Dsh.GitInspect.GitDiffTarget target, IReadOnlyList<string>? paths = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Dsh.GitInspect.GitCommitSummary>> LogAsync(string projectPath, int maxCount, string? revision = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Dsh.GitInspect.GitWorktree>> ListWorktreesAsync(string projectPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Dsh.GitInspect.GitWorktree> AddWorktreeAsync(string projectPath, Dsh.GitInspect.GitWorktreeAddRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveWorktreeAsync(string projectPath, string path, bool force = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class StubEditorService : IEditorService
    {
        public IEditorCatalog Catalog { get; } = new StubEditorCatalog();
        public EditorDecision? LastDecision => null;
        public ValueTask<EditorResult> LaunchAsync(EditorRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public EditorWorkspace InspectWorkspace(string root) => throw new NotSupportedException();
        public EditorBinding? GetBinding(string root, string? sessionId = null) => null;
        public void Bind(EditorBinding binding) { }
        public ValueTask<IReadOnlyList<EditorDiagnostic>> DoctorAsync(string workspaceRoot, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<EditorDiagnostic>>([]);
    }

    private sealed class StubEditorCatalog : IEditorCatalog
    {
        public IReadOnlyList<EditorDescriptor> Descriptors => [];
        public EditorDescriptor? Find(EditorId id) => null;
        public IDisposable Register(EditorDescriptor descriptor) => new Noop();
        public ValueTask<IReadOnlyList<EditorInstallation>> DetectAsync(bool refresh = false, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<EditorInstallation>>([]);
        private sealed class Noop : IDisposable { public void Dispose() { } }
    }
}
