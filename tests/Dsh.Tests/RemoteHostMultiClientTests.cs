using System.Text.Json;
using Dsh.Core;
using Dsh.Host;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Pty;
using Dsh.RemoteHost;

namespace Dsh.Tests;

/**
 * 多客户端连同一个真实 host(harness 后端 + loopback 监听): 审批/问答广播与先答者生效、断线后仍可被别的客户端回答、
 * host.pending.list、会话登记表推送与重启恢复。审批经 harness 的审批瀑布直接发起(ApprovalService.Request 要求处在一轮对话之内, 而这里不调用 LLM)。
 */
[Collection(SerialProcessCollection.Name)]
public sealed class RemoteHostMultiClientTests
{
    private static readonly TimeSpan Arrival = TimeSpan.FromSeconds(5);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory(Timeout = 5000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Approval_IsBroadcast_FirstAnswerWins_AndResolutionReachesEveryone(bool rememberForSession)
    {
        await using var host = await HarnessHostFixture.CreateAsync("mha", TestContext.Current.CancellationToken);
        var alice = await host.ConnectAsync("alice", Ct);
        var bob = await host.ConnectAsync("bob", Ct);
        foreach (var client in new[] { alice, bob })
        {
            var initial = await NextAsync(client.PendingChangesAsync(Ct));
            Assert.Empty(initial.Pending.Approvals);
            Assert.Empty(initial.Pending.Questions);
            Assert.Null(initial.Resolution);
        }

        var decision = RaiseApprovalAsync(host, host.Environment.Agent, "bash");
        var seenByAlice = Assert.Single((await NextAsync(alice.PendingChangesAsync(Ct), change => change.Pending.Approvals.Count > 0)).Pending.Approvals);
        var seenByBob = Assert.Single((await NextAsync(bob.PendingChangesAsync(Ct), change => change.Pending.Approvals.Count > 0)).Pending.Approvals);
        Assert.Equal(seenByAlice, seenByBob);
        Assert.Equal(host.Environment.Agent.Id.Value, seenByAlice.SessionId);
        Assert.Equal("bash", seenByAlice.Tool);

        Assert.True(await alice.RespondApprovalAsync(seenByAlice.RequestId, true, cancellationToken: Ct, rememberForSession: rememberForSession));
        Assert.False(await bob.RespondApprovalAsync(seenByBob.RequestId, false, cancellationToken: Ct));
        Assert.Equal(rememberForSession ? ApprovalOutcome.AllowedForSession : ApprovalOutcome.AllowedOnce, await decision.WaitAsync(Arrival, Ct));

        foreach (var client in new[] { alice, bob })
        {
            var change = await NextAsync(client.PendingChangesAsync(Ct), change => change.Resolution is not null);
            Assert.Empty(change.Pending.Approvals);
            Assert.Empty(change.Pending.Questions);
            var resolved = Assert.IsType<RemotePendingResolved>(change.Resolution);
            Assert.Equal(seenByAlice.RequestId, resolved.RequestId);
            Assert.Equal(PendingKinds.Approval, resolved.Kind);
            Assert.Equal(PendingResolutions.Allowed, resolved.Resolution);
            Assert.Equal("alice", resolved.ResolvedBy);
            Assert.Equal(alice.Hello.ConnectionId, resolved.ResolvedByConnection);
        }
        Assert.Empty((await bob.ListPendingAsync(Ct)).Approvals);
        var late = await host.ConnectAsync("after-answer", Ct);
        var fresh = await NextAsync(late.PendingChangesAsync(Ct));
        Assert.Empty(fresh.Pending.Approvals);
        Assert.Empty(fresh.Pending.Questions);
        Assert.Null(fresh.Resolution);
    }

    [Fact(Explicit = true)]
    public async Task PendingRequests_SurviveDisconnect_AndReachLateJoiners()
    {
        await using var host = await HarnessHostFixture.CreateAsync("mhd", TestContext.Current.CancellationToken);
        var first = await host.ConnectAsync("first", Ct);
        var approval = RaiseApprovalAsync(host, host.Environment.Agent, "write");
        var question = AskQuestionAsync(host, host.Environment.Agent);
        var beforeDisconnect = await NextAsync(first.PendingChangesAsync(Ct), change => change.Pending.Approvals.Count == 1 && change.Pending.Questions.Count == 1);
        Assert.Null(beforeDisconnect.Resolution);
        await first.DisposeAsync();

        var late = await host.ConnectAsync("late", Ct);
        var pending = await late.ListPendingAsync(Ct);
        var pendingApproval = Assert.Single(pending.Approvals);
        var pendingQuestion = Assert.Single(pending.Questions);
        var initial = await NextAsync(late.PendingChangesAsync(Ct));
        Assert.Equal(pendingApproval, Assert.Single(initial.Pending.Approvals));
        Assert.Equal(pendingQuestion.RequestId, Assert.Single(initial.Pending.Questions).RequestId);
        Assert.Null(initial.Resolution);

        Assert.True(await late.RespondApprovalAsync(pendingApproval.RequestId, false, cancellationToken: Ct));
        Assert.True(await late.RespondQuestionAsync(pendingQuestion.RequestId, [new RemoteQuestionAnswerItem("q1", ["B"], null)], Ct));
        Assert.Equal(ApprovalOutcome.Rejected, await approval.WaitAsync(Arrival, Ct));
        Assert.Equal(["B"], (await question.WaitAsync(Arrival, Ct)).Answers[0].Selected);
        Assert.False(await late.RespondQuestionAsync(pendingQuestion.RequestId, [new RemoteQuestionAnswerItem("q1", ["A"], null)], Ct));
        var approvalChange = await NextAsync(late.PendingChangesAsync(Ct), change => change.Resolution?.RequestId == pendingApproval.RequestId);
        Assert.Empty(approvalChange.Pending.Approvals);
        Assert.Equal(pendingQuestion.RequestId, Assert.Single(approvalChange.Pending.Questions).RequestId);
        Assert.Equal((PendingResolutions.Rejected, "late"), (approvalChange.Resolution!.Resolution, approvalChange.Resolution.ResolvedBy));
        var questionChange = await NextAsync(late.PendingChangesAsync(Ct), change => change.Resolution?.RequestId == pendingQuestion.RequestId);
        Assert.Empty(questionChange.Pending.Approvals);
        Assert.Empty(questionChange.Pending.Questions);
        Assert.Equal((PendingResolutions.Answered, late.Hello.ConnectionId), (questionChange.Resolution!.Resolution, questionChange.Resolution.ResolvedByConnection));
        var empty = await late.ListPendingAsync(Ct);
        Assert.Empty(empty.Approvals);
        Assert.Empty(empty.Questions);
    }

    [Fact(Explicit = true)]
    public async Task Interrupt_CancelsPendingRequests_AndBroadcastsCancellation()
    {
        await using var host = await HarnessHostFixture.CreateAsync("mhi", TestContext.Current.CancellationToken);
        var client = await host.ConnectAsync("solo", Ct);
        var created = await client.CreateSessionAsync(host.Environment.App.Home.Root, Ct);
        var agent = Agent(host, created.Id);
        var approval = RaiseApprovalAsync(host, agent, "bash");
        await NextAsync(client.PendingChangesAsync(Ct), change => change.Pending.Approvals.Count > 0);

        await client.InterruptAsync(created.Id, Ct);

        Assert.Equal(ApprovalOutcome.Cancelled, await approval.WaitAsync(Arrival, Ct));
        var change = await NextAsync(client.PendingChangesAsync(Ct), change => change.Resolution is not null);
        Assert.Empty(change.Pending.Approvals);
        var resolved = Assert.IsType<RemotePendingResolved>(change.Resolution);
        Assert.Equal(PendingResolutions.Cancelled, resolved.Resolution);
        Assert.Equal(PendingResolver.Host.Name, resolved.ResolvedBy);
        Assert.Null(resolved.ResolvedByConnection);
    }

    [Fact(Explicit = true)]
    public async Task SessionRegistry_PushesChanges_AndDerivesWaitingForUser()
    {
        await using var host = await HarnessHostFixture.CreateAsync("mhs", TestContext.Current.CancellationToken);
        var creator = await host.ConnectAsync("creator", Ct);
        var watcher = await host.ConnectAsync("watcher", Ct);
        var changes = watcher.HostSessionChangesAsync(Ct);
        var initial = await NextAsync(changes);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<RemoteSessionSummary>>(initial.Snapshot));
        Assert.Null(initial.Session);
        var cwd = host.Environment.App.Home.Root;

        var created = await creator.CreateSessionAsync(cwd, Ct);
        var registered = (await NextAsync(changes, change => change.Session?.SessionId == created.Id)).Session!;
        Assert.Equal(HostSessionKinds.Harness, registered.Kind);
        Assert.Equal(HostAgentKinds.Dsharp, registered.AgentKind);
        Assert.Equal(cwd, registered.ProjectPath);
        Assert.Equal(HostSessionStatuses.Idle, registered.Status);

        var approval = RaiseApprovalAsync(host, Agent(host, created.Id), "bash");
        var waiting = (await NextAsync(changes, change => change.Session?.SessionId == created.Id && change.Session.Status == HostSessionStatuses.WaitingForUser)).Session!;
        Assert.Equal(1, waiting.PendingCount);
        Assert.Contains(await creator.ListHostSessionsAsync(Ct), session => session == waiting);

        var request = Assert.Single((await NextAsync(creator.PendingChangesAsync(Ct), change => change.Pending.Approvals.Count > 0)).Pending.Approvals);
        await creator.RespondApprovalAsync(request.RequestId, true, cancellationToken: Ct);
        await approval.WaitAsync(Arrival, Ct);
        var answered = (await NextAsync(changes, change => change.Session?.SessionId == created.Id && change.Session.PendingCount == 0)).Session!;
        Assert.Equal(HostSessionStatuses.Idle, answered.Status);
        var activity = answered.LastActivityAt + 10_000;
        host.Backend.Sessions.Touch(created.Id, activity);
        host.Backend.Sessions.Touch(created.Id, activity + 1);
        Assert.Equal(activity, (await NextAsync(changes, change => change.Session?.SessionId == created.Id && change.Session.LastActivityAt == activity)).Session!.LastActivityAt);
        Assert.Equal(activity + 1, (await NextAsync(changes, change => change.Session?.SessionId == created.Id && change.Session.LastActivityAt == activity + 1)).Session!.LastActivityAt);
    }

