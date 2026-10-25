using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Dsh.Boot;
using Dsh.Fleet;
using Dsh.Runtime;
using Dsh.Transport;
using Dsh.Web;

namespace Dsh.Tests;

/// <summary>Actual SSH listeners, HTTP consumers and route leases establish local-forward ownership and failure boundaries.</summary>
public sealed class FleetLocalForwardEgressTests
{
    private const string Target = "http://example.com/";
    private const string Credential = "synthetic-forward-user:synthetic-forward-secret";
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(5);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OccupiedPort_FailsWithoutChangingPortOrKillingTheIncumbent_AndFreshOperationCanSucceed(bool candidateHandoffRace)
    {
        FleetSshEndToEndTests.RequireEnvironment();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        await using var forwarding = new TestForwardProxy(keepAlive: true);
        await using var gate = new SshAuthenticationGate();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(SignalTimeout);
        using var fixedIncumbent = new TcpListener(IPAddress.Loopback, 0);
        int? fixedPort = null;
        if (!candidateHandoffRace)
        {
            fixedIncumbent.Start();
            fixedPort = ((IPEndPoint)fixedIncumbent.LocalEndpoint).Port;
        }
        var machine = CreateMachine(ssh, forwarding, localPort: fixedPort);
        await using var tunnel = new SshOutboundTunnel(gate.Wrap(machine));
        using var outbound = new OutboundProxy();
        outbound.ConfigureBorrowed(tunnel.ProxyUri.OriginalString, tunnel.ConnectAsync);
        using var consumer = new HttpClient(outbound.CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        var pending = consumer.GetStringAsync($"{Target}conflict", deadline.Token);
        await gate.WaitForBlockedAuthenticationAsync(pending, deadline.Token);
        var owned = Assert.IsType<Process>(tunnel.Process);
        var ownedId = owned.Id;
        var port = Assert.IsType<int>(tunnel.LocalPort);
        var askPass = owned.StartInfo.Environment["SSH_ASKPASS"];
        Assert.Equal(gate.AskPass, askPass);
        Assert.True(File.Exists(askPass), "Authentication must still own its actual askpass while the target handshake is held.");
        using var candidateIncumbent = new TcpListener(IPAddress.Loopback, port);
        var incumbent = candidateHandoffRace ? candidateIncumbent : fixedIncumbent;
        if (candidateHandoffRace)
            candidateIncumbent.Start();
        else
            Assert.Equal(fixedPort, port);
        var exited = owned.WaitForExitAsync(deadline.Token);
        await gate.ReleaseAsync(deadline.Token);
        var failure = await Record.ExceptionAsync(async () => await pending.WaitAsync(deadline.Token));
        Assert.True(failure is HttpRequestException or IOException or InvalidOperationException,
            "A real bind conflict must fail the operation, not succeed, cancel at the watchdog, or silently move to another port.");
        await exited;
        Assert.Equal(port, tunnel.LocalPort);
        Assert.Empty(forwarding.Requests);
        Assert.False(File.Exists(askPass), "The failed child must remove its authentication-owned askpass.");

        // The incumbent must still accept and transmit its own bytes after the owned SSH has failed.
        using (var witness = new TcpClient())
        {
            var accepted = incumbent.AcceptTcpClientAsync(deadline.Token);
            await witness.ConnectAsync(IPAddress.Loopback, port, deadline.Token);
            using var server = await accepted;
            await server.GetStream().WriteAsync("incumbent-owned"u8.ToArray(), deadline.Token);
            var bytes = new byte["incumbent-owned".Length];
            await witness.GetStream().ReadExactlyAsync(bytes, deadline.Token);
            Assert.Equal("incumbent-owned"u8.ToArray(), bytes);
        }
        incumbent.Stop();
        AssertPortCanRebind(port);

        // The controller keeps this exact delegate and client after transport failure; only a new request may renew it.
        var next = consumer.GetStringAsync($"{Target}fresh", deadline.Token);
        await gate.WaitForBlockedAuthenticationAsync(next, deadline.Token);
        var freshProcess = Assert.IsType<Process>(tunnel.Process);
        Assert.NotSame(owned, freshProcess);
        Assert.NotEqual(ownedId, freshProcess.Id);
        Assert.False(freshProcess.HasExited);
        var freshPort = Assert.IsType<int>(tunnel.LocalPort);
        if (fixedPort is not null)
            Assert.Equal(fixedPort, freshPort);
        Assert.Contains($"127.0.0.1:{freshPort}:localhost:{forwarding.Port}", freshProcess.StartInfo.ArgumentList);
        var freshAskPass = freshProcess.StartInfo.Environment["SSH_ASKPASS"];
        Assert.Equal(gate.AskPass, freshAskPass);
        Assert.NotEqual(askPass, freshAskPass);
        Assert.True(File.Exists(freshAskPass));
        Assert.False(File.Exists(askPass));
        await gate.ReleaseAsync(deadline.Token);
        Assert.Equal(TestForwardProxy.ProxiedBody, await next.WaitAsync(deadline.Token));
        Assert.Equal($"GET {Target}fresh HTTP/1.1", Assert.Single(forwarding.Requests));
        AssertOnlyLoopbackListener(freshPort);
        Assert.Same(freshProcess, tunnel.Process);
        Assert.False(freshProcess.HasExited);
        var freshExited = freshProcess.WaitForExitAsync(deadline.Token);
        consumer.Dispose();
        await tunnel.DisposeAsync();
        await tunnel.DisposeAsync();
        await freshExited;
        Assert.False(File.Exists(freshAskPass));
        AssertPortCanRebind(freshPort);
    }

    [Theory]
    [InlineData("ForkAfterAuthentication=yes")]
    [InlineData("ClearAllForwardings=yes")]
    [InlineData("ForkAfterAuthentication=yes", "ClearAllForwardings=yes")]
    public async Task UserSshLifetimeOptions_CannotDetachOrRemoveTheManagedForward(params string[] options)
    {
        FleetSshEndToEndTests.RequireEnvironment();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        await using var forwarding = new TestForwardProxy(keepAlive: true);
        var machine = CreateMachine(ssh, forwarding);
        await using var tunnel = new SshOutboundTunnel(machine with
        {
            SshOptions = [.. machine.SshOptions ?? [], .. options],
        });
        using var outbound = new OutboundProxy();
        outbound.ConfigureBorrowed(tunnel.ProxyUri.OriginalString, tunnel.ConnectAsync);
        using var consumer = new HttpClient(outbound.CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(SignalTimeout);
        Assert.Equal(TestForwardProxy.ProxiedBody, await consumer.GetStringAsync($"{Target}managed-options", deadline.Token));
        Assert.Equal($"GET {Target}managed-options HTTP/1.1", Assert.Single(forwarding.Requests));
        var process = Assert.IsType<Process>(tunnel.Process);
        Assert.False(process.HasExited, "The actual SSH owner must remain foreground and alive after authenticating.");
        var port = Assert.IsType<int>(tunnel.LocalPort);
        AssertOnlyLoopbackListener(port);
        var askPass = process.StartInfo.Environment["SSH_ASKPASS"];
        Assert.False(string.IsNullOrWhiteSpace(askPass));
        Assert.False(File.Exists(askPass));
        var exited = process.WaitForExitAsync(deadline.Token);
        consumer.Dispose();
        await tunnel.DisposeAsync();
        await tunnel.DisposeAsync();
        await exited;
        Assert.False(File.Exists(askPass));
        AssertPortCanRebind(port);
    }

    [Fact]
    public async Task ExplicitDisposal_WithObstructedOwnedSecret_CanRetryRemovalAndReapAllResources()
    {
        FleetSshEndToEndTests.RequireEnvironment();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        await using var forwarding = new TestForwardProxy();
        await using var gate = new SshAuthenticationGate();
        var tunnel = new SshOutboundTunnel(gate.Wrap(CreateMachine(ssh, forwarding)));
        using var outbound = new OutboundProxy();
        outbound.ConfigureBorrowed(tunnel.ProxyUri.OriginalString, tunnel.ConnectAsync);
        using var consumer = new HttpClient(outbound.CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(SignalTimeout);
        var root = TempTree.CreateSocketDirectory("f81d");
        var quarantine = Path.Combine(root, "owned-askpass");
        string? askPass = null;
        Task<string>? pending = null;
        try
        {
            pending = consumer.GetStringAsync($"{Target}obstructed-secret", Ct);
            await gate.WaitForBlockedAuthenticationAsync(pending, deadline.Token);
            var process = Assert.IsType<Process>(tunnel.Process);
            var port = Assert.IsType<int>(tunnel.LocalPort);
            askPass = process.StartInfo.Environment["SSH_ASKPASS"];
            Assert.Equal(gate.AskPass, askPass);
            Assert.True(File.Exists(askPass));
            var exited = process.WaitForExitAsync(deadline.Token);
            File.Move(askPass!, quarantine);
            Directory.CreateDirectory(askPass!);

            var removalFailure = await Record.ExceptionAsync(async () => await tunnel.DisposeAsync());
            Assert.True(removalFailure is IOException or UnauthorizedAccessException,
                "The first explicit disposal must report the real owned-secret deletion failure.");
            Assert.Same(process, tunnel.Process);
            Assert.Equal(port, tunnel.LocalPort);
            Assert.True(Directory.Exists(askPass));
            Assert.True(File.Exists(quarantine), "Failed removal must not discard ownership of the quarantined synthetic secret.");

            Directory.Delete(askPass!);
            File.Move(quarantine, askPass!);
            await tunnel.DisposeAsync();
            await tunnel.DisposeAsync();
            await exited;
            var requestFailure = await Record.ExceptionAsync(async () => await pending.WaitAsync(deadline.Token));
            Assert.True(requestFailure is HttpRequestException or IOException or OperationCanceledException or ObjectDisposedException,
                "Explicit disposal must settle the actual blocked consumer, not leave its stream pending.");
            Assert.False(deadline.IsCancellationRequested, "Consumer cleanup must finish before the watchdog, not because of it.");
            Assert.False(File.Exists(askPass));
            Assert.False(Directory.Exists(askPass));
            Assert.False(File.Exists(quarantine));
            Assert.Empty(forwarding.Requests);
            Assert.Equal(0, forwarding.Connections);
            AssertPortCanRebind(port);
        }
        finally
        {
            try
            {
                if (askPass is not null && Directory.Exists(askPass))
                    Directory.Delete(askPass);
                if (askPass is not null && File.Exists(quarantine))
                    File.Move(quarantine, askPass);
                await tunnel.DisposeAsync();
                if (pending is not null)
                {
                    try
                    {
                        await pending.WaitAsync(deadline.Token);
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
    }

    [Fact]
    public async Task CancellingPendingAuthentication_ReapsOwnedSshAndAskpass_WithoutOpeningProxy()
    {
        FleetSshEndToEndTests.RequireEnvironment();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        await using var forwarding = new TestForwardProxy();
        await using var gate = new SshAuthenticationGate();
        await using var tunnel = new SshOutboundTunnel(gate.Wrap(CreateMachine(ssh, forwarding)));
        using var outbound = new OutboundProxy();
        outbound.ConfigureBorrowed(tunnel.ProxyUri.OriginalString, tunnel.ConnectAsync);
        using var consumer = new HttpClient(outbound.CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(SignalTimeout);
        var pending = consumer.GetStringAsync($"{Target}cancel-authentication", cancelled.Token);
        await gate.WaitForBlockedAuthenticationAsync(pending, deadline.Token);
        var process = Assert.IsType<Process>(tunnel.Process);
        var port = Assert.IsType<int>(tunnel.LocalPort);
        var askPass = process.StartInfo.Environment["SSH_ASKPASS"];
        Assert.Equal(gate.AskPass, askPass);
        Assert.True(File.Exists(askPass));
        var exited = process.WaitForExitAsync(deadline.Token);
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending.WaitAsync(deadline.Token));
        await tunnel.DisposeAsync();
        await tunnel.DisposeAsync();
        await exited;
        Assert.False(File.Exists(askPass));
        AssertPortCanRebind(port);
        Assert.Empty(forwarding.Requests);
        Assert.Equal(0, forwarding.Connections);
    }

    [Fact]
    public async Task CancellingOnePendingAuthentication_PreservesTheOtherConsumersSharedStartup()
    {
        FleetSshEndToEndTests.RequireEnvironment();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        await using var forwarding = new TestForwardProxy(keepAlive: true);
        await using var gate = new SshAuthenticationGate();
        await using var tunnel = new SshOutboundTunnel(gate.Wrap(CreateMachine(ssh, forwarding)));
        using var outbound = new OutboundProxy();
        outbound.ConfigureBorrowed(tunnel.ProxyUri.OriginalString, tunnel.ConnectAsync);
        using var firstConsumer = new HttpClient(outbound.CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        using var secondConsumer = new HttpClient(outbound.CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(SignalTimeout);
        var first = firstConsumer.GetStringAsync($"{Target}cancelled-during-authentication", cancelled.Token);
        await gate.WaitForBlockedAuthenticationAsync(first, deadline.Token);
        var process = Assert.IsType<Process>(tunnel.Process);
        var port = Assert.IsType<int>(tunnel.LocalPort);
        var askPass = process.StartInfo.Environment["SSH_ASKPASS"];
        var second = secondConsumer.GetStringAsync($"{Target}surviving-authentication", deadline.Token);
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await first.WaitAsync(deadline.Token));
        Assert.False(process.HasExited);
        Assert.True(File.Exists(askPass), "The remaining authenticating consumer must retain the real SSH and its askpass.");
        await gate.ReleaseAsync(deadline.Token);
        Assert.Equal(TestForwardProxy.ProxiedBody, await second.WaitAsync(deadline.Token));
        Assert.Same(process, tunnel.Process);
        AssertOnlyLoopbackListener(port);
        Assert.Equal($"GET {Target}surviving-authentication HTTP/1.1", Assert.Single(forwarding.Requests));
        Assert.False(File.Exists(askPass));
        var exited = process.WaitForExitAsync(deadline.Token);
        secondConsumer.Dispose();
        await tunnel.DisposeAsync();
        await exited;
        AssertPortCanRebind(port);
    }

    [Fact]
    public async Task ConcurrentConsumers_ShareOneSsh_AndCancellationOnlyReleasesItsOwnSocket()
    {
        FleetSshEndToEndTests.RequireEnvironment();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        await using var forwarding = new TestForwardProxy(pauseResponses: true, keepAlive: true);
        await using var tunnel = new SshOutboundTunnel(CreateMachine(ssh, forwarding));
        var streams = new ConcurrentQueue<SshProxyStream>();
        async ValueTask<Stream> Connect(DnsEndPoint endpoint, CancellationToken token)
        {
            var stream = (SshProxyStream)await tunnel.ConnectAsync(endpoint, token);
            streams.Enqueue(stream);
            return stream;
        }
        using var outbound = new OutboundProxy();
        outbound.ConfigureBorrowed(tunnel.ProxyUri.OriginalString, Connect);
        using var firstClient = new HttpClient(outbound.CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        using var secondClient = new HttpClient(outbound.CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(SignalTimeout);
        var generation = outbound.CaptureRoute(new Uri(Target));
        var first = firstClient.GetStringAsync($"{Target}cancelled-consumer", cancelled.Token);
        Assert.Equal($"GET {Target}cancelled-consumer HTTP/1.1", await forwarding.WaitForRequestAsync(deadline.Token));
        var second = secondClient.GetStringAsync($"{Target}surviving-consumer", Ct);
        Assert.Equal($"GET {Target}surviving-consumer HTTP/1.1", await forwarding.WaitForRequestAsync(deadline.Token));
        var connected = streams.ToArray();
        Assert.Equal(2, connected.Length);
        Assert.NotSame(connected[0], connected[1]);
        Assert.Same(connected[0].Process, connected[1].Process);
        var process = connected[1].Process;
        var port = Assert.IsType<int>(tunnel.LocalPort);
        AssertOnlyLoopbackListener(port);
        var askPass = process.StartInfo.Environment["SSH_ASKPASS"];
        Assert.False(string.IsNullOrWhiteSpace(askPass));
        Assert.False(File.Exists(askPass));
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await first.WaitAsync(deadline.Token));
        forwarding.ReleaseResponse();
        forwarding.ReleaseResponse();
        Assert.Equal(TestForwardProxy.ProxiedBody, await second.WaitAsync(deadline.Token));
        await connected[0].DisposeAsync();
        await connected[0].DisposeAsync();
        Assert.False(process.HasExited, "One consumer cancellation must leave the second consumer's actual shared SSH alive.");
        Assert.False(generation.CancellationToken.IsCancellationRequested);
        Assert.Equal(generation.Generation, outbound.CaptureRoute(new Uri(Target)).Generation);
        AssertOnlyLoopbackListener(port);
        var exited = process.WaitForExitAsync(deadline.Token);
        secondClient.Dispose();
        await connected[1].DisposeAsync();
        await connected[1].DisposeAsync();
        await tunnel.DisposeAsync();
        await tunnel.DisposeAsync();
        await exited;
        Assert.False(File.Exists(askPass));
        AssertPortCanRebind(port);
        Assert.Equal([$"GET {Target}cancelled-consumer HTTP/1.1", $"GET {Target}surviving-consumer HTTP/1.1"], forwarding.Requests.ToArray());
    }

    [Theory]
    [InlineData("proxyCommand", false)]
    [InlineData("proxyCommand", true)]
    [InlineData("localForward", false)]
    [InlineData("localForward", true)]
    public async Task CancellationAtProxyResponseBoundary_ReleasesOwnedProcessAndPort(string mode, bool responseBody)
    {
        FleetSshEndToEndTests.RequireEnvironment();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        await using var forwarding = new TestForwardProxy(pauseResponses: !responseBody, pauseBody: responseBody);
        await using var tunnel = new SshOutboundTunnel(CreateMachine(ssh, forwarding, mode));
        SshProxyStream? stream = null;
        async ValueTask<Stream> Connect(DnsEndPoint endpoint, CancellationToken token)
        {
            stream = (SshProxyStream)await tunnel.ConnectAsync(endpoint, token);
            return stream;
        }
        using var outbound = new OutboundProxy();
        outbound.ConfigureBorrowed(tunnel.ProxyUri.OriginalString, Connect);
        using var consumer = new HttpClient(outbound.CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(SignalTimeout);
        var target = $"{Target}{(responseBody ? "response-body" : "accepted-no-response")}";
        Task<string> pending;
        HttpResponseMessage? response = null;
        if (responseBody)
        {
            response = await consumer.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, cancelled.Token).WaitAsync(deadline.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            pending = response.Content.ReadAsStringAsync(cancelled.Token);
        }
        else
            pending = consumer.GetStringAsync(target, cancelled.Token);
        try
        {
            Assert.Equal($"GET {target} HTTP/1.1", await forwarding.WaitForRequestAsync(deadline.Token));
            Assert.NotNull(stream);
            Assert.False(pending.IsCompleted, "The real response barrier must hold the consumer at its requested cancellation boundary.");
            var process = stream.Process;
            var askPass = process.StartInfo.Environment["SSH_ASKPASS"];
            var localPort = tunnel.LocalPort;
            var exited = process.WaitForExitAsync(deadline.Token);
            await cancelled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending.WaitAsync(deadline.Token));
            response?.Dispose();
            consumer.Dispose();
            await stream.DisposeAsync();
            await stream.DisposeAsync();
            forwarding.ReleaseResponse();
            Assert.Equal(1, await forwarding.WaitForClosedConnectionAsync(deadline.Token));
            await tunnel.DisposeAsync();
            await tunnel.DisposeAsync();
            await exited;
            Assert.False(File.Exists(askPass));
            if (localPort is { } port)
                AssertPortCanRebind(port);
            Assert.Equal([$"GET {target} HTTP/1.1"], forwarding.Requests.ToArray());
        }
        finally
        {
            response?.Dispose();
            await cancelled.CancelAsync();
        }
    }

    [Fact]
    public async Task BorrowedRouteLeases_SwitchClearAndRepeatedDisposal_RetireOnlyTheOwnedGeneration()
    {
        FleetSshEndToEndTests.RequireEnvironment();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        await using var firstProxy = new TestForwardProxy(keepAlive: true);
        await using var secondProxy = new TestForwardProxy(keepAlive: true);
        await using var baseline = new TestForwardProxy();
        var root = TempTree.CreateSocketDirectory("f81r");
        try
        {
            TempTree.AssertFitsMacOsSocketLimit(Path.Combine(root, "run", "host.sock"));
            using var firstReservation = new TcpListener(IPAddress.Loopback, 0);
            using var secondReservation = new TcpListener(IPAddress.Loopback, 0);
            firstReservation.Start();
            secondReservation.Start();
            var firstPort = ((IPEndPoint)firstReservation.LocalEndpoint).Port;
            var secondPort = ((IPEndPoint)secondReservation.LocalEndpoint).Port;
            var home = new HarnessHome(root);
            new HarnessSettings { Network = new NetworkSettings { OutboundProxy = new OutboundProxySettings
            {
                Url = $"http://127.0.0.1:{baseline.Port}", ProbeUrl = $"{Target}lease-validation",
            } } }.Save(home);
            var store = new MachineStore(home);
            store.Upsert(BorrowedOutboundProxyTests.ObserveSsh(ssh.Machine("A", root), root) with
            {
                OutboundTunnel = new("localForward", $"http://127.0.0.1:{firstProxy.Port}", firstPort),
            });
            store.Upsert(BorrowedOutboundProxyTests.ObserveSsh(ssh.Machine("B", root), root) with
            {
                OutboundTunnel = new("localForward", $"http://127.0.0.1:{secondProxy.Port}", secondPort),
            });
            firstReservation.Stop();
            secondReservation.Stop();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            deadline.CancelAfter(SignalTimeout);
            using var outbound = new OutboundProxy();
            await using var route = new BorrowedOutboundRoute(home, outbound);
            await using var firstLease = await route.AcquireAsync(deadline.Token);
            await using var secondLease = await route.AcquireAsync(deadline.Token);
            using var firstConsumer = new HttpClient(outbound.CreateHandler());
            using var secondConsumer = new HttpClient(outbound.CreateHandler());
            await route.SelectAsync("A", deadline.Token);
            Assert.Equal(TestForwardProxy.ProxiedBody, await firstConsumer.GetStringAsync($"{Target}first-lease", deadline.Token));
            using var firstProcess = Process.GetProcessById(await LastObservedProcessIdAsync(root, deadline.Token));
            Assert.Equal(TestForwardProxy.ProxiedBody, await secondConsumer.GetStringAsync($"{Target}second-lease", deadline.Token));
            Assert.Equal(firstProcess.Id, await LastObservedProcessIdAsync(root, deadline.Token));
            AssertOnlyLoopbackListener(firstPort);
            await firstLease.DisposeAsync();
            await firstLease.DisposeAsync();
            Assert.Equal(TestForwardProxy.ProxiedBody, await secondConsumer.GetStringAsync($"{Target}after-one-lease", deadline.Token));
            Assert.False(firstProcess.HasExited);
            var firstExited = firstProcess.WaitForExitAsync(deadline.Token);
            await route.SelectAsync("B", deadline.Token);
            await firstExited;
            AssertPortCanRebind(firstPort);
            Assert.Equal(TestForwardProxy.ProxiedBody, await secondConsumer.GetStringAsync($"{Target}replacement-lease", deadline.Token));
            using var secondProcess = Process.GetProcessById(await LastObservedProcessIdAsync(root, deadline.Token));
            Assert.NotEqual(firstProcess.Id, secondProcess.Id);
            AssertOnlyLoopbackListener(secondPort);
            var secondExited = secondProcess.WaitForExitAsync(deadline.Token);
            await route.SelectAsync(null, deadline.Token);
            await secondExited;
            AssertPortCanRebind(secondPort);
            Assert.Equal(TestForwardProxy.ProxiedBody, await secondConsumer.GetStringAsync($"{Target}baseline-after-clear", deadline.Token));
            Assert.Equal($"GET {Target}baseline-after-clear HTTP/1.1", Assert.Single(baseline.Requests));
            await route.SelectAsync("A", deadline.Token);
            Assert.Equal(TestForwardProxy.ProxiedBody, await secondConsumer.GetStringAsync($"{Target}final-live-lease", deadline.Token));
            using var finalProcess = Process.GetProcessById(await LastObservedProcessIdAsync(root, deadline.Token));
            AssertOnlyLoopbackListener(firstPort);
            var finalExited = finalProcess.WaitForExitAsync(deadline.Token);
            Assert.Equal([$"GET {Target}lease-validation HTTP/1.1", $"GET {Target}first-lease HTTP/1.1", $"GET {Target}second-lease HTTP/1.1", $"GET {Target}after-one-lease HTTP/1.1", $"GET {Target}lease-validation HTTP/1.1", $"GET {Target}final-live-lease HTTP/1.1"], firstProxy.Requests.ToArray());
            Assert.Equal([$"GET {Target}lease-validation HTTP/1.1", $"GET {Target}replacement-lease HTTP/1.1"], secondProxy.Requests.ToArray());
            await secondLease.DisposeAsync();
            await secondLease.DisposeAsync();
            await finalExited;
            AssertPortCanRebind(firstPort);
            await route.DisposeAsync();
            await route.DisposeAsync();
            Assert.Equal("A", HarnessSettings.Load(home).Network?.BorrowedOutboundMachine);
            await AssertOwnedSshRetiredAsync(root, "localForward", firstProxy.Port, secondProxy.Port);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Theory]
    [InlineData("proxyCommand", "http")]
    [InlineData("proxyCommand", "socks5")]
    [InlineData("localForward", "http")]
    [InlineData("localForward", "socks5")]
    public async Task WebFetch_PinsPublicDnsAndRejectsMixedPrivateAnswersBeforeSsh(string mode, string scheme)
    {
        FleetSshEndToEndTests.RequireEnvironment();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        await using var forwarding = new TestForwardProxy(socks: scheme == "socks5", requiredCredential: Credential);
        var machine = CreateMachine(ssh, forwarding, mode) with
        {
            OutboundTunnel = new(mode, $"{scheme}://{Credential}@localhost:{forwarding.Port}"),
        };
        await using var tunnel = new SshOutboundTunnel(machine);
        using var outbound = new OutboundProxy();
        outbound.ConfigureBorrowed(tunnel.ProxyUri.OriginalString, tunnel.ConnectAsync);
        var provider = new HttpFetchProvider(resolveAddresses: (_, _) =>
            Task.FromResult<IReadOnlyList<PublicAddress>>([new("1.1.1.1", 4)]), proxy: outbound);
        var result = await provider.Fetch(new WebFetchRequest("http://unresolvable.test/page"), Ct);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal(TestForwardProxy.ProxiedBody, result.Body.Content);
        Assert.Contains("Host: unresolvable.test", forwarding.Headers);
        string[] expected = scheme == "socks5"
            ? ["SOCKS 1.1.1.1:80", "GET /page HTTP/1.1"]
            : ["GET http://1.1.1.1/page HTTP/1.1"];
        Assert.Equal(expected, forwarding.Requests.ToArray());
        Assert.All(forwarding.Credentials, credential => Assert.Equal(Credential, credential));
        var blocked = new HttpFetchProvider(resolveAddresses: (host, token) =>
            WebFetchHttpNetwork.ResolvePublicAddresses(host, token, (_, _) =>
                Task.FromResult<IReadOnlyList<PublicAddress>>([new("1.1.1.1", 4), new("127.0.0.1", 4)])), proxy: outbound);
        var error = await Assert.ThrowsAsync<WebError>(() => blocked.Fetch(new WebFetchRequest("http://mixed-private.test/"), Ct));
        Assert.Equal(WebErrorCodes.BlockedUrl, error.Code);
        Assert.Equal(expected, forwarding.Requests.ToArray());
        Assert.Equal(1, forwarding.Connections);
    }

    [Theory(Explicit = true, Timeout = 5000)]
    [InlineData("proxyCommand")]
    [InlineData("localForward")]
    public async Task PublicHttpsRedirect_ToPrivateDestination_IsBlockedBeforeAnotherProxyRequest(string mode)
    {
        FleetSshEndToEndTests.RequireEnvironment();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        await using var forwarding = new TestForwardProxy(forward: true);
        await using var tunnel = new SshOutboundTunnel(CreateMachine(ssh, forwarding, mode));
        using var outbound = new OutboundProxy();
        outbound.ConfigureBorrowed(tunnel.ProxyUri.OriginalString, tunnel.ConnectAsync);
        IReadOnlyList<PublicAddress>? publicAddresses = null;
        var provider = new HttpFetchProvider(resolveAddresses: async (host, token) =>
        {
            Assert.Equal("httpbin.org", host);
            return publicAddresses = await WebFetchHttpNetwork.ResolvePublicAddresses(host, token);
        }, proxy: outbound);
        var error = await Assert.ThrowsAsync<WebError>(async () =>
        {
            var response = await provider.Fetch(new WebFetchRequest(
                "https://httpbin.org/redirect-to?url=http%3A%2F%2F127.0.0.1%2Fprivate&status_code=302"), Ct);
            Assert.Fail($"The public redirect prerequisite returned terminal HTTP {response.StatusCode} instead of redirecting.");
        });
        Assert.Equal(WebErrorCodes.RedirectBlocked, error.Code);
        Assert.NotNull(publicAddresses);
        Assert.Contains(Assert.Single(forwarding.Requests), publicAddresses.Select(address =>
            $"CONNECT {(address.Family == 6 ? $"[{address.Address}]" : address.Address)}:443 HTTP/1.1"));
    }

    [Fact(Explicit = true, Timeout = 5000)]
    public async Task LocalForward_ProductionWebFetchExampleDomain_UsesValidatedTlsAndOnlyLoopbackListener()
    {
        FleetSshEndToEndTests.RequireEnvironment();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        await using var forwarding = new TestForwardProxy(forward: true, requiredCredential: Credential);
        await using var tunnel = new SshOutboundTunnel(CreateMachine(ssh, forwarding) with
        {
            OutboundTunnel = new("localForward", $"http://{Credential}@localhost:{forwarding.Port}"),
        });
        IReadOnlyList<PublicAddress>? addresses = null;
        var observedListener = false;
        async ValueTask<Stream> Connect(DnsEndPoint endpoint, CancellationToken token)
        {
            var stream = await tunnel.ConnectAsync(endpoint, token);
            AssertOnlyLoopbackListener(Assert.IsType<int>(tunnel.LocalPort));
            observedListener = true;
            return stream;
        }
        using var outbound = new OutboundProxy();
        outbound.ConfigureBorrowed(tunnel.ProxyUri.OriginalString, Connect);
        var provider = new HttpFetchProvider(resolveAddresses: async (host, token) =>
        {
            Assert.Equal("example.com", host);
            return addresses = await WebFetchHttpNetwork.ResolvePublicAddresses(host, token);
        }, proxy: outbound);
        // Production fetch uses the original HTTPS URI and default platform certificate validation, with no TLS callback.
        var result = await provider.Fetch(new WebFetchRequest("https://example.com/"), Ct);
        Assert.Equal("https://example.com/", result.Url);
        Assert.Equal(200, result.StatusCode);
        Assert.Contains("Example Domain", result.Body.Content, StringComparison.Ordinal);
        Assert.True(observedListener, "The completed web_fetch must have traversed an actual loopback-only SSH listener.");
        Assert.NotNull(addresses);
        Assert.Contains(Assert.Single(forwarding.Requests), addresses.Select(address => $"CONNECT {(address.Family == 6 ? $"[{address.Address}]" : address.Address)}:443 HTTP/1.1"));
        Assert.All(forwarding.Credentials, credential => Assert.Equal(Credential, credential));
    }

    internal static void AssertOnlyLoopbackListener(int port)
    {
        var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Where(endpoint => endpoint.Port == port).ToArray();
        Assert.Equal(IPAddress.Loopback, Assert.Single(listeners).Address);
    }

    internal static void AssertPortCanRebind(int port)
    {
        using var rebound = new TcpListener(IPAddress.Loopback, port);
        rebound.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        rebound.Start();
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, port), rebound.LocalEndpoint);
    }

    internal static async Task AssertOwnedSshRetiredAsync(string root, string mode, params int[] proxyPorts)
    {
        if (mode == "proxyCommand")
        {
            await BorrowedOutboundProxyTests.AssertOwnedSshRetiredAsync(root, proxyPorts);
            return;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(SignalTimeout);
        var commands = await File.ReadAllLinesAsync(Path.Combine(root, "owned-ssh-commands.txt"), deadline.Token);
        Assert.NotEmpty(commands);
        var ports = new HashSet<int>();
        foreach (var command in commands)
        {
            var arguments = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var index = Array.IndexOf(arguments, "-L");
            Assert.True(index >= 0 && index + 1 < arguments.Length, "The recorded owner must be the real local-forward SSH, not a remote Host or Network RPC.");
            var fields = arguments[index + 1].Split(':');
            Assert.Equal("127.0.0.1", fields[0]);
            Assert.Contains(int.Parse(fields[^1], System.Globalization.CultureInfo.InvariantCulture), proxyPorts);
            ports.Add(int.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture));
        }
        foreach (var resource in await File.ReadAllLinesAsync(Path.Combine(root, "owned-ssh-resources.txt"), deadline.Token))
        {
            var fields = resource.Split('|', 2);
            Assert.Equal(2, fields.Length);
            var pid = int.Parse(fields[0], System.Globalization.CultureInfo.InvariantCulture);
            try
            {
                using var process = Process.GetProcessById(pid);
                await process.WaitForExitAsync(deadline.Token);
            }
            catch (ArgumentException)
            {
                // A reaped child no longer has an OS process identity.
            }
            Assert.False(string.IsNullOrWhiteSpace(fields[1]));
            Assert.False(File.Exists(fields[1]), "The retired real SSH must leave no authentication-owned askpass file.");
        }
        foreach (var port in ports)
            AssertPortCanRebind(port);
    }

    private static async Task<int> LastObservedProcessIdAsync(string root, CancellationToken cancellationToken)
    {
        var resources = await File.ReadAllLinesAsync(Path.Combine(root, "owned-ssh-resources.txt"), cancellationToken);
        return int.Parse(resources[^1].Split('|', 2)[0], System.Globalization.CultureInfo.InvariantCulture);
    }

    private static MachineSpec CreateMachine(TestSshServer ssh, TestForwardProxy forwarding, string mode = "localForward", int? localPort = null)
    {
        var machine = ssh.Machine("local-forward", ssh.Home);
        return machine with
        {
            Auth = SshAuth.Password,
            Password = "synthetic-forward-password",
            SshOptions = [.. machine.SshOptions ?? [], $"IdentityFile={machine.KeyPath}", "PreferredAuthentications=publickey", "IdentitiesOnly=yes", "IdentityAgent=none"],
            OutboundTunnel = new(mode, $"http://localhost:{forwarding.Port}", localPort),
        };
    }
}
