using System.Net;
using Dsh.RemoteHost;
using Dsh.Transport;

namespace Dsh.Tests;

[Collection(SerialProcessCollection.Name)]
public sealed class RemoteNetworkClientTests
{
    private const int TestTimeoutMilliseconds = 5000;
    private const string ProbeTarget = "http://1.1.1.1/network-client-cancellation";

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task ConflictingSave_PreservesTheNetworkErrorCode_WithoutChangingCommittedSettings()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await HarnessHostFixture.CreateAsync("nerr", ct);
        var client = await host.ConnectAsync("network-error-consumer", ct);
        IWorkspaceNetwork network = client.Network;
        var initial = await network.GetOutboundAsync(ct);
        var committed = await network.SetOutboundAsync(new(initial.Revision, new("http://saved.example.invalid:3128")), ct);

        var error = await Assert.ThrowsAsync<JsonRpcException>(() => network.SetOutboundAsync(new(initial.Revision, new("http://stale.example.invalid:8080")), ct));

        Assert.Equal(NetworkProtocol.RevisionConflict, error.Code);
        var actual = await network.GetOutboundAsync(ct);
        Assert.Equal((committed.Revision, committed.Settings.Url), (actual.Revision, actual.Settings.Url));
        var invalid = await Assert.ThrowsAsync<JsonRpcException>(() => network.SetOutboundAsync(new("", new()), ct));
        Assert.Equal(JsonRpcException.InvalidParams, invalid.Code);
        Assert.True(await client.PingAsync(ct) > 0);
    }

    [Theory(Timeout = TestTimeoutMilliseconds)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancelledProbe_ReleasesTheActualConnection_AndAllowsTheOperationIdToBeReused(bool cancelBeforeSending)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var held = new TestForwardProxy(pauseResponses: true, observePeerCloseWhilePaused: true);
        await using var next = new TestForwardProxy(statusCode: HttpStatusCode.NotFound);
        await using var host = await HarnessHostFixture.CreateAsync("ncan", ct);
        var client = await host.ConnectAsync("network-cancel-consumer", ct);
        IWorkspaceNetwork network = client.Network;
        var initial = await network.GetOutboundAsync(ct);
        var operationId = Guid.NewGuid();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (cancelBeforeSending) await cancellation.CancelAsync();
        var pending = network.ProbeOutboundAsync(Request(held, operationId), cancellation.Token);
        if (!cancelBeforeSending)
        {
            await held.WaitForRequestAsync(ct);
            await cancellation.CancelAsync();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        var result = await network.ProbeOutboundAsync(Request(next, operationId), ct);
        Assert.True(result.Reachable);
        Assert.Equal((int)HttpStatusCode.NotFound, result.HttpStatus);
        Assert.Single(next.Requests);
        while (held.ActiveConnections != 0) await held.WaitForClosedConnectionAsync(ct);
        if (cancelBeforeSending) Assert.Empty(held.Requests);
        Assert.Equal(initial.Revision, (await network.GetOutboundAsync(ct)).Revision);
        Assert.True(await client.PingAsync(ct) > 0);
    }

    private static RemoteOutboundProxyProbeRequest Request(TestForwardProxy proxy, Guid operationId)
        => new(new RemoteOutboundProxySettings($"http://127.0.0.1:{proxy.Port}"), ProbeTarget, operationId);
}
