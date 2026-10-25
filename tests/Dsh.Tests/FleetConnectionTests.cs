using Dsh.Core;
using Dsh.Fleet;
using Dsh.Interaction;
using Dsh.RemoteHost;

namespace Dsh.Tests;

/**
 * Dsh.Fleet against real hosts: two independent harness homes, each with its own host (real harness backend + loopback listener), act as two machines.
 * Covers the aggregated session and pending views, answering through the fleet, reconnecting after a host restart, and resuming a session subscription from the last seq.
 */
[Collection(SerialProcessCollection.Name)]
public sealed class FleetConnectionTests
{
    private static readonly TimeSpan Arrival = TimeSpan.FromSeconds(5);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /**
     * Fast heartbeat and backoff so that drops are noticed and repaired within the test's time budget.
     * The ping timeout stays at the default: a stopped host closes the connection and fails the next ping at once, while a short timeout drops healthy links on a loaded machine.
     */
    internal static readonly FleetOptions TestOptions = new()
    {
        ClientName = "fleet-test",
        HeartbeatInterval = TimeSpan.FromMilliseconds(200),
        InitialBackoff = TimeSpan.FromMilliseconds(100),
        MaxBackoff = TimeSpan.FromSeconds(1),
    };

    [Fact(Explicit = true)]
    public async Task Connected_Exposes_Preexisting_Sessions_Pending_And_Notifications()
    {
        await using var host = await HarnessHostFixture.CreateAsync("fss", TestContext.Current.CancellationToken);
        var creator = await host.ConnectAsync("creator", Ct);
        var session = await creator.CreateSessionAsync(host.Root, Ct);
        var approval = RemoteHostMultiClientTests.RaiseApprovalAsync(host, RemoteHostMultiClientTests.Agent(host, session.Id), "bash");
        var added = await RemoteHostMultiClientTests.NextAsync(creator.Notifications.NotificationChangesAsync(Ct), change => change.Added?.SessionId == session.Id);
        await using var connection = new MachineConnection(MachineSpec.Local(host.Root), TestOptions);
        connection.Start();
        await connection.WaitConnectedAsync(Ct);
        Assert.Equal(HostSessionStatuses.WaitingForUser, Assert.Single(connection.Sessions, item => item.SessionId == session.Id).Status);
        var pending = Assert.Single(connection.Pending.Approvals);
        Assert.Equal(added.Added!.Id, pending.RequestId);
        Assert.Equal(1, connection.UnreadNotifications);
        Assert.Equal(added.Page.Items, connection.Notifications!.Items);
        Assert.True(await creator.RespondApprovalAsync(pending.RequestId, false, cancellationToken: Ct));
        Assert.Equal(ApprovalOutcome.Rejected, await approval.WaitAsync(Arrival, Ct));
    }

    [Fact(Explicit = true)]
    public async Task TwoMachines_AggregateSessionsAndPending_AndRecoverAfterHostRestart()
    {
        await using var alpha = await HarnessHostFixture.CreateAsync("fa", TestContext.Current.CancellationToken);
        await using var beta = await HarnessHostFixture.CreateAsync("fb", Ct);
        await using var fleet = new FleetClient([MachineSpec.Local(alpha.Root, "alpha"), MachineSpec.Local(beta.Root, "beta")], TestOptions);
        fleet.Start();
        await fleet.WaitSettledAsync(Ct);
        // A first attempt can fail on a loaded machine; the connection retries on its own.
        foreach (var machine in fleet.Machines)
            await machine.WaitConnectedAsync(Ct);
        Assert.All(fleet.Statuses(), status => Assert.Equal(MachineConnectionState.Connected, status.State));

        var alphaProject = Directory.CreateDirectory(Path.Combine(alpha.Root, "project-a")).FullName;
        var betaProject = Directory.CreateDirectory(Path.Combine(beta.Root, "project-b")).FullName;
        var alphaSession = await CreateSessionAsync(fleet, "alpha", alphaProject);
        var betaSession = await CreateSessionAsync(fleet, "beta", betaProject);
        await WaitForAsync(() => fleet.Sessions().Count(session => session.Session.SessionId == alphaSession || session.Session.SessionId == betaSession) == 2);

        var groups = fleet.Groups();
        Assert.Equal(["alpha", "beta"], groups.Select(group => group.Machine));
        Assert.Contains(groups[0].Projects, project => project.ProjectPath == alphaProject && project.Sessions.Any(session => session.SessionId == alphaSession));
        Assert.Contains(groups[1].Projects, project => project.ProjectPath == betaProject && project.Sessions.Any(session => session.SessionId == betaSession));
        Assert.All(fleet.Statuses(), status => Assert.Equal(HostProtocol.ReleaseVersion, status.HostVersion));

        var decision = RemoteHostMultiClientTests.RaiseApprovalAsync(beta, beta.Environment.Agent, "bash");
        await WaitForAsync(() => fleet.Pending().Any(item => item is { Machine: "beta", Kind: PendingKinds.Approval }));
        var pending = Assert.Single(fleet.Pending());
        Assert.Equal("bash", pending.Approval!.Tool);
        Assert.True(await (await fleet.Find("beta")!.WaitConnectedAsync(Ct)).Client.RespondApprovalAsync(pending.RequestId, true, null, Ct));
        Assert.Equal(ApprovalOutcome.AllowedOnce, await decision.WaitAsync(Arrival, Ct));
        await WaitForAsync(() => fleet.Pending().Count == 0);

        var states = RecordStates(fleet.Find("alpha")!);
        await alpha.RestartHostAsync(Ct);
        await WaitForAsync(() => states.Contains(MachineConnectionState.Reconnecting) && fleet.Find("alpha")!.State == MachineConnectionState.Connected);
        var restartedApproval = RemoteHostMultiClientTests.RaiseApprovalAsync(alpha, alpha.Environment.Agent, "write");
        await WaitForAsync(() => fleet.Pending().Any(item => item is { Machine: "alpha", Kind: PendingKinds.Approval }));
        Assert.Contains(fleet.Sessions(), session => session.Machine == "alpha" && session.Session.SessionId == alphaSession);
        Assert.Equal(MachineConnectionState.Connected, fleet.Find("beta")!.State);
        Assert.True(await (await fleet.Find("alpha")!.WaitConnectedAsync(Ct)).Client.RespondApprovalAsync(Assert.Single(fleet.Pending()).RequestId, false, null, Ct));
        Assert.Equal(ApprovalOutcome.Rejected, await restartedApproval.WaitAsync(Arrival, Ct));
    }

