using System.Diagnostics;
using System.Net;
using Dsh.RemoteHost;
using Dsh.Transport;

namespace Dsh.Tests;

/** Owner-only endpoint and token preamble of the resident remote host daemon, exercised over its real loopback endpoint. */
[Collection(SerialProcessCollection.Name)]
public sealed class RemoteHostListenerAuthTests
{
    private const UnixFileMode OwnerReadWrite = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode OwnerAll = OwnerReadWrite | UnixFileMode.UserExecute;

    [Fact(Explicit = true)]
    public async Task Serve_RestrictsSocketTokenAndRunDirectoryToOwner()
    {
        await WithDaemonAsync("rp", TestContext.Current.CancellationToken, root =>
        {
            if (OperatingSystem.IsWindows())
                return Task.CompletedTask;
            Assert.Equal(OwnerAll, File.GetUnixFileMode(RemoteHostEndpoint.RunDirectory(root)));
            Assert.Equal(OwnerReadWrite, File.GetUnixFileMode(RemoteHostEndpoint.SocketPath(root)));
            Assert.Equal(OwnerReadWrite, File.GetUnixFileMode(LocalEndpointToken.PathFor(RemoteHostEndpoint.SocketPath(root))));
            return Task.CompletedTask;
        });
    }

    [Fact(Explicit = true)]
    public async Task ClientWithoutToken_IsDisconnected_AndDaemonKeepsServing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await WithDaemonAsync("ru", cancellationToken, async root =>
        {
            await using (var raw = await ConnectRawAsync(root, cancellationToken))
                await Assert.ThrowsAnyAsync<IOException>(() => RemoteHostClient.ConnectAsync(raw, null, cancellationToken));

            await using var stream = await RemoteHostConnector.ConnectAsync(root, cancellationToken);
            await using var client = await RemoteHostClient.ConnectAsync(stream, null, cancellationToken);
            Assert.Equal(HostProtocol.Version, (await client.InfoAsync(cancellationToken)).ProtocolVersion);
        });
    }

    [Fact(Explicit = true)]
    public async Task Probe_PreservesAuthenticationErrorsAndCallerCancellation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var hostToken = Guid.NewGuid().ToString("N");
        await WithDaemonAsync("rc", cancellationToken, async root =>
        {
            using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            await cancelled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                RemoteHostConnector.TryConnectClientAsync(root, hostToken, null, cancelled.Token));
            await Assert.ThrowsAsync<IOException>(() =>
                RemoteHostConnector.TryConnectClientAsync(root, $"{hostToken}-invalid", null, cancellationToken));

            await using var client = Assert.IsType<RemoteHostClient>(
                await RemoteHostConnector.TryConnectClientAsync(root, hostToken, null, cancellationToken));
            Assert.Equal(HostProtocol.Version, (await client.InfoAsync(cancellationToken)).ProtocolVersion);
        }, hostToken);
    }

    [Fact(Explicit = true)]
    public async Task Connect_WhenDaemonIssuedNoToken_FailsWithExplicitError()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await WithDaemonAsync("rt", cancellationToken, async root =>
        {
            var endpoint = OperatingSystem.IsWindows() ? RemoteHostEndpoint.PortFile(root) : RemoteHostEndpoint.SocketPath(root);
            File.Delete(LocalEndpointToken.PathFor(endpoint));

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RemoteHostConnector.ConnectAsync(root, cancellationToken));
            Assert.Contains(LocalEndpointToken.PathFor(endpoint), error.Message);
        });
    }

    [Fact(Explicit = true)]
    public async Task OwnedStartup_OutlivesOrdinaryProbeBudget_AndAuthenticatesWhenReady()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = RealHostProcesses.CreateHome("rs");
        var startup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Process? host = null;
        try
        {
            var connecting = RemoteHostConnector.ConnectAsync(root, cancellationToken, attempts: 1, startupLifetime: startup.Task);
            await Assert.ThrowsAsync<IOException>(() => RemoteHostConnector.ConnectAsync(root, cancellationToken, attempts: 1));
            host = RealHostProcesses.StartHost(RealHostProcesses.Executable, root);
            await using var stream = await connecting;
            await using var client = await RemoteHostClient.ConnectAsync(stream, null, cancellationToken);
            Assert.Equal(HostProtocol.Version, (await client.InfoAsync(cancellationToken)).ProtocolVersion);
        }
        finally
        {
            startup.TrySetResult();
            RealHostProcesses.KillAll(root);
            host?.Dispose();
            TempTree.Delete(root);
        }
    }

    [Theory(Timeout = 5000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnedStartup_StopsWaitingOnExitOrCancellation(bool cancel)
    {
        var root = TempTree.CreateSocketDirectory("rx");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var startup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var connecting = RemoteHostConnector.ConnectAsync(root, cancellation.Token, attempts: 1, startupLifetime: startup.Task);
            if (cancel)
            {
                await cancellation.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connecting);
            }
            else
            {
                startup.SetResult();
                await Assert.ThrowsAsync<IOException>(() => connecting);
            }
        }
        finally
        {
            startup.TrySetResult();
            TempTree.Delete(root);
        }
    }

    [Fact(Explicit = true)]
    public async Task KilledHost_IsReplacedWithAnEndpointReaderOpen_AndFreshClientAuthenticates()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var executable = RealHostProcesses.Executable;
        var root = RealHostProcesses.CreateHome("rr");
        Process? first = null;
        Process? replacement = null;
        try
        {
            first = RealHostProcesses.StartHost(executable, root);
            await using (var stream = await RemoteHostConnector.ConnectAsync(root, cancellationToken))
            await using (var client = await RemoteHostClient.ConnectAsync(stream, null, cancellationToken))
                Assert.Equal(HostProtocol.Version, (await client.InfoAsync(cancellationToken)).ProtocolVersion);

            var endpoint = RemoteHostEndpoint.EndpointFile(root);
            var oldToken = LocalEndpointToken.Read(endpoint);
            Assert.NotNull(oldToken);
            // Windows holds the port snapshot with the production reader's sharing contract.
            // Unix has no port file; retain the old token snapshot across the same real host restart instead.
            var snapshotPath = OperatingSystem.IsWindows() ? endpoint : LocalEndpointToken.PathFor(endpoint);
            await using var snapshot = new FileStream(snapshotPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1, useAsync: true);
            using var reader = new StreamReader(snapshot, leaveOpen: true);
            var oldContents = await reader.ReadToEndAsync(cancellationToken);

            first.Kill();
            await first.WaitForExitAsync(cancellationToken);
            replacement = RealHostProcesses.StartHost(executable, root);

            // A protocol handshake and requests prove the new endpoint/token pair reaches the replacement host,
            // while the old reader is still open and cannot share an in-place write.
            await using var freshStream = await RemoteHostConnector.ConnectAsync(root, cancellationToken);
            await using var freshClient = await RemoteHostClient.ConnectAsync(freshStream, null, cancellationToken);
            Assert.True(freshClient.Hello.Ok);
            Assert.Equal(HostProtocol.Version, (await freshClient.InfoAsync(cancellationToken)).ProtocolVersion);
            Assert.True(await freshClient.PingAsync(cancellationToken) > 0);
            Assert.NotEqual(oldToken, LocalEndpointToken.Read(endpoint));
            Assert.False(replacement.HasExited);

            reader.DiscardBufferedData();
            snapshot.Position = 0;
            Assert.Equal(oldContents, await reader.ReadToEndAsync(cancellationToken));
        }
        finally
        {
            if (replacement is { HasExited: false })
                replacement.Kill(entireProcessTree: true);
            RealHostProcesses.KillAll(root);
            first?.Dispose();
            replacement?.Dispose();
            TempTree.Delete(root);
        }
    }

    private static async Task WithDaemonAsync(string prefix, CancellationToken cancellationToken, Func<string, Task> body, string? hostToken = null)
    {
        // Socket paths are capped at 104 characters on macOS, so the daemon root is a shallow directory inside the repository.
        var root = TempTree.CreateSocketDirectory(prefix);
        TempTree.AssertFitsMacOsSocketLimit(RemoteHostEndpoint.SocketPath(root));
        using var daemonCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var server = new RemoteHostServer(new RemoteHostServerOptions(hostToken));
        var daemon = Task.Run(() => RemoteHostListener.ServeLoopbackAsync(server, root, daemonCts.Token), cancellationToken);
        try
        {
            // A successful connect proves the endpoint is bound before the test inspects it.
            await (await RemoteHostConnector.ConnectAsync(root, cancellationToken)).DisposeAsync();
            await body(root);
        }
        finally
        {
            await daemonCts.CancelAsync();
            try
            {
                await daemon;
            }
            catch (OperationCanceledException)
            {
            }

            TempTree.Delete(root);
        }
    }

    /** Connects to the daemon endpoint without presenting the token, like any other local process could. */
    private static async Task<Stream> ConnectRawAsync(string root, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            return await TransportConnection.ConnectUnixAsync(RemoteHostEndpoint.SocketPath(root), cancellationToken);
        var port = int.Parse(await File.ReadAllTextAsync(RemoteHostEndpoint.PortFile(root), cancellationToken));
        return await TransportConnection.ConnectTcpAsync(IPAddress.Loopback.ToString(), port, cancellationToken);
    }
}


