using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Dsh.Fleet;
using Dsh.RemoteHost;
using Dsh.Runtime;
using Dsh.Transport;

namespace Dsh.Tests;

[Collection(SerialProcessCollection.Name)]
public sealed class FleetProxyCommandEgressTests
{
    private const string Credential = "synthetic-user:synthetic-secret";
    private const string ReplacementCredential = "replacement-user:replacement-secret";
    private const string Target = "http://example.com/";
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(5);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("proxyCommand", false)]
    [InlineData("proxyCommand", true)]
    [InlineData("localForward", false)]
    [InlineData("localForward", true)]
    public async Task HttpAndAuthenticatedSocks_UseRealSshCommandStreams(string mode, bool socks)
    {
        FleetSshEndToEndTests.RequireEnvironment();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        await using var remoteProxy = new TestForwardProxy(socks: socks, requiredCredential: Credential);
        var machine = CreateMachine(ssh, remoteProxy, socks, mode: mode);
        await using var tunnel = new SshOutboundTunnel(machine);
        using var outbound = new OutboundProxy();
        outbound.ConfigureBorrowed(tunnel.ProxyUri.OriginalString, tunnel.ConnectAsync);
        using var client = new HttpClient(outbound.CreateHandler());

        Assert.Equal(TestForwardProxy.ProxiedBody, await client.GetStringAsync(Target, Ct));
        Assert.True(remoteProxy.Credentials.All(value => value == Credential), "The remote proxy must receive only the configured synthetic credentials.");
        Assert.Equal(socks
            ? ["SOCKS example.com:80", "GET / HTTP/1.1"]
            : new[] { $"GET {Target} HTTP/1.1" }, remoteProxy.Requests.ToArray());
        client.Dispose();
        await tunnel.DisposeAsync();
        await tunnel.DisposeAsync();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(SignalTimeout);
        Assert.Equal(1, await remoteProxy.WaitForClosedConnectionAsync(deadline.Token));
        Assert.Equal(0, remoteProxy.ActiveConnections);
    }

    [Theory]
    [InlineData("proxyCommand")]
    [InlineData("localForward")]
    public async Task JumpAuthentication_DoesNotDeleteAskPassBeforeTheTargetsEncryptedKeyPrompt(string mode)
    {
        FleetSshEndToEndTests.RequireEnvironment();
        if (!File.Exists("/bin/bash"))
            Assert.Skip("The nested SSH diagnostic relay requires POSIX bash and tee.");
        var root = TempTree.CreateDirectory("proxy-askpass");
        try
        {
            await using var target = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
            await using var jump = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
            await using var origin = new TestForwardProxy(keepAlive: true);
            await using var remoteProxy = new TestForwardProxy(requiredCredential: Credential, forward: true);
            var machine = CreateMachine(target, remoteProxy, mode: mode);
            var encryptedKey = Path.Combine(root, "encrypted_client_key");
            const string passphrase = "synthetic-key-passphrase";
            File.Copy(machine.KeyPath!, encryptedKey);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(encryptedKey, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            await TestSshServer.RunAsync("ssh-keygen", ["-q", "-p", "-P", "", "-N", passphrase, "-f", encryptedKey],
                TestContext.Current.CancellationToken);

            var jumpMachine = jump.Machine("askpass-jump", jump.Home);
            var diagnosticFile = Path.Combine(root, "jump.stderr");
            var askPassFile = Path.Combine(root, "askpass.path");
            var jumpPidFile = Path.Combine(root, "jump.pid");
            var readyFile = Path.Combine(root, "jump.ready");
            var releaseFile = Path.Combine(root, "jump.release");
            // OpenSSH runs this after jump authentication and holds the target handshake until the LocalCommand is released.
            var jumpCommand = $"""echo "$PPID" > {RemoteShell.Quote(jumpPidFile)}; echo "$SSH_ASKPASS" > {RemoteShell.Quote(askPassFile)}; : > {RemoteShell.Quote(readyFile)}; while [ ! -f {RemoteShell.Quote(releaseFile)} ]; do sleep 0.02; done""";
            var jumpOptions = string.Join(' ', jumpMachine.SshOptions!.Select(option => $"-o {RemoteShell.Quote(option)}"));
            // tee relays the actual nested authentication event to the production transport while retaining its ordering evidence.
            var proxyCommand = $"""exec ssh -v -T -F /dev/null -p {jumpMachine.Port} -i {RemoteShell.Quote(jumpMachine.KeyPath!)} {jumpOptions} -o StrictHostKeyChecking=accept-new -o BatchMode=yes -o IdentitiesOnly=yes -o IdentityAgent=none -o PermitLocalCommand=yes -o {RemoteShell.Quote($"LocalCommand={jumpCommand}")} -W %h:%p {jumpMachine.User}@{jumpMachine.Host} 2> >(tee {RemoteShell.Quote(diagnosticFile)} >&2)""";
            machine = machine with
            {
                Auth = SshAuth.Password,
                Password = passphrase,
                KeyPath = null,
                Proxy = $"/bin/bash -c {RemoteShell.Quote(proxyCommand)}",
                SshOptions = [.. machine.SshOptions!, $"IdentityFile=\"{encryptedKey}\"",
                    "IdentitiesOnly=yes", "IdentityAgent=none", "PreferredAuthentications=publickey"],
            };
            await using var tunnel = new SshOutboundTunnel(machine);
            var connections = new ConcurrentQueue<SshProxyStream>();
            async ValueTask<Stream> Connect(DnsEndPoint endpoint, CancellationToken token)
            {
                var stream = (SshProxyStream)await tunnel.ConnectAsync(endpoint, token);
                connections.Enqueue(stream);
                return stream;
            }
            // The handler explicitly proxies the isolated loopback origin because OutboundProxy intentionally bypasses local destinations.
            using var handler = new SocketsHttpHandler
            {
                Proxy = new WebProxy(tunnel.ProxyUri)
                {
                    BypassProxyOnLocal = false,
                    Credentials = new NetworkCredential("synthetic-user", "synthetic-secret"),
                },
                ConnectCallback = (context, token) => Connect(context.DnsEndPoint, token),
            };
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(SignalTimeout);
            var requestTarget = $"http://127.0.0.1:{origin.Port}/encrypted-key";
            var pending = client.GetStringAsync(requestTarget, TestContext.Current.CancellationToken);
            try
            {
                while (!File.Exists(readyFile) || !File.Exists(diagnosticFile) ||
                    !(await File.ReadAllTextAsync(diagnosticFile, deadline.Token)).Contains("Authenticated to ", StringComparison.Ordinal))
                    await Task.Delay(20, deadline.Token);

                var askPass = (await File.ReadAllTextAsync(askPassFile, deadline.Token)).Trim();
                Assert.True(File.Exists(askPass), "The authentication-owned askpass must survive the jump authentication.");
                Assert.Empty(connections);
                Assert.False(pending.IsCompleted, "The target connection must not complete at the jump's authentication event.");
                Assert.True(int.TryParse((await File.ReadAllTextAsync(jumpPidFile, deadline.Token)).Trim(), out var jumpPid) && jumpPid > 0,
                    "The authenticated jump must publish its owned SSH process identity.");
                using var jumpProcess = Process.GetProcessById(jumpPid);
                var jumpExited = jumpProcess.WaitForExitAsync(deadline.Token);

                // Releasing the jump lets the target request its encrypted-key passphrase while the fixture rejects account-password authentication.
                await File.WriteAllTextAsync(releaseFile, "", deadline.Token);
                Assert.Equal(TestForwardProxy.ProxiedBody, await pending.WaitAsync(deadline.Token));
                Assert.Equal([$"GET {requestTarget} HTTP/1.1"], remoteProxy.Requests.ToArray());
                Assert.Equal(["GET /encrypted-key HTTP/1.1"], origin.Requests.ToArray());
                Assert.Equal(Credential, Assert.Single(remoteProxy.Credentials));
                var owned = Assert.Single(connections);
                Assert.Equal(askPass, owned.Process.StartInfo.Environment["SSH_ASKPASS"]);
                Assert.False(File.Exists(askPass), "Target authentication must remove its owned askpass before transport disposal.");
                Assert.False(owned.Process.HasExited);
                var targetExited = owned.Process.WaitForExitAsync(deadline.Token);
                await tunnel.DisposeAsync();
                await tunnel.DisposeAsync();
                await targetExited;
                await jumpExited;
                await owned.DisposeAsync();
                await owned.DisposeAsync();
                Assert.False(owned.CanRead);
                Assert.False(owned.CanWrite);
                Assert.False(File.Exists(askPass), "Repeated cleanup must not recreate the owned askpass.");
                Assert.Equal(1, await remoteProxy.WaitForClosedConnectionAsync(deadline.Token));
                Assert.Equal(0, remoteProxy.ActiveConnections);
            }
            finally
            {
                await tunnel.DisposeAsync();
                try
                {
                    await pending;
                }
                catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException or ObjectDisposedException)
                {
                }
            }
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Theory]
    [InlineData("proxyCommand")]
    [InlineData("localForward")]
    public async Task BaselineChangesAndInvalidBorrowedUpdates_DoNotInterruptTheBorrowedRequest(string mode)
    {
        FleetSshEndToEndTests.RequireEnvironment();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        await using var remoteProxy = new TestForwardProxy(requiredCredential: Credential, pauseResponses: true);
        await using var latestBaseline = new TestForwardProxy(requiredCredential: ReplacementCredential);
        await using var tunnel = new SshOutboundTunnel(CreateMachine(ssh, remoteProxy, mode: mode));
        using var outbound = new OutboundProxy();
        outbound.ConfigureBorrowed(tunnel.ProxyUri.OriginalString, tunnel.ConnectAsync);
        using var client = new HttpClient(outbound.CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(SignalTimeout);
        var pending = client.GetStringAsync(Target, Ct);
        await remoteProxy.WaitForRequestAsync(deadline.Token);

        outbound.Configure($"http://{ReplacementCredential}@127.0.0.1:{latestBaseline.Port}", []);
        Assert.Throws<ArgumentException>(() => outbound.ConfigureBorrowed(null, tunnel.ConnectAsync));
        Assert.Throws<ArgumentException>(() => outbound.ConfigureBorrowed(tunnel.ProxyUri.OriginalString, null));
        Assert.Throws<ArgumentException>(() => outbound.ConfigureBorrowed("unsupported://proxy.invalid:1234", tunnel.ConnectAsync));
        remoteProxy.ReleaseResponse();
        Assert.Equal(TestForwardProxy.ProxiedBody, await pending.WaitAsync(deadline.Token));
        outbound.ConfigureBorrowed(null, null);
        Assert.Equal(TestForwardProxy.ProxiedBody, await client.GetStringAsync($"{Target}baseline", deadline.Token));
        Assert.Equal($"GET {Target}baseline HTTP/1.1", Assert.Single(latestBaseline.Requests));
        Assert.True(latestBaseline.Credentials.All(value => value == ReplacementCredential), "Clearing borrowing must restore the latest baseline authentication.");
        Assert.Single(remoteProxy.Requests);
    }

    [Fact]
    public async Task LastClosedSocket_DoesNotLoseTheSharedSshFailure()
    {
        FleetSshEndToEndTests.RequireEnvironment();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        await using var remoteProxy = new TestForwardProxy();
        await using var tunnel = new SshOutboundTunnel(CreateMachine(ssh, remoteProxy, authenticated: false, mode: "localForward"));
        SshProxyStream? connected = null;
        async ValueTask<Stream> Connect(DnsEndPoint endpoint, CancellationToken token)
        {
            connected = (SshProxyStream)await tunnel.ConnectAsync(endpoint, token);
            return connected;
        }
        using var outbound = new OutboundProxy();
        outbound.ConfigureBorrowed(tunnel.ProxyUri.OriginalString, Connect);
        var originalRoute = outbound.CaptureRoute(new Uri(Target));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(SignalTimeout);
        using (var first = new HttpClient(outbound.CreateHandler()))
            Assert.Equal(TestForwardProxy.ProxiedBody, await first.GetStringAsync($"{Target}before-idle-exit", deadline.Token));
        Assert.Equal(1, await remoteProxy.WaitForClosedConnectionAsync(deadline.Token));
        Assert.Equal(0, remoteProxy.ActiveConnections);
        var oldStream = Assert.IsType<SshProxyStream>(connected);
        await oldStream.DisposeAsync();
        Assert.False(oldStream.CanRead);
        Assert.False(oldStream.Process.HasExited);
        Assert.False(originalRoute.CancellationToken.IsCancellationRequested);
        var oldPort = Assert.IsType<int>(tunnel.LocalPort);
        var exited = oldStream.Process.WaitForExitAsync(deadline.Token);
        oldStream.Process.Kill(entireProcessTree: true);
        await exited;
        Assert.True(originalRoute.CancellationToken.IsCancellationRequested,
            "A shared SSH failure must retire the captured route even after every HTTP socket and pool has closed.");
        FleetLocalForwardEgressTests.AssertPortCanRebind(oldPort);
        using (var next = new HttpClient(outbound.CreateHandler()))
            Assert.Equal(TestForwardProxy.ProxiedBody, await next.GetStringAsync($"{Target}after-idle-exit", deadline.Token));
        Assert.NotSame(oldStream.Process, connected!.Process);
        Assert.Equal([$"GET {Target}before-idle-exit HTTP/1.1", $"GET {Target}after-idle-exit HTTP/1.1"], remoteProxy.Requests.ToArray());
        var finalPort = Assert.IsType<int>(tunnel.LocalPort);
        var finalExited = connected.Process.WaitForExitAsync(deadline.Token);
        await tunnel.DisposeAsync();
        await tunnel.DisposeAsync();
        await finalExited;
        FleetLocalForwardEgressTests.AssertPortCanRebind(finalPort);
    }

    [Theory]
    [InlineData("proxyCommand")]
    [InlineData("localForward")]
    public async Task KillingTheOwnedSsh_AbortsWithoutReplay_AndOnlyANewRequestRebuilds(string mode)
    {
        FleetSshEndToEndTests.RequireEnvironment();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        await using var remoteProxy = new TestForwardProxy(pauseResponses: true);
        await using var tunnel = new SshOutboundTunnel(CreateMachine(ssh, remoteProxy, authenticated: false, mode: mode));
        var connections = new ConcurrentQueue<SshProxyStream>();
        async ValueTask<Stream> Connect(DnsEndPoint endpoint, CancellationToken token)
        {
            var stream = (SshProxyStream)await tunnel.ConnectAsync(endpoint, token);
            connections.Enqueue(stream);
            return stream;
        }
        using var outbound = new OutboundProxy();
        outbound.ConfigureBorrowed(tunnel.ProxyUri.OriginalString, Connect);
        using var client = new HttpClient(outbound.CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(SignalTimeout);
        var originalRoute = outbound.CaptureRoute(new Uri(Target));
        var paused = client.GetStringAsync($"{Target}interrupted", Ct);
        await remoteProxy.WaitForRequestAsync(deadline.Token);
        var owned = Assert.Single(connections);
        var originalPort = tunnel.LocalPort;
        var exited = owned.Process.WaitForExitAsync(deadline.Token);
        var retiredAtExit = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        owned.Process.Exited += (_, _) => retiredAtExit.TrySetResult(originalRoute.CancellationToken.IsCancellationRequested);
        owned.Process.Kill(entireProcessTree: true);
        await exited;
        Assert.True(await retiredAtExit.Task.WaitAsync(deadline.Token),
            "An unexpected SSH exit must retire the captured HTTP route before exit observers can release a pending response.");
        remoteProxy.ReleaseResponse();
        var failure = await Record.ExceptionAsync(async () => await paused.WaitAsync(deadline.Token));
        Assert.True(failure is HttpRequestException or IOException or OperationCanceledException, "The interrupted request must fail rather than fall back or time out.");
        Assert.False(deadline.IsCancellationRequested, "The interrupted send must fail before the watchdog.");
        Assert.Equal([$"GET {Target}interrupted HTTP/1.1"], remoteProxy.Requests.ToArray());
        if (originalPort is { } port)
            FleetLocalForwardEgressTests.AssertPortCanRebind(port);
        var fresh = client.GetStringAsync($"{Target}fresh", Ct);
        await remoteProxy.WaitForRequestAsync(deadline.Token);
        remoteProxy.ReleaseResponse();
        Assert.Equal(TestForwardProxy.ProxiedBody, await fresh.WaitAsync(deadline.Token));
        Assert.Equal([$"GET {Target}interrupted HTTP/1.1", $"GET {Target}fresh HTTP/1.1"], remoteProxy.Requests.ToArray());
        var recovered = connections.ToArray();
        Assert.Equal(2, recovered.Length);
        Assert.NotSame(recovered[0].Process, recovered[1].Process);
        Assert.NotEqual(originalRoute.Generation, outbound.CaptureRoute(new Uri(Target)).Generation);
    }

    [Theory]
    [InlineData("proxyCommand", false)]
    [InlineData("proxyCommand", true)]
    [InlineData("localForward", false)]
    [InlineData("localForward", true)]
    public async Task KillOrCancelAndRepeatedDispose_PreserveTheRemoteHostAndRunningPty(string mode, bool cancelRequest)
    {
        var executable = FleetSshEndToEndTests.RequireEnvironment();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        await using var remoteProxy = new TestForwardProxy(requiredCredential: Credential, pauseResponses: true);
        var home = TempTree.CreateSocketDirectory("fps");
        try
        {
            TempTree.AssertFitsMacOsSocketLimit(RemoteHostEndpoint.SocketPath(home));
            var machine = ssh.Machine("proxy-host-survival", home) with
            {
                OutboundTunnel = new OutboundTunnelSpec(mode, $"http://{Credential}@localhost:{remoteProxy.Port}"),
            };
            await using var host = await SshHostLauncher.ConnectAsync(machine, executable, "proxy-host-survival", Ct);
            Assert.True((await host.Client.InfoAsync(Ct)).HomeRoot == home, "SSH must connect to the dedicated target Host.");
            var hostEndpoint = RemoteHostEndpoint.EndpointFile(home);
            var hostIdentity = LocalEndpointToken.Read(hostEndpoint);
            Assert.NotNull(hostIdentity);
            var shell = await host.Client.Pty.StartPtyAsync(new RemotePtyStartRequest("/bin/sh", [], home, Title: "proxy-survival-shell"), Ct);
            await using var terminal = RemoteHostPtyTests.TerminalCapture.Attach(host.Client, shell);
            await terminal.SendAsync("printf '%s\\n' \"$$\" > before.pid; echo before-$((40+2))\n");
            await terminal.WaitForAsync("before-42", SignalTimeout);
            var shellPid = await File.ReadAllTextAsync(Path.Combine(home, "before.pid"), Ct);
            Assert.True(int.TryParse(shellPid.Trim(), out var pid) && pid > 0, "The real shell must publish its process identity.");

            async Task AssertOriginalHostAndShellAsync(string markerPrefix, int left, int right)
            {
                using var arrival = CancellationTokenSource.CreateLinkedTokenSource(Ct);
                arrival.CancelAfter(SignalTimeout);
                await host.Client.PingAsync(arrival.Token);
                Assert.True(hostIdentity == LocalEndpointToken.Read(hostEndpoint), "Tunnel cleanup must not replace the target Host identity.");
                var registered = Assert.Single(await host.Client.ListHostSessionsAsync(arrival.Token), session => session.SessionId == shell);
                Assert.Equal(HostSessionKinds.Terminal, registered.Kind);
                Assert.Equal(HostSessionStatuses.Running, registered.Status);
                await terminal.SendAsync($"printf '%s\\n' \"$$\" > {markerPrefix}.pid; echo {markerPrefix}-$(({left}+{right}))\n");
                await terminal.WaitForAsync($"{markerPrefix}-{left + right}", SignalTimeout);
                Assert.Equal(shellPid, await File.ReadAllTextAsync(Path.Combine(home, $"{markerPrefix}.pid"), arrival.Token));
            }

            await using var gate = new SshAuthenticationGate();
            await using var tunnel = new SshOutboundTunnel(gate.Wrap(machine));
            var connections = new ConcurrentQueue<SshProxyStream>();
            async ValueTask<Stream> Connect(DnsEndPoint endpoint, CancellationToken token)
            {
                var stream = (SshProxyStream)await tunnel.ConnectAsync(endpoint, token);
                connections.Enqueue(stream);
                return stream;
            }
            using var outbound = new OutboundProxy();
            outbound.ConfigureBorrowed(tunnel.ProxyUri.OriginalString, Connect);
            using var client = new HttpClient(outbound.CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };
            using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            deadline.CancelAfter(SignalTimeout);
            var phase = cancelRequest ? "after-cancel" : "after-kill";
            var requestTarget = $"{Target}{phase}";
            var pending = client.GetStringAsync(requestTarget, cancelled.Token);
            try
            {
                await gate.WaitForBlockedAuthenticationAsync(pending, deadline.Token);
                var askPass = gate.AskPass;
                Assert.False(string.IsNullOrWhiteSpace(askPass), "The actual pending SSH must identify its authentication-owned askpass.");
                Assert.True(File.Exists(askPass), "The target handshake must still own its real askpass before authentication.");
                await gate.ReleaseAsync(deadline.Token);
                Assert.Equal($"GET {requestTarget} HTTP/1.1", await remoteProxy.WaitForRequestAsync(deadline.Token));
                var owned = Assert.Single(connections);
                var localPort = tunnel.LocalPort;
                Assert.Equal(askPass, owned.Process.StartInfo.Environment["SSH_ASKPASS"]);
                Assert.False(File.Exists(askPass), "Successful target authentication must remove its owned askpass before request interruption.");
                // Subscribe before termination: production cleanup may promptly dispose the Process object.
                var exited = owned.Process.WaitForExitAsync(deadline.Token);
                if (cancelRequest)
                    await cancelled.CancelAsync();
                else
                    owned.Process.Kill();
                if (!cancelRequest || mode == "proxyCommand")
                    await exited;
                remoteProxy.ReleaseResponse();
                if (cancelRequest)
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending.WaitAsync(deadline.Token));
                else
                {
                    var failure = await Record.ExceptionAsync(async () => await pending.WaitAsync(deadline.Token));
                    Assert.True(failure is HttpRequestException or IOException or OperationCanceledException,
                        "Killing only the owned proxy SSH must fail the paused request, without success or a watchdog timeout.");
                }
                Assert.False(deadline.IsCancellationRequested,
                    $"The interrupted request must settle before the watchdog; streams={connections.Count}, active={remoteProxy.ActiveConnections}, transportFailed={owned.FailureToken.IsCancellationRequested}, requests={string.Join(" | ", remoteProxy.Requests)}.");
                Assert.Equal(1, await remoteProxy.WaitForClosedConnectionAsync(deadline.Token));
                Assert.Equal(0, remoteProxy.ActiveConnections);
                await AssertOriginalHostAndShellAsync(phase, 50, 5);

                await owned.DisposeAsync();
                await owned.DisposeAsync();
                await tunnel.DisposeAsync();
                await tunnel.DisposeAsync();
                await exited;
                Assert.False(File.Exists(askPass));
                if (localPort is { } port)
                    FleetLocalForwardEgressTests.AssertPortCanRebind(port);
                Assert.False(owned.CanRead);
                Assert.False(owned.CanWrite);
                await AssertOriginalHostAndShellAsync("after-dispose", 60, 6);
                Assert.Single(connections);
                Assert.Equal([$"GET {requestTarget} HTTP/1.1"], remoteProxy.Requests.ToArray());
                Assert.True(remoteProxy.Credentials.All(value => value == Credential), "Only the configured synthetic proxy credentials may be used.");
            }
            finally
            {
                await cancelled.CancelAsync();
                client.Dispose();
                await tunnel.DisposeAsync();
                foreach (var stream in connections)
                    await stream.DisposeAsync();
                try
                {
                    await pending;
                }
                catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException or ObjectDisposedException)
                {
                }
            }
        }
        finally
        {
            RealHostProcesses.KillAll(home);
            TempTree.Delete(home);
        }
    }

    [Theory]
    [InlineData("proxyCommand")]
    [InlineData("localForward")]
    public async Task ChangingCredentialsAtTheSameProxyEndpoint_RetiresTheOldAuthenticatedPool(string mode)
    {
        FleetSshEndToEndTests.RequireEnvironment();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        await using var remoteProxy = new TestForwardProxy(requiredCredential: Credential, keepAlive: true);
        var machine = CreateMachine(ssh, remoteProxy, mode: mode);
        await using var original = new SshOutboundTunnel(machine);
        await using var replacement = new SshOutboundTunnel(machine with
        {
            OutboundTunnel = machine.OutboundTunnel! with { ProxyUrl = $"http://{ReplacementCredential}@localhost:{remoteProxy.Port}" },
        });
        using var outbound = new OutboundProxy();
        outbound.ConfigureBorrowed(original.ProxyUri.OriginalString, original.ConnectAsync);
        using var client = new HttpClient(outbound.CreateHandler());
        Assert.Equal(TestForwardProxy.ProxiedBody, await client.GetStringAsync(Target, Ct));

        remoteProxy.RequireCredential(ReplacementCredential);
        outbound.ConfigureBorrowed(replacement.ProxyUri.OriginalString, replacement.ConnectAsync);
        Assert.Equal(TestForwardProxy.ProxiedBody, await client.GetStringAsync($"{Target}replacement", Ct));

        Assert.Equal([Credential, ReplacementCredential], remoteProxy.Credentials.ToArray());
        Assert.Equal([$"GET {Target} HTTP/1.1", $"GET {Target}replacement HTTP/1.1"], remoteProxy.Requests.ToArray());
        Assert.Equal(2, remoteProxy.Connections);
        await original.DisposeAsync();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(SignalTimeout);
        Assert.Equal(1, await remoteProxy.WaitForClosedConnectionAsync(deadline.Token));
        Assert.Equal(1, remoteProxy.ActiveConnections);
    }

    [Theory]
    [InlineData("proxyCommand")]
    [InlineData("localForward")]
    public async Task CancellingOneRequest_DoesNotRetireAnotherConsumersRoute(string mode)
    {
        FleetSshEndToEndTests.RequireEnvironment();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        await using var remoteProxy = new TestForwardProxy(pauseResponses: true);
        await using var tunnel = new SshOutboundTunnel(CreateMachine(ssh, remoteProxy, authenticated: false, mode: mode));
        using var outbound = new OutboundProxy();
        outbound.ConfigureBorrowed(tunnel.ProxyUri.OriginalString, tunnel.ConnectAsync);
        using var client = new HttpClient(outbound.CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(SignalTimeout);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var original = outbound.CaptureRoute(new Uri(Target));
        var first = client.GetStringAsync($"{Target}cancelled", cancelled.Token);
        await remoteProxy.WaitForRequestAsync(deadline.Token);
        var second = client.GetStringAsync($"{Target}surviving", Ct);
        await remoteProxy.WaitForRequestAsync(deadline.Token);
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await first.WaitAsync(deadline.Token));
        remoteProxy.ReleaseResponse();
        remoteProxy.ReleaseResponse();
        Assert.Equal(TestForwardProxy.ProxiedBody, await second.WaitAsync(deadline.Token));
        Assert.False(original.CancellationToken.IsCancellationRequested);
        Assert.Equal(original.Generation, outbound.CaptureRoute(new Uri(Target)).Generation);
        Assert.Equal([$"GET {Target}cancelled HTTP/1.1", $"GET {Target}surviving HTTP/1.1"], remoteProxy.Requests.ToArray());
    }

    [Theory]
    [InlineData("proxyCommand")]
    [InlineData("localForward")]
    public async Task BypassIsDirect_AndForcedHttp3FailsWithoutOpeningSsh(string mode)
    {
        FleetSshEndToEndTests.RequireEnvironment();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        await using var remoteProxy = new TestForwardProxy();
        await using var origin = new TestForwardProxy();
        await using var tunnel = new SshOutboundTunnel(CreateMachine(ssh, remoteProxy, authenticated: false, mode: mode));
        using var outbound = new OutboundProxy();
        outbound.ConfigureBorrowed(tunnel.ProxyUri.OriginalString, tunnel.ConnectAsync);
        using var client = new HttpClient(outbound.CreateHandler());
        Assert.Equal(TestForwardProxy.ProxiedBody, await client.GetStringAsync($"http://127.0.0.1:{origin.Port}/direct", Ct));
        Assert.Equal("GET /direct HTTP/1.1", Assert.Single(origin.Requests));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/")
        {
            Version = HttpVersion.Version30,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        await Assert.ThrowsAsync<NotSupportedException>(() => client.SendAsync(request, Ct));
        Assert.Empty(remoteProxy.Requests);
        Assert.Equal(0, remoteProxy.Connections);
    }

    private static MachineSpec CreateMachine(TestSshServer ssh, TestForwardProxy proxy, bool socks = false, bool authenticated = true, string mode = "proxyCommand")
        => ssh.Machine("command-stream", ssh.Home) with
        {
            OutboundTunnel = new OutboundTunnelSpec(mode,
                $"{(socks ? "socks5" : "http")}://{(authenticated ? $"{Credential}@" : "")}localhost:{proxy.Port}"),
        };
}
