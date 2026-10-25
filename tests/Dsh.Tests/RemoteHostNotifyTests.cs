using System.Collections;
using System.Text.Json;
using Dsh.Notifications;
using Dsh.RemoteHost;
using Dsh.Transport;

namespace Dsh.Tests;

/** host.notify.* against a real host: approvals and session exits enter the history, read marks are shared, the history survives a restart. */
[Collection(SerialProcessCollection.Name)]
public sealed class RemoteHostNotifyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Explicit = true)]
    public async Task TwoClients_SeeTheSameNotifications_AndTheSameReadState_AcrossARestart()
    {
        await using var host = await HarnessHostFixture.CreateAsync("mna", TestContext.Current.CancellationToken);
        var alice = await host.ConnectAsync("alice", Ct);
        var bob = await host.ConnectAsync("bob", Ct);
        var aliceChanges = alice.Notifications.NotificationChangesAsync(Ct);
        var bobChanges = bob.Notifications.NotificationChangesAsync(Ct);
        var aliceInitial = await RemoteHostMultiClientTests.NextAsync(aliceChanges);
        var bobInitial = await RemoteHostMultiClientTests.NextAsync(bobChanges);
        Assert.Empty(aliceInitial.Page.Items);
        Assert.Equal((0, 0), (aliceInitial.Page.Total, aliceInitial.Page.UnreadCount));
        Assert.Null(aliceInitial.Added);
        Assert.Null(aliceInitial.ReadIds);
        Assert.Empty(bobInitial.Page.Items);
        Assert.Equal((0, 0), (bobInitial.Page.Total, bobInitial.Page.UnreadCount));
        var session = await alice.CreateSessionAsync(host.Environment.App.Home.Root, Ct);

        var decision = RemoteHostMultiClientTests.RaiseApprovalAsync(host, RemoteHostMultiClientTests.Agent(host, session.Id), "bash");
        var request = Assert.Single((await RemoteHostMultiClientTests.NextAsync(alice.PendingChangesAsync(Ct), change => change.Pending.Approvals.Count > 0)).Pending.Approvals);
        var addedChange = await RemoteHostMultiClientTests.NextAsync(aliceChanges, change => change.Added?.Id == request.RequestId);
        var bobAddedChange = await RemoteHostMultiClientTests.NextAsync(bobChanges, change => change.Added?.Id == request.RequestId);
        var added = Assert.IsType<NotificationRecord>(addedChange.Added);
        var seenByBob = Assert.IsType<NotificationRecord>(bobAddedChange.Added);
        Assert.Equal(added, Assert.Single(addedChange.Page.Items));
        Assert.Equal(seenByBob, Assert.Single(bobAddedChange.Page.Items));
        Assert.Equal((1, 1), (addedChange.Page.Total, addedChange.Page.UnreadCount));
        Assert.Equal(1, bobAddedChange.Page.UnreadCount);
        Assert.True(await alice.RespondApprovalAsync(request.RequestId, false, cancellationToken: Ct));
        Assert.Equal(Dsh.Core.ApprovalOutcome.Rejected, await decision.WaitAsync(TimeSpan.FromSeconds(5), Ct));

        Assert.True(alice.Supports(NotificationsProtocol.Capability));
        Assert.Equal(added, seenByBob);
        Assert.Equal((request.RequestId, session.Id, NotificationKind.ApprovalRequest), (added.Id, added.SessionId, added.Kind));
        Assert.Equal(new NotificationTarget(NotificationView.PendingRequest, request.RequestId), added.Target);
        Assert.Equal(1, await alice.Notifications.MarkNotificationsReadAsync(new RemoteNotifyMarkRead([added.Id]), Ct));
        var changed = await RemoteHostMultiClientTests.NextAsync(bobChanges, change => change.ReadIds?.Contains(added.Id) == true);
        Assert.Equal([added.Id], changed.ReadIds);
        Assert.Equal(0, changed.Page.UnreadCount);
        Assert.True(Assert.Single(changed.Page.Items).IsRead);
        Assert.True(Assert.Single((await bob.Notifications.ListNotificationsAsync(cancellationToken: Ct)).Items).IsRead);

        await host.RestartHostAsync(Ct);

        var after = await host.ConnectAsync("after", Ct);
        var page = await after.Notifications.ListNotificationsAsync(cancellationToken: Ct);
        var kept = Assert.Single(page.Items);
        Assert.Equal((added.Id, true, 0), (kept.Id, kept.IsRead, page.UnreadCount));
        var restored = await RemoteHostMultiClientTests.NextAsync(after.Notifications.NotificationChangesAsync(Ct));
        Assert.Equal(page.Items, restored.Page.Items);
        Assert.Equal((page.Total, page.UnreadCount), (restored.Page.Total, restored.Page.UnreadCount));
        Assert.Null(restored.Added);
        Assert.Null(restored.ReadIds);
    }

    [Fact(Explicit = true)]
    public async Task ConcurrentReadAndAppend_PublishSnapshotsInCommitOrder()
    {
        await using var host = await HarnessHostFixture.CreateAsync("mnx", TestContext.Current.CancellationToken);
        var client = await host.ConnectAsync("reader", Ct);
        var changes = client.Notifications.NotificationChangesAsync(Ct);
        await RemoteHostMultiClientTests.NextAsync(changes);
        host.Backend.Notifications!.Report(NotificationKind.Info, null, "first", id: "first");
        await RemoteHostMultiClientTests.NextAsync(changes, change => change.Added?.Id == "first");
        var marking = client.Notifications.MarkNotificationsReadAsync(new RemoteNotifyMarkRead(["first"]), Ct);
        host.Backend.Notifications.Report(NotificationKind.Info, null, "second", id: "second");
        Assert.Equal(1, await marking);
        var first = await RemoteHostMultiClientTests.NextAsync(changes);
        var last = await RemoteHostMultiClientTests.NextAsync(changes);
        Assert.Equal(first.Added is null ? 0 : 2, first.Page.UnreadCount);
        Assert.Equal(1, last.Page.UnreadCount);
        Assert.True(Assert.Single(last.Page.Items, item => item.Id == "first").IsRead);
        Assert.False(Assert.Single(last.Page.Items, item => item.Id == "second").IsRead);
        var stored = await client.Notifications.ListNotificationsAsync(cancellationToken: Ct);
        Assert.Equal(stored.Items, last.Page.Items);
        Assert.Equal(stored.UnreadCount, last.Page.UnreadCount);
    }

    [Fact(Explicit = true)]
    public async Task RpcList_DuringReadCommit_ReturnsAtomicFilteredPages_AndCanCancelWaiting()
    {
        await using var host = await HarnessHostFixture.CreateAsync("mnl", TestContext.Current.CancellationToken);
        var client = await host.ConnectAsync("reader", Ct);
        var changes = client.Notifications.NotificationChangesAsync(Ct);
        await RemoteHostMultiClientTests.NextAsync(changes);
        var notifications = host.Backend.Notifications!;
        notifications.Report(NotificationKind.Info, "target", "first", id: "first");
        notifications.Report(NotificationKind.Info, "target", "second", id: "second");
        notifications.Report(NotificationKind.Info, "other", "outside filter", id: "other");
        await RemoteHostMultiClientTests.NextAsync(changes, change => change.Added?.Id == "other");

        using var ids = new PausedReadIds(["first", "second"], Ct);
        var marking = Task.Run(() => notifications.MarkReadAsync(new RemoteNotifyMarkRead(ids), Ct), Ct);
        try
        {
            await ids.Entered.Task.WaitAsync(Ct);
            var paged = QueryRpcAsync(notifications, new RemoteNotifyQuery(SessionId: "target", Kinds: [NotificationKind.Info], Offset: 1, Limit: 1), Ct);
            var unread = QueryRpcAsync(notifications, new RemoteNotifyQuery(SessionId: "target", UnreadOnly: true), Ct);
            Assert.False(paged.IsCompleted);
            Assert.False(unread.IsCompleted);
            using var canceled = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            var waiting = QueryRpcAsync(notifications, new RemoteNotifyQuery(), canceled.Token);
            await canceled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

            ids.Resume();
            Assert.Equal(2, await marking);
            var page = await paged;
            var item = Assert.Single(page.Items);
            Assert.Equal(("first", true, 2, 1), (item.Id, item.IsRead, page.Total, page.UnreadCount));
            var unreadPage = await unread;
            Assert.Empty(unreadPage.Items);
            Assert.Equal((0, 1), (unreadPage.Total, unreadPage.UnreadCount));
            var committed = await RemoteHostMultiClientTests.NextAsync(changes, change => change.ReadIds is not null);
            Assert.Equal(["first", "second"], committed.ReadIds);
            Assert.Equal((3, 1), (committed.Page.Total, committed.Page.UnreadCount));
            Assert.All(committed.Page.Items.Where(record => record.SessionId == "target"), record => Assert.True(record.IsRead));
            Assert.False(Assert.Single(committed.Page.Items, record => record.Id == "other").IsRead);
        }
        finally
        {
            ids.Resume();
            await marking;
        }
    }

    [Fact(Explicit = true)]
    public async Task SessionExit_IsRecordedWithItsExitCode_AndListFiltersAndPages()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("uses a POSIX shell");
        await using var host = await HarnessHostFixture.CreateAsync("mne", TestContext.Current.CancellationToken);
        var client = await host.ConnectAsync("solo", Ct);
        var changes = client.Notifications.NotificationChangesAsync(Ct);
        Assert.Empty((await RemoteHostMultiClientTests.NextAsync(changes)).Page.Items);

        var first = await client.Pty.StartPtyAsync(new RemotePtyStartRequest("/bin/sh", ["-c", "sleep 1; exit 3"], Title: "short job"), Ct);
        var exitedChange = await RemoteHostMultiClientTests.NextAsync(changes, change => change.Added?.SessionId == first);
        var exited = Assert.IsType<NotificationRecord>(exitedChange.Added);
        Assert.Equal(exited, Assert.Single(exitedChange.Page.Items));
        Assert.Equal((1, 1), (exitedChange.Page.Total, exitedChange.Page.UnreadCount));
        var second = await client.Pty.StartPtyAsync(new RemotePtyStartRequest("/bin/sh", ["-c", "sleep 1; exit 4"], Title: "second job"), Ct);
        var secondChange = await RemoteHostMultiClientTests.NextAsync(changes, change => change.Added?.SessionId == second);
        Assert.Equal((2, 2), (secondChange.Page.Total, secondChange.Page.UnreadCount));
        Assert.Contains(secondChange.Page.Items, record => record.SessionId == second);
        Assert.Equal(1, await client.Notifications.MarkNotificationsReadAsync(new RemoteNotifyMarkRead(All: true, SessionId: second), Ct));

        var unread = await client.Notifications.ListNotificationsAsync(new RemoteNotifyQuery(UnreadOnly: true), Ct);
        var paged = await client.Notifications.ListNotificationsAsync(new RemoteNotifyQuery(Kinds: [NotificationKind.SessionExited], Offset: 1, Limit: 1), Ct);

        Assert.Equal((NotificationKind.SessionExited, "short job exited", "exit code 3"), (exited.Kind, exited.Title, exited.Body));
        Assert.Equal(first, Assert.Single(unread.Items).SessionId);
        Assert.Equal((2, 1), (paged.Total, paged.UnreadCount));
        Assert.Equal(first, Assert.Single(paged.Items).SessionId);
    }

    private static async Task<RemoteNotifyPage> QueryRpcAsync(HostNotificationCenter notifications, RemoteNotifyQuery query, CancellationToken cancellationToken)
    {
        var message = new JsonRpcMessage
        {
            Method = NotificationsProtocol.MethodNotifyList,
            Params = JsonSerializer.SerializeToElement(query, NotificationsProtocolJsonContext.Default.RemoteNotifyQuery)
        };
        var result = await HostNotifyMethods.HandleAsync(notifications, NotificationsProtocol.MethodNotifyList, message, cancellationToken);
        return JsonSerializer.Deserialize(result!.Value, NotificationsProtocolJsonContext.Default.RemoteNotifyPage)!;
    }

    /** Pauses the real read operation after it owns the mutation gate, before it commits to NotificationHistory. */
    private sealed class PausedReadIds(IReadOnlyList<string> ids, CancellationToken cancellationToken) : IReadOnlyList<string>, IDisposable
    {
        private readonly ManualResetEventSlim _resume = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Count => ids.Count;
        public string this[int index] => ids[index];
        public IEnumerator<string> GetEnumerator()
        {
            Entered.TrySetResult();
            _resume.Wait(cancellationToken);
            return ids.GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public void Resume() => _resume.Set();
        public void Dispose() => _resume.Dispose();
    }
}