    [Fact(Explicit = true)]
    public async Task SessionRegistry_RecoversAfterHostRestart_AndReconcilesTerminalsWithTheDaemon()
    {
        await using var host = await HarnessHostFixture.CreateAsync("mhr", TestContext.Current.CancellationToken);
        var before = await host.ConnectAsync("before", Ct);
        var harness = await before.CreateSessionAsync(host.Environment.App.Home.Root, Ct);
        var ptyId = await before.Pty.StartPtyAsync(new RemotePtyStartRequest(Shell.FileName, Shell.Arguments, Title: "build shell"), Ct);

        await host.RestartHostAsync(Ct);

        var after = await host.ConnectAsync("after", Ct);
        var activity = host.Backend.Sessions.Get(ptyId)!.LastActivityAt + 1;
        host.Backend.Sessions.Touch(ptyId, activity);
        var sessions = await after.ListHostSessionsAsync(Ct);
        var changes = after.HostSessionChangesAsync(Ct);
        var initial = await NextAsync(changes);
        ReadOnlySpan<IReadOnlyList<RemoteSessionSummary>> snapshots = [sessions, Assert.IsAssignableFrom<IReadOnlyList<RemoteSessionSummary>>(initial.Snapshot)];
        // The handshake snapshot and list response describe independently sampled registry states.
        foreach (var snapshot in snapshots)
        {
            var restoredHarness = Assert.Single(snapshot, session => session.SessionId == harness.Id);
            Assert.Equal(HostSessionStatuses.Exited, restoredHarness.Status);
            var restoredTerminal = Assert.Single(snapshot, session => session.SessionId == ptyId);
            Assert.Equal(HostSessionKinds.Terminal, restoredTerminal.Kind);
            Assert.Equal("build shell", restoredTerminal.Title);
            Assert.Equal(HostSessionStatuses.Running, restoredTerminal.Status);
        }
        Assert.Null(initial.Session);
        await after.ResumeSessionAsync(harness.Id, Ct);
        Assert.Equal(HostSessionStatuses.Idle, (await NextAsync(changes, change => change.Session?.SessionId == harness.Id)).Session!.Status);
        await after.Pty.StopPtyAsync(ptyId, Ct);
        Assert.Equal(HostSessionStatuses.Exited, (await NextAsync(changes, change => change.Session?.SessionId == ptyId && change.Session.Status == HostSessionStatuses.Exited)).Session!.Status);
    }