    /** The host goes away while events are appended; after the reconnect the subscription delivers exactly the missed events, each once. */
    [Fact(Explicit = true)]
    public async Task Subscription_ResumesFromTheLastSeq_AfterReconnect()
    {
        await using var host = await HarnessHostFixture.CreateAsync("fs", TestContext.Current.CancellationToken);
        await using var connection = new MachineConnection(MachineSpec.Local(host.Root, "solo"), TestOptions);
        connection.Start();
        await connection.WaitConnectedAsync(Ct);
        var session = host.Environment.Agent.Session;
        var delivered = new List<long>();
        using var subscription = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var reading = ReadSeqsAsync(connection.SubscribeAsync(session.Id.Value, 0, subscription.Token), delivered);

        await WaitForAsync(() => Snapshot(delivered).Count == session.Seq);
        var states = RecordStates(connection);
        await StopHostAndWaitForDisconnectAsync(host, connection);
        var missedFrom = session.Seq;
        session.Append(new TurnStartPayload(1));
        session.Append(new TurnStartPayload(2));
        await host.StartHostAsync(Ct);

        await WaitForAsync(() => Snapshot(delivered).Count == session.Seq);
        Assert.Contains(MachineConnectionState.Reconnecting, states);
        Assert.Equal(Enumerable.Range(0, (int)session.Seq).Select(seq => (long)seq), Snapshot(delivered));
        Assert.Equal([missedFrom, missedFrom + 1], Snapshot(delivered).Skip((int)missedFrom));
        await subscription.CancelAsync();
        await reading;
    }

