using System.Diagnostics;
using Dsh.Boot;
using Dsh.RemoteHost;
using Dsh.Transport;

namespace Dsh.Tests;

/** Exercises the real CLI stdio bridge against a task-owned host and the production RPC client. */
public sealed class HostStdioBridgeTests
{
    private const int RequestRounds = 5;
    private const string SessionTitle = "stdio bridge session";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task StdioHost_ServesManyRequestsOnOneConnection()
    {
        await using var host = await HarnessHostFixture.CreateAsync("hsm", Ct);
        var direct = await host.ConnectAsync("stdio-reference", Ct);
        var created = await direct.CreateSessionAsync(host.Environment.App.Home.Root, cancellationToken: Ct);
        await direct.SessionEditing.RenameSessionAsync(created.Id, SessionTitle, Ct);
        var expected = await direct.ListSessionsAsync(Ct);
        Assert.Contains(expected, session => session.Id == created.Id && session.Title == SessionTitle);
        await using var bridge = new StdioHost(host.Root);
        await using var client = await RemoteHostClient.ConnectAsync(bridge.Stream, null, "stdio-test", Ct);

        for (var round = 0; round < RequestRounds; round++)
        {
            Assert.True(await client.PingAsync(Ct) > 0);
            var sessions = await client.ListSessionsAsync(Ct);
            Assert.Equal(expected.Select(session => session.Id).Order(), sessions.Select(session => session.Id).Order());
            Assert.Contains(sessions, session => session.Id == created.Id && session.Title == SessionTitle);
        }
    }

    [Fact]
    public async Task StdioHost_CompletesHelloAndListsSessions()
    {
        await using var host = await HarnessHostFixture.CreateAsync("hsh", Ct);
        await using var readiness = await RemoteHostConnector.ConnectAsync(host.Root, Ct);
        await using var bridge = new StdioHost(host.Root);
        await using var client = await RemoteHostClient.ConnectAsync(bridge.Stream, null, "stdio-test", Ct);

        Assert.True(client.Hello.Ok);
        Assert.Equal(HostProtocol.Version, client.Hello.ProtocolVersion);
        Assert.Null(client.Hello.Mismatch);
        // Successful RPC decoding verifies stdout contains protocol frames rather than startup diagnostics.
        var info = await client.InfoAsync(Ct);
        Assert.Equal(host.Environment.App.Home.Root, info.HomeRoot);
        Assert.NotNull(await client.ListSessionsAsync(Ct));
        Assert.True(await client.PingAsync(Ct) > 0);
    }

    /** Owns only the stdio bridge process; the separately owned host outlives this connection. */
    private sealed class StdioHost : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _stderr;

        public StdioHost(string root)
        {
            var start = new ProcessStartInfo(RealHostProcesses.Executable, ["host", "serve", "--stdio"])
            {
                WorkingDirectory = root,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.Environment[HarnessHome.HomeEnv] = root;
            start.Environment[HarnessHome.ChildHomeEnv] = root;
            _process = TestProcessScope.Start(start);
            _stderr = _process.StandardError.ReadToEndAsync();
            Stream = new StandardIoDuplexStream(_process.StandardOutput.BaseStream, _process.StandardInput.BaseStream, ownsStreams: true);
        }

        public Stream Stream { get; }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Stream.DisposeAsync();
                try
                {
                    if (!_process.HasExited)
                        _process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) when (_process.HasExited) { }
                await _process.WaitForExitAsync();
                await _stderr;
            }
            finally { _process.Dispose(); }
        }
    }
}