using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Dsh.Core;
using Dsh.Host;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Pty;
using Dsh.RemoteHost;
using Dsh.Transport;

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
        IWorkspaceHost alice = await host.ConnectAsync("alice", Ct);
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
        Assert.Equal(ApprovalHints.Impact(seenByAlice.Tool, seenByAlice.Arguments), seenByAlice.Impact);

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

    [Theory(Timeout = 60000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Client_Approval_Cancellation_Is_Not_Rejection_And_Cannot_Be_Overridden(bool allow)
    {
        await using var host = await HarnessHostFixture.CreateAsync("mhc", TestContext.Current.CancellationToken);
        IWorkspaceHost alice = await host.ConnectAsync("alice", Ct);
        var bob = await host.ConnectAsync("bob", Ct);
        var decision = RaiseApprovalAsync(host, host.Environment.Agent, "write");
        var request = Assert.Single((await NextAsync(alice.PendingChangesAsync(Ct),
            change => change.Pending.Approvals.Count > 0)).Pending.Approvals);
        var other = Assert.Single((await NextAsync(bob.PendingChangesAsync(Ct),
            change => change.Pending.Approvals.Count > 0)).Pending.Approvals);
        Assert.Equal(request.RequestId, other.RequestId);

        Assert.True(await alice.RespondApprovalAsync(request.RequestId, allow, cancellationToken: Ct,
            rememberForSession: true, cancelled: true));
        Assert.False(await bob.RespondApprovalAsync(request.RequestId, true, cancellationToken: Ct));
        Assert.Equal(ApprovalOutcome.Cancelled, await decision.WaitAsync(Arrival, Ct));
        foreach (var client in new[] { alice, bob })
        {
            var change = await NextAsync(client.PendingChangesAsync(Ct),
                change => change.Resolution?.RequestId == request.RequestId);
            Assert.Empty(change.Pending.Approvals);
            Assert.Equal(PendingResolutions.Cancelled, change.Resolution!.Resolution);
            Assert.Equal("alice", change.Resolution.ResolvedBy);
            Assert.Equal(alice.Hello.ConnectionId, change.Resolution.ResolvedByConnection);
        }
    }

    [Theory(Timeout = 60000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Question_Answer_And_Dismissal_Are_First_Response_Wins_Across_Clients(bool dismissFirst)
    {
        await using var host = await HarnessHostFixture.CreateAsync("mhq", TestContext.Current.CancellationToken);
        IWorkspaceHost alice = await host.ConnectAsync("alice", Ct);
        var bob = await host.ConnectAsync("bob", Ct);
        var service = host.Environment.App.Ctx.Get<UserQuestionService>(UserQuestionService.ServiceName)!;
        var waiting = service.Ask(new AskUserQuestionRequest(
        [
            new AskUserQuestionItem("q1", "Choose targets", Detail: "Select all required work.", Header: "Targets",
                Options:
                [
                    new AskUserQuestionOption("Tests", "Exercise behavior", "tests", Default: true),
                    new AskUserQuestionOption("Disabled", Value: "disabled", Disabled: true),
                    new AskUserQuestionOption("Docs", Value: "docs"),
                ], MultiSelect: true, AllowCustom: false),
            new AskUserQuestionItem("q2", "Additional notes?"),
        ], host.Environment.Agent), Ct);
        var request = Assert.Single((await NextAsync(alice.PendingChangesAsync(Ct),
            change => change.Pending.Questions.Count > 0)).Pending.Questions);
        var other = Assert.Single((await NextAsync(bob.PendingChangesAsync(Ct),
            change => change.Pending.Questions.Count > 0)).Pending.Questions);
        Assert.Equal(request.RequestId, other.RequestId);
        var remote = request.Questions[0];
        Assert.Equal("Select all required work.", remote.Detail);
        Assert.Equal("Targets", remote.Header);
        Assert.True(remote.MultiSelect);
        Assert.False(remote.AllowCustom);
        Assert.True(request.Questions[1].AllowCustom);
        Assert.Equal(new RemoteQuestionOption("Tests", "Exercise behavior", "tests", Default: true), remote.Options[0]);
        Assert.Equal(new RemoteQuestionOption("Disabled", null, "disabled", Disabled: true), remote.Options[1]);
        Assert.Equal(remote.Options[0], other.Questions[0].Options[0]);
        Assert.Equal(remote.AllowCustom, other.Questions[0].AllowCustom);
        RemoteQuestionAnswerItem[] answers =
        [
            new("q1", ["tests", "docs"], null),
            new("q2", [], "Keep the notes."),
        ];

        Assert.True(await alice.RespondQuestionAsync(request.RequestId,
            dismissFirst ? [new("q1", ["disabled"], "Discard this partial answer")] : answers, Ct,
            status: dismissFirst ? "dismissed" : "answered", reason: dismissFirst ? "discard this reason" : null));
        Assert.False(await bob.RespondQuestionAsync(request.RequestId, answers, Ct,
            status: dismissFirst ? "answered" : "dismissed", reason: "user_cancelled"));
        var result = await waiting.WaitAsync(Arrival, Ct);
        Assert.Equal(dismissFirst, result.IsDismissed);
        Assert.Equal(dismissFirst ? "dismissed" : "answered", result.Status);
        if (dismissFirst)
        {
            Assert.Empty(result.Answers);
            Assert.Equal("user_cancelled", result.Reason);
        }
        else
        {
            Assert.Equal(["tests", "docs"], result.Answers[0].Selected);
            Assert.Equal("Keep the notes.", result.Answers[1].Custom);
            Assert.Null(result.Reason);
        }
        foreach (var client in new[] { alice, bob })
        {
            var change = await NextAsync(client.PendingChangesAsync(Ct),
                change => change.Resolution?.RequestId == request.RequestId);
            Assert.Empty(change.Pending.Questions);
            Assert.Equal(dismissFirst ? PendingResolutions.Dismissed : PendingResolutions.Answered,
                change.Resolution!.Resolution);
            Assert.Equal("alice", change.Resolution.ResolvedBy);
            Assert.Equal(alice.Hello.ConnectionId, change.Resolution.ResolvedByConnection);
        }
    }

    [Theory(Timeout = 60000)]
    [InlineData("unsupported_status")]
    [InlineData("missing_group")]
    [InlineData("missing_question")]
    [InlineData("foreign_question")]
    [InlineData("extra_question")]
    [InlineData("duplicate_question")]
    [InlineData("disabled_option")]
    [InlineData("label_instead_of_value")]
    [InlineData("unknown_option")]
    [InlineData("multiple_single")]
    [InlineData("duplicate_selection")]
    [InlineData("empty_single")]
    [InlineData("empty_multi")]
    [InlineData("forbidden_custom")]
    [InlineData("blank_custom")]
    public async Task Question_InvalidResponseLeavesAuthoritativePending_AndAnotherClientsValidAnswerWins(string invalidCase)
    {
        await using var host = await HarnessHostFixture.CreateAsync("mqv", TestContext.Current.CancellationToken);
        var alice = await host.ConnectAsync("invalid-answer", Ct);
        var bob = await host.ConnectAsync("valid-answer", Ct);
        var service = host.Environment.App.Ctx.Get<UserQuestionService>(UserQuestionService.ServiceName)!;
        var waiting = service.Ask(new(
        [
            new("single", "Choose one", Options:
                [new("Alpha", Value: "a"), new("Beta", Value: "b"), new("Disabled", Value: "disabled", Disabled: true)], AllowCustom: false),
            new("multi", "Choose several", Options:
                [new("X", Value: "x"), new("Fallback"), new("Disabled", Value: "disabled", Disabled: true)], MultiSelect: true, AllowCustom: false),
            new("custom", "Explain"),
        ], host.Environment.Agent), Ct);
        var request = Assert.Single((await NextAsync(alice.PendingChangesAsync(Ct),
            change => change.Pending.Questions.Count > 0)).Pending.Questions);
        Assert.Equal(request.RequestId, Assert.Single((await NextAsync(bob.PendingChangesAsync(Ct),
            change => change.Pending.Questions.Count > 0)).Pending.Questions).RequestId);
        RemoteQuestionAnswerItem[] valid =
        [
            new("single", ["a"], null),
            new("multi", ["x", "Fallback"], null),
            new("custom", [], "Keep these notes"),
        ];
        var answers = valid.ToArray();
        var status = "answered";
        switch (invalidCase)
        {
            case "unsupported_status": status = "unsupported"; break;
            case "missing_group": answers = []; break;
            case "missing_question": answers = answers[..2]; break;
            case "foreign_question": answers[0] = answers[0] with { Id = "unknown" }; break;
            case "extra_question": answers = [.. answers, new("unknown", [], "Unrequested")]; break;
            case "duplicate_question": answers[1] = answers[0]; break;
            case "disabled_option": answers[0] = answers[0] with { Selected = ["disabled"] }; break;
            case "label_instead_of_value": answers[0] = answers[0] with { Selected = ["Alpha"] }; break;
            case "unknown_option": answers[0] = answers[0] with { Selected = ["unknown"] }; break;
            case "multiple_single": answers[0] = answers[0] with { Selected = ["a", "b"] }; break;
            case "duplicate_selection": answers[1] = answers[1] with { Selected = ["x", "x"] }; break;
            case "empty_single": answers[0] = answers[0] with { Selected = [] }; break;
            case "empty_multi": answers[1] = answers[1] with { Selected = [] }; break;
            case "forbidden_custom": answers[0] = answers[0] with { Custom = "Not allowed" }; break;
            case "blank_custom": answers[2] = answers[2] with { Custom = " \t " }; break;
            default: throw new ArgumentException("Unknown invalid response case.", nameof(invalidCase));
        }
        Assert.False(await alice.RespondQuestionAsync(request.RequestId, answers, Ct, status));
        Assert.False(waiting.IsCompleted);
        foreach (var client in new[] { alice, bob })
            Assert.Equal(request.RequestId, Assert.Single((await client.ListPendingAsync(Ct)).Questions).RequestId);
        Assert.True(await bob.RespondQuestionAsync(request.RequestId, valid, Ct));
        Assert.False(await alice.RespondQuestionAsync(request.RequestId, answers, Ct, status));
        var result = await waiting.WaitAsync(Arrival, Ct);
        Assert.Equal("answered", result.Status);
        Assert.Equal(["a"], result.Answers[0].Selected);
        Assert.Equal(["x", "Fallback"], result.Answers[1].Selected);
        Assert.Equal("Keep these notes", result.Answers[2].Custom);
        foreach (var client in new[] { alice, bob })
        {
            var change = await NextAsync(client.PendingChangesAsync(Ct), item => item.Resolution?.RequestId == request.RequestId);
            Assert.Empty(change.Pending.Questions);
            Assert.Equal(PendingResolutions.Answered, change.Resolution!.Resolution);
            Assert.Equal("valid-answer", change.Resolution.ResolvedBy);
        }
    }

    [Fact(Timeout = 60000)]
    public async Task MissingCancellationCapability_RefusesNewOutcomes_KeepsPending_AndAllowsExistingResponses()
    {
        await using var host = await HarnessHostFixture.CreateAsync("mhg", TestContext.Current.CancellationToken);
        var upstream = await host.ConnectAsync("capability-filter", Ct);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var socket = new TcpClient();
        var accept = listener.AcceptTcpClientAsync(Ct);
        await socket.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, Ct);
        using var accepted = await accept;
        await using var peer = new JsonRpcPeer(accepted.GetStream());
        // Only negotiation is filtered. Every decision and pending query reaches the real harness Host.
        peer.RequestHandler = async (message, token) =>
        {
            if (message.Method == HostProtocol.MethodHello)
                return JsonSerializer.SerializeToElement(upstream.Hello with
                {
                    Capabilities = upstream.Hello.Capabilities!.Where(capability => capability != DecisionProtocol.Capability).ToArray(),
                }, HostProtocolJsonContext.Default.HostHelloResponse);
            return await upstream.RequestAsync(message.Method!, message.Params, token);
        };
        peer.Start();
        await using var client = await RemoteHostClient.ConnectAsync(socket.GetStream(), cancellationToken: Ct);
        var approval = RaiseApprovalAsync(host, host.Environment.Agent, "write");
        var question = AskQuestionAsync(host, host.Environment.Agent);
        var waiting = (await NextAsync(upstream.PendingChangesAsync(Ct),
            change => change.Pending.Approvals.Count > 0 && change.Pending.Questions.Count > 0)).Pending;
        var approvalId = Assert.Single(waiting.Approvals).RequestId;
        var questionId = Assert.Single(waiting.Questions).RequestId;

        var cancelError = await Assert.ThrowsAsync<NotSupportedException>(() =>
            client.RespondApprovalAsync(approvalId, false, cancellationToken: Ct, cancelled: true));
        var dismissError = await Assert.ThrowsAsync<NotSupportedException>(() =>
            client.RespondQuestionAsync(questionId, [], Ct, "dismissed", "user_cancelled"));
        Assert.Contains("Unsupported", cancelError.Message, StringComparison.Ordinal);
        Assert.Contains("Unsupported", dismissError.Message, StringComparison.Ordinal);
        var stillPending = await client.ListPendingAsync(Ct);
        Assert.Equal(approvalId, Assert.Single(stillPending.Approvals).RequestId);
        Assert.Equal(questionId, Assert.Single(stillPending.Questions).RequestId);
        Assert.False(approval.IsCompleted);
        Assert.False(question.IsCompleted);

        Assert.True(await client.RespondApprovalAsync(approvalId, false, cancellationToken: Ct));
        Assert.Equal(ApprovalOutcome.Rejected, await approval.WaitAsync(Arrival, Ct));
        Assert.True(await client.RespondQuestionAsync(questionId, [new("q1", ["A"], null)], Ct));
        var answer = await question.WaitAsync(Arrival, Ct);
        Assert.Equal("answered", answer.Status);
        Assert.Equal(["A"], Assert.Single(answer.Answers).Selected);
        var resolved = await client.ListPendingAsync(Ct);
        Assert.Empty(resolved.Approvals);
        Assert.Empty(resolved.Questions);
    }

    [Fact(Timeout = 60000)]
    public async Task Question_Dismissal_Reaches_Model_Facing_Tool_As_Structured_Success()
    {
        await using var host = await HarnessHostFixture.CreateAsync("mht", TestContext.Current.CancellationToken);
        var alice = await host.ConnectAsync("alice", Ct);
        var bob = await host.ConnectAsync("bob", Ct);
        var tools = host.Environment.App.Ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        var waiting = tools.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create("host-dismissal"),
            Name = "ask_user_question",
            Arguments = JsonDocument.Parse("""{"questions":[{"id":"q","question":"Proceed?"}]}""").RootElement,
            Agent = host.Environment.Agent,
            Signal = Ct,
        });
        var request = Assert.Single((await NextAsync(alice.PendingChangesAsync(Ct),
            change => change.Pending.Questions.Count > 0)).Pending.Questions);

        Assert.True(await alice.RespondQuestionAsync(request.RequestId, [], Ct, "dismissed", "user_cancelled"));
        Assert.False(await bob.RespondQuestionAsync(request.RequestId, [new("q", ["yes"], null)], Ct));
        var result = Assert.IsType<ToolExecutionResult.Success>(await waiting.WaitAsync(Arrival, Ct));
        const string expected = """{"status":"dismissed","answers":[],"reason":"user_cancelled"}""";
        Assert.Equal(expected, result.Value.GetRawText());
        Assert.Equal(expected, Assert.IsType<TextBlock>(Assert.Single(result.Content)).Text);
    }

    [Fact(Timeout = 60000)]
    public async Task Host_Question_Cancellation_Remains_Aborted_Not_User_Dismissed()
    {
        await using var host = await HarnessHostFixture.CreateAsync("mhb", TestContext.Current.CancellationToken);
        var alice = await host.ConnectAsync("alice", Ct);
        var bob = await host.ConnectAsync("bob", Ct);
        var waiting = AskQuestionAsync(host, host.Environment.Agent);
        var request = Assert.Single((await NextAsync(alice.PendingChangesAsync(Ct),
            change => change.Pending.Questions.Count > 0)).Pending.Questions);

        host.Backend.Pending.CancelSession(request.SessionId);

        var error = await Assert.ThrowsAsync<UserQuestionException>(async () => await waiting.WaitAsync(Arrival, Ct));
        Assert.Equal(UserQuestionException.AskAborted, error.Code);
        Assert.False(await bob.RespondQuestionAsync(request.RequestId, [], Ct, "dismissed", "user_cancelled"));
        foreach (var client in new[] { alice, bob })
        {
            var change = await NextAsync(client.PendingChangesAsync(Ct),
                change => change.Resolution?.RequestId == request.RequestId);
            Assert.Empty(change.Pending.Questions);
            Assert.Equal(PendingResolutions.Cancelled, change.Resolution!.Resolution);
            Assert.Equal(PendingResolver.Host.Name, change.Resolution.ResolvedBy);
            Assert.Null(change.Resolution.ResolvedByConnection);
        }
    }

    [Fact(Timeout = 60000)]
    public async Task CallerCancellation_RemovesOnlyItsQuestionFromBothClients_AndRejectsLateDismissal()
    {
        await using var host = await HarnessHostFixture.CreateAsync("mhc", TestContext.Current.CancellationToken);
        var alice = await host.ConnectAsync("alice", Ct);
        var bob = await host.ConnectAsync("bob", Ct);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var service = host.Environment.App.Ctx.Get<UserQuestionService>(UserQuestionService.ServiceName)!;
        var cancelled = service.Ask(new([new("cancelled", "First", Options: [new("A")])], host.Environment.Agent), cancellation.Token);
        var surviving = service.Ask(new([new("surviving", "Second", Options: [new("B")])], host.Environment.Agent), Ct);
        var pending = (await NextAsync(alice.PendingChangesAsync(Ct), change => change.Pending.Questions.Count == 2)).Pending;
        var cancelledId = Assert.Single(pending.Questions, request => request.Questions[0].Id == "cancelled").RequestId;
        var survivingId = Assert.Single(pending.Questions, request => request.Questions[0].Id == "surviving").RequestId;

        cancellation.Cancel();

        var error = await Assert.ThrowsAsync<UserQuestionException>(() => cancelled.WaitAsync(Arrival, Ct));
        Assert.Equal(UserQuestionException.AskAborted, error.Code);
        foreach (var client in new[] { alice, bob })
        {
            var change = await NextAsync(client.PendingChangesAsync(Ct), item => item.Resolution?.RequestId == cancelledId);
            Assert.Equal(survivingId, Assert.Single(change.Pending.Questions).RequestId);
            Assert.Equal(PendingResolutions.Cancelled, change.Resolution!.Resolution);
            Assert.Equal(PendingResolver.Host.Name, change.Resolution.ResolvedBy);
            Assert.Null(change.Resolution.ResolvedByConnection);
        }
        Assert.False(await alice.RespondQuestionAsync(cancelledId, [], Ct, "dismissed", "user_cancelled"));
        Assert.True(await bob.RespondQuestionAsync(survivingId, [new("surviving", ["B"], null)], Ct));
        var answer = await surviving.WaitAsync(Arrival, Ct);
        Assert.False(answer.IsDismissed);
        Assert.Equal(["B"], Assert.Single(answer.Answers).Selected);
        Assert.Empty((await alice.ListPendingAsync(Ct)).Questions);
    }

    [Fact(Timeout = 60000)]
    public async Task CallerCancellation_RemovesOnlyItsApprovalFromBothClients_AndRejectsLateAllowance()
    {
        await using var host = await HarnessHostFixture.CreateAsync("mac", TestContext.Current.CancellationToken);
        var alice = await host.ConnectAsync("alice", Ct);
        var bob = await host.ConnectAsync("bob", Ct);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var agent = host.Environment.Agent;
        agent.Session.Append(new TurnStartPayload(1));
        ApprovalService.SetApprovalPolicy(agent.Session, ApprovalPolicy.Ask);
        var service = host.Environment.App.Ctx.Get<ApprovalService>(ApprovalService.ServiceName)!;
        var cancelled = service.Request(new(agent, "bash", ToolCallId.Create("caller-cancel")), cancellation.Token);
        var surviving = RaiseApprovalAsync(host, agent, "write");
        var pending = (await NextAsync(alice.PendingChangesAsync(Ct), change => change.Pending.Approvals.Count == 2)).Pending;
        var cancelledId = Assert.Single(pending.Approvals, request => request.Tool == "bash").RequestId;
        var survivingId = Assert.Single(pending.Approvals, request => request.Tool == "write").RequestId;
        cancellation.Cancel();
        Assert.Equal(ApprovalOutcome.Cancelled, await cancelled.WaitAsync(Arrival, Ct));
        foreach (var client in new[] { alice, bob })
        {
            var change = await NextAsync(client.PendingChangesAsync(Ct), item => item.Resolution?.RequestId == cancelledId);
            Assert.Equal(survivingId, Assert.Single(change.Pending.Approvals).RequestId);
            Assert.Equal(PendingResolutions.Cancelled, change.Resolution!.Resolution);
            Assert.Equal(PendingResolver.Host.Name, change.Resolution.ResolvedBy);
        }
        Assert.False(await alice.RespondApprovalAsync(cancelledId, true, cancellationToken: Ct, rememberForSession: true));
        Assert.False(ApprovalService.HasGrant(agent.Session, "bash"));
        Assert.True(await bob.RespondApprovalAsync(survivingId, false, cancellationToken: Ct));
        Assert.Equal(ApprovalOutcome.Rejected, await surviving.WaitAsync(Arrival, Ct));
        Assert.Empty((await alice.ListPendingAsync(Ct)).Approvals);
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
                decision = pending.AskApprovalAsync(new RemoteApprovalRequest("permission", "agent", "write", null, "allow?"), cancellationToken: Ct);
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
            new ApprovalRequestNotification(request, Ct),
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




