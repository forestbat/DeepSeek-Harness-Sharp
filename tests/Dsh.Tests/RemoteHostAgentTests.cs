using System.Text.Json;
using Dsh.AgentAdapters;
using Dsh.Core;
using Dsh.Fleet;
using Dsh.Gui.ViewModels;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Notifications;
using Dsh.RemoteHost;
using Dsh.Transport;

namespace Dsh.Tests;

/**
 * host.agent.* against a real host with real agent processes.
 * Terminal mode runs a local script as an unknown agent, then the installed Codex and Claude Code up to their first screen (no prompt is ever sent to them).
 * ACP mode runs the installed native ACP agents; only the LlmSmoke tests send prompts to their configured models.
 */
[Collection(SerialProcessCollection.Name)]
public sealed class RemoteHostAgentTests : IDisposable
{
    private static readonly TimeSpan FirstScreen = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Handshake = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Turn = TimeSpan.FromSeconds(5);
    private const string PermissionOptions = """
        [{"optionId":"allow-once","name":"Allow once","kind":"allow_once"},{"optionId":"allow-always","name":"Remember in the provider","kind":"allow_always"},{"optionId":"reject-once","name":"Reject once","kind":"reject_once"}]
        """;
    private const string PermissionPeer = """
        exec 3< "$2"
        while IFS= read -r line; do
          id=${line#*\"id\":}
          id=${id%%,*}
          id=${id%%\}*}
          case "$line" in
            *'"method":"initialize"'*)
              printf '{"jsonrpc":"2.0","id":%s,"result":{"protocolVersion":1,"agentCapabilities":{}}}\n' "$id"
              ;;
            *'"method":"session/new"'*)
              printf '{"jsonrpc":"2.0","id":%s,"result":{"sessionId":"choice-acp"}}\n' "$id"
              ;;
            *'"method":"session/prompt"'*)
              prompt_id=$id
              IFS= read -r permission <&3
              printf '%s\n' "$permission"
              ;;
            *'"id":"choice-permission-'*)
              printf '%s\n' "$line" >> "$1"
              if IFS= read -r permission <&3; then
                printf '%s\n' "$permission"
              else
                printf '{"jsonrpc":"2.0","id":%s,"result":{"stopReason":"end_turn"}}\n' "$prompt_id"
              fi
              ;;
          esac
        done
        """;

    private readonly string _workspace = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "../../../../../artifacts020/test-tmp", $"agent-{Guid.NewGuid():N}"));

    public RemoteHostAgentTests() => Directory.CreateDirectory(_workspace);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
            Directory.Delete(_workspace, recursive: true);
    }

    [Fact(Explicit = true)]
    public async Task UnknownTerminalAgent_PromptTextDoesNotInferWaiting_AndReportsItsExitCode()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("uses a POSIX shell script as the agent");
        await using var host = await HarnessHostFixture.CreateAsync("mat", TestContext.Current.CancellationToken);
        var client = await host.ConnectAsync("terminal", Ct);
        var changes = client.HostSessionChangesAsync(Ct);
        var added = client.Notifications.NotificationChangesAsync(Ct);
        var script = new RemoteAgentCommand("/bin/sh", ["-c", """printf 'Proceed? [y/N] '; read answer; echo "working on $answer"; sleep 3; exit 3"""]);

        var session = await client.Agent.StartAgentAsync(new RemoteAgentStartRequest(
            AgentKinds.Terminal, _workspace, Command: script, Title: "unknown agent"), Ct);
        await Assert.ThrowsAsync<JsonRpcException>(() => client.Agent.PromptAgentAsync(session.SessionId, "y", Ct));
        await using var terminal = RemoteHostPtyTests.TerminalCapture.Attach(client, session.SessionId);
        await terminal.WaitForAsync("Proceed? [y/N]", TimeSpan.FromSeconds(5));
        var atPrompt = Assert.Single(await client.ListHostSessionsAsync(Ct), item => item.SessionId == session.SessionId);
        Assert.Equal((HostSessionStatuses.Running, 0), (atPrompt.Status, atPrompt.PendingCount));
        Assert.Empty((await client.ListPendingAsync(Ct)).Approvals);
        Assert.Empty((await client.ListPendingAsync(Ct)).Questions);
        Assert.Empty((await client.Notifications.ListNotificationsAsync(new RemoteNotifyQuery(SessionId: session.SessionId), Ct)).Items);
        await terminal.SendAsync("y\r");
        await terminal.WaitForAsync("working on y", TimeSpan.FromSeconds(5));
        var running = (await RemoteHostMultiClientTests.NextAsync(changes, item => item.Session?.SessionId == session.SessionId && item.Session.Status == HostSessionStatuses.Running)).Session!;
        var exited = (await RemoteHostMultiClientTests.NextAsync(changes, item => item.Session?.SessionId == session.SessionId && item.Session.Status == HostSessionStatuses.Exited)).Session!;
        var exitNotice = (await RemoteHostMultiClientTests.NextAsync(added, item => item.Added?.SessionId == session.SessionId && item.Added.Kind == NotificationKind.SessionExited)).Added!;

        Assert.Equal((HostSessionKinds.External, AgentKinds.Terminal, _workspace), (session.Kind, session.AgentKind, session.ProjectPath));
        Assert.Equal("unknown agent", atPrompt.Title);
        Assert.Null(running.ExitCode);
        Assert.Equal(3, exited.ExitCode);
        Assert.Equal("exit code 3", exitNotice.Body);
    }

    [Fact(Explicit = true)]
    public async Task CustomTerminalAgent_SurvivesHostRestart_WithoutExposingItsLaunchSecrets()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("uses a POSIX shell script as the agent");
        await using var host = await HarnessHostFixture.CreateAsync("mas", TestContext.Current.CancellationToken);
        var request = new RemoteAgentStartRequest(AgentKinds.Terminal, _workspace, Title: "private agent",
            Command: new RemoteAgentCommand("/bin/sh",
                ["-c", """pwd > agent-cwd.txt; printf 'environment=%s argv=%s\n' "$PRIVATE_AGENT_TOKEN" "$1"; read answer; printf 'received=%s\n' "$answer"; exit 7""", "agent", "private-argument-42"]),
            Environment: new Dictionary<string, string> { ["PRIVATE_AGENT_TOKEN"] = "private-environment-42" });
        RemoteSessionSummary session;
        await using (var transport = await RemoteHostConnector.ConnectAsync(host.Root, Ct))
        await using (var peer = new JsonRpcPeer(transport))
        {
            var pushed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            peer.NotificationReceived += message =>
            {
                if (message.Method == HostProtocol.NotificationSessionsChanged && message.Params is { } parameters
                    && parameters.GetRawText().Contains("private agent", StringComparison.Ordinal))
                    pushed.TrySetResult(parameters.GetRawText());
            };
            peer.Start();
            var started = (await peer.RequestAsync(AgentProtocol.MethodAgentStart,
                JsonSerializer.SerializeToElement(request, AgentProtocolJsonContext.Default.RemoteAgentStartRequest), Ct))!.Value;
            session = JsonSerializer.Deserialize(started, AgentProtocolJsonContext.Default.RemoteSessionSummary)!;
            var listed = (await peer.RequestAsync(HostProtocol.MethodSessionsList, cancellationToken: Ct))!.Value;
            foreach (var json in new[] { started.GetRawText(), listed.GetRawText(), await pushed.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct) })
            {
                Assert.DoesNotContain("private-argument-42", json);
                Assert.DoesNotContain("private-environment-42", json);
                Assert.DoesNotContain("PRIVATE_AGENT_TOKEN", json);
            }
        }
        var client = await host.ConnectAsync("terminal-before", Ct);
        await using (var terminal = RemoteHostPtyTests.TerminalCapture.Attach(client, session.SessionId))
        {
            await terminal.WaitForAsync("environment=private-environment-42 argv=private-argument-42", TimeSpan.FromSeconds(5));
            Assert.Equal(_workspace, (await File.ReadAllTextAsync(Path.Combine(_workspace, "agent-cwd.txt"), Ct)).Trim());
        }
        await host.RestartHostAsync(Ct);
        var after = await host.ConnectAsync("terminal-after", Ct);
        var restored = Assert.Single(await after.ListHostSessionsAsync(Ct), item => item.SessionId == session.SessionId);
        Assert.Equal((HostSessionStatuses.Running, "private agent", _workspace), (restored.Status, restored.Title, restored.ProjectPath));
        var changes = after.HostSessionChangesAsync(Ct);
        await using var reattached = RemoteHostPtyTests.TerminalCapture.Attach(after, session.SessionId);
        await reattached.SendAsync("reconnected\r");
        await reattached.WaitForAsync("received=reconnected", TimeSpan.FromSeconds(5));
        var exited = (await RemoteHostMultiClientTests.NextAsync(changes,
            item => item.Session?.SessionId == session.SessionId && item.Session.Status == HostSessionStatuses.Exited)).Session!;
        Assert.Equal(7, exited.ExitCode);
    }

    [Fact(Explicit = true)]
    public async Task FailedAcpHandshake_DoesNotCreateASessionOrHistory()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("uses a POSIX process that exits before the ACP handshake");
        await using var host = await HarnessHostFixture.CreateAsync("maf", TestContext.Current.CancellationToken);
        var client = await host.ConnectAsync("failed-acp", Ct);
        var original = await client.ListSessionsAsync(Ct);
        await Assert.ThrowsAsync<JsonRpcException>(() => client.Agent.StartAgentAsync(
            new RemoteAgentStartRequest(AgentKinds.Acp, _workspace,
                Command: new RemoteAgentCommand("/bin/sh", ["-c", "exit 9"])), Ct));
        Assert.Empty(await client.ListHostSessionsAsync(Ct));
        Assert.Equal(original, await client.ListSessionsAsync(Ct));
    }

    [Fact(Explicit = true)]
    public async Task HarnessSession_StartsThroughAgentStart_InANewOrAnExistingWorktree()
    {
        using var repo = new GitTestRepository();
        repo.Commit(("a.txt", "one\n"));
        await using var host = await HarnessHostFixture.CreateAsync("mah", TestContext.Current.CancellationToken);
        var client = await host.ConnectAsync("harness", Ct);
        await HarnessHostFixture.OpenProjectAsync(client, repo.Sandbox, Ct);
        var worktree = new RemoteWorktreeChoice("../harness-wt", NewBranch: "harness-task");
        var request = new RemoteAgentStartRequest(HostAgentKinds.Dsharp, repo.Root, Worktree: worktree);

        var first = await client.Agent.StartAgentAsync(request, Ct);
        var second = await client.Agent.StartAgentAsync(request, Ct);
        var worktrees = await client.Git.ListWorktreesAsync(repo.Root, Ct);

        var expected = Path.Combine(repo.Sandbox, "harness-wt");
        Assert.Equal((HostSessionKinds.Harness, HostAgentKinds.Dsharp, expected), (first.Kind, first.AgentKind, first.ProjectPath));
        Assert.Equal(expected, second.ProjectPath);
        Assert.NotEqual(first.SessionId, second.SessionId);
        Assert.Equal(("harness-task", 2), (Assert.Single(worktrees, item => item.Path == expected).Branch, worktrees.Count));
    }

    [Theory(Timeout = 5000)]
    [InlineData(AgentKinds.Codex)]
    [InlineData(AgentKinds.ClaudeCode)]
    public async Task BuiltInTerminalAgent_ReachesItsFirstScreen_AndStopsWithoutAPrompt(string agentKind)
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("the PTY agents run on POSIX hosts in these tests");
        await using var host = await HarnessHostFixture.CreateAsync("mab", TestContext.Current.CancellationToken);
        var client = await host.ConnectAsync("builtin", Ct);
        if (!(await client.Agent.AgentCatalogAsync(Ct)).Single(entry => entry.AgentKind == agentKind).SupportsTerminal)
            Assert.Skip($"{agentKind} is not installed on this machine");
        var changes = client.HostSessionChangesAsync(Ct);

        var session = await client.Agent.StartAgentAsync(new RemoteAgentStartRequest(agentKind, _workspace, Columns: 120, Rows: 40), Ct);
        await using var terminal = RemoteHostPtyTests.TerminalCapture.Attach(client, session.SessionId);
        await terminal.WaitForAsync(agentKind == AgentKinds.Codex ? "Codex" : "Claude", FirstScreen);
        var running = Assert.Single(await client.ListHostSessionsAsync(Ct), item => item.SessionId == session.SessionId);
        Assert.Equal((HostSessionStatuses.Running, 0), (running.Status, running.PendingCount));
        await client.Agent.StopAgentAsync(session.SessionId, Ct);
        var exited = (await RemoteHostMultiClientTests.NextAsync(changes, item => item.Session?.SessionId == session.SessionId && item.Session.Status == HostSessionStatuses.Exited)).Session!;

        Assert.Equal((HostSessionKinds.External, agentKind), (session.Kind, session.AgentKind));
        Assert.Equal(_workspace, running.ProjectPath);
        Assert.Equal(HostSessionStatuses.Exited, exited.Status);
    }

    [Theory(Explicit = true)]
    [InlineData(AgentKinds.OpenCode)]
    [InlineData(AgentKinds.KiloCode)]
    public async Task BuiltInAcp_StopsAndResumesTheSameSession_AlsoAfterAHostRestart(string agentKind)
    {
        RequireAcpAdapter(agentKind);
        await using var host = await HarnessHostFixture.CreateAsync("maa", TestContext.Current.CancellationToken);
        var client = await host.ConnectAsync("acp", Ct);
        var changes = client.HostSessionChangesAsync(Ct);

        var session = await client.Agent.StartAgentAsync(new RemoteAgentStartRequest(agentKind, _workspace, Mode: RemoteAgentModes.Acp), Ct);
        await client.SessionEditing.RenameSessionAsync(session.SessionId, "renamed acp", Ct);
        await client.Agent.StopAgentAsync(session.SessionId, Ct);
        var stopped = (await RemoteHostMultiClientTests.NextAsync(changes, item => item.Session?.SessionId == session.SessionId && item.Session.Status == HostSessionStatuses.Exited)).Session!;
        var resumed = await client.Agent.ResumeAgentAsync(session.SessionId, Ct);
        var resumedSnapshot = await client.GetSessionSnapshotAsync(session.SessionId, Ct);
        await host.RestartHostAsync(Ct);
        var after = await host.ConnectAsync("after", Ct);
        var restored = Assert.Single(await after.ListHostSessionsAsync(Ct), item => item.SessionId == session.SessionId);
        var coldSnapshot = await after.GetSessionSnapshotAsync(session.SessionId, Ct);
        Assert.Equal(resumedSnapshot.Header, coldSnapshot.Header);
        Assert.Equal(resumedSnapshot.Events, coldSnapshot.Events);
        var reopened = await after.Agent.ResumeAgentAsync(session.SessionId, Ct);
        await after.Agent.StopAgentAsync(session.SessionId, Ct);

        Assert.Equal((HostSessionKinds.Acp, agentKind, HostSessionStatuses.Idle), (session.Kind, session.AgentKind, session.Status));
        Assert.Equal(HostSessionStatuses.Exited, stopped.Status);
        Assert.Equal((session.SessionId, HostSessionStatuses.Idle), (resumed.SessionId, resumed.Status));
        Assert.Equal(HostSessionStatuses.Exited, restored.Status);
        Assert.Equal((session.SessionId, HostSessionStatuses.Idle, "renamed acp"), (reopened.SessionId, reopened.Status, reopened.Title));
    }

    [Theory(Timeout = 60000)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task CustomAcp_ApprovalCancellation_Preserves_Cancelled_Wire_Outcome(bool cancelled, bool allow)
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("uses a POSIX shell as an ACP wire peer");
        await using var host = await HarnessHostFixture.CreateAsync("mcc", TestContext.Current.CancellationToken);
        var alice = await host.ConnectAsync("alice", Ct);
        var bob = await host.ConnectAsync("bob", Ct);
        var responsePath = Path.Combine(_workspace, "permission-response.json");
        // This process is an ACP wire peer; it does not simulate or call a model.
        var session = await StartPermissionPeerAsync(alice, responsePath, PermissionRequest("cancel", null, PermissionOptions));
        await alice.Agent.PromptAgentAsync(session.SessionId, "Request the protocol permission.", Ct);
        var request = Assert.Single((await RemoteHostMultiClientTests.NextAsync(alice.PendingChangesAsync(Ct),
            change => change.Pending.Approvals.Count > 0)).Pending.Approvals);
        var other = Assert.Single((await RemoteHostMultiClientTests.NextAsync(bob.PendingChangesAsync(Ct),
            change => change.Pending.Approvals.Count > 0)).Pending.Approvals);
        Assert.Equal(request, other);

        Assert.True(await alice.RespondApprovalAsync(request.RequestId, allow, cancellationToken: Ct,
            rememberForSession: true, cancelled: cancelled));
        Assert.False(await bob.RespondApprovalAsync(request.RequestId, true, cancellationToken: Ct));
        await ReadEventsAsync(alice, session.SessionId, item => item.Data is TurnEndPayload, cancellationToken: Ct);
        using var response = JsonDocument.Parse(await File.ReadAllTextAsync(responsePath, Ct));
        var outcome = response.RootElement.GetProperty("result").GetProperty("outcome");
        Assert.Equal(cancelled ? "cancelled" : "selected", outcome.GetProperty("outcome").GetString());
        if (cancelled)
            Assert.False(outcome.TryGetProperty("optionId", out _));
        else
            Assert.Equal("reject-once", outcome.GetProperty("optionId").GetString());
        foreach (var client in new[] { alice, bob })
        {
            var change = await RemoteHostMultiClientTests.NextAsync(client.PendingChangesAsync(Ct),
                change => change.Resolution?.RequestId == request.RequestId);
            Assert.Empty(change.Pending.Approvals);
            Assert.Equal(cancelled ? PendingResolutions.Cancelled : PendingResolutions.Rejected,
                change.Resolution!.Resolution);
        }
        await alice.Agent.StopAgentAsync(session.SessionId, Ct);
    }

    [Fact(Timeout = 60000)]
    public async Task CustomAcp_OnceCannotSelectProviderAlways_AndUnsupportedResponseLeavesItPending()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("uses a POSIX shell as an ACP wire peer");
        await using var host = await HarnessHostFixture.CreateAsync("mco", TestContext.Current.CancellationToken);
        var client = await host.ConnectAsync("approval", Ct);
        var responsePath = Path.Combine(_workspace, "once-response.jsonl");
        const string options = """[{"optionId":"provider-always","name":"Allow permanently","kind":"allow_always"},{"optionId":"reject","name":"Reject once","kind":"reject_once"}]""";
        var session = await StartPermissionPeerAsync(client, responsePath, PermissionRequest("once", "edit_file", options));
        await client.Agent.PromptAgentAsync(session.SessionId, "Request the protocol permission.", Ct);
        var request = Assert.Single((await RemoteHostMultiClientTests.NextAsync(client.PendingChangesAsync(Ct),
            change => change.Pending.Approvals.Count > 0)).Pending.Approvals);
        Assert.False(await client.RespondApprovalAsync(request.RequestId, true, cancellationToken: Ct));
        Assert.Equal(request.RequestId, Assert.Single((await client.ListPendingAsync(Ct)).Approvals).RequestId);
        Assert.True(await client.RespondApprovalAsync(request.RequestId, false, cancellationToken: Ct, cancelled: true));
        await ReadEventsAsync(client, session.SessionId, item => item.Data is TurnEndPayload, cancellationToken: Ct);
        using var response = JsonDocument.Parse(await File.ReadAllTextAsync(responsePath, Ct));
        Assert.Equal("cancelled", response.RootElement.GetProperty("result").GetProperty("outcome").GetProperty("outcome").GetString());
    }

    [Fact(Timeout = 60000)]
    public async Task CustomAcp_MissingStableToolName_CannotRememberAnUnscopedGrant()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("uses a POSIX shell as an ACP wire peer");
        await using var host = await HarnessHostFixture.CreateAsync("mcn", TestContext.Current.CancellationToken);
        var client = await host.ConnectAsync("approval", Ct);
        var responsePath = Path.Combine(_workspace, "unnamed-response.jsonl");
        var session = await StartPermissionPeerAsync(client, responsePath, PermissionRequest("unnamed", null, PermissionOptions));
        await client.Agent.PromptAgentAsync(session.SessionId, "Request the protocol permission.", Ct);
        var request = Assert.Single((await RemoteHostMultiClientTests.NextAsync(client.PendingChangesAsync(Ct),
            change => change.Pending.Approvals.Count > 0)).Pending.Approvals);
        Assert.False(await client.RespondApprovalAsync(request.RequestId, true, cancellationToken: Ct, rememberForSession: true));
        Assert.Equal(request.RequestId, Assert.Single((await client.ListPendingAsync(Ct)).Approvals).RequestId);
        Assert.True(await client.RespondApprovalAsync(request.RequestId, true, cancellationToken: Ct));
        var events = await ReadEventsAsync(client, session.SessionId, item => item.Data is TurnEndPayload, cancellationToken: Ct);
        Assert.Empty(events.Select(item => item.Data).OfType<ApprovalGrantedPayload>());
        using var response = JsonDocument.Parse(await File.ReadAllTextAsync(responsePath, Ct));
        Assert.Equal("allow-once", response.RootElement.GetProperty("result").GetProperty("outcome").GetProperty("optionId").GetString());
    }

    [Fact(Timeout = 60000)]
    public async Task CustomAcp_AlwaysUsesOnlyTheCurrentHostSessionTool_AndNeverProviderAlways()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("uses a POSIX shell as an ACP wire peer");
        await using var host = await HarnessHostFixture.CreateAsync("mca", TestContext.Current.CancellationToken);
        var client = await host.ConnectAsync("approval", Ct);
        var responsePath = Path.Combine(_workspace, "session-responses.jsonl");
        var first = PermissionRequest("first", "edit_file", PermissionOptions);
        var session = await StartPermissionPeerAsync(client, responsePath, first,
            PermissionRequest("second", "edit_file", PermissionOptions), PermissionRequest("other", "write_other_file", PermissionOptions));
        await client.Agent.PromptAgentAsync(session.SessionId, "Request the protocol permissions.", Ct);
        var request = Assert.Single((await RemoteHostMultiClientTests.NextAsync(client.PendingChangesAsync(Ct),
            change => change.Pending.Approvals.Count > 0)).Pending.Approvals);
        Assert.True(await client.RespondApprovalAsync(request.RequestId, true, cancellationToken: Ct, rememberForSession: true));
        var other = Assert.Single((await RemoteHostMultiClientTests.NextAsync(client.PendingChangesAsync(Ct),
            change => change.Pending.Approvals.Any(item => item.RequestId != request.RequestId))).Pending.Approvals);
        using var target = JsonDocument.Parse(other.Arguments!);
        Assert.Equal("other.txt", target.RootElement.GetProperty("path").GetString());
        Assert.Equal("write_other_file", other.Tool);
        Assert.True(await client.RespondApprovalAsync(other.RequestId, false, cancellationToken: Ct));
        var events = await ReadEventsAsync(client, session.SessionId, item => item.Data is TurnEndPayload, cancellationToken: Ct);
        Assert.Equal("edit_file", Assert.Single(events.Select(item => item.Data).OfType<ApprovalGrantedPayload>()).ToolName);
        var responses = await File.ReadAllLinesAsync(responsePath, Ct);
        var selected = responses.Select(line =>
        {
            using var response = JsonDocument.Parse(line);
            return response.RootElement.GetProperty("result").GetProperty("outcome").GetProperty("optionId").GetString();
        }).ToArray();
        Assert.Equal(["allow-once", "allow-once", "reject-once"], selected);
        var separate = await StartPermissionPeerAsync(client, Path.Combine(_workspace, "separate-response.jsonl"), first);
        await client.Agent.PromptAgentAsync(separate.SessionId, "Request the same tool in another session.", Ct);
        var separateRequest = Assert.Single((await RemoteHostMultiClientTests.NextAsync(client.PendingChangesAsync(Ct),
            change => change.Pending.Approvals.Any(item => item.SessionId == separate.SessionId))).Pending.Approvals);
        Assert.Equal("edit_file", separateRequest.Tool);
        Assert.True(await client.RespondApprovalAsync(separateRequest.RequestId, false, cancellationToken: Ct, cancelled: true));
        await ReadEventsAsync(client, separate.SessionId, item => item.Data is TurnEndPayload, cancellationToken: Ct);
    }

    [Theory(Timeout = 60000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CustomAcp_PendingCard_PreservesActionsAfterAnUnsupportedReply(bool advertiseAvailability)
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("uses a POSIX shell as an ACP wire peer");
        await using var host = await HarnessHostFixture.CreateAsync("mcp", TestContext.Current.CancellationToken);
        var client = await host.ConnectAsync("card", Ct);
        var session = await StartPermissionPeerAsync(client, Path.Combine(_workspace, "card-response.jsonl"),
            PermissionRequest("card", null, PermissionOptions));
        await client.Agent.PromptAgentAsync(session.SessionId, "Request the protocol permission.", Ct);
        var request = Assert.Single((await RemoteHostMultiClientTests.NextAsync(client.PendingChangesAsync(Ct),
            change => change.Pending.Approvals.Count > 0)).Pending.Approvals);
        await using var connection = new MachineConnection(MachineSpec.Local(host.Root), FleetConnectionTests.TestOptions);
        connection.Start();
        await connection.WaitConnectedAsync(Ct);
        var shown = advertiseAvailability ? request : request with { Availability = null };
        var card = new PendingDecisionViewModel(new("local", request.RequestId, session.SessionId, PendingKinds.Approval, shown, null), () => connection);
        var superseded = 0;
        card.Superseded += _ => superseded++;
        if (advertiseAvailability)
            Assert.False(card.AlwaysCommand.CanExecute(null));
        await card.AlwaysCommand.ExecuteAsync(null);
        Assert.False(card.IsBusy);
        Assert.Equal(0, superseded);
        Assert.Equal(request.RequestId, Assert.Single((await client.ListPendingAsync(Ct)).Approvals).RequestId);
        Assert.True(card.AllowCommand.CanExecute(null));
        await card.AllowCommand.ExecuteAsync(null);
        Assert.False(card.AllowCommand.CanExecute(null));
        Assert.Empty((await client.ListPendingAsync(Ct)).Approvals);
        await ReadEventsAsync(client, session.SessionId, item => item.Data is TurnEndPayload, cancellationToken: Ct);
    }

    [Theory(Explicit = true)]
    [InlineData(AgentKinds.OpenCode)]
    [InlineData(AgentKinds.KiloCode)]
    [Trait("Category", "LlmSmoke")]
    public async Task BuiltInAcp_PromptTurn_PersistsAndRemainsQueryableAcrossRestart(string agentKind)
    {
        RequireAcpAdapter(agentKind);
        await using var host = await HarnessHostFixture.CreateAsync("map", TestContext.Current.CancellationToken);
        var client = await host.ConnectAsync("acp", Ct);
        var session = await client.Agent.StartAgentAsync(new RemoteAgentStartRequest(agentKind, _workspace,
            InitialPrompt: "Reply with exactly the word pong and nothing else. Do not use any tools.", Mode: RemoteAgentModes.Acp), Ct);
        var events = await ReadEventsAsync(client, session.SessionId, item => item.Data is TurnEndPayload, cancellationToken: Ct);
        var idle = (await RemoteHostMultiClientTests.NextAsync(client.HostSessionChangesAsync(Ct), item => item.Session?.SessionId == session.SessionId && item.Session.Status == HostSessionStatuses.Idle)).Session!;

        Assert.IsType<TurnStartPayload>(events[0].Data);
        Assert.IsType<UserMessagePayload>(events[1].Data);
        Assert.Contains(events, item => item.Data is AssistantChunkPayload);
        var reply = string.Concat(events.Select(item => item.Data).OfType<AssistantMessagePayload>().SelectMany(payload => payload.Message.Content).OfType<TextBlock>().Select(block => block.Text));
        Assert.Contains("pong", reply, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<TurnEndReason.Completed>(Assert.IsType<TurnEndPayload>(events[^1].Data).Reason);
        Assert.Equal(HostSessionStatuses.Idle, idle.Status);
        await AssertHistoryAcrossRestartAsync(host, session.SessionId, events, "pong");
    }

    [Fact(Explicit = true)]
    [Trait("Category", "LlmSmoke")]
    public async Task OpenCodeAcp_PermissionRequest_ReachesEveryClient_AndTheFirstAnswerWins()
    {
        RequireAcpAdapter(AgentKinds.OpenCode);
        await using var host = await HarnessHostFixture.CreateAsync("mar", TestContext.Current.CancellationToken);
        var alice = await host.ConnectAsync("alice", Ct);
        var bob = await host.ConnectAsync("bob", Ct);
        // Inline OpenCode config for the agent process only: shell commands ask for permission instead of running.
        var askForShell = new Dictionary<string, string> { ["OPENCODE_CONFIG_CONTENT"] = """{"permission":{"bash":"ask","edit":"ask"}}""" };
        var session = await alice.Agent.StartAgentAsync(new RemoteAgentStartRequest(AgentKinds.OpenCode, _workspace, Mode: RemoteAgentModes.Acp, Environment: askForShell), Ct);

        await alice.Agent.PromptAgentAsync(session.SessionId, "Run exactly this shell command once: echo dsh-acp-probe > acp-probe.txt", Ct);
        var request = Assert.Single((await RemoteHostMultiClientTests.NextAsync(alice.PendingChangesAsync(Ct), change => change.Pending.Approvals.Count > 0, Turn)).Pending.Approvals);
        var seenByBob = Assert.Single((await RemoteHostMultiClientTests.NextAsync(bob.PendingChangesAsync(Ct), change => change.Pending.Approvals.Count > 0)).Pending.Approvals);
        var allowed = await alice.RespondApprovalAsync(request.RequestId, true, cancellationToken: Ct);
        var late = await bob.RespondApprovalAsync(request.RequestId, false, cancellationToken: Ct);

        Assert.Equal(request, seenByBob);
        Assert.Equal(session.SessionId, request.SessionId);
        Assert.Equal((true, false), (allowed, late));
        var events = await ReadApprovedTurnAsync(bob, session.SessionId, request.RequestId);
        Assert.IsType<TurnEndReason.Completed>(Assert.IsType<TurnEndPayload>(events[^1].Data).Reason);
        Assert.Contains(events, item => item.Data is ToolCallPayload);
        Assert.Contains(events, item => item.Data is ToolResultPayload { Error: null });
        Assert.Equal("dsh-acp-probe", (await File.ReadAllTextAsync(Path.Combine(_workspace, "acp-probe.txt"), Ct)).Trim());
        await AssertHistoryAcrossRestartAsync(host, session.SessionId, events, "acp-probe");
    }

    /** A real model may request another tool (for example, reading back its output) after the first approval. */
    private static async Task<List<SessionEvent>> ReadApprovedTurnAsync(RemoteHostClient client, string sessionId, string answeredRequestId)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var approvals = AnswerApprovalsAsync();
        var events = ReadEventsAsync(client, sessionId, item => item.Data is TurnEndPayload, cancellationToken: lifetime.Token);
        try
        {
            var completed = await Task.WhenAny(events, approvals);
            await completed;
            return await events;
        }
        finally
        {
            await lifetime.CancelAsync();
            try
            {
                await Task.WhenAll(events, approvals);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
        }

        async Task AnswerApprovalsAsync()
        {
            var answered = new HashSet<string>(StringComparer.Ordinal) { answeredRequestId };
            await foreach (var change in client.PendingChangesAsync(lifetime.Token))
                foreach (var request in change.Pending.Approvals)
                    if (request.SessionId == sessionId && answered.Add(request.RequestId))
                        await client.RespondApprovalAsync(request.RequestId, true, cancellationToken: lifetime.Token);
            throw new InvalidOperationException("the pending stream ended before the ACP turn completed");
        }
    }

    /** Preserve the complete ACP history across application recomposition, then continue the same conversation. */
    private static async Task AssertHistoryAcrossRestartAsync(HarnessHostFixture host, string sessionId, IReadOnlyList<SessionEvent> events, string queryText)
    {
        var id = SessionId.Create(sessionId);
        var expected = events.Select(item => DshJson.Serialize(item)).ToArray();
        await host.StopHostAsync();
        var persistence = host.Environment.App.Ctx.Get<ISessionPersistence>(ISessionPersistence.ServiceName)!;
        var snapshot = Assert.Single(persistence.List(), item => item.Header.Id == id);
        using (var stored = persistence.Open(id, SessionAccess.Read))
            Assert.Equal(expected, stored.Read().Select(item => DshJson.Serialize(item)));
        AssertSearchable(host, sessionId, queryText, events);

        await host.RestartHostAsync(Ct);
        var client = await host.ConnectAsync("history", Ct);
        var restored = Assert.Single(await client.ListHostSessionsAsync(Ct), item => item.SessionId == sessionId);
        Assert.Equal((sessionId, HostSessionKinds.Acp, HostSessionStatuses.Exited), (restored.SessionId, restored.Kind, restored.Status));
        Assert.Equal(snapshot.Header.Cwd, restored.ProjectPath);
        var reopened = host.Environment.App.Ctx.Get<ISessionPersistence>(ISessionPersistence.ServiceName)!;
        Assert.Single(reopened.List(), item => item.Header.Id == id);
        IReadOnlyList<SessionEvent> history;
        using (var stored = reopened.Open(id, SessionAccess.Read))
        {
            Assert.Equal(snapshot.Header, stored.Header);
            history = stored.Read();
        }
        Assert.Equal(expected, history.Select(item => DshJson.Serialize(item)));
        Assert.Equal(Enumerable.Range(0, history.Count).Select(seq => (long)seq), history.Select(item => item.Seq));
        Assert.Equal(expected, (await ReadEventsAsync(client, sessionId, item => item.Seq == history[^1].Seq)).Select(item => DshJson.Serialize(item)));
        AssertSearchable(host, sessionId, queryText, history);

        var resumed = await client.Agent.ResumeAgentAsync(sessionId, Ct);
        Assert.Equal((sessionId, HostSessionStatuses.Idle), (resumed.SessionId, resumed.Status));
        var resumedSnapshot = await client.GetSessionSnapshotAsync(sessionId, Ct);
        Assert.Equal(expected, resumedSnapshot.Events.Take(history.Count).Select(item => DshJson.Serialize(item)));
        var resumedHistory = resumedSnapshot.Events.ToArray();
        var marker = $"acpresumed{Guid.NewGuid():N}";
        await client.Agent.PromptAgentAsync(sessionId, $"Reply with exactly {marker} and nothing else. Do not use any tools.", Ct);
        var continued = await ReadEventsAsync(client, sessionId, item => item.Data is TurnEndPayload, resumedHistory.Length);
        Assert.IsType<TurnStartPayload>(continued[0].Data);
        Assert.IsType<UserMessagePayload>(continued[1].Data);
        Assert.Contains(continued, item => item.Data is AssistantMessagePayload assistant
            && assistant.Message.Content.OfType<TextBlock>().Any(block => block.Text.Contains(marker, StringComparison.Ordinal)));
        Assert.IsType<TurnEndReason.Completed>(Assert.IsType<TurnEndPayload>(continued[^1].Data).Reason);
        AssertSearchable(host, sessionId, marker, continued, SessionEventTypes.AssistantMessage);
        await host.StopHostAsync();
        using (var stored = reopened.Open(id, SessionAccess.Read))
        {
            Assert.Equal(snapshot.Header, stored.Header);
            Assert.Equal(resumedHistory.Concat(continued).Select(item => DshJson.Serialize(item)), stored.Read().Select(item => DshJson.Serialize(item)));
        }
        AssertSearchable(host, sessionId, queryText, events);
        AssertSearchable(host, sessionId, marker, continued, SessionEventTypes.AssistantMessage);
    }

    private static void AssertSearchable(HarnessHostFixture host, string sessionId, string text, IReadOnlyList<SessionEvent> events, string? eventType = null)
    {
        var query = host.Environment.App.Ctx.Get<ISessionQueryService>(ISessionQueryService.ServiceName)!;
        Assert.Contains(query.Search(text), hit => hit.SessionId == sessionId
            && (eventType is null || hit.Type == eventType)
            && events.Any(item => item.Seq == hit.Seq && item.Type == hit.Type));
    }

    private static string PermissionRequest(string id, string? toolName, string options)
        => $$$"""{"jsonrpc":"2.0","id":"choice-permission-{{{id}}}","method":"session/request_permission","params":{"sessionId":"choice-acp","toolCall":{"toolCallId":"tool-{{{id}}}","name":{{{JsonSerializer.Serialize(toolName)}}},"title":"Update file","kind":"edit","rawInput":{"path":"{{{id}}}.txt"}},"options":{{{options}}}}}""";

    private async Task<RemoteSessionSummary> StartPermissionPeerAsync(RemoteHostClient client, string responsePath, params string[] requests)
    {
        var requestsPath = $"{responsePath}.requests.jsonl";
        await File.WriteAllLinesAsync(requestsPath, requests, Ct);
        return await client.Agent.StartAgentAsync(new RemoteAgentStartRequest(
            AgentKinds.Acp, _workspace, Mode: RemoteAgentModes.Acp,
            Command: new RemoteAgentCommand("/bin/sh", ["-c", PermissionPeer, "choice-acp-peer", responsePath, requestsPath])), Ct);
    }

    private static void RequireAcpAdapter(string agentKind)
    {
        var adapter = Assert.IsType<AgentAdapter>(AgentAdapterCatalog.Find(agentKind));
        if (AgentExecutableLocator.Resolve(adapter.Acp!.Executable) is null)
            Assert.Skip($"{adapter.DisplayName} is not installed on this machine");
    }

    /** Read a bounded portion of the public event stream, checking its session identity and contiguous sequence numbers. */
    private static async Task<List<SessionEvent>> ReadEventsAsync(RemoteHostClient client, string sessionId, Func<SessionEvent, bool> until, long fromSeq = 0, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct, cancellationToken);
        timeout.CancelAfter(Turn + Handshake);
        var events = new List<SessionEvent>();
        await foreach (var item in client.SubscribeAsync(sessionId, fromSeq, timeout.Token))
        {
            Assert.Equal(sessionId, item.SessionId);
            var sessionEvent = item.Event!;
            Assert.Equal(fromSeq + events.Count, sessionEvent.Seq);
            events.Add(sessionEvent);
            if (until(sessionEvent))
                return events;
        }
        throw new InvalidOperationException("the event stream ended before the expected session event");
    }
}



