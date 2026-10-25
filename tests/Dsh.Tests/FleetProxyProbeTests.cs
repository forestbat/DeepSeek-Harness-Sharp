using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using Dsh.Boot;
using Dsh.Fleet;
using Dsh.RemoteHost;
using Dsh.Runtime;
using Dsh.Transport;

namespace Dsh.Tests;

[Collection(SerialProcessCollection.Name)]
public sealed class FleetProxyProbeTests
{
    private const string PublicTarget = "http://1.1.1.1/layer-probe";
    private static readonly TimeSpan CancellationProofTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DeadlineTimerTolerance = TimeSpan.FromMilliseconds(100);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static FleetOptions Options => new()
    {
        ClientName = "layer-probe-test",
        AutoReconnect = false,
        Deploy = new DeploySettings { AutoDeploy = false },
    };

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task LocalAndRemote_ActualHttpStatusMeansEgressNotBusinessSuccess(HttpStatusCode status)
    {
        await using var target = new TestForwardProxy(statusCode: status);
        await using var host = await HarnessHostFixture.CreateAsync("psta", TestContext.Current.CancellationToken);
        TempTree.AssertFitsMacOsSocketLimit(RemoteHostEndpoint.SocketPath(host.Root));
        TempTree.AssertFitsMacOsSocketLimit(Path.Combine(host.Root, "dsh.sock"));
        await host.ConnectAsync("status-observer", Ct);
        await using var connection = new MachineConnection(MachineSpec.Local(host.Root, "target"), Options);
        await ConnectAsync(connection, Ct);
        var service = new FleetProxyProbe(Options);
        var url = $"http://127.0.0.1:{target.Port}/target";
        var localId = Guid.NewGuid();
        var remoteId = Guid.NewGuid();
        var local = await service.ProbeLocalAsync(host.Root, new OutboundProxySettings { ProbeUrl = url }, localId, Ct);
        var remote = await service.ProbeRemoteAsync(connection, new(ProbeUrl: url), remoteId, Ct);
        Assert.Equal((ProxyProbeLayer.LocalOutbound, ProxyProbeOutcome.Passed, ProxyProbeStage.HttpTarget, (int?)status, 0),
            (local.Layer, local.Outcome, local.Stage, local.HttpStatus, local.ExitCode));
        Assert.Equal((ProxyProbeLayer.RemoteOutbound, ProxyProbeOutcome.Passed, ProxyProbeStage.HttpTarget, (int?)status, 0),
            (remote.Layer, remote.Outcome, remote.Stage, remote.HttpStatus, remote.ExitCode));
        Assert.Equal((MachineSpec.LocalName, host.Root, localId), (local.Machine, local.Home, local.OperationId));
        Assert.Equal(("target", host.Environment.App.Home.Root, remoteId), (remote.Machine, remote.Home, remote.OperationId));
        Assert.NotEqual(local.OperationId, remote.OperationId);
        Assert.Equal(2, target.Requests.Count);
    }

    [Fact(Explicit = true)]
    public async Task LocalAndRemote_Proxy407FailsAuthenticationWithoutMaskingHealthyLayer()
    {
        await using var rejected = new TestForwardProxy(requiredCredential: "expected:credential");
        await using var healthy = new TestForwardProxy();
        await using var host = await HarnessHostFixture.CreateAsync("paut", TestContext.Current.CancellationToken);
        TempTree.AssertFitsMacOsSocketLimit(RemoteHostEndpoint.SocketPath(host.Root));
        TempTree.AssertFitsMacOsSocketLimit(Path.Combine(host.Root, "dsh.sock"));
        await host.ConnectAsync("auth-observer", Ct);
        await using var connection = new MachineConnection(MachineSpec.Local(host.Root, "auth-target"), Options);
        await ConnectAsync(connection, Ct);
        var service = new FleetProxyProbe(Options);
        var local = await service.ProbeLocalAsync(host.Root, Settings(rejected), Guid.NewGuid(), Ct);
        var remote = await service.ProbeRemoteAsync(connection, new(RemoteSettings(rejected)), Guid.NewGuid(), Ct);
        foreach (var result in new[] { local, remote })
        {
            Assert.Equal((ProxyProbeOutcome.Failed, ProxyProbeStage.ProxyAuth, (int?)407, 1),
                (result.Outcome, result.Stage, result.HttpStatus, result.ExitCode));
        }
        var independent = await service.ProbeRemoteAsync(connection, new(RemoteSettings(healthy)), Guid.NewGuid(), Ct);
        Assert.Equal(ProxyProbeOutcome.Passed, independent.Outcome);
        Assert.Equal(ProxyProbeOutcome.Failed, local.Outcome);
        Assert.Equal(407, remote.HttpStatus);
        Assert.Single(healthy.Requests);
    }