    [Fact(Explicit = true)]
    public async Task SessionRegistry_PersistsOnlyBaseFacts_WhileARealDecisionIsPending()
    {
        var root = TempTree.CreateDirectory("registry");
        try
        {
            var path = Path.Combine(root, "sessions.json");
            using var pending = new HostPendingDecisions();
            var launch = new HostAgentLaunch("agent-command", ["--private", "argument-secret"],
                new Dictionary<string, string> { ["TOKEN"] = "credential-secret" }, "agent-conversation");
            Task<RemoteApprovalResponse?> decision;
            await using (var registry = await HostSessionRegistry.OpenAsync(path, pending, TestContext.Current.CancellationToken))
            {
                registry.Register(new RemoteSessionSummary("agent", HostSessionKinds.Acp, "custom", root, "original",
                    HostSessionStatuses.Idle, 10, 20, 0, null), launch);
                registry.Register(new RemoteSessionSummary("agent", HostSessionKinds.Acp, "custom", root, "renamed",
                    HostSessionStatuses.Idle, 99, 21, 0, null));
                decision = pending.AskApprovalAsync(new RemoteApprovalRequest("permission", "agent", "write", null, "allow?"));
                Assert.Equal((HostSessionStatuses.WaitingForUser, 1), (registry.Get("agent")!.Status, registry.Get("agent")!.PendingCount));
                await registry.FlushAsync();
                if (!OperatingSystem.IsWindows())
                    Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            }
            using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, Ct)))
            {
                var saved = Assert.Single(document.RootElement.EnumerateArray());
                Assert.Equal(HostSessionStatuses.Idle, saved.GetProperty("Status").GetString());
                Assert.False(saved.TryGetProperty("PendingCount", out _));
                Assert.False(saved.TryGetProperty("Deleted", out _));
            }
            pending.CancelSession("agent");
            Assert.Null(await decision);
            await using var restored = await HostSessionRegistry.OpenAsync(path, pending, Ct);
            var record = Assert.Single(restored.List());
            Assert.Equal(("agent", HostSessionKinds.Acp, "custom", root, "renamed"), (record.SessionId, record.Kind, record.AgentKind, record.ProjectPath, record.Title));
            Assert.Equal((10L, 21L, HostSessionStatuses.Exited, 0), (record.CreatedAt, record.LastActivityAt, record.Status, record.PendingCount));
            var restoredLaunch = Assert.IsType<HostAgentLaunch>(restored.AgentLaunch(record.SessionId));
            Assert.Equal((launch.Executable, launch.AcpSessionId), (restoredLaunch.Executable, restoredLaunch.AcpSessionId));
            Assert.Equal(launch.Arguments, restoredLaunch.Arguments);
            Assert.Equal(launch.Environment, restoredLaunch.Environment);
            restored.Remove(record.SessionId);
            Assert.Null(restored.AgentLaunch(record.SessionId));
        }
        finally
        {
            TempTree.Delete(root);
        }
    }


    internal static (string FileName, string[] Arguments) Shell
        => OperatingSystem.IsWindows() ? ("cmd.exe", ["/v:on"]) : ("/bin/sh", []);

    internal static IAgent Agent(HarnessHostFixture host, string sessionId)
        => host.Environment.App.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!.Get(SessionId.Create(sessionId))!;

    /** 与 ApprovalService 派发应答者的方式相同: 在该 agent 的作用域上跑审批瀑布, 没有应答者时得到 Unavailable。 */
    internal static async Task<ApprovalOutcome> RaiseApprovalAsync(HarnessHostFixture host, IAgent agent, string tool)
    {
        var ctx = host.Environment.App.Ctx;
        var request = new ApprovalRequest(agent, tool, new ToolCallId($"call-{Guid.NewGuid():N}"), "needs approval", "{}");
        var result = await ctx.Events.Waterfall(
            DshScope.ScopeTarget(ctx, agent.ScopeKey),
            new ApprovalRequestNotification(request),
            () => new ValueTask<object?>(ApprovalOutcome.Unavailable));
        return Assert.IsType<ApprovalOutcome>(result);
    }

    private static Task<AskUserQuestionAnswer> AskQuestionAsync(HarnessHostFixture host, IAgent agent)
        => host.Environment.App.Ctx.Get<UserQuestionService>(UserQuestionService.ServiceName)!.Ask(
            new AskUserQuestionRequest([new AskUserQuestionItem("q1", "pick one", Options: [new AskUserQuestionOption("A"), new AskUserQuestionOption("B")])], agent),
            Ct);

    internal static async Task<T> NextAsync<T>(IAsyncEnumerable<T> stream, Func<T, bool>? match = null, TimeSpan? within = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(within ?? Arrival);
        await foreach (var item in stream.WithCancellation(timeout.Token))
        {
            if (match is null || match(item))
                return item;
        }
        throw new InvalidOperationException("stream ended before the expected item arrived");
    }
}




