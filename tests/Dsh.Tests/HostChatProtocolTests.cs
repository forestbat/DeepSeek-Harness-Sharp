using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.RemoteHost;
using Dsh.Runtime;

namespace Dsh.Tests;

[Collection(SerialProcessCollection.Name)]
public sealed class HostChatProtocolTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Timeout = 5000)]
    public async Task SessionEditing_UsesHostCommandsAndPersistence_AndBroadcastsDeletion()
    {
        await using var host = await HarnessHostFixture.CreateAsync("hce", TestContext.Current.CancellationToken);
        var editor = await host.ConnectAsync("editor", Ct);
        var observer = await host.ConnectAsync("observer", Ct);
        var created = await editor.CreateSessionAsync(host.Environment.App.Home.Root,
            Ct);

        var commands = await editor.SessionEditing.ListSessionCommandsAsync(created.Id, Ct);
        Assert.Contains(commands, command => command.Name == "provider");
        Assert.True((await editor.SessionEditing.ExecuteSessionCommandAsync(created.Id, "/provider list", Ct)).Known);
        Assert.False((await editor.SessionEditing.ExecuteSessionCommandAsync(created.Id, "/does-not-exist", Ct)).Known);
        await editor.SessionEditing.RenameSessionAsync(created.Id, "shared title", Ct);
        await editor.SessionEditing.SetSessionApprovalPolicyAsync(created.Id, nameof(ApprovalPolicy.Never), Ct);
        var snapshot = await observer.GetSessionSnapshotAsync(created.Id, Ct);
        Assert.Equal("shared title", snapshot.Header.Title);
        Assert.Contains(snapshot.Events, item => item.Data is ApprovalPolicyPayload { Policy: ApprovalPolicy.Never });

        await editor.SessionEditing.DeleteSessionAsync(created.Id, Ct);
        var removed = await RemoteHostMultiClientTests.NextAsync(observer.HostSessionChangesAsync(Ct),
            item => item.Session?.SessionId == created.Id && item.Session.Deleted);
        Assert.Equal(created.Id, removed.Session!.SessionId);
        Assert.DoesNotContain(await observer.ListHostSessionsAsync(Ct), item => item.SessionId == created.Id);
        Assert.DoesNotContain(await observer.ListSessionsAsync(Ct), item => item.Id == created.Id);
    }

    [Fact(Timeout = 5000)]
    public async Task Truncation_NotifiesOtherClientsBeforeReusedSequenceNumbers()
    {
        await using var host = await HarnessHostFixture.CreateAsync("hct", TestContext.Current.CancellationToken);
        var editor = await host.ConnectAsync("editor", Ct);
        var observer = await host.ConnectAsync("observer", Ct);
        var created = await editor.CreateSessionAsync(host.Environment.App.Home.Root,
            Ct);
        var session = RemoteHostMultiClientTests.Agent(host, created.Id).Session;
        session.Append(new TurnStartPayload(1));
        using var following = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await using var stream = observer.SubscribeAsync(created.Id, 0, following.Token).GetAsyncEnumerator(following.Token);
        Assert.True(await stream.MoveNextAsync());
        var first = stream.Current.Event!;
        Assert.Equal(0, first.Seq);

        Assert.True(session.TryTruncateTo(0));
        Assert.True(await stream.MoveNextAsync());
        Assert.True(stream.Current.Reset);
        Assert.Empty((await observer.GetSessionSnapshotAsync(created.Id, Ct)).Events);
        session.Append(new TurnStartPayload(2));
        Assert.True(await stream.MoveNextAsync());
        var replacement = stream.Current.Event!;
        Assert.Equal(0, replacement.Seq);
        Assert.Equal(2, Assert.IsType<TurnStartPayload>(replacement.Data).Turn);
        await following.CancelAsync();
    }

    [Fact(Timeout = 5000)]
    public async Task PluginPermissionsAndCatalog_AreReadFromAndChangedOnHost()
    {
        await using var host = await HarnessHostFixture.CreateAsync("hcm", TestContext.Current.CancellationToken);
        var editor = await host.ConnectAsync("editor", Ct);
        var observer = await host.ConnectAsync("observer", Ct);
        const string package = "@deepseek-ai/dsh-gui";
        Assert.Contains(await observer.Plugins.ListPluginsAsync(Ct), plugin => plugin.Package == package);
        await editor.Plugins.SetPluginPermissionAsync(package, "network", false, Ct);
        var changed = Assert.Single(await observer.Plugins.ListPluginsAsync(Ct), plugin => plugin.Package == package);
        Assert.True(changed.ReadAllowed);
        Assert.True(changed.WriteAllowed);
        Assert.False(changed.NetworkAllowed);
        await editor.Plugins.SetPluginPermissionAsync(package, "network", true, Ct);
        Assert.True(Assert.Single(await observer.Plugins.ListPluginsAsync(Ct), plugin => plugin.Package == package).NetworkAllowed);
    }
}

