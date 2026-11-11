using System.Text.Json;
using Dsh.Notifications;
using Dsh.Agent;

namespace Dsh.Tests;

public sealed class NotificationHistoryTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "../../../../../artifacts020/test-tmp", $"notifications-{Guid.NewGuid():N}"));


    private string FilePath => Path.Combine(_directory, "history.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task Query_ReturnsNewestFirstAndFiltersByMachineSessionAndUnread()
    {
        var history = await OpenAsync();
        await history.AppendAsync(Record("n1", "m1", "s1", minutes: 0), TestContext.Current.CancellationToken);
        await history.AppendAsync(Record("n2", "m1", "s2", minutes: 1), TestContext.Current.CancellationToken);
        await history.AppendAsync(Record("n3", "m2", "s3", minutes: 2), TestContext.Current.CancellationToken);
        await history.MarkReadAsync(["n2"], TestContext.Current.CancellationToken);

        Assert.Equal(["n3", "n2", "n1"], Ids(history.Query()));
        Assert.Equal(["n2", "n1"], Ids(history.Query(new NotificationFilter(MachineId: "m1"))));
        Assert.Equal(["n1"], Ids(history.Query(new NotificationFilter(SessionId: "s1"))));
        Assert.Equal(["n3", "n1"], Ids(history.Query(new NotificationFilter(UnreadOnly: true))));
        Assert.Equal(1, history.CountUnread(new NotificationFilter(MachineId: "m1")));
    }

    [Fact]
    public async Task ReadMarks_SurviveReopen()
    {
        var history = await OpenAsync();
        await history.AppendAsync(Record("n1", "m1", "s1", minutes: 0), TestContext.Current.CancellationToken);
        await history.AppendAsync(Record("n2", "m1", "s1", minutes: 1), TestContext.Current.CancellationToken);
        var marked = await history.MarkAllReadAsync(new NotificationFilter(SessionId: "s1"), TestContext.Current.CancellationToken);
        var markedAgain = await history.MarkReadAsync(["n1"], TestContext.Current.CancellationToken);

        var reopened = await OpenAsync();

        Assert.Equal((2, 0), (marked.Count, markedAgain.Count));
        Assert.Equal(["n2", "n1"], Ids(reopened.Query()));
        Assert.All(reopened.Query(), record => Assert.True(record.IsRead));
        Assert.Equal(0, reopened.CountUnread());
        await reopened.AppendAsync(Record("n3", "m1", "s1", minutes: 2), TestContext.Current.CancellationToken);
        var afterAppend = await OpenAsync();
        Assert.Equal(["n3", "n2", "n1"], Ids(afterAppend.Query()));
        Assert.Equal(["n3"], Ids(afterAppend.Query(new NotificationFilter(UnreadOnly: true))));
    }

    [Fact]
    public async Task Append_IgnoresReplayedIds()
    {
        var history = await OpenAsync();
        var changes = 0;
        history.Changed += () => changes++;

        var first = await history.AppendAsync(Record("n1", "m1", "s1", minutes: 0), TestContext.Current.CancellationToken);
        var replay = await history.AppendAsync(Record("n1", "m1", "s1", minutes: 0), TestContext.Current.CancellationToken);

        Assert.Equal((true, false, 1), (first, replay, changes));
        Assert.Single(history.Query());
    }

    [Fact]
    public async Task FailedSave_DoesNotPublishReadMarksOrLosePersistedNotifications()
    {
        var history = await OpenAsync();
        await history.AppendAsync(Record("n1", "m1", "s1", 0), TestContext.Current.CancellationToken);
        var changes = 0;
        history.Changed += () => changes++;
        Directory.CreateDirectory($"{FilePath}.tmp");

        var error = await Xunit.Record.ExceptionAsync(() => history.MarkReadAsync(["n1"], TestContext.Current.CancellationToken));

        Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
        Assert.Equal(0, changes);
        Assert.Equal(1, history.CountUnread());
        Assert.False(Assert.Single((await OpenAsync()).Query()).IsRead);
    }

    [Fact]
    public async Task SavingReadMarks_AllowsAnExistingSnapshotReader()
    {
        var history = await OpenAsync();
        await history.AppendAsync(Record("n1", "m1", "s1", 0), TestContext.Current.CancellationToken);
        await using var oldSnapshot = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

        await history.MarkReadAsync(["n1"], TestContext.Current.CancellationToken);

        Assert.True(Assert.Single((await OpenAsync()).Query()).IsRead);
        var oldRecords = await JsonSerializer.DeserializeAsync(oldSnapshot, NotificationsJsonContext.Default.IReadOnlyListNotificationRecord, TestContext.Current.CancellationToken);
        Assert.False(Assert.Single(oldRecords!).IsRead);
    }

    private Task<NotificationHistory> OpenAsync()
        => NotificationHistory.OpenAsync(FilePath, TestContext.Current.CancellationToken);

    private static NotificationRecord Record(string id, string machine, string? session, int minutes)
        => new(id, machine, session, NotificationKind.ApprovalRequest, $"title {id}", null, Start.AddMinutes(minutes),
            new NotificationTarget(NotificationView.PendingRequest, $"req-{id}"));

    private static string[] Ids(IEnumerable<NotificationRecord> records) => records.Select(record => record.Id).ToArray();

}