    [Fact(Explicit = true)]
    public async Task Independent_Readers_Keep_Their_Cursors_Through_Cancel_Reset_And_Reconnect()
    {
        await using var host = await HarnessHostFixture.CreateAsync("ff", TestContext.Current.CancellationToken);
        await using var connection = new MachineConnection(MachineSpec.Local(host.Root), TestOptions);
        connection.Start();
        await connection.WaitConnectedAsync(Ct);
        var session = host.Environment.Agent.Session;
        using var firstStop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var secondStop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var first = new List<RemoteSessionEvent>();
        var second = new List<RemoteSessionEvent>();
        var firstTask = ReadEventsAsync(connection.SubscribeAsync(session.Id.Value, 0, firstStop.Token), first);
        await WaitForAsync(() => Events(first).Count == session.Seq);
        var sharedFrom = session.Seq;
        session.Append(new TurnStartPayload(1));
        await WaitForAsync(() => Events(first).Count == session.Seq);

        // Each late reader gets its own Host history without replacing another reader's subscription.
        var secondTask = ReadEventsAsync(connection.SubscribeAsync(session.Id.Value, sharedFrom, secondStop.Token), second);
        await WaitForAsync(() => Events(second).Count == 1);
        Assert.Equal(sharedFrom, EventSeq(Events(second)[0]));
        session.Append(new TurnStartPayload(2));
        await WaitForAsync(() => Events(second).Count == 2 && Events(first).Count == session.Seq);
        await firstStop.CancelAsync();
        await firstTask;
        session.Append(new TurnStartPayload(3));
        await WaitForAsync(() => Events(second).Count == 3);

        await StopHostAndWaitForDisconnectAsync(host, connection);
        session.Append(new TurnStartPayload(4));
        await host.StartHostAsync(Ct);
        await WaitForAsync(() => Events(second).Count == 4);
        Assert.Equal(new[] { sharedFrom, sharedFrom + 1, sharedFrom + 2, sharedFrom + 3 }, Events(second).Select(EventSeq));

        Assert.True(session.TryTruncateTo(sharedFrom));
        await WaitForAsync(() => Events(second).Any(item => item.Reset));
        session.Append(new TurnStartPayload(5));
        await WaitForAsync(() => Events(second).SkipWhile(item => !item.Reset).Skip(1)
            .Any(item => !item.Reset && EventSeq(item) == sharedFrom));
        var afterReset = Events(second).SkipWhile(item => !item.Reset).Skip(1).Where(item => !item.Reset).Select(EventSeq).ToArray();
        Assert.Equal(Enumerable.Range(0, (int)session.Seq).Select(index => (long)index), afterReset);

        using var lateStop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var late = new List<RemoteSessionEvent>();
        var lateTask = ReadEventsAsync(connection.SubscribeAsync(session.Id.Value, 0, lateStop.Token), late);
        await WaitForAsync(() => Events(late).Count(item => !item.Reset) == session.Seq);
        Assert.Equal(afterReset, Events(late).Where(item => !item.Reset).Select(EventSeq));
        using var tailStop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var tail = new List<RemoteSessionEvent>();
        var tailTask = ReadEventsAsync(connection.SubscribeAsync(session.Id.Value, sharedFrom, tailStop.Token), tail);
        await WaitForAsync(() => Events(tail).Count == 1);
        Assert.Equal(sharedFrom, EventSeq(Assert.Single(Events(tail))));
        await tailStop.CancelAsync();
        await tailTask;
        await lateStop.CancelAsync();
        await lateTask;
        await secondStop.CancelAsync();
        await secondTask;
    }

    private static async Task StopHostAndWaitForDisconnectAsync(HarnessHostFixture host, MachineConnection connection)
    {
        var lease = await connection.WaitConnectedAsync(Ct);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Capture this link's loss before stopping; State is not the lifetime barrier for its consumers.
        using var registration = lease.Lifetime.Register(() => disconnected.TrySetResult());
        await host.StopHostAsync();
        await disconnected.Task.WaitAsync(Arrival, Ct);
    }

    private static async Task ReadEventsAsync(IAsyncEnumerable<RemoteSessionEvent> events, List<RemoteSessionEvent> delivered)
    {
        try
        {
            await foreach (var item in events)
                lock (delivered)
                    delivered.Add(item);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static List<RemoteSessionEvent> Events(List<RemoteSessionEvent> delivered)
    {
        lock (delivered)
            return [.. delivered];
    }

    private static long EventSeq(RemoteSessionEvent item) => item.Event!.Seq;

    /** Disposal cancels a running attempt, which can then fail with its own error (a killed ssh exits with 255); DisposeAsync must still complete. */
    [Fact(Explicit = true)]
    public async Task Dispose_WhileTheCancelledAttemptFailsWithItsOwnError_Completes()
    {
        var home = TempTree.CreateSocketDirectory("fx");
        try
        {
            var starting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var options = TestOptions with
            {
                StartLocalHost = async (_, cancellationToken) =>
                {
                    starting.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.Infinite, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                    }

                    throw new IOException("the host process was killed");
                },
            };
            var connection = new MachineConnection(MachineSpec.Local(home, "dying"), options);
            connection.Start();
            await starting.Task.WaitAsync(TestContext.Current.CancellationToken);

            await connection.DisposeAsync();

            Assert.Equal(MachineConnectionState.Offline, connection.State);
        }
        finally
        {
            TempTree.Delete(home);
        }
    }

    private static async Task<string> CreateSessionAsync(FleetClient fleet, string machine, string cwd)
    {
        var lease = await fleet.Find(machine)!.WaitConnectedAsync(Ct);
        var created = await lease.Client.CreateSessionAsync(cwd, Ct);
        return created.Id;
    }

    internal static List<MachineConnectionState> RecordStates(MachineConnection connection)
    {
        var states = new List<MachineConnectionState>();
        connection.StateChanged += changed =>
        {
            lock (states)
                states.Add(changed.State);
        };
        return states;
    }

    private static async Task ReadSeqsAsync(IAsyncEnumerable<RemoteSessionEvent> events, List<long> delivered)
    {
        try
        {
            await foreach (var item in events)
            {
                if (item.Event is { } sessionEvent)
                    lock (delivered)
                        delivered.Add(sessionEvent.Seq);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static List<long> Snapshot(List<long> delivered)
    {
        lock (delivered)
            return [.. delivered];
    }

    internal static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(Arrival);
        while (!condition())
            await Task.Delay(50, timeout.Token);
    }
}





