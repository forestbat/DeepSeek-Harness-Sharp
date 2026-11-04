using System.Net;
using System.Net.Sockets;
using Dsh.Boot;
using Dsh.Fleet;
using Dsh.Gui.Services;
using Dsh.Gui.ViewModels;
using Dsh.RemoteHost;
using Dsh.Runtime;

namespace Dsh.Tests;

[Collection(SerialProcessCollection.Name)]
public sealed class NetworkSettingsGuiTests : IDisposable
{
    private const int TestTimeout = 5000;
    private const string ProbeTarget = "http://1.1.1.1/gui-component-probe";
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(5);
    private readonly string _root = TempTree.CreateDirectory("network-gui");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static FleetOptions Options => new()
    {
        ClientName = "network-gui-component",
        AutoReconnect = false,
        Deploy = new DeploySettings { AutoDeploy = false },
    };

    public void Dispose() => TempTree.Delete(_root);

    [Fact(Timeout = TestTimeout)]
    public async Task RemoteReadSaveClear_IsIndependentOfMachineSave_AndAliasesShareOnlyTheirHost()
    {
        await using var alpha = await CreateHostAsync("ngra", TestContext.Current.CancellationToken);
        await using var beta = await CreateHostAsync("ngrb", Ct);
        var observer = await alpha.ConnectAsync("independent-alpha", Ct);
        var other = await beta.ConnectAsync("independent-beta", Ct);
        var original = await observer.Network.GetOutboundAsync(Ct);
        var betaOriginal = await other.Network.GetOutboundAsync(Ct);
        var specs = new[] { MachineSpec.Local(alpha.Root, "alpha"), MachineSpec.Local(alpha.Root, "alias"), MachineSpec.Local(beta.Root, "beta") };
        var store = Store(specs);
        await using var fleet = await ConnectFleetAsync(specs);
        var vm = await MachineViewModelAsync(store, fleet, "alpha");
        try
        {
            await vm.ReadRemoteOutboundCommand.ExecuteAsync(null);
            Assert.Equal((original.OwnerId, original.HostHome, original.Revision),
                (vm.RemoteOutboundState!.OwnerId, vm.RemoteOutboundState.HostHome, vm.RemoteOutboundState.Revision));
            vm.RemoteOutboundUrl = "http://proxy.example.invalid:3128";
            vm.RemoteOutboundProbeUrl = ProbeTarget;
            var hostFile = await File.ReadAllTextAsync(alpha.Environment.App.Home.SettingsFile, Ct);
            await vm.SaveCommand.ExecuteAsync(null);
            await vm.WaitForNetworkOperationsAsync().WaitAsync(CompletionTimeout, Ct);
            Assert.Equal(hostFile, await File.ReadAllTextAsync(alpha.Environment.App.Home.SettingsFile, Ct));
            await vm.ReadRemoteOutboundCommand.ExecuteAsync(null);
            vm.RemoteOutboundUrl = "http://proxy.example.invalid:3128";
            vm.RemoteOutboundProbeUrl = ProbeTarget;
            await vm.SaveRemoteOutboundCommand.ExecuteAsync(null);
            var saved = await observer.Network.GetOutboundAsync(Ct);
            Assert.Equal("http://proxy.example.invalid:3128", saved.Settings.Url);
            Assert.NotEqual(original.Revision, saved.Revision);
            Assert.Equal(saved.Settings.Url, HarnessSettings.Load(alpha.Environment.App.Home).Network!.OutboundProxy!.Url);
            Assert.Null(HarnessSettings.Load(new HarnessHome(_root)).Network?.OutboundProxy);
            vm.Selected = vm.Machines.Single(row => row.Name == "alias");
            await vm.WaitForNetworkOperationsAsync().WaitAsync(CompletionTimeout, Ct);
            await vm.ReadRemoteOutboundCommand.ExecuteAsync(null);
            Assert.Equal((saved.OwnerId, saved.HostHome, saved.Revision),
                (vm.RemoteOutboundState!.OwnerId, vm.RemoteOutboundState.HostHome, vm.RemoteOutboundState.Revision));
            await vm.ClearRemoteOutboundCommand.ExecuteAsync(null);
            Assert.Null(HarnessSettings.Load(alpha.Environment.App.Home).Network?.OutboundProxy);
            Assert.Null((await observer.Network.GetOutboundAsync(Ct)).Settings.Url);
            AssertState(betaOriginal, await other.Network.GetOutboundAsync(Ct));
            Assert.True(await observer.PingAsync(Ct) > 0);
            Assert.True(await other.PingAsync(Ct) > 0);
        }
        finally { await CloseAsync(vm); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task StaleRevision_RejectsSaveAndPreservesDraftUntilExplicitRead()
    {
        await using var host = await CreateHostAsync("ngrv", TestContext.Current.CancellationToken);
        var observer = await host.ConnectAsync("revision-writer", Ct);
        var specs = new[] { MachineSpec.Local(host.Root, "alpha") };
        await using var fleet = await ConnectFleetAsync(specs);
        var vm = await MachineViewModelAsync(Store(specs), fleet, "alpha");
        try
        {
            await vm.ReadRemoteOutboundCommand.ExecuteAsync(null);
            var revision = vm.RemoteOutboundState!.Revision;
            vm.RemoteOutboundUrl = "http://draft.example.invalid:3128";
            vm.RemoteOutboundProbeUrl = ProbeTarget;
            var committed = await observer.Network.SetOutboundAsync(new(revision,
                new RemoteOutboundProxySettings("http://other.example.invalid:8080", ProbeUrl: ProbeTarget)), Ct);
            await vm.SaveRemoteOutboundCommand.ExecuteAsync(null);
            Assert.True(vm.RemoteOutboundRequiresRefresh);
            Assert.Equal("http://draft.example.invalid:3128", vm.RemoteOutboundUrl);
            Assert.Equal(ProbeTarget, vm.RemoteOutboundProbeUrl);
            Assert.Equal(revision, vm.RemoteOutboundState.Revision);
            AssertState(committed, await observer.Network.GetOutboundAsync(Ct));
            await vm.ReadRemoteOutboundCommand.ExecuteAsync(null);
            Assert.False(vm.RemoteOutboundRequiresRefresh);
            Assert.Equal(committed.Revision, vm.RemoteOutboundState.Revision);
            Assert.Equal(committed.Settings.Url, vm.RemoteOutboundUrl);
        }
        finally { await CloseAsync(vm); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task FailedRead_DoesNotReplaceKnownStateOrDraftWithEmptyDefaults()
    {
        await using var host = await CreateHostAsync("ngrf", TestContext.Current.CancellationToken);
        var observer = await host.ConnectAsync("read-observer", Ct);
        var initial = await observer.Network.GetOutboundAsync(Ct);
        await observer.Network.SetOutboundAsync(new(initial.Revision, new("http://saved.example.invalid:8080")), Ct);
        var specs = new[] { MachineSpec.Local(host.Root, "alpha") };
        await using var fleet = await ConnectFleetAsync(specs);
        var vm = await MachineViewModelAsync(Store(specs), fleet, "alpha");
        try
        {
            await vm.ReadRemoteOutboundCommand.ExecuteAsync(null);
            var known = vm.RemoteOutboundState;
            vm.RemoteOutboundUrl = "http://unsaved.example.invalid:3128";
            vm.AttachFleet(null);
            await vm.ReadRemoteOutboundCommand.ExecuteAsync(null);
            Assert.Equal(known, vm.RemoteOutboundState);
            Assert.True(vm.RemoteOutboundRequiresRefresh);
            Assert.Equal("http://unsaved.example.invalid:3128", vm.RemoteOutboundUrl);
            Assert.Equal("http://saved.example.invalid:8080", (await observer.Network.GetOutboundAsync(Ct)).Settings.Url);
            var current = await observer.Network.GetOutboundAsync(Ct);
            var changed = await observer.Network.SetOutboundAsync(new(current.Revision,
                new("http://changed-while-disconnected.example.invalid:8080")), Ct);
            vm.AttachFleet(fleet);
            await vm.WaitForNetworkOperationsAsync().WaitAsync(CompletionTimeout, Ct);
            Assert.Equal(changed.Revision, vm.RemoteOutboundState!.Revision);
            Assert.Equal("http://unsaved.example.invalid:3128", vm.RemoteOutboundUrl);
            Assert.True(vm.RemoteOutboundRequiresRefresh);
            AssertState(changed, await observer.Network.GetOutboundAsync(Ct));
            await vm.SaveRemoteOutboundCommand.ExecuteAsync(null);
            AssertState(changed, await observer.Network.GetOutboundAsync(Ct));
            Assert.Equal("http://unsaved.example.invalid:3128", vm.RemoteOutboundUrl);
        }
        finally { await CloseAsync(vm); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoteDraftProbe_Reports404Or407WithoutPersistingOrLeakingCredentials(bool rejectAuthentication)
    {
        // Controlled errors establish classification, not public egress or business success.
        const string user = "synthetic-gui-user";
        var password = $"synthetic-{Guid.NewGuid():N}";
        await using var proxy = new TestForwardProxy(requiredCredential: rejectAuthentication ? "different:credential" : $"{user}:{password}",
            statusCode: HttpStatusCode.NotFound);
        await using var host = await CreateHostAsync("ngdp", TestContext.Current.CancellationToken);
        var observer = await host.ConnectAsync("draft-observer", Ct);
        var specs = new[] { MachineSpec.Local(host.Root, "alpha") };
        var store = Store(specs);
        await using var fleet = await ConnectFleetAsync(specs);
        var vm = await MachineViewModelAsync(store, fleet, "alpha");
        try
        {
            await vm.ReadRemoteOutboundCommand.ExecuteAsync(null);
            var state = await observer.Network.GetOutboundAsync(Ct);
            var remoteFile = await File.ReadAllTextAsync(host.Environment.App.Home.SettingsFile, Ct);
            var clientFile = await File.ReadAllTextAsync(store.FilePath, Ct);
            vm.RemoteOutboundUrl = $"http://{user}:{password}@127.0.0.1:{proxy.Port}";
            vm.RemoteOutboundProbeUrl = ProbeTarget;
            await vm.ProbeRemoteCommand.ExecuteAsync(null);
            var result = Assert.IsType<ProxyProbeResult>(vm.RemoteProbe.Result);
            Assert.Equal((rejectAuthentication ? ProxyProbeOutcome.Failed : ProxyProbeOutcome.Passed,
                    rejectAuthentication ? ProxyProbeStage.ProxyAuth : ProxyProbeStage.HttpTarget,
                    (int?)(rejectAuthentication ? 407 : 404)),
                (result.Outcome, result.Stage, result.HttpStatus));
            Assert.Equal(("alpha", host.Environment.App.Home.Root), (result.Machine, result.Home));
            Assert.Equal(!rejectAuthentication, vm.RemoteProbe.IsPassed);
            AssertSafe(vm.RemoteProbe, user, password);
            AssertState(state, await observer.Network.GetOutboundAsync(Ct));
            Assert.Equal(remoteFile, await File.ReadAllTextAsync(host.Environment.App.Home.SettingsFile, Ct));
            Assert.Equal(clientFile, await File.ReadAllTextAsync(store.FilePath, Ct));
        }
        finally { await CloseAsync(vm); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ThreeLayers_RunInParallel_SameLayerIsSingleFlight_AndCancellationIsIndependent()
    {
        FleetSshEndToEndTests.RequireEnvironment();
        await using var ssh = await TestSshServer.StartAsync(Ct);
        await using var gate = new SshAuthenticationGate();
        await using var localTarget = new TestForwardProxy(pauseResponses: true, observePeerCloseWhilePaused: true,
            statusCode: HttpStatusCode.NotFound);
        await using var remoteTarget = new TestForwardProxy(pauseResponses: true, observePeerCloseWhilePaused: true,
            statusCode: HttpStatusCode.NotFound);
        await using var host = await CreateHostAsync("ngpl", TestContext.Current.CancellationToken);
        var observer = await host.ConnectAsync("parallel-survivor", Ct);
        await using var connection = new MachineConnection(MachineSpec.Local(host.Root, "alpha"), Options);
        connection.Start();
        await connection.WaitSettledAsync(Ct);
        Assert.Equal(MachineConnectionState.Connected, connection.State);
        var service = new FleetProxyProbe(Options);
        var sshProbe = new NetworkProbeViewModel(ProxyProbeLayer.Ssh);
        var localProbe = new NetworkProbeViewModel(ProxyProbeLayer.LocalOutbound);
        var remoteProbe = new NetworkProbeViewModel(ProxyProbeLayer.RemoteOutbound);
        var machine = gate.Wrap(ssh.Machine("ssh-target", host.Root));
        var sshPending = sshProbe.RunAsync(machine.Name, machine.HarnessHome,
            (id, token) => service.ProbeSshAsync(machine, id, token), machine.Password);
        Task localPending = Task.CompletedTask;
        Task remotePending = Task.CompletedTask;
        try
        {
            await gate.WaitForBlockedAuthenticationAsync(sshPending, Ct);
            localPending = localProbe.RunAsync(MachineSpec.LocalName, _root,
                (id, token) => service.ProbeLocalAsync(_root, LocalSettings(localTarget), id, token));
            await localTarget.WaitForRequestAsync(Ct);
            remotePending = remoteProbe.RunAsync("alpha", host.Environment.App.Home.Root,
                (id, token) => service.ProbeRemoteAsync(connection, new(RemoteSettings(remoteTarget)), id, token));
            await remoteTarget.WaitForRequestAsync(Ct);
            Assert.All(new[] { sshProbe, localProbe, remoteProbe }, probe => Assert.True(probe.IsRunning));
            Assert.Throws<InvalidOperationException>(() =>
            {
                _ = sshProbe.RunAsync(machine.Name, machine.HarnessHome,
                    (id, token) => service.ProbeSshAsync(machine, id, token));
            });
            Assert.Throws<InvalidOperationException>(() =>
            {
                _ = localProbe.RunAsync(MachineSpec.LocalName, _root,
                    (id, token) => service.ProbeLocalAsync(_root, LocalSettings(localTarget), id, token));
            });
            Assert.Throws<InvalidOperationException>(() =>
            {
                _ = remoteProbe.RunAsync("alpha", host.Environment.App.Home.Root,
                    (id, token) => service.ProbeRemoteAsync(connection, new(RemoteSettings(remoteTarget)), id, token));
            });
            Assert.Single(localTarget.Requests);
            Assert.Single(remoteTarget.Requests);
            Assert.False(sshPending.IsCompleted);
            Assert.False(localPending.IsCompleted);
            Assert.False(remotePending.IsCompleted);
            await localProbe.CancelCommand.ExecuteAsync(null).WaitAsync(CompletionTimeout, Ct);
            await localPending.WaitAsync(CompletionTimeout, Ct);
            Assert.Equal(ProxyProbeOutcome.Cancelled, localProbe.Result!.Outcome);
            Assert.False(localProbe.IsPassed);
            Assert.True(sshProbe.IsRunning);
            Assert.True(remoteProbe.IsRunning);
            Assert.True(await observer.PingAsync(Ct) > 0);
            await Task.WhenAll(sshProbe.CancelCommand.ExecuteAsync(null), remoteProbe.CancelCommand.ExecuteAsync(null))
                .WaitAsync(CompletionTimeout, Ct);
            await Task.WhenAll(sshPending, remotePending).WaitAsync(CompletionTimeout, Ct);
            Assert.All(new[] { sshProbe, localProbe, remoteProbe },
                probe => Assert.Equal(ProxyProbeOutcome.Cancelled, probe.Result!.Outcome));
            Assert.NotEqual(sshProbe.Result!.OperationId, localProbe.Result!.OperationId);
            Assert.NotEqual(localProbe.Result.OperationId, remoteProbe.Result!.OperationId);
            Assert.False(File.Exists(gate.AskPass));
            Assert.True(await observer.PingAsync(Ct) > 0);
            Assert.Equal(MachineConnectionState.Connected, connection.State);
        }
        finally
        {
            foreach (var probe in new[] { sshProbe, localProbe, remoteProbe })
                probe.Invalidate();
            await Task.WhenAll(sshPending, localPending, remotePending).WaitAsync(CompletionTimeout, Ct);
        }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MachineSwitchOrPageLeave_CancelsOldProbe_AndCannotOverwriteNextMachine(bool leavePage)
    {
        await using var alpha = await CreateHostAsync("ngsa", TestContext.Current.CancellationToken);
        await using var beta = await CreateHostAsync("ngsb", Ct);
        await using var held = new TestForwardProxy(pauseResponses: true, observePeerCloseWhilePaused: true,
            statusCode: HttpStatusCode.NotFound);
        await using var next = new TestForwardProxy(statusCode: HttpStatusCode.NotFound);
        var specs = new[] { MachineSpec.Local(alpha.Root, "alpha"), MachineSpec.Local(beta.Root, "beta") };
        await using var fleet = await ConnectFleetAsync(specs);
        var vm = await MachineViewModelAsync(Store(specs), fleet, "alpha");
        Task pending = Task.CompletedTask;
        try
        {
            await vm.WaitForNetworkOperationsAsync().WaitAsync(CompletionTimeout, Ct);
            await vm.ReadRemoteOutboundCommand.ExecuteAsync(null);
            vm.RemoteOutboundUrl = RemoteSettings(held).Url!;
            vm.RemoteOutboundProbeUrl = ProbeTarget;
            pending = vm.ProbeRemoteCommand.ExecuteAsync(null);
            await held.WaitForRequestAsync(Ct);
            if (leavePage)
                vm.DeactivateNetwork();
            vm.Selected = vm.Machines.Single(row => row.Name == "beta");
            await vm.WaitForNetworkOperationsAsync().WaitAsync(CompletionTimeout, Ct);
            Assert.Null(vm.RemoteProbe.Result);
            vm.ActivateNetwork();
            await vm.WaitForNetworkOperationsAsync().WaitAsync(CompletionTimeout, Ct);
            await vm.ReadRemoteOutboundCommand.ExecuteAsync(null);
            vm.RemoteOutboundUrl = RemoteSettings(next).Url!;
            vm.RemoteOutboundProbeUrl = ProbeTarget;
            await vm.ProbeRemoteCommand.ExecuteAsync(null);
            var current = Assert.IsType<ProxyProbeResult>(vm.RemoteProbe.Result);
            Assert.Equal(("beta", beta.Environment.App.Home.Root, (int?)404),
                (current.Machine, current.Home, current.HttpStatus));
            held.ReleaseResponse();
            await pending.WaitAsync(CompletionTimeout, Ct);
            Assert.Same(current, vm.RemoteProbe.Result);
            Assert.Null(HarnessSettings.Load(alpha.Environment.App.Home).Network?.OutboundProxy);
            Assert.Null(HarnessSettings.Load(beta.Environment.App.Home).Network?.OutboundProxy);
        }
        finally
        {
            await CloseAsync(vm);
            await pending.WaitAsync(CompletionTimeout, Ct);
        }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task InvalidatedGeneration_RejectsAnAlreadyCompletedRealProbeDeliveredLate()
    {
        await using var target = new TestForwardProxy(statusCode: HttpStatusCode.NotFound);
        var service = new FleetProxyProbe(Options);
        var vm = new NetworkProbeViewModel(ProxyProbeLayer.LocalOutbound);
        var completed = new TaskCompletionSource<ProxyProbeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deliver = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = vm.RunAsync(MachineSpec.LocalName, _root, async (id, token) =>
        {
            var result = await service.ProbeLocalAsync(_root, LocalSettings(target), id, token);
            completed.TrySetResult(result);
            await deliver.Task.WaitAsync(Ct);
            return result;
        });
        try
        {
            var late = await completed.Task.WaitAsync(CompletionTimeout, TestContext.Current.CancellationToken);
            Assert.Equal(404, late.HttpStatus);
            vm.Invalidate();
            deliver.TrySetResult();
            await pending.WaitAsync(CompletionTimeout, Ct);
            await vm.WaitForCompletionAsync().WaitAsync(CompletionTimeout, Ct);
            Assert.Null(vm.Result);
            Assert.False(vm.IsPassed);
            await vm.RunAsync(MachineSpec.LocalName, _root,
                (id, token) => service.ProbeLocalAsync(_root, LocalSettings(target), id, token));
            Assert.NotEqual(late.OperationId, vm.Result!.OperationId);
            Assert.Equal(404, vm.Result.HttpStatus);
        }
        finally
        {
            deliver.TrySetResult();
            vm.Invalidate();
            await pending.WaitAsync(CompletionTimeout, Ct);
            await vm.WaitForCompletionAsync().WaitAsync(CompletionTimeout, Ct);
        }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task CancelBeforeDelivery_RejectsAnAlreadyCompletedRealProbeResult()
    {
        await using var target = new TestForwardProxy(statusCode: HttpStatusCode.NotFound);
        var service = new FleetProxyProbe(Options);
        var vm = new NetworkProbeViewModel(ProxyProbeLayer.LocalOutbound);
        var completed = new TaskCompletionSource<ProxyProbeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deliver = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = vm.RunAsync(MachineSpec.LocalName, _root, async (id, token) =>
        {
            var result = await service.ProbeLocalAsync(_root, LocalSettings(target), id, token);
            completed.TrySetResult(result);
            await deliver.Task.WaitAsync(Ct);
            return result;
        });
        try
        {
            var late = await completed.Task.WaitAsync(CompletionTimeout, TestContext.Current.CancellationToken);
            Assert.Equal((ProxyProbeOutcome.Passed, (int?)404), (late.Outcome, late.HttpStatus));
            var cancel = vm.CancelCommand.ExecuteAsync(null);
            deliver.TrySetResult();
            await cancel.WaitAsync(CompletionTimeout, Ct);
            await pending.WaitAsync(CompletionTimeout, Ct);
            Assert.Equal(ProxyProbeOutcome.Cancelled, vm.Result!.Outcome);
            Assert.Equal(late.OperationId, vm.Result.OperationId);
            Assert.False(vm.IsPassed);
        }
        finally
        {
            deliver.TrySetResult();
            vm.Invalidate();
            await pending.WaitAsync(CompletionTimeout, Ct);
        }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task HiddenMachineView_CannotCancelTheVisibleOwnersRealProbe()
    {
        await using var held = new TestForwardProxy(pauseResponses: true, statusCode: HttpStatusCode.NotFound);
        await using var host = await CreateHostAsync("ngvo", TestContext.Current.CancellationToken);
        await using var fleet = await ConnectFleetAsync([MachineSpec.Local(host.Root, "alpha")]);
        var vm = await MachineViewModelAsync(Store([MachineSpec.Local(host.Root, "alpha")]), fleet, "alpha");
        var hidden = new object();
        var visible = new object();
        Task pending = Task.CompletedTask;
        try
        {
            vm.ActivateNetwork(hidden);
            vm.ActivateNetwork(visible);
            await vm.WaitForNetworkOperationsAsync().WaitAsync(CompletionTimeout, TestContext.Current.CancellationToken);
            vm.RemoteOutboundUrl = RemoteSettings(held).Url!;
            vm.RemoteOutboundProbeUrl = ProbeTarget;
            pending = vm.ProbeRemoteCommand.ExecuteAsync(null);
            await held.WaitForRequestAsync(Ct);
            Assert.False(vm.DeactivateNetwork(hidden));
            held.ReleaseResponse();
            await pending.WaitAsync(CompletionTimeout, Ct);
            Assert.Equal((ProxyProbeOutcome.Passed, (int?)404), (vm.RemoteProbe.Result!.Outcome, vm.RemoteProbe.Result.HttpStatus));
            vm.BeginNetworkShutdown();
            vm.ActivateNetwork(visible);
            await vm.ProbeRemoteCommand.ExecuteAsync(null);
            Assert.Null(vm.RemoteProbe.Result);
            Assert.Single(held.Requests);
        }
        finally
        {
            held.ReleaseResponse();
            await CloseAsync(vm);
            await pending.WaitAsync(CompletionTimeout, Ct);
        }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task VisibleOwnerTransfer_CancelsThePreviousPagesInFlightProbeAndRejectsItsResult()
    {
        await using var held = new TestForwardProxy(pauseResponses: true, observePeerCloseWhilePaused: true);
        await using var host = await CreateHostAsync("ngot", TestContext.Current.CancellationToken);
        var machine = MachineSpec.Local(host.Root, "alpha");
        await using var fleet = await ConnectFleetAsync([machine]);
        var vm = await MachineViewModelAsync(Store([machine]), fleet, machine.Name);
        var previous = new object();
        var current = new object();
        Task pending = Task.CompletedTask;
        try
        {
            vm.ActivateNetwork(previous);
            await vm.WaitForNetworkOperationsAsync().WaitAsync(CompletionTimeout, Ct);
            vm.RemoteOutboundUrl = RemoteSettings(held).Url!;
            vm.RemoteOutboundProbeUrl = ProbeTarget;
            pending = vm.ProbeRemoteCommand.ExecuteAsync(null);
            await held.WaitForRequestAsync(Ct);
            vm.ActivateNetwork(current);
            Assert.False(vm.DeactivateNetwork(previous));
            await pending.WaitAsync(CompletionTimeout, Ct);
            await held.WaitForClosedConnectionAsync(Ct).AsTask().WaitAsync(CompletionTimeout, Ct);
            await vm.WaitForNetworkOperationsAsync().WaitAsync(CompletionTimeout, Ct);
            Assert.Null(vm.RemoteProbe.Result);
            Assert.False(vm.RemoteProbe.IsRunning);
            Assert.False(vm.RemoteProbe.IsPassed);
        }
        finally
        {
            held.ReleaseResponse();
            await CloseAsync(vm);
            await pending.WaitAsync(CompletionTimeout, Ct);
        }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task SettingsShutdown_RejectsNewProbeSaveAndBorrowOperations()
    {
        await using var proxy = new TestForwardProxy(statusCode: HttpStatusCode.NotFound);
        await using var host = await CreateHostAsync("ngsd", TestContext.Current.CancellationToken);
        var observer = await host.ConnectAsync("shutdown-observer", TestContext.Current.CancellationToken);
        var home = host.Environment.App.Home;
        var vm = SettingsViewModel(host.Environment.App, observer);
        var saved = await File.ReadAllTextAsync(home.SettingsFile, Ct);
        try
        {
            vm.BeginNetworkShutdown();
            vm.EditOutboundProxyCommand.Execute(null);
            vm.OutboundProxyUrl = LocalSettings(proxy).Url!;
            vm.OutboundProxyProbeUrl = ProbeTarget;
            vm.SelectedBorrowedOutboundMachine = "unconfigured-after-shutdown";
            await vm.CheckProxyCommand.ExecuteAsync(null);
            await vm.SaveNetworkCommand.ExecuteAsync(null);
            await vm.ValidateBorrowedOutboundCommand.ExecuteAsync(null);
            await vm.SelectBorrowedOutboundCommand.ExecuteAsync(null);
            await vm.ClearBorrowedOutboundCommand.ExecuteAsync(null);
            await vm.WaitForNetworkOperationsAsync().WaitAsync(CompletionTimeout, Ct);
            Assert.Null(vm.LocalProxyProbe.Result);
            Assert.Null(vm.BorrowedOutboundProbe.Result);
            Assert.Empty(proxy.Requests);
            Assert.Equal(saved, await File.ReadAllTextAsync(home.SettingsFile, Ct));
        }
        finally { await CloseAsync(vm); }
    }

    private static OutboundProxySettings LocalSettings(TestForwardProxy proxy)
        => new() { Url = $"http://127.0.0.1:{proxy.Port}", ProbeUrl = ProbeTarget };

    private static RemoteOutboundProxySettings RemoteSettings(TestForwardProxy proxy)
        => new($"http://127.0.0.1:{proxy.Port}", ProbeUrl: ProbeTarget);

    [Fact(Timeout = TestTimeout)]
    public async Task RemoteCredentials_AreMaskedUntilExplicitEdit_AndOtherEditsPreserveTheSecret()
    {
        const string user = "synthetic-remote-user";
        var password = $"synthetic-{Guid.NewGuid():N}";
        await using var proxy = new TestForwardProxy(requiredCredential: $"{user}:{password}", statusCode: HttpStatusCode.NotFound);
        await using var host = await CreateHostAsync("ngcr", TestContext.Current.CancellationToken);
        var observer = await host.ConnectAsync("credential-observer", Ct);
        var initial = await observer.Network.GetOutboundAsync(Ct);
        var url = $"http://{user}:{password}@127.0.0.1:{proxy.Port}";
        await observer.Network.SetOutboundAsync(new(initial.Revision, new(url, ProbeUrl: ProbeTarget)), Ct);
        var specs = new[] { MachineSpec.Local(host.Root, "alpha") };
        await using var fleet = await ConnectFleetAsync(specs);
        var vm = await MachineViewModelAsync(Store(specs), fleet, "alpha");
        try
        {
            Assert.DoesNotContain(user, vm.RemoteOutboundUrl);
            Assert.DoesNotContain(password, vm.RemoteOutboundUrl);
            Assert.NotEqual(url, vm.RemoteOutboundUrl);
            vm.RemoteOutboundBypass = "internal.example.invalid";
            await vm.SaveRemoteOutboundCommand.ExecuteAsync(null);
            Assert.Equal(url, (await observer.Network.GetOutboundAsync(Ct)).Settings.Url);
            await vm.ProbeRemoteCommand.ExecuteAsync(null);
            Assert.Equal(404, vm.RemoteProbe.Result!.HttpStatus);
            AssertSafe(vm.RemoteProbe, user, password);
            vm.RemoteOutboundProbeUrl = ProbeTarget + "/edited";
            await vm.SaveRemoteOutboundCommand.ExecuteAsync(null);
            Assert.Equal(url, (await observer.Network.GetOutboundAsync(Ct)).Settings.Url);
            Assert.DoesNotContain(password, vm.RemoteOutboundUrl);
        }
        finally { await CloseAsync(vm); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task LocalCredentialsAndDraftProbe_PreserveRealValuesWithoutSavingTheDraft()
    {
        const string user = "synthetic-local-user";
        var password = $"synthetic-{Guid.NewGuid():N}";
        const string probeSecret = "synthetic-local-probe-token";
        await using var savedProxy = new TestForwardProxy(requiredCredential: $"{user}:{password}", statusCode: HttpStatusCode.NotFound);
        await using var draftProxy = new TestForwardProxy(statusCode: HttpStatusCode.NotFound);
        await using var host = await CreateHostAsync("ngcl", TestContext.Current.CancellationToken);
        var observer = await host.ConnectAsync("local-settings-observer", Ct);
        var home = host.Environment.App.Home;
        var url = $"http://{user}:{password}@127.0.0.1:{savedProxy.Port}";
        var probeUrl = $"{ProbeTarget}?token={probeSecret}";
        HarnessSettings.SaveOutboundProxy(home, new() { Url = url, ProbeUrl = probeUrl },
            OutboundProxy.FromContext(host.Environment.App.Ctx));
        var vm = SettingsViewModel(host.Environment.App, observer);
        try
        {
            Assert.False(vm.IsEditingOutboundProxy);
            Assert.DoesNotContain(user, vm.OutboundProxyUrl);
            Assert.DoesNotContain(password, vm.OutboundProxyUrl);
            Assert.DoesNotContain(probeSecret, vm.OutboundProxyProbeUrl);
            vm.OutboundProxyBypassText = "internal.example.invalid";
            await vm.SaveNetworkCommand.ExecuteAsync(null);
            Assert.Equal(url, HarnessSettings.Load(home).Network!.OutboundProxy!.Url);
            Assert.Equal(probeUrl, HarnessSettings.Load(home).Network!.OutboundProxy!.ProbeUrl);
            await vm.CheckProxyCommand.ExecuteAsync(null);
            Assert.Equal(404, vm.LocalProxyProbe.Result!.HttpStatus);
            AssertSafe(vm.LocalProxyProbe, user, password, probeSecret);
            vm.EditOutboundProxyCommand.Execute(null);
            Assert.True(vm.IsEditingOutboundProxy);
            Assert.Equal(url, vm.OutboundProxyUrl);
            Assert.Equal(probeUrl, vm.OutboundProxyProbeUrl);
            vm.OutboundProxyUrl = LocalSettings(draftProxy).Url!;
            vm.OutboundProxyProbeUrl = ProbeTarget + "/unsaved";
            var savedFile = await File.ReadAllTextAsync(home.SettingsFile, Ct);
            await vm.CheckProxyCommand.ExecuteAsync(null);
            Assert.Equal(404, vm.LocalProxyProbe.Result!.HttpStatus);
            Assert.Single(draftProxy.Requests);
            Assert.Equal(savedFile, await File.ReadAllTextAsync(home.SettingsFile, Ct));
            Assert.Equal(url, HarnessSettings.Load(home).Network!.OutboundProxy!.Url);
            vm.OutboundProxyUrl = url;
            await vm.SaveNetworkCommand.ExecuteAsync(null);
            Assert.False(vm.IsEditingOutboundProxy);
            Assert.DoesNotContain(password, vm.OutboundProxyUrl);
            Assert.Equal(url, HarnessSettings.Load(home).Network!.OutboundProxy!.Url);
        }
        finally { await CloseAsync(vm); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalPageLeaveOrSubpageSwitch_CancelsTheRequestAndClearsItsResult(bool switchSubpage)
    {
        await using var held = new TestForwardProxy(pauseResponses: true, observePeerCloseWhilePaused: true,
            statusCode: HttpStatusCode.NotFound);
        await using var host = await CreateHostAsync("nglv", TestContext.Current.CancellationToken);
        var observer = await host.ConnectAsync("local-cancel-survivor", Ct);
        var vm = SettingsViewModel(host.Environment.App, observer);
        SelectSection(vm, SettingsSection.Network);
        vm.EditOutboundProxyCommand.Execute(null);
        vm.OutboundProxyUrl = LocalSettings(held).Url!;
        vm.OutboundProxyProbeUrl = ProbeTarget;
        var pending = vm.CheckProxyCommand.ExecuteAsync(null);
        try
        {
            await held.WaitForRequestAsync(Ct);
            if (switchSubpage)
                SelectSection(vm, SettingsSection.Appearance);
            else
                vm.DeactivateNetwork();
            await vm.WaitForNetworkOperationsAsync().WaitAsync(CompletionTimeout, Ct);
            held.ReleaseResponse();
            await pending.WaitAsync(CompletionTimeout, Ct);
            Assert.Null(vm.LocalProxyProbe.Result);
            Assert.False(vm.LocalProxyProbe.IsPassed);
            Assert.True(await observer.PingAsync(Ct) > 0);
        }
        finally
        {
            await CloseAsync(vm);
            await pending.WaitAsync(CompletionTimeout, Ct);
        }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("proxyCommand")]
    [InlineData("localForward")]
    public async Task BorrowedSelection_ValidatesSwitchesAndClearsWithoutOverwritingOtherSettings(string mode)
    {
        FleetSshEndToEndTests.RequireEnvironment();
        await using var ssh = await TestSshServer.StartAsync(Ct);
        // Controlled responses prove route selection only, never public-network success.
        await using var baseline = new TestForwardProxy();
        await using var first = new TestForwardProxy();
        await using var second = new TestForwardProxy();
        await using var host = await CreateHostAsync("ngbr", TestContext.Current.CancellationToken);
        var home = host.Environment.App.Home;
        var observer = await host.ConnectAsync("borrowed-independent-host", Ct);
        var proxy = OutboundProxy.FromContext(host.Environment.App.Ctx);
        var originalProxy = new OutboundProxySettings { Url = LocalSettings(baseline).Url, ProbeUrl = ProbeTarget,
            Bypass = ["internal.example.invalid"] };
        HarnessSettings.SaveOutboundProxy(home, originalProxy, proxy);
        var machines = new[]
        {
            ssh.Machine("first", home.Root) with { Proxy = "none", OutboundTunnel = new(mode, LocalSettings(first).Url!) },
            ssh.Machine("second", home.Root) with { Proxy = "none", OutboundTunnel = new(mode, LocalSettings(second).Url!) },
        };
        var store = new MachineStore(home);
        var deploy = new DeploySettings { AutoDeploy = false, ServerProxy = "http://deployment.example.invalid:3128" };
        store.Save(new FleetConfig([.. machines], deploy));
        var gui = new GuiSettings(home);
        gui.Save(gui.Load() with { Theme = GuiSettings.ThemeLight, FontSize = 15 });
        await using var hostRouteLease = await host.Environment.App.Ctx.Get<FleetService>(FleetService.ServiceName)!
            .OutboundRoute.AcquireAsync(Ct);
        using var app = await ConfigBoot.Compose(new HarnessOptions(home, home.Root));
        var service = app.Ctx.Get<FleetService>(FleetService.ServiceName)!;
        var workspaceOptions = Options with
        {
            LocalHome = home,
            LocalProxy = service.OutboundRoute.Proxy,
            AcquireOutboundRoute = service.OutboundRoute.AcquireAsync,
        };
        await using var workspace = new FleetWorkspace(new FleetWorkspaceServices(store, workspaceOptions, () => new FleetClient([MachineSpec.Local(host.Root)], workspaceOptions), service.OutboundRoute));
        workspace.Start();
        await workspace.Client.WaitSettledAsync(Ct);
        Assert.Equal(MachineConnectionState.Connected, workspace.Find(MachineSpec.LocalName)!.State);
        var vm = SettingsViewModel(app, observer, workspace);
        using var localConsumer = new HttpClient(service.OutboundRoute.Proxy.CreateHandler());
        using var hostConsumer = new HttpClient(proxy.CreateHandler());
        try
        {
            vm.SelectedBorrowedOutboundMachine = "first";
            var beforeValidation = await File.ReadAllTextAsync(home.SettingsFile, Ct);
            await vm.ValidateBorrowedOutboundCommand.ExecuteAsync(null);
            Assert.Equal(ProxyProbeOutcome.Passed, vm.BorrowedOutboundProbe.Result!.Outcome);
            Assert.Equal(beforeValidation, await File.ReadAllTextAsync(home.SettingsFile, Ct));
            Assert.Null(HarnessSettings.Load(home).Network!.BorrowedOutboundMachine);
            await vm.SelectBorrowedOutboundCommand.ExecuteAsync(null);
            Assert.Equal("first", HarnessSettings.Load(home).Network!.BorrowedOutboundMachine);
            Assert.Equal(TestForwardProxy.ProxiedBody, await localConsumer.GetStringAsync(ProbeTarget + "/local-first", Ct));
            Assert.Equal(TestForwardProxy.ProxiedBody, await hostConsumer.GetStringAsync(ProbeTarget + "/host-first", Ct));
            Assert.Contains(first.Requests, request => request.Contains("/host-first", StringComparison.Ordinal));
            vm.SelectedBorrowedOutboundMachine = "second";
            await vm.SelectBorrowedOutboundCommand.ExecuteAsync(null);
            Assert.Equal("second", HarnessSettings.Load(home).Network!.BorrowedOutboundMachine);
            Assert.Equal(TestForwardProxy.ProxiedBody, await localConsumer.GetStringAsync(ProbeTarget + "/local-second", Ct));
            Assert.Equal(TestForwardProxy.ProxiedBody, await hostConsumer.GetStringAsync(ProbeTarget + "/host-second", Ct));
            Assert.Contains(second.Requests, request => request.Contains("/host-second", StringComparison.Ordinal));
            await vm.ClearBorrowedOutboundCommand.ExecuteAsync(null);
            var after = HarnessSettings.Load(home);
            Assert.Null(after.Network!.BorrowedOutboundMachine);
            Assert.Equal(originalProxy.Url, after.Network.OutboundProxy!.Url);
            Assert.Equal(originalProxy.ProbeUrl, after.Network.OutboundProxy.ProbeUrl);
            Assert.Equal(originalProxy.Bypass, after.Network.OutboundProxy.Bypass);
            Assert.Equal(deploy.ServerProxy, store.Load().Deploy.ServerProxy);
            foreach (var machine in machines)
            {
                var saved = store.Load().Machines.Single(item => item.Name == machine.Name);
                Assert.Equal(machine.Proxy, saved.Proxy);
                Assert.Equal(machine.OutboundTunnel, saved.OutboundTunnel);
            }
            Assert.Equal((GuiSettings.ThemeLight, 15d), (gui.Load().Theme, gui.Load().FontSize));
            Assert.Equal(TestForwardProxy.ProxiedBody, await localConsumer.GetStringAsync(ProbeTarget + "/local-baseline", Ct));
            Assert.Equal(TestForwardProxy.ProxiedBody, await hostConsumer.GetStringAsync(ProbeTarget + "/host-baseline", Ct));
            Assert.True(await observer.PingAsync(Ct) > 0);
        }
        finally { await CloseAsync(vm); }
    }

    private static SettingsViewModel SettingsViewModel(HarnessApp app, RemoteHostClient client, FleetWorkspace? workspace = null)
        => new(app.Ctx, app.Home,
            _ => throw new InvalidOperationException("Network settings must not execute session commands."),
            new GuiSettings(app.Home), new SettingsFacade(app.Ctx, app.Home, workspace),
            () => Task.FromResult<IWorkspacePlugins>(client.Plugins));

    private static void SelectSection(SettingsViewModel vm, SettingsSection section)
    {
        var item = vm.Sections.Single(candidate => candidate.Section == section);
        item.SelectCommand!.Execute(item);
    }

    private static async Task CloseAsync(SettingsViewModel vm)
    {
        vm.DeactivateNetwork();
        await vm.WaitForNetworkOperationsAsync().WaitAsync(CompletionTimeout, Ct);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task SshButton_UsesTheUnsavedMachineDraftWithoutRequiringAHost()
    {
        FleetSshEndToEndTests.RequireEnvironment();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        var nonexistentHome = Path.Combine(ssh.Root, "not-created");
        var machine = ssh.Machine("ssh-only", nonexistentHome) with { RemotePath = "/not-installed" };
        var store = Store([machine]);
        var vm = new MachinesViewModel(new FleetWorkspaceServices(store, Options));
        vm.Load(machine.Name);
        var before = await File.ReadAllTextAsync(store.FilePath, Ct);
        try
        {
            vm.Editor!.Name = "unsaved-ssh-name";
            await vm.ProbeSshCommand.ExecuteAsync(null);
            var result = Assert.IsType<ProxyProbeResult>(vm.SshProbe.Result);
            Assert.Equal((ProxyProbeLayer.Ssh, ProxyProbeOutcome.Passed, ProxyProbeStage.Ssh, "unsaved-ssh-name"),
                (result.Layer, result.Outcome, result.Stage, result.Machine));
            Assert.Equal(nonexistentHome, result.Home);
            Assert.False(Directory.Exists(nonexistentHome));
            Assert.Equal(before, await File.ReadAllTextAsync(store.FilePath, Ct));
        }
        finally { await CloseAsync(vm); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task Local407AndUnsupportedSsh_LeaveTheIndependentRemote404ResultIntact()
    {
        await using var remoteError = new TestForwardProxy(statusCode: HttpStatusCode.NotFound);
        await using var localError = new TestForwardProxy(requiredCredential: "required:credential");
        await using var host = await CreateHostAsync("ngmx", TestContext.Current.CancellationToken);
        var observer = await host.ConnectAsync("independent-layers", Ct);
        var specs = new[] { MachineSpec.Local(host.Root, "alpha") };
        await using var fleet = await ConnectFleetAsync(specs);
        var machines = await MachineViewModelAsync(Store(specs), fleet, "alpha");
        var settings = SettingsViewModel(host.Environment.App, observer);
        try
        {
            machines.RemoteOutboundUrl = RemoteSettings(remoteError).Url!;
            machines.RemoteOutboundProbeUrl = ProbeTarget;
            await machines.ProbeRemoteCommand.ExecuteAsync(null);
            var remote = Assert.IsType<ProxyProbeResult>(machines.RemoteProbe.Result);
            Assert.Equal(404, remote.HttpStatus);
            settings.EditOutboundProxyCommand.Execute(null);
            settings.OutboundProxyUrl = LocalSettings(localError).Url!;
            settings.OutboundProxyProbeUrl = ProbeTarget;
            await settings.CheckProxyCommand.ExecuteAsync(null);
            Assert.Equal((ProxyProbeOutcome.Failed, ProxyProbeStage.ProxyAuth, (int?)407),
                (settings.LocalProxyProbe.Result!.Outcome, settings.LocalProxyProbe.Result.Stage, settings.LocalProxyProbe.Result.HttpStatus));
            Assert.False(settings.LocalProxyProbe.IsPassed);
            await machines.ProbeSshCommand.ExecuteAsync(null);
            Assert.Equal(ProxyProbeOutcome.Unsupported, machines.SshProbe.Result!.Outcome);
            Assert.False(machines.SshProbe.IsPassed);
            Assert.Same(remote, machines.RemoteProbe.Result);
            Assert.True(await observer.PingAsync(Ct) > 0);
        }
        finally
        {
            await CloseAsync(settings);
            await CloseAsync(machines);
        }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BorrowedDraftProbe_Classifies404And407WithoutSavingOrReplacingTheCurrentRoute(bool rejectAuthentication)
    {
        FleetSshEndToEndTests.RequireEnvironment();
        await using var ssh = await TestSshServer.StartAsync(Ct);
        await using var target = new TestForwardProxy(statusCode: HttpStatusCode.NotFound,
            requiredCredential: rejectAuthentication ? "required:credential" : null);
        await using var host = await CreateHostAsync("ngbe", TestContext.Current.CancellationToken);
        var observer = await host.ConnectAsync("borrowed-draft-observer", Ct);
        var home = host.Environment.App.Home;
        var route = OutboundProxy.FromContext(host.Environment.App.Ctx);
        HarnessSettings.SaveOutboundProxy(home, new() { ProbeUrl = ProbeTarget }, route);
        var machine = ssh.Machine("borrowed-draft", home.Root) with
        {
            OutboundTunnel = new("proxyCommand", LocalSettings(target).Url!),
        };
        new MachineStore(home).Upsert(machine);
        var vm = SettingsViewModel(host.Environment.App, observer);
        try
        {
            vm.SelectedBorrowedOutboundMachine = machine.Name;
            var file = await File.ReadAllTextAsync(home.SettingsFile, Ct);
            var captured = route.CaptureRoute(new Uri(ProbeTarget));
            await vm.ValidateBorrowedOutboundCommand.ExecuteAsync(null);
            var result = Assert.IsType<ProxyProbeResult>(vm.BorrowedOutboundProbe.Result);
            Assert.Equal((rejectAuthentication ? ProxyProbeOutcome.Failed : ProxyProbeOutcome.Passed,
                    rejectAuthentication ? ProxyProbeStage.ProxyAuth : ProxyProbeStage.HttpTarget,
                    (int?)(rejectAuthentication ? 407 : 404)),
                (result.Outcome, result.Stage, result.HttpStatus));
            Assert.Equal(!rejectAuthentication, vm.BorrowedOutboundProbe.IsPassed);
            Assert.Equal(file, await File.ReadAllTextAsync(home.SettingsFile, Ct));
            Assert.Null(HarnessSettings.Load(home).Network!.BorrowedOutboundMachine);
            var after = route.CaptureRoute(new Uri(ProbeTarget));
            Assert.Equal((captured.Generation, captured.ProxyUri, captured.CancellationToken),
                (after.Generation, after.ProxyUri, after.CancellationToken));
            Assert.True(await observer.PingAsync(Ct) > 0);
        }
        finally { await CloseAsync(vm); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalAndBorrowedChecks_ShareOneLocalLayerSlot(bool borrowedFirst)
    {
        FleetSshEndToEndTests.RequireEnvironment();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        await using var held = new TestForwardProxy(pauseResponses: true, statusCode: HttpStatusCode.NotFound);
        await using var host = await CreateHostAsync("ngsl", Ct);
        var home = host.Environment.App.Home;
        HarnessSettings.SaveOutboundProxy(home, LocalSettings(held), OutboundProxy.FromContext(host.Environment.App.Ctx));
        var machine = ssh.Machine("shared-local-layer", home.Root) with { OutboundTunnel = new("proxyCommand", LocalSettings(held).Url!) };
        new MachineStore(home).Upsert(machine);
        var observer = await host.ConnectAsync("local-layer-slot", Ct);
        var vm = SettingsViewModel(host.Environment.App, observer);
        vm.SelectedBorrowedOutboundMachine = machine.Name;
        var first = borrowedFirst ? vm.ValidateBorrowedOutboundCommand : vm.CheckProxyCommand;
        var second = borrowedFirst ? vm.CheckProxyCommand : vm.ValidateBorrowedOutboundCommand;
        var other = borrowedFirst ? vm.LocalProxyProbe : vm.BorrowedOutboundProbe;
        var pending = first.ExecuteAsync(null);
        try
        {
            await held.WaitForRequestAsync(Ct);
            Assert.False(second.CanExecute(null));
            await second.ExecuteAsync(null);
            Assert.Null(other.Result);
            Assert.Single(held.Requests);
            held.ReleaseResponse();
            await pending.WaitAsync(CompletionTimeout, Ct);
            Assert.True(second.CanExecute(null));
            held.ReleaseResponse();
            await second.ExecuteAsync(null);
            Assert.Equal(ProxyProbeOutcome.Passed, vm.LocalProxyProbe.Result!.Outcome);
            Assert.Equal(ProxyProbeOutcome.Passed, vm.BorrowedOutboundProbe.Result!.Outcome);
            Assert.Equal(2, held.Requests.Count);
        }
        finally
        {
            held.ReleaseResponse();
            await CloseAsync(vm);
            await pending.WaitAsync(CompletionTimeout, Ct);
        }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BorrowedSelectionValidationAndBaseProbe_ShareTheSameLocalLayerSlot(bool selectionFirst)
    {
        FleetSshEndToEndTests.RequireEnvironment();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        await using var held = new TestForwardProxy(pauseResponses: true);
        await using var host = await CreateHostAsync("ngbs", Ct);
        var home = host.Environment.App.Home;
        var service = host.Environment.App.Ctx.Get<FleetService>(FleetService.ServiceName)!;
        HarnessSettings.SaveOutboundProxy(home, LocalSettings(held), service.OutboundRoute.Proxy);
        var machine = ssh.Machine("selection-layer-slot", home.Root) with
        {
            OutboundTunnel = new("proxyCommand", LocalSettings(held).Url!),
        };
        new MachineStore(home).Upsert(machine);
        await using var lease = await service.OutboundRoute.AcquireAsync(Ct);
        var observer = await host.ConnectAsync("selection-layer-slot-observer", Ct);
        var vm = SettingsViewModel(host.Environment.App, observer);
        vm.SelectedBorrowedOutboundMachine = machine.Name;
        var first = selectionFirst ? vm.SelectBorrowedOutboundCommand : vm.CheckProxyCommand;
        var second = selectionFirst ? vm.CheckProxyCommand : vm.SelectBorrowedOutboundCommand;
        var pending = first.ExecuteAsync(null);
        try
        {
            await held.WaitForRequestAsync(Ct);
            Assert.False(second.CanExecute(null));
            await second.ExecuteAsync(null).WaitAsync(CompletionTimeout, Ct);
            Assert.Single(held.Requests);
            held.ReleaseResponse();
            await pending.WaitAsync(CompletionTimeout, Ct);
            Assert.True(second.CanExecute(null));
            held.ReleaseResponse();
            await second.ExecuteAsync(null).WaitAsync(CompletionTimeout, Ct);
            Assert.Equal(machine.Name, HarnessSettings.Load(home).Network!.BorrowedOutboundMachine);
            Assert.True(vm.LocalProxyProbe.IsPassed);
        }
        finally
        {
            held.ReleaseResponse();
            await CloseAsync(vm);
            await pending.WaitAsync(CompletionTimeout, Ct);
        }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("proxyCommand")]
    [InlineData("localForward")]
    public async Task BorrowedProbe_UsesTheLatestSavedMachineWithoutReplacingTheConsumerRoute(string mode)
    {
        FleetSshEndToEndTests.RequireEnvironment();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        await using var original = new TestForwardProxy();
        await using var changed = new TestForwardProxy(requiredCredential: "fixture:required");
        await using var host = await CreateHostAsync("ngbc", Ct);
        var home = host.Environment.App.Home;
        var service = host.Environment.App.Ctx.Get<FleetService>(FleetService.ServiceName)!;
        HarnessSettings.SaveOutboundProxy(home, new() { ProbeUrl = ProbeTarget }, service.OutboundRoute.Proxy);
        var machine = ssh.Machine("changed-borrow", home.Root) with { OutboundTunnel = new(mode, LocalSettings(original).Url!) };
        var store = new MachineStore(home);
        store.Upsert(machine);
        await using var lease = await service.OutboundRoute.AcquireAsync(Ct);
        await service.OutboundRoute.SelectAsync(machine.Name, Ct);
        using var consumer = new HttpClient(service.OutboundRoute.Proxy.CreateHandler());
        Assert.Equal(TestForwardProxy.ProxiedBody, await consumer.GetStringAsync(ProbeTarget + "/before-change", Ct));
        var current = service.OutboundRoute.Proxy.CaptureRoute(new Uri(ProbeTarget));
        store.Upsert(machine with { OutboundTunnel = new(mode, LocalSettings(changed).Url!) });
        var result = await service.OutboundRoute.ProbeAsync(machine.Name, Guid.NewGuid(), Ct);
        Assert.Equal((ProxyProbeOutcome.Failed, ProxyProbeStage.ProxyAuth, (int?)407),
            (result.Outcome, result.Stage, result.HttpStatus));
        var after = service.OutboundRoute.Proxy.CaptureRoute(new Uri(ProbeTarget));
        Assert.Equal((current.Generation, current.ProxyUri, current.CancellationToken),
            (after.Generation, after.ProxyUri, after.CancellationToken));
        Assert.Equal(machine.Name, HarnessSettings.Load(home).Network!.BorrowedOutboundMachine);
        Assert.Equal(TestForwardProxy.ProxiedBody, await consumer.GetStringAsync(ProbeTarget + "/after-change", Ct));
        Assert.Contains(original.Requests, request => request.Contains("/after-change", StringComparison.Ordinal));
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BorrowedProbe_PreservesAnOwnedFixedPortWhileCheckingUpdatedCredentialsOrDestination(bool changeRemoteEndpoint)
    {
        FleetSshEndToEndTests.RequireEnvironment();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        await using var original = new TestForwardProxy(requiredCredential: "fixture:current");
        await using var changed = new TestForwardProxy(requiredCredential: "fixture:required");
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        await using var host = await CreateHostAsync("ngfp", Ct);
        var home = host.Environment.App.Home;
        var service = host.Environment.App.Ctx.Get<FleetService>(FleetService.ServiceName)!;
        HarnessSettings.SaveOutboundProxy(home, new() { ProbeUrl = ProbeTarget }, service.OutboundRoute.Proxy);
        var machine = ssh.Machine("fixed-port-borrow", home.Root) with
        {
            OutboundTunnel = new("localForward", $"http://fixture:current@127.0.0.1:{original.Port}", port),
        };
        var store = new MachineStore(home);
        store.Upsert(machine);
        await using var lease = await service.OutboundRoute.AcquireAsync(Ct);
        await service.OutboundRoute.SelectAsync(machine.Name, Ct);
        using var consumer = new HttpClient(service.OutboundRoute.Proxy.CreateHandler());
        Assert.Equal(TestForwardProxy.ProxiedBody, await consumer.GetStringAsync(ProbeTarget + "/fixed-before", Ct));
        using var incumbentWitness = new TcpListener(IPAddress.Loopback, port);
        Assert.Throws<SocketException>(() => incumbentWitness.Start());
        var current = service.OutboundRoute.Proxy.CaptureRoute(new Uri(ProbeTarget));
        var replacement = machine with
        {
            OutboundTunnel = machine.OutboundTunnel with
            {
                ProxyUrl = changeRemoteEndpoint ? LocalSettings(changed).Url! : $"http://fixture:wrong@127.0.0.1:{original.Port}",
            },
        };
        store.Upsert(replacement);
        var saved = await File.ReadAllTextAsync(home.SettingsFile, Ct);
        var result = await service.OutboundRoute.ProbeAsync(machine.Name, Guid.NewGuid(), Ct);
        Assert.Equal((ProxyProbeOutcome.Failed, ProxyProbeStage.ProxyAuth, (int?)407),
            (result.Outcome, result.Stage, result.HttpStatus));
        Assert.Equal(saved, await File.ReadAllTextAsync(home.SettingsFile, Ct));
        var after = service.OutboundRoute.Proxy.CaptureRoute(new Uri(ProbeTarget));
        Assert.Equal((current.Generation, current.ProxyUri, current.CancellationToken),
            (after.Generation, after.ProxyUri, after.CancellationToken));
        Assert.Equal(TestForwardProxy.ProxiedBody, await consumer.GetStringAsync(ProbeTarget + "/fixed-after", Ct));
        Assert.Contains(original.Requests, request => request.Contains("/fixed-after", StringComparison.Ordinal));
    }

    private static void AssertState(RemoteOutboundProxyState expected, RemoteOutboundProxyState actual)
    {
        Assert.Equal((expected.OwnerId, expected.HostHome, expected.Revision, expected.Settings.Url, expected.Settings.ProbeUrl),
            (actual.OwnerId, actual.HostHome, actual.Revision, actual.Settings.Url, actual.Settings.ProbeUrl));
        Assert.Equal(expected.Settings.Bypass ?? [], actual.Settings.Bypass ?? []);
    }

    private MachineStore Store(IEnumerable<MachineSpec> specs)
    {
        var store = new MachineStore(new HarnessHome(_root));
        store.Save(new FleetConfig([.. specs], Options.Deploy));
        return store;
    }

    private static async Task<MachinesViewModel> MachineViewModelAsync(MachineStore store, FleetClient fleet, string selected)
    {
        var vm = new MachinesViewModel(new FleetWorkspaceServices(store, Options));
        vm.Load(selected);
        vm.AttachFleet(fleet);
        await vm.WaitForNetworkOperationsAsync().WaitAsync(CompletionTimeout, Ct);
        return vm;
    }

    private static async Task<HarnessHostFixture> CreateHostAsync(string prefix, CancellationToken cancellationToken)
    {
        var host = await HarnessHostFixture.CreateAsync(prefix, cancellationToken);
        TempTree.AssertFitsMacOsSocketLimit(RemoteHostEndpoint.SocketPath(host.Root));
        TempTree.AssertFitsMacOsSocketLimit(Path.Combine(host.Root, "dsh.sock"));
        await host.ConnectAsync("fixture-ready", cancellationToken);
        return host;
    }

    private static async Task<FleetClient> ConnectFleetAsync(IEnumerable<MachineSpec> machines)
    {
        var fleet = new FleetClient(machines, Options);
        try
        {
            fleet.Start();
            await fleet.WaitSettledAsync(Ct);
            Assert.All(fleet.Machines, connection => Assert.Equal(MachineConnectionState.Connected, connection.State));
            return fleet;
        }
        catch
        {
            await fleet.DisposeAsync();
            throw;
        }
    }

    private static async Task CloseAsync(MachinesViewModel vm)
    {
        vm.DeactivateNetwork();
        await vm.WaitForNetworkOperationsAsync().WaitAsync(CompletionTimeout, Ct);
        vm.AttachFleet(null);
    }

    private static void AssertSafe(NetworkProbeViewModel probe, params string[] secrets)
    {
        var visible = probe.OutcomeText + probe.StageText + probe.HttpStatusText + probe.IdentityText + probe.Detail;
        foreach (var secret in secrets)
            Assert.DoesNotContain(secret, visible);
    }
}