    [Fact(Explicit = true)]
    public async Task LocalAndRemote_ConnectionRefusalIsIsolatedAndIdentifiesProxyConnect()
    {
        using var unavailable = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        unavailable.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        await using var healthy = new TestForwardProxy();
        await using var host = await HarnessHostFixture.CreateAsync("pref", TestContext.Current.CancellationToken);
        TempTree.AssertFitsMacOsSocketLimit(RemoteHostEndpoint.SocketPath(host.Root));
        TempTree.AssertFitsMacOsSocketLimit(Path.Combine(host.Root, "dsh.sock"));
        await host.ConnectAsync("refusal-observer", Ct);
        await using var connection = new MachineConnection(MachineSpec.Local(host.Root, "refused"), Options);
        await ConnectAsync(connection, Ct);
        var service = new FleetProxyProbe(Options);
        var bad = new OutboundProxySettings { Url = $"http://{unavailable.LocalEndPoint}", ProbeUrl = PublicTarget };
        var local = await service.ProbeLocalAsync(host.Root, bad, Guid.NewGuid(), Ct);
        var remote = await service.ProbeRemoteAsync(connection, new(new(bad.Url, ProbeUrl: PublicTarget)), Guid.NewGuid(), Ct);
        foreach (var result in new[] { local, remote })
        {
            Assert.Equal((ProxyProbeOutcome.Failed, ProxyProbeStage.ProxyConnect, 1), (result.Outcome, result.Stage, result.ExitCode));
            Assert.Null(result.HttpStatus);
        }
        Assert.Equal(ProxyProbeOutcome.Passed,
            (await service.ProbeLocalAsync(host.Root, Settings(healthy), Guid.NewGuid(), Ct)).Outcome);
        Assert.Equal(MachineConnectionState.Connected, connection.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldHost_MissingCapabilityOrStructuredSchemaIsUnsupported(bool oldSchema)
    {
        await using var proxy = new TestForwardProxy();
        await using var host = await HarnessHostFixture.CreateAsync("pold", TestContext.Current.CancellationToken,
            backend => new LegacyProbeBackend(backend, oldSchema));
        TempTree.AssertFitsMacOsSocketLimit(RemoteHostEndpoint.SocketPath(host.Root));
        TempTree.AssertFitsMacOsSocketLimit(Path.Combine(host.Root, "dsh.sock"));
        await host.ConnectAsync("legacy-observer", Ct);
        await using var connection = new MachineConnection(MachineSpec.Local(host.Root, "old-host"), Options);
        await ConnectAsync(connection, Ct);
        var id = Guid.NewGuid();
        var result = await new FleetProxyProbe(Options).ProbeRemoteAsync(connection, new(RemoteSettings(proxy)), id, Ct);
        Assert.Equal((ProxyProbeOutcome.Unsupported, 1, "old-host", id),
            (result.Outcome, result.ExitCode, result.Machine, result.OperationId));
        Assert.Null(result.HttpStatus);
        Assert.Equal(oldSchema ? 1 : 0, proxy.Requests.Count);
        Assert.True(await (await connection.WaitConnectedAsync(Ct)).Client.PingAsync(Ct) > 0);
    }

    [Fact(Explicit = true)]
    public async Task BaselineFailure_IsNotMaskedByGoodBorrowedRoute_AndNeitherRouteNorFilesChange()
    {
        var root = RealHostProcesses.CreateHome("pbas");
        try
        {
            using var unavailable = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            unavailable.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            await using var borrowed = new TestForwardProxy();
            using var route = new OutboundProxy();
            var baseline = new OutboundProxySettings { Url = $"http://{unavailable.LocalEndPoint}", ProbeUrl = PublicTarget };
            HarnessSettings.SaveOutboundProxy(new HarnessHome(root), baseline, route);
            route.ConfigureBorrowed($"http://127.0.0.1:{borrowed.Port}", async (_, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(IPAddress.Loopback, borrowed.Port, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            });
            var before = await File.ReadAllTextAsync(Path.Combine(root, "settings.yaml"), TestContext.Current.CancellationToken);
            var captured = route.CaptureRoute(new Uri(PublicTarget));
            var options = Options with { LocalProxy = route, LocalHome = new HarnessHome(root) };
            using var consumer = new HttpClient(route.CreateHandler());
            Assert.Equal(TestForwardProxy.ProxiedBody, await consumer.GetStringAsync(PublicTarget, Ct));
            var result = await new FleetProxyProbe(options).ProbeLocalAsync(root, baseline, Guid.NewGuid(), Ct);
            Assert.Equal((ProxyProbeOutcome.Failed, ProxyProbeStage.ProxyConnect), (result.Outcome, result.Stage));
            Assert.Equal(before, await File.ReadAllTextAsync(Path.Combine(root, "settings.yaml"), Ct));
            var after = route.CaptureRoute(new Uri(PublicTarget));
            Assert.Equal((captured.Generation, captured.ProxyUri, captured.CancellationToken),
                (after.Generation, after.ProxyUri, after.CancellationToken));
            Assert.Equal(TestForwardProxy.ProxiedBody, await consumer.GetStringAsync(PublicTarget, Ct));
            Assert.Equal(2, borrowed.Requests.Count);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Fact(Explicit = true)]
    public async Task RemoteDraftProbe_LeavesSavedFilesAndLiveConsumerRouteUnchanged()
    {
        await using var host = await HarnessHostFixture.CreateAsync("pdrf", TestContext.Current.CancellationToken);
        TempTree.AssertFitsMacOsSocketLimit(RemoteHostEndpoint.SocketPath(host.Root));
        TempTree.AssertFitsMacOsSocketLimit(Path.Combine(host.Root, "dsh.sock"));
        await using var saved = new TestForwardProxy();
        await using var draft = new TestForwardProxy();
        var client = await host.ConnectAsync("independent-consumer", Ct);
        var state = await client.Network.GetOutboundAsync(Ct);
        var committed = await client.Network.SetOutboundAsync(new(state.Revision, RemoteSettings(saved)), Ct);
        var path = host.Environment.App.Home.SettingsFile;
        var before = await File.ReadAllTextAsync(path, Ct);
        var route = OutboundProxy.FromContext(host.Environment.App.Ctx);
        var generation = route.CaptureRoute(new Uri(PublicTarget));
        await using var connection = new MachineConnection(MachineSpec.Local(host.Root, "draft"), Options);
        await ConnectAsync(connection, Ct);
        var result = await new FleetProxyProbe(Options).ProbeRemoteAsync(connection, new(RemoteSettings(draft)), Guid.NewGuid(), Ct);
        Assert.Equal(ProxyProbeOutcome.Passed, result.Outcome);
        Assert.Equal(before, await File.ReadAllTextAsync(path, Ct));
        var after = await client.Network.GetOutboundAsync(Ct);
        Assert.Equal((committed.OwnerId, committed.HostHome, committed.Revision, committed.Settings.Url),
            (after.OwnerId, after.HostHome, after.Revision, after.Settings.Url));
        var afterRoute = route.CaptureRoute(new Uri(PublicTarget));
        Assert.Equal((generation.Generation, generation.ProxyUri, generation.CancellationToken),
            (afterRoute.Generation, afterRoute.ProxyUri, afterRoute.CancellationToken));
        using var consumer = new HttpClient(route.CreateHandler());
        Assert.Equal(TestForwardProxy.ProxiedBody, await consumer.GetStringAsync(PublicTarget, Ct));
        Assert.Single(saved.Requests);
        Assert.Single(draft.Requests);
    }

    [Fact(Explicit = true)]
    public async Task SshMissingIdentity_ReportsFailureWithoutPublishingItsPrivatePath()
    {
        FleetSshEndToEndTests.RequireEnvironment();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        var privatePath = Path.Combine(ssh.Root, "synthetic-private-user", "missing_ed25519");
        var machine = ssh.Machine("missing-identity", Path.Combine(ssh.Root, "no-host")) with { KeyPath = privatePath };
        var raw = await new SshTransport(machine).CaptureAsync("true", Ct);
        Assert.NotEqual(0, raw.ExitCode);
        Assert.Contains(privatePath, raw.FailureDetail, StringComparison.Ordinal);
        var result = await new FleetProxyProbe(Options).ProbeSshAsync(machine, Guid.NewGuid(), Ct);
        Assert.Equal((ProxyProbeLayer.Ssh, ProxyProbeOutcome.Failed, ProxyProbeStage.Ssh, 1, machine.Name),
            (result.Layer, result.Outcome, result.Stage, result.ExitCode, result.Machine));
        Assert.DoesNotContain(privatePath, result.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-private-user", result.Detail, StringComparison.Ordinal);
        Assert.False(Directory.Exists(machine.HarnessHome));
    }

    [Fact(Explicit = true)]
    public async Task Ssh_OnlyRunsTrueWithoutHostOrDeployment_AndRejectsForcedNonzeroExit()
    {
        FleetSshEndToEndTests.RequireEnvironment();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        var nonexistentHome = Path.Combine(ssh.Root, "never-created");
        await using var accepting = await ObservedCommandSshServer.StartAsync(ssh, 0, Ct);
        await using var rejecting = await ObservedCommandSshServer.StartAsync(ssh, 23, Ct);
        var machine = ssh.Machine("ssh-only", nonexistentHome) with { Port = accepting.Port, RemotePath = "/not-installed" };
        var service = new FleetProxyProbe(Options);
        var id = Guid.NewGuid();
        var passed = await service.ProbeSshAsync(machine, id, Ct);
        Assert.Equal((ProxyProbeLayer.Ssh, ProxyProbeOutcome.Passed, ProxyProbeStage.Ssh, 0, machine.Name, id),
            (passed.Layer, passed.Outcome, passed.Stage, passed.ExitCode, passed.Machine, passed.OperationId));
        Assert.Equal(nonexistentHome, passed.Home);
        Assert.False(Directory.Exists(nonexistentHome));
        Assert.False(Directory.Exists(Path.Combine(ssh.Home, ".dsharp")));
        Assert.Equal(["true"], await File.ReadAllLinesAsync(accepting.CommandsFile, Ct));
        var rejected = machine with { Port = rejecting.Port };
        var raw = await new SshTransport(rejected).CaptureAsync("true", Ct);
        Assert.False(raw.TransportFailed);
        Assert.Equal(23, raw.ExitCode);
        var failed = await service.ProbeSshAsync(rejected, Guid.NewGuid(), Ct);
        Assert.Equal((ProxyProbeOutcome.Failed, ProxyProbeStage.Ssh, 1), (failed.Outcome, failed.Stage, failed.ExitCode));
        Assert.Equal(ProxyProbeOutcome.Passed, passed.Outcome);
        Assert.False(Directory.Exists(nonexistentHome));
        Assert.Equal(["true", "true"], await File.ReadAllLinesAsync(rejecting.CommandsFile, Ct));
    }

    [Fact(Explicit = true)]
    public async Task ThreeHeldLayers_RunIndependently_RejectSameLayerBusy_AndCancelWithoutClosingOtherConsumer()
    {
        FleetSshEndToEndTests.RequireEnvironment();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        await using var gate = new SshAuthenticationGate();
        await using var localTarget = new TestForwardProxy(pauseResponses: true, observePeerCloseWhilePaused: true);
        await using var remoteTarget = new TestForwardProxy(pauseResponses: true, observePeerCloseWhilePaused: true);
        await using var host = await HarnessHostFixture.CreateAsync("pall", Ct);
        TempTree.AssertFitsMacOsSocketLimit(RemoteHostEndpoint.SocketPath(host.Root));
        TempTree.AssertFitsMacOsSocketLimit(Path.Combine(host.Root, "dsh.sock"));
        await host.ConnectAsync("healthy-second-consumer", Ct);
        await using var connection = new MachineConnection(MachineSpec.Local(host.Root, "parallel-target"), Options);
        var other = (await ConnectAsync(connection, Ct)).Client;
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var service = new FleetProxyProbe(Options);
        var machine = gate.Wrap(ssh.Machine("held-ssh", host.Root));
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var sshPending = service.ProbeSshAsync(machine, ids[0], cancelled.Token);
        await gate.WaitForBlockedAuthenticationAsync(sshPending, Ct);
        var localPending = service.ProbeLocalAsync(host.Root, Settings(localTarget), ids[1], cancelled.Token);
        await localTarget.WaitForRequestAsync(Ct);
        var remotePending = service.ProbeRemoteAsync(connection, new(RemoteSettings(remoteTarget)), ids[2], cancelled.Token);
        await remoteTarget.WaitForRequestAsync(Ct);
        Assert.False(sshPending.IsCompleted);
        Assert.False(localPending.IsCompleted);
        Assert.False(remotePending.IsCompleted);
        var duplicateSsh = await service.ProbeSshAsync(machine, Guid.NewGuid(), Ct);
        var duplicateLocal = await service.ProbeLocalAsync(host.Root, Settings(localTarget), Guid.NewGuid(), Ct);
        var duplicateRemote = await service.ProbeRemoteAsync(connection, new(RemoteSettings(remoteTarget)), Guid.NewGuid(), Ct);
        foreach (var busy in new[] { duplicateSsh, duplicateLocal, duplicateRemote })
            Assert.Equal((ProxyProbeOutcome.Failed, ProxyProbeStage.Configuration, 1), (busy.Outcome, busy.Stage, busy.ExitCode));
        Assert.Single(localTarget.Requests);
        Assert.Single(remoteTarget.Requests);
        using var releaseDeadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        releaseDeadline.CancelAfter(CancellationProofTimeout);
        await cancelled.CancelAsync();
        var results = await Task.WhenAll(sshPending, localPending, remotePending).WaitAsync(releaseDeadline.Token);
        Assert.Equal(ids, results.Select(result => result.OperationId));
        Assert.Equal(new[] { ProxyProbeStage.Ssh, ProxyProbeStage.HttpTarget, ProxyProbeStage.HttpTarget }, results.Select(result => result.Stage));
        Assert.All(results, result => Assert.Equal((ProxyProbeOutcome.Cancelled, 130), (result.Outcome, result.ExitCode)));
        await localTarget.WaitForClosedConnectionAsync(releaseDeadline.Token);
        await localTarget.WaitForClosedConnectionAsync(releaseDeadline.Token);
        await remoteTarget.WaitForClosedConnectionAsync(releaseDeadline.Token);
        await remoteTarget.WaitForClosedConnectionAsync(releaseDeadline.Token);
        Assert.Equal(0, localTarget.ActiveConnections);
        Assert.Equal(0, remoteTarget.ActiveConnections);
        Assert.NotNull(gate.AskPass);
        Assert.False(File.Exists(gate.AskPass));
        Assert.True(await other.PingAsync(Ct) > 0);
        Assert.Equal(MachineConnectionState.Connected, connection.State);
        await using var healthy = new TestForwardProxy();
        Assert.Equal(ProxyProbeOutcome.Passed,
            (await service.ProbeLocalAsync(host.Root, Settings(healthy), Guid.NewGuid(), Ct)).Outcome);
        Assert.Equal(ProxyProbeOutcome.Passed,
            (await service.ProbeRemoteAsync(connection, new(RemoteSettings(healthy)), Guid.NewGuid(), Ct)).Outcome);
        Assert.Equal(ProxyProbeOutcome.Passed,
            (await service.ProbeSshAsync(ssh.Machine("after-cancel", host.Root), Guid.NewGuid(), Ct)).Outcome);
    }

    [Fact(Explicit = true)]
    public async Task PortableLocalAndRemoteCancellation_KeepOtherHostConsumerAlive()
    {
        await using var local = new TestForwardProxy(pauseResponses: true, observePeerCloseWhilePaused: true);
        await using var remote = new TestForwardProxy(pauseResponses: true, observePeerCloseWhilePaused: true);
        await using var host = await HarnessHostFixture.CreateAsync("pcan", TestContext.Current.CancellationToken);
        TempTree.AssertFitsMacOsSocketLimit(RemoteHostEndpoint.SocketPath(host.Root));
        TempTree.AssertFitsMacOsSocketLimit(Path.Combine(host.Root, "dsh.sock"));
        await host.ConnectAsync("survivor", Ct);
        await using var connection = new MachineConnection(MachineSpec.Local(host.Root, "cancel-target"), Options);
        var other = (await ConnectAsync(connection, Ct)).Client;
        var service = new FleetProxyProbe(Options);
        using var localCancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var remoteCancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var localPending = service.ProbeLocalAsync(host.Root, Settings(local), Guid.NewGuid(), localCancellation.Token);
        await local.WaitForRequestAsync(Ct);
        var remotePending = service.ProbeRemoteAsync(connection, new(RemoteSettings(remote)), Guid.NewGuid(), remoteCancellation.Token);
        await remote.WaitForRequestAsync(Ct);
        var busyLocal = await service.ProbeLocalAsync(host.Root, Settings(local), Guid.NewGuid(), Ct);
        var busyRemote = await service.ProbeRemoteAsync(connection, new(RemoteSettings(remote)), Guid.NewGuid(), Ct);
        foreach (var busy in new[] { busyLocal, busyRemote })
            Assert.Equal((ProxyProbeOutcome.Failed, ProxyProbeStage.Configuration, 1), (busy.Outcome, busy.Stage, busy.ExitCode));
        Assert.Single(local.Requests);
        Assert.Single(remote.Requests);
        using var localRelease = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        localRelease.CancelAfter(CancellationProofTimeout);
        await localCancellation.CancelAsync();
        var localResult = await localPending.WaitAsync(localRelease.Token);
        Assert.Equal((ProxyProbeOutcome.Cancelled, ProxyProbeStage.HttpTarget, 130),
            (localResult.Outcome, localResult.Stage, localResult.ExitCode));
        Assert.False(remotePending.IsCompleted);
        Assert.True(await other.PingAsync(Ct) > 0);
        await local.WaitForClosedConnectionAsync(localRelease.Token);
        await local.WaitForClosedConnectionAsync(localRelease.Token);
        using var remoteRelease = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        remoteRelease.CancelAfter(CancellationProofTimeout);
        await remoteCancellation.CancelAsync();
        var remoteResult = await remotePending.WaitAsync(remoteRelease.Token);
        Assert.Equal((ProxyProbeOutcome.Cancelled, ProxyProbeStage.HttpTarget, 130),
            (remoteResult.Outcome, remoteResult.Stage, remoteResult.ExitCode));
        await remote.WaitForClosedConnectionAsync(remoteRelease.Token);
        await remote.WaitForClosedConnectionAsync(remoteRelease.Token);
        Assert.Equal(0, local.ActiveConnections);
        Assert.Equal(0, remote.ActiveConnections);
        Assert.True(await other.PingAsync(Ct) > 0);
        Assert.Equal(MachineConnectionState.Connected, connection.State);
    }

    [Fact(Explicit = true)]
    public async Task RemoteCancellation_WhileBorrowingReportsSshAndDoesNotDisposeSharedConnection()
    {
        var root = RealHostProcesses.CreateHome("pbor");
        try
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var options = Options with
            {
                StartLocalHost = async (_, cancellationToken) =>
                {
                    started.TrySetResult();
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                },
            };
            await using var connection = new MachineConnection(MachineSpec.Local(root, "starting"), options);
            connection.Start();
            await started.Task.WaitAsync(TestContext.Current.CancellationToken);
            using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            var pending = new FleetProxyProbe(options).ProbeRemoteAsync(connection, new(), Guid.NewGuid(), cancelled.Token);
            await cancelled.CancelAsync();
            var result = await pending;
            Assert.Equal((ProxyProbeOutcome.Cancelled, ProxyProbeStage.Ssh, 130), (result.Outcome, result.Stage, result.ExitCode));
            Assert.Equal(MachineConnectionState.Connecting, connection.State);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Theory]
    [InlineData(ProxyProbeLayer.Ssh)]
    [InlineData(ProxyProbeLayer.RemoteOutbound)]
    public async Task StartupDeadline_ReleasesItsOwnedTransportWithoutStoppingTheExistingHost(ProxyProbeLayer layer)
    {
        FleetSshEndToEndTests.RequireEnvironment();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        await using var gate = new SshAuthenticationGate();
        await using var host = await HarnessHostFixture.CreateAsync("pbud", Ct);
        var observer = await host.ConnectAsync("independent-deadline-observer", Ct);
        var target = ssh.Machine("held-deadline", host.Root);
        var machine = gate.Wrap(target with { SshOptions = ["ConnectTimeout=0", .. target.SshOptions ?? []] });
        var budget = layer == ProxyProbeLayer.Ssh ? FleetProxyProbe.SshTimeout : FleetProxyProbe.RemoteTimeout;
        var service = new FleetProxyProbe(Options);
        await using (var connection = layer == ProxyProbeLayer.RemoteOutbound ? new MachineConnection(machine, Options) : null)
        {
            connection?.Start();
            var elapsed = Stopwatch.StartNew();
            var pending = connection is null
                ? service.ProbeSshAsync(machine, Guid.NewGuid(), Ct)
                : service.ProbeRemoteAsync(connection, new(null), Guid.NewGuid(), Ct);
            await gate.WaitForBlockedAuthenticationAsync(pending, Ct);
            Assert.NotNull(gate.AskPass);
            Assert.True(File.Exists(gate.AskPass), "The blocked authentication must own its askpass file before the deadline.");
            var result = await pending.WaitAsync(budget + CancellationProofTimeout, Ct);
            Assert.Equal((layer, ProxyProbeOutcome.Failed, ProxyProbeStage.Ssh, 1), (result.Layer, result.Outcome, result.Stage, result.ExitCode));
            Assert.True(elapsed.Elapsed >= budget - DeadlineTimerTolerance, "The held transport must not fail before the production layer deadline.");
        }
        using var released = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        released.CancelAfter(CancellationProofTimeout);
        await gate.WaitForClosedAsync(released.Token);
        Assert.False(File.Exists(gate.AskPass));
        Assert.True(await observer.PingAsync(Ct) > 0);
    }

    [Fact(Explicit = true)]
    public async Task LocalTransportDeadline_IsFailedNotUserCancellation()
    {
        await using var target = new TestForwardProxy(pauseResponses: true, observePeerCloseWhilePaused: true);
        var root = RealHostProcesses.CreateHome("pdl");
        try
        {
            var pending = new FleetProxyProbe(Options).ProbeLocalAsync(root, Settings(target), Guid.NewGuid(), TestContext.Current.CancellationToken);
            await target.WaitForRequestAsync(Ct);
            var result = await pending.WaitAsync(FleetProxyProbe.LocalTimeout + CancellationProofTimeout, Ct);
            Assert.Equal((ProxyProbeOutcome.Failed, ProxyProbeStage.HttpTarget, 1), (result.Outcome, result.Stage, result.ExitCode));
            await target.WaitForClosedConnectionAsync(Ct);
            await target.WaitForClosedConnectionAsync(Ct);
            Assert.Equal(0, target.ActiveConnections);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Fact(Explicit = true)]
    public async Task InvalidConfiguration_RedactsSecretsInResultAndJson()
    {
        var root = RealHostProcesses.CreateHome("pred");
        try
        {
            var user = $"probe-user-{Guid.NewGuid():N}";
            var password = $"synthetic:{Guid.NewGuid():N}/?@";
            var settings = new OutboundProxySettings { Url = $"ftp://{user}:{Uri.EscapeDataString(password)}@example.invalid", ProbeUrl = PublicTarget };
            var result = await new FleetProxyProbe(Options).ProbeLocalAsync(root, settings, Guid.NewGuid(), TestContext.Current.CancellationToken);
            Assert.Equal((ProxyProbeOutcome.Failed, ProxyProbeStage.Configuration, 1), (result.Outcome, result.Stage, result.ExitCode));
            var json = JsonSerializer.Serialize(result, FleetProxyProbeJsonContext.Default.ProxyProbeResult);
            Assert.DoesNotContain(user, result.Detail + json);
            Assert.DoesNotContain(password, result.Detail + json);
            Assert.DoesNotContain(Uri.EscapeDataString(password), result.Detail + json);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    private static async Task<HostLease> ConnectAsync(MachineConnection connection, CancellationToken cancellationToken)
    {
        connection.Start();
        await connection.WaitSettledAsync(cancellationToken);
        Assert.True(connection.State == MachineConnectionState.Connected, connection.Detail);
        return await connection.WaitConnectedAsync(cancellationToken);
    }

    private static OutboundProxySettings Settings(TestForwardProxy proxy)
        => new() { Url = $"http://127.0.0.1:{proxy.Port}", ProbeUrl = PublicTarget };

    private static RemoteOutboundProxySettings RemoteSettings(TestForwardProxy proxy)
        => new($"http://127.0.0.1:{proxy.Port}", ProbeUrl: PublicTarget);

    // This server reuses the existing real SSH fixture's keys and configuration while observing and forcing actual remote command exits.
    private sealed class ObservedCommandSshServer(Process process, int port, string commandsFile) : IAsyncDisposable
    {
        private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(5);
        public int Port { get; } = port;
        public string CommandsFile { get; } = commandsFile;

        public static async Task<ObservedCommandSshServer> StartAsync(TestSshServer source, int exitCode, CancellationToken cancellationToken)
        {
            int port;
            using (var listener = new TcpListener(IPAddress.Loopback, 0))
            {
                listener.Start();
                port = ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            var commands = Path.Combine(source.Root, $"commands-{Guid.NewGuid():N}");
            var forced = $"printf '%s\\n' \"$SSH_ORIGINAL_COMMAND\" >> {RemoteShell.Quote(commands)}; exit {exitCode}";
            var start = new ProcessStartInfo("/usr/sbin/sshd",
                ["-D", "-e", "-f", Path.Combine(source.Root, "sshd_config"), "-p", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "-o", $"PidFile={commands}.pid", "-o", $"ForceCommand={forced}"])
            {
                UseShellExecute = false,
                RedirectStandardError = true,
            };
            var child = TestProcessScope.Start(start) ?? throw new InvalidOperationException("Could not start the owned forced-command SSH server.");
            var fixture = new ObservedCommandSshServer(child, port, commands);
            var listening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            child.ErrorDataReceived += (_, args) =>
            {
                if (args.Data is { } line && line.StartsWith("Server listening on ", StringComparison.Ordinal))
                    listening.TrySetResult();
                else if (args.Data is null)
                    listening.TrySetException(new IOException("The owned forced-command SSH server exited before listening."));
            };
            child.BeginErrorReadLine();
            try
            {
                await listening.Task.WaitAsync(StartupTimeout, cancellationToken);
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            process.Dispose();
        }
    }

    // This wire-compatibility decorator removes only optional capability/schema fields from the real backend.
    internal sealed class LegacyProbeBackend(IRemoteHostBackend backend, bool oldSchema) : IRemoteHostBackend, IRemoteNetworkBackend
    {
        public HostPendingDecisions Pending => backend.Pending;
        public HostSessionRegistry Sessions => backend.Sessions;
        public IRemoteSessionBackend Session => backend.Session;
        public Task<HostInfo> InfoAsync(CancellationToken cancellationToken) => backend.InfoAsync(cancellationToken);
        public IRemoteAgentBackend? Agent => backend.Agent;
        public IRemotePtyBackend? Pty => backend.Pty;
        public HostFileSystem? Files => backend.Files;
        public HostGitService? Git => backend.Git;
        public HostNotificationCenter? Notifications => backend.Notifications;
        public IRemoteSessionEditingBackend? SessionEditing => backend.SessionEditing;
        public IRemotePluginsBackend? Plugins => backend.Plugins;
        public IRemoteNetworkBackend? Network => oldSchema ? this : null;
        public Task<RemoteOutboundProxyState> GetOutboundAsync(CancellationToken ct) => backend.Network!.GetOutboundAsync(ct);
        public Task<RemoteOutboundProxyState> SetOutboundAsync(RemoteOutboundProxySetRequest request, CancellationToken ct)
            => backend.Network!.SetOutboundAsync(request, ct);
        public async Task<RemoteOutboundProxyProbeResult> ProbeOutboundAsync(RemoteOutboundProxyProbeRequest request, CancellationToken ct)
        {
            var actual = await backend.Network!.ProbeOutboundAsync(request, ct);
            return actual with { Stage = null, HttpStatus = null, HostHome = null };
        }
    }
}

[Collection(SerialProcessCollection.Name)]
public sealed class FleetProxyProbeCliTests
{
    private const int SigInt = 2;
    private const string PublicTarget = "http://1.1.1.1/cli-layer-probe";
    private static readonly TimeSpan ProcessExitTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CancellationProofTimeout = TimeSpan.FromSeconds(5);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Explicit = true)]
    public async Task S12_ActualCli_SelectsOnlyRequestedLayer_AndPreservesOldDoctorContract()
    {
        var root = RealHostProcesses.CreateHome("pcli");
        try
        {
            await using var target = new TestForwardProxy(statusCode: HttpStatusCode.NotFound);
            await using var host = await HarnessHostFixture.CreateAsync("pct", TestContext.Current.CancellationToken);
            TempTree.AssertFitsMacOsSocketLimit(RemoteHostEndpoint.SocketPath(host.Root));
            TempTree.AssertFitsMacOsSocketLimit(Path.Combine(host.Root, "dsh.sock"));
            var home = new HarnessHome(root);
            new MachineStore(home).Upsert(MachineSpec.Local(host.Root, "target"));
            using var baseline = new OutboundProxy();
            HarnessSettings.SaveOutboundProxy(home, new OutboundProxySettings { ProbeUrl = $"http://127.0.0.1:{target.Port}/local" }, baseline);
            var observer = await host.ConnectAsync("cli-observer", Ct);
            var state = await observer.Network.GetOutboundAsync(Ct);
            await observer.Network.SetOutboundAsync(new(state.Revision, new(ProbeUrl: $"http://127.0.0.1:{target.Port}/remote")), Ct);
            var before = await File.ReadAllTextAsync(home.SettingsFile, Ct);
            var remoteBefore = await File.ReadAllTextAsync(host.Environment.App.Home.SettingsFile, Ct);
            var localCli = await FleetCliProcess.RunAsync(RealHostProcesses.Executable, root,
                "doctor", "--proxy-layer", "local_outbound", "--machine", "target");
            var local = ParseSingle(localCli);
            Assert.Equal((ProxyProbeLayer.LocalOutbound, ProxyProbeOutcome.Passed, ProxyProbeStage.HttpTarget, (int?)404, root),
                (local.Layer, local.Outcome, local.Stage, local.HttpStatus, local.Home));
            var remoteCli = await FleetCliProcess.RunAsync(RealHostProcesses.Executable, root,
                "doctor", "--proxy-layer", "remote_outbound", "--machine", "target");
            var remote = ParseSingle(remoteCli);
            Assert.Equal((ProxyProbeLayer.RemoteOutbound, ProxyProbeOutcome.Passed, ProxyProbeStage.HttpTarget, (int?)404, "target", host.Environment.App.Home.Root),
                (remote.Layer, remote.Outcome, remote.Stage, remote.HttpStatus, remote.Machine, remote.Home));
            Assert.NotEqual(Guid.Empty, local.OperationId);
            Assert.NotEqual(local.OperationId, remote.OperationId);
            Assert.Equal(0, localCli.ExitCode);
            Assert.Equal(0, remoteCli.ExitCode);
            var unselectedLocal = ParseSingle(await FleetCliProcess.RunAsync(RealHostProcesses.Executable, root,
                "doctor", "--proxy-layer", "local_outbound"));
            Assert.Equal((ProxyProbeLayer.LocalOutbound, ProxyProbeOutcome.Passed, root),
                (unselectedLocal.Layer, unselectedLocal.Outcome, unselectedLocal.Home));
            Assert.Equal(3, target.Requests.Count);
            Assert.Contains("GET /local HTTP/1.1", target.Requests);
            Assert.Contains("GET /remote HTTP/1.1", target.Requests);
            var unsupported = ParseSingle(await FleetCliProcess.RunAsync(RealHostProcesses.Executable, root,
                "doctor", "--proxy-layer", "ssh", "--machine", "target"));
            Assert.Equal((ProxyProbeLayer.Ssh, ProxyProbeOutcome.Unsupported, 1), (unsupported.Layer, unsupported.Outcome, unsupported.ExitCode));
            var oldDoctor = await FleetCliProcess.RunAsync(RealHostProcesses.Executable, root, "doctor", "--machine", "target");
            Assert.Equal(0, oldDoctor.ExitCode);
            Assert.Equal(before, await File.ReadAllTextAsync(home.SettingsFile, Ct));
            Assert.Equal(remoteBefore, await File.ReadAllTextAsync(host.Environment.App.Home.SettingsFile, Ct));
            Assert.True(await observer.PingAsync(Ct) > 0);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Fact(Explicit = true)]
    public async Task S12_ActualCli_SshSucceedsWithoutRemoteHostOrInstalledDsharp()
    {
        var executable = FleetSshEndToEndTests.RequireEnvironment();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        var root = RealHostProcesses.CreateHome("pcss");
        try
        {
            var missing = Path.Combine(ssh.Root, "no-host");
            new MachineStore(new HarnessHome(root)).Upsert(ssh.Machine("ssh-target", missing) with { RemotePath = "/not-installed" });
            var cli = await FleetCliProcess.RunAsync(executable, root, "doctor", "--proxy-layer", "ssh", "--machine", "ssh-target");
            var result = ParseSingle(cli);
            Assert.Equal((0, ProxyProbeLayer.Ssh, ProxyProbeOutcome.Passed, ProxyProbeStage.Ssh, "ssh-target"),
                (cli.ExitCode, result.Layer, result.Outcome, result.Stage, result.Machine));
            Assert.False(Directory.Exists(missing));
            Assert.False(Directory.Exists(Path.Combine(ssh.Home, ".dsharp")));
            var all = await FleetCliProcess.RunAsync(executable, root, "doctor", "--proxy-layer", "ssh");
            var selected = all.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => JsonSerializer.Deserialize(line, FleetProxyProbeJsonContext.Default.ProxyProbeResult)!).ToArray();
            Assert.Equal(2, selected.Length);
            Assert.All(selected, probe => Assert.Equal(ProxyProbeLayer.Ssh, probe.Layer));
            Assert.Contains(selected, probe => probe.Machine == "ssh-target" && probe.Outcome == ProxyProbeOutcome.Passed);
            Assert.Contains(selected, probe => probe.Machine == MachineSpec.LocalName && probe.Outcome == ProxyProbeOutcome.Unsupported);
            Assert.Equal(1, all.ExitCode);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Theory]
    [InlineData("local_outbound")]
    [InlineData("remote_outbound")]
    public async Task S15_ActualCli_SigIntAfterHttpRequest_ReturnsCancelled130_AndOtherConsumerSurvives(string layer)
    {
        RequireOwnedPosixSignal();
        var root = RealHostProcesses.CreateHome("pcan");
        try
        {
            await using var held = new TestForwardProxy(pauseResponses: true, observePeerCloseWhilePaused: true);
            await using var healthy = new TestForwardProxy();
            await using var host = await HarnessHostFixture.CreateAsync("pcah", TestContext.Current.CancellationToken);
            TempTree.AssertFitsMacOsSocketLimit(RemoteHostEndpoint.SocketPath(host.Root));
            TempTree.AssertFitsMacOsSocketLimit(Path.Combine(host.Root, "dsh.sock"));
            var observer = await host.ConnectAsync("surviving-cli-consumer", Ct);
            var home = new HarnessHome(root);
            new MachineStore(home).Upsert(MachineSpec.Local(host.Root, "target"));
            using var baseline = new OutboundProxy();
            HarnessSettings.SaveOutboundProxy(home, new OutboundProxySettings
                { Url = $"http://127.0.0.1:{held.Port}", ProbeUrl = PublicTarget }, baseline);
            var initial = await observer.Network.GetOutboundAsync(Ct);
            await observer.Network.SetOutboundAsync(new(initial.Revision, new($"http://127.0.0.1:{held.Port}", ProbeUrl: PublicTarget)), Ct);
            var before = await File.ReadAllTextAsync(home.SettingsFile, Ct);
            var remoteBefore = await File.ReadAllTextAsync(host.Environment.App.Home.SettingsFile, Ct);
            using var releaseDeadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            await using (var cli = new OwnedProbeCli(root, "doctor", "--proxy-layer", layer, "--machine", "target"))
            {
                await held.WaitForRequestAsync(Ct);
                releaseDeadline.CancelAfter(CancellationProofTimeout);
                var result = ParseSingle(await cli.InterruptAsync(releaseDeadline.Token));
                Assert.Equal((ProxyProbeOutcome.Cancelled, ProxyProbeStage.HttpTarget, 130), (result.Outcome, result.Stage, result.ExitCode));
                Assert.Equal(layer == "local_outbound" ? ProxyProbeLayer.LocalOutbound : ProxyProbeLayer.RemoteOutbound, result.Layer);
                Assert.Equal(layer == "local_outbound" ? root : host.Environment.App.Home.Root, result.Home);
                Assert.NotEqual(Guid.Empty, result.OperationId);
            }
            await held.WaitForClosedConnectionAsync(releaseDeadline.Token);
            await held.WaitForClosedConnectionAsync(releaseDeadline.Token);
            Assert.Equal(0, held.ActiveConnections);
            Assert.True(await observer.PingAsync(Ct) > 0);
            Assert.Equal(before, await File.ReadAllTextAsync(home.SettingsFile, Ct));
            Assert.Equal(remoteBefore, await File.ReadAllTextAsync(host.Environment.App.Home.SettingsFile, Ct));
            var current = await observer.Network.GetOutboundAsync(Ct);
            var followup = await observer.Network.ProbeOutboundAsync(new(new($"http://127.0.0.1:{healthy.Port}", ProbeUrl: PublicTarget)), Ct);
            Assert.True(followup.Reachable);
            var after = await observer.Network.GetOutboundAsync(Ct);
            Assert.Equal((current.OwnerId, current.HostHome, current.Revision, current.Settings.Url),
                (after.OwnerId, after.HostHome, after.Revision, after.Settings.Url));
            Assert.Single(healthy.Requests);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Fact(Explicit = true)]
    public async Task S15_ActualCli_SigIntAfterSshAuthenticationEvent_ReturnsCancelled130_AndReleasesOwnedAuthentication()
    {
        RequireOwnedPosixSignal();
        FleetSshEndToEndTests.RequireEnvironment();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        await using var gate = new SshAuthenticationGate();
        var root = RealHostProcesses.CreateHome("pcsg");
        try
        {
            var machine = gate.Wrap(ssh.Machine("held-ssh", Path.Combine(ssh.Root, "no-host")));
            var pidFile = Path.Combine(root, "probe-ssh.pid");
            machine = machine with
            {
                Proxy = $"/bin/bash -c {RemoteShell.Quote($"printf '%%s\\n' \"$PPID\" > {RemoteShell.Quote(pidFile)}; exec {machine.Proxy}")}",
            };
            await using var survivor = new OwnedSshEcho(ssh.Machine("surviving-consumer", root));
            await survivor.ExchangeAsync("before-probe\n", Ct);
            new MachineStore(new HarnessHome(root)).Upsert(machine);
            await using (var cli = new OwnedProbeCli(root, "doctor", "--proxy-layer", "ssh", "--machine", machine.Name))
            {
                await gate.WaitForBlockedAuthenticationAsync(cli.Exited, Ct);
                using var probeSsh = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(pidFile, Ct), System.Globalization.CultureInfo.InvariantCulture));
                try
                {
                    var sshExited = probeSsh.WaitForExitAsync(Ct);
                    var result = ParseSingle(await cli.InterruptAsync(Ct));
                    Assert.Equal((ProxyProbeLayer.Ssh, ProxyProbeOutcome.Cancelled, ProxyProbeStage.Ssh, 130, machine.Name),
                        (result.Layer, result.Outcome, result.Stage, result.ExitCode, result.Machine));
                    Assert.NotNull(gate.AskPass);
                    Assert.False(File.Exists(gate.AskPass));
                    await sshExited.WaitAsync(ProcessExitTimeout, Ct);
                }
                finally
                {
                    if (!probeSsh.HasExited)
                        probeSsh.Kill(entireProcessTree: true);
                    using var cleanup = new CancellationTokenSource(ProcessExitTimeout);
                    await probeSsh.WaitForExitAsync(cleanup.Token);
                }
            }
            await survivor.ExchangeAsync("after-probe\n", Ct);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Theory]
    [InlineData("local_outbound", false, ProxyProbeStage.ProxyConnect, null)]
    [InlineData("remote_outbound", false, ProxyProbeStage.ProxyConnect, null)]
    [InlineData("local_outbound", true, ProxyProbeStage.ProxyAuth, 407)]
    [InlineData("remote_outbound", true, ProxyProbeStage.ProxyAuth, 407)]
    public async Task S12_ActualCli_FailureDoesNotMaskTheHealthyLayer(string layer, bool authenticationFailure,
        ProxyProbeStage expectedStage, int? expectedStatus)
    {
        var cancellation = TestContext.Current.CancellationToken;
        var root = RealHostProcesses.CreateHome("pcfl");
        try
        {
            using var unavailable = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            unavailable.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            // This controlled HTTP proxy challenges missing credentials; it is not a public-service business smoke.
            await using var rejected = authenticationFailure ? new TestForwardProxy(requiredCredential: "controlled:credential") : null;
            await using var healthy = new TestForwardProxy();
            await using var host = await HarnessHostFixture.CreateAsync("pcfh", cancellation);
            TempTree.AssertFitsMacOsSocketLimit(RemoteHostEndpoint.SocketPath(host.Root));
            TempTree.AssertFitsMacOsSocketLimit(Path.Combine(host.Root, "dsh.sock"));
            var observer = await host.ConnectAsync("failure-observer", cancellation);
            var home = new HarnessHome(root);
            new MachineStore(home).Upsert(MachineSpec.Local(host.Root, "target"));
            var bad = new OutboundProxySettings
            {
                Url = authenticationFailure ? $"http://127.0.0.1:{rejected!.Port}" : $"http://{unavailable.LocalEndPoint}",
                ProbeUrl = PublicTarget,
            };
            var good = new OutboundProxySettings { Url = $"http://127.0.0.1:{healthy.Port}", ProbeUrl = PublicTarget };
            using var baseline = new OutboundProxy();
            HarnessSettings.SaveOutboundProxy(home, layer == "local_outbound" ? bad : good, baseline);
            var state = await observer.Network.GetOutboundAsync(cancellation);
            var remoteSettings = layer == "remote_outbound" ? bad : good;
            await observer.Network.SetOutboundAsync(new(state.Revision, new(remoteSettings.Url, ProbeUrl: PublicTarget)), cancellation);
            var before = await File.ReadAllTextAsync(home.SettingsFile, cancellation);
            var remoteBefore = await File.ReadAllTextAsync(host.Environment.App.Home.SettingsFile, cancellation);
            var failed = ParseSingle(await FleetCliProcess.RunAsync(RealHostProcesses.Executable, root,
                "doctor", "--proxy-layer", layer, "--machine", "target"));
            Assert.Equal((ProxyProbeOutcome.Failed, expectedStage, expectedStatus, 1),
                (failed.Outcome, failed.Stage, failed.HttpStatus, failed.ExitCode));
            Assert.Equal(layer == "local_outbound" ? ProxyProbeLayer.LocalOutbound : ProxyProbeLayer.RemoteOutbound, failed.Layer);
            var healthyLayer = layer == "local_outbound" ? "remote_outbound" : "local_outbound";
            var passed = ParseSingle(await FleetCliProcess.RunAsync(RealHostProcesses.Executable, root,
                "doctor", "--proxy-layer", healthyLayer, "--machine", "target"));
            Assert.Equal((ProxyProbeOutcome.Passed, ProxyProbeStage.HttpTarget, (int?)200, 0),
                (passed.Outcome, passed.Stage, passed.HttpStatus, passed.ExitCode));
            Assert.Single(healthy.Requests);
            if (rejected is not null)
                Assert.Equal(1, rejected.AuthenticationChallenges);
            Assert.Equal(before, await File.ReadAllTextAsync(home.SettingsFile, cancellation));
            Assert.Equal(remoteBefore, await File.ReadAllTextAsync(host.Environment.App.Home.SettingsFile, cancellation));
            Assert.True(await observer.PingAsync(cancellation) > 0);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Fact(Explicit = true)]
    public async Task S12_ActualCli_RejectsUnavailableSshWithoutAHostRequirement()
    {
        FleetSshEndToEndTests.RequireEnvironment();
        var cancellation = TestContext.Current.CancellationToken;
        var root = RealHostProcesses.CreateHome("pcsf");
        try
        {
            using var unavailable = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            unavailable.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var machine = MachineSpec.Ssh("rejected-ssh", IPAddress.Loopback.ToString(),
                ((IPEndPoint)unavailable.LocalEndPoint!).Port, Environment.UserName, SshAuth.Agent);
            var home = new HarnessHome(root);
            new MachineStore(home).Upsert(machine);
            var before = await File.ReadAllTextAsync(home.SettingsFile, cancellation);
            var result = ParseSingle(await FleetCliProcess.RunAsync(RealHostProcesses.Executable, root,
                "doctor", "--proxy-layer", "ssh", "--machine", machine.Name));
            Assert.Equal((ProxyProbeLayer.Ssh, ProxyProbeOutcome.Failed, ProxyProbeStage.Ssh, 1),
                (result.Layer, result.Outcome, result.Stage, result.ExitCode));
            Assert.Equal(before, await File.ReadAllTextAsync(home.SettingsFile, cancellation));
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    private static ProxyProbeResult ParseSingle(FleetCliProcess.Result cli)
    {
        TestContext.Current.TestOutputHelper?.WriteLine($"doctor exit={cli.ExitCode}: {cli.Output}");
        var lines = cli.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var line = Assert.Single(lines);
        using var document = JsonDocument.Parse(line);
        var fields = document.RootElement;
        Assert.Contains(fields.GetProperty("Layer").GetString(), new[] { "ssh", "local_outbound", "remote_outbound" });
        Assert.Contains(fields.GetProperty("Outcome").GetString(), new[] { "Passed", "Failed", "Cancelled", "Unsupported" });
        Assert.Contains(fields.GetProperty("Stage").GetString(), new[] { "configuration", "ssh", "proxy_connect", "proxy_auth", "http_target", "tunnel" });
        Assert.Equal(cli.ExitCode, fields.GetProperty("ExitCode").GetInt32());
        var result = JsonSerializer.Deserialize(line, FleetProxyProbeJsonContext.Default.ProxyProbeResult);
        Assert.NotNull(result);
        Assert.Equal(result.ExitCode, cli.ExitCode);
        return result;
    }

    private static void RequireOwnedPosixSignal()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            Assert.Skip("Owned-PID SIGINT evidence requires Linux or macOS; Windows must not inject foreground console input.");
    }

    // This independent authenticated SSH consumer exchanges bytes across cancellation and reaps its owned child before cleanup.
    private sealed class OwnedSshEcho : IAsyncDisposable
    {
        private readonly SshCommandChannel _channel;

        public OwnedSshEcho(MachineSpec machine) => _channel = new SshTransport(machine).OpenCommand("cat");

        public async Task ExchangeAsync(string text, CancellationToken cancellationToken)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            await _channel.Stream.WriteAsync(bytes, cancellationToken);
            await _channel.Stream.FlushAsync(cancellationToken);
            var received = new byte[bytes.Length];
            await _channel.Stream.ReadExactlyAsync(received, cancellationToken);
            Assert.Equal(bytes, received);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!_channel.Process.HasExited)
                    _channel.Process.Kill(entireProcessTree: true);
                await _channel.Process.WaitForExitAsync();
            }
            finally
            {
                _channel.Dispose();
            }
        }
    }

    // This lifetime owns one real CLI PID, its output pumps, and its cancellation signal without using the test token to kill it.
    private sealed class OwnedProbeCli : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _output;
        private readonly Task<string> _error;

        public OwnedProbeCli(string home, params string[] arguments)
        {
            var start = new ProcessStartInfo(RealHostProcesses.Executable, arguments)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = home,
            };
            start.Environment[HarnessHome.HomeEnv] = home;
            _process = TestProcessScope.Start(start) ?? throw new InvalidOperationException("Could not start the owned probe CLI.");
            _process.StandardInput.Close();
            _output = _process.StandardOutput.ReadToEndAsync();
            _error = _process.StandardError.ReadToEndAsync();
            Exited = _process.WaitForExitAsync();
        }

        public Task Exited { get; }

        public async Task<FleetCliProcess.Result> InterruptAsync(CancellationToken cancellationToken)
        {
            Assert.False(_process.HasExited, "The CLI must still be executing its selected layer at the observed fixture event.");
            Assert.Equal(0, kill(_process.Id, SigInt));
            await Exited.WaitAsync(ProcessExitTimeout, cancellationToken);
            return new FleetCliProcess.Result(_process.ExitCode, await _output, await _error);
        }

        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
            await Exited;
            await Task.WhenAll(_output, _error);
            _process.Dispose();
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int processId, int signal);
}

