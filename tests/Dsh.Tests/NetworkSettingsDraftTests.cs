using System.Net;
using Dsh.Boot;
using Dsh.Fleet;
using Dsh.Gui.ViewModels;
using Dsh.RemoteHost;
using Dsh.Runtime;

namespace Dsh.Tests;

[Collection(SerialProcessCollection.Name)]
public sealed class NetworkSettingsDraftTests : IDisposable
{
    private const int TestTimeout = 5000;
    private const string ProbeTarget = "http://1.1.1.1/gui-draft-probe";
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(5);
    private readonly string _root = TempTree.CreateDirectory("network-draft");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static FleetOptions Options => new()
    {
        ClientName = "network-draft-component",
        AutoReconnect = false,
        Deploy = new DeploySettings { AutoDeploy = false },
    };

    public void Dispose() => TempTree.Delete(_root);

    [Theory(Explicit = true)]
    [InlineData(nameof(MachinesViewModel.RemoteOutboundUrl))]
    [InlineData(nameof(MachinesViewModel.RemoteOutboundBypass))]
    [InlineData(nameof(MachinesViewModel.RemoteOutboundProbeUrl))]
    public async Task RemoteRawDraftChange_InvalidatesThePendingProbeWithoutWriting(string property)
    {
        const string credential = "fixture:draft-secret";
        await using var proxy = new TestForwardProxy(requiredCredential: credential, pauseResponses: true,
            statusCode: HttpStatusCode.NotFound);
        await using var host = await CreateHostAsync("ndrp");
        var observer = await host.ConnectAsync("remote-draft-observer", TestContext.Current.CancellationToken);
        var initial = await observer.Network.GetOutboundAsync(Ct);
        var saved = await observer.Network.SetOutboundAsync(new(initial.Revision,
            new($"http://{credential}@127.0.0.1:{proxy.Port}", ProbeUrl: ProbeTarget)), Ct);
        var machine = MachineSpec.Local(host.Root, "alpha");
        await using var fleet = await ConnectFleetAsync(machine);
        var vm = await CreateViewModelAsync(machine, fleet);
        Task pending = Task.CompletedTask;
        try
        {
            Assert.DoesNotContain("draft-secret", vm.RemoteOutboundUrl);
            var file = await File.ReadAllTextAsync(host.Environment.App.Home.SettingsFile, Ct);
            pending = vm.ProbeRemoteCommand.ExecuteAsync(null);
            await proxy.WaitForRequestAsync(Ct);
            Assert.True(vm.RemoteProbe.IsRunning);
            switch (property)
            {
                case nameof(MachinesViewModel.RemoteOutboundUrl):
                    // Even a raw edit with the same normalized URI invalidates the old form's result.
                    vm.RemoteOutboundUrl += " ";
                    break;
                case nameof(MachinesViewModel.RemoteOutboundBypass):
                    vm.RemoteOutboundBypass = "changed.invalid";
                    break;
                case nameof(MachinesViewModel.RemoteOutboundProbeUrl):
                    vm.RemoteOutboundProbeUrl += "/changed";
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(property));
            }
            proxy.ReleaseResponse();
            await pending.WaitAsync(CompletionTimeout, Ct);
            Assert.Null(vm.RemoteProbe.Result);
            Assert.False(vm.RemoteProbe.IsPassed);
            Assert.False(vm.RemoteProbe.IsRunning);
            Assert.Equal(file, await File.ReadAllTextAsync(host.Environment.App.Home.SettingsFile, Ct));
            Assert.Equal(saved.Revision, (await observer.Network.GetOutboundAsync(Ct)).Revision);

            if (property == nameof(MachinesViewModel.RemoteOutboundBypass))
            {
                await vm.SaveRemoteOutboundCommand.ExecuteAsync(null);
                var committed = await observer.Network.GetOutboundAsync(Ct);
                Assert.Equal(saved.Settings.Url, committed.Settings.Url);
                Assert.Equal(saved.Settings.ProbeUrl, committed.Settings.ProbeUrl);
                Assert.Equal(new[] { "changed.invalid" }, committed.Settings.Bypass);
                Assert.DoesNotContain("draft-secret", vm.RemoteOutboundUrl);
            }
        }
        finally { await CloseAsync(vm); }
    }

    [Fact(Explicit = true)]
    public async Task SshRawTransportEdit_CancelsBlockedAuthenticationAndClearsItsResult()
    {
        FleetSshEndToEndTests.RequireEnvironment();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        await using var gate = new SshAuthenticationGate();
        var machine = gate.Wrap(ssh.Machine("ssh-draft", _root));
        var store = Store(machine);
        var vm = new MachinesViewModel(new FleetWorkspaceServices(store, Options));
        vm.Load(machine.Name);
        try
        {
            var file = await File.ReadAllTextAsync(store.FilePath, Ct);
            var pending = vm.ProbeSshCommand.ExecuteAsync(null);
            await gate.WaitForBlockedAuthenticationAsync(pending, Ct);
            // ToSpec trims this value, but the diagnostic still belongs to the earlier raw draft.
            vm.Editor!.Host += " ";
            await pending.WaitAsync(CompletionTimeout, Ct);
            Assert.Null(vm.SshProbe.Result);
            Assert.False(vm.SshProbe.IsRunning);
            Assert.False(vm.SshProbe.IsPassed);
            Assert.False(File.Exists(gate.AskPass));
            Assert.Equal(file, await File.ReadAllTextAsync(store.FilePath, Ct));
        }
        finally { await CloseAsync(vm); }
    }

    [Fact(Explicit = true)]
    public async Task ReplacingEditor_CancelsItsProbe_AndOldEditorCannotInvalidateTheReplacement()
    {
        FleetSshEndToEndTests.RequireEnvironment();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        await using var gate = new SshAuthenticationGate();
        var machine = gate.Wrap(ssh.Machine("ssh-editor", _root));
        var vm = new MachinesViewModel(new FleetWorkspaceServices(Store(machine), Options));
        vm.Load(machine.Name);
        try
        {
            var previous = vm.Editor!;
            var pending = vm.ProbeSshCommand.ExecuteAsync(null);
            await gate.WaitForBlockedAuthenticationAsync(pending, Ct);
            vm.Editor = MachineEditorViewModel.From(machine);
            await pending.WaitAsync(CompletionTimeout, Ct);
            Assert.Null(vm.SshProbe.Result);
            Assert.False(File.Exists(gate.AskPass));

            pending = vm.ProbeSshCommand.ExecuteAsync(null);
            await gate.WaitForBlockedAuthenticationAsync(pending, Ct);
            previous.Host += " ";
            await gate.ReleaseAsync(Ct);
            await pending.WaitAsync(CompletionTimeout, Ct);
            Assert.True(vm.SshProbe.IsPassed);
            Assert.Equal(machine.Name, vm.SshProbe.Result!.Machine);
            vm.Editor.Name = "replacement-draft";
            Assert.Null(vm.SshProbe.Result);
            Assert.False(vm.SshProbe.IsPassed);
        }
        finally { await CloseAsync(vm); }
    }

    [Theory(Explicit = true)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelAfterActualRpcBeforeUiDelivery_RetainsDraftAndRequiresReadAfterAWrite(bool write)
    {
        await using var host = await CreateHostAsync("ndlc");
        var observer = await host.ConnectAsync("late-cancel-observer", TestContext.Current.CancellationToken);
        var machine = MachineSpec.Local(host.Root, "alpha");
        await using var fleet = await ConnectFleetAsync(machine);
        var vm = await CreateViewModelAsync(machine, fleet);
        using var delivery = new PausedContinuationContext();
        try
        {
            var revision = vm.RemoteOutboundState!.Revision;
            vm.RemoteOutboundUrl = "http://draft.example.invalid:3128";
            vm.RemoteOutboundBypass = "draft.invalid";
            vm.RemoteOutboundProbeUrl = ProbeTarget;
            RemoteOutboundProxyState? changed = null;
            if (!write)
                changed = await observer.Network.SetOutboundAsync(new(revision,
                    new("http://other.example.invalid:8080", ["other.invalid"], ProbeTarget + "/other")), Ct);

            // Only the real VM's await delivery is held; Fleet and the Host complete their normal RPC.
            var pending = delivery.Invoke(() => write
                ? vm.SaveRemoteOutboundCommand.ExecuteAsync(null)
                : vm.ReadRemoteOutboundCommand.ExecuteAsync(null));
            await delivery.WaitForPostAsync().WaitAsync(CompletionTimeout, Ct);
            Assert.False(pending.IsCompleted);
            var committed = await observer.Network.GetOutboundAsync(Ct);
            Assert.NotEqual(revision, committed.Revision);
            Assert.Equal(write ? "http://draft.example.invalid:3128" : changed!.Settings.Url, committed.Settings.Url);
            Assert.True(vm.RemoteOutboundBusy);
            var cancelling = vm.CancelRemoteOutboundCommand.ExecuteAsync(null);
            delivery.Resume();
            await Task.WhenAll(pending, cancelling).WaitAsync(CompletionTimeout, Ct);

            Assert.False(vm.RemoteOutboundBusy);
            Assert.Equal(revision, vm.RemoteOutboundState.Revision);
            Assert.Equal("http://draft.example.invalid:3128", vm.RemoteOutboundUrl);
            Assert.Equal("draft.invalid", vm.RemoteOutboundBypass);
            Assert.Equal(ProbeTarget, vm.RemoteOutboundProbeUrl);
            Assert.Equal(committed.Revision, (await observer.Network.GetOutboundAsync(Ct)).Revision);

            if (write)
            {
                Assert.True(vm.RemoteOutboundRequiresRefresh);
                vm.RemoteOutboundUrl = "http://next.example.invalid:3128";
                await vm.SaveRemoteOutboundCommand.ExecuteAsync(null);
                Assert.Equal("http://next.example.invalid:3128", vm.RemoteOutboundUrl);
                Assert.Equal(committed.Revision, (await observer.Network.GetOutboundAsync(Ct)).Revision);
                await vm.ReadRemoteOutboundCommand.ExecuteAsync(null);
                Assert.False(vm.RemoteOutboundRequiresRefresh);
                Assert.Equal(committed.Revision, vm.RemoteOutboundState.Revision);
                    vm.RemoteOutboundUrl = "http://next.example.invalid:3128";
                await vm.SaveRemoteOutboundCommand.ExecuteAsync(null);
                Assert.Equal("http://next.example.invalid:3128", (await observer.Network.GetOutboundAsync(Ct)).Settings.Url);
            }
        }
        finally
        {
            delivery.Resume();
            await CloseAsync(vm);
        }
    }

    private MachineStore Store(MachineSpec machine)
    {
        var store = new MachineStore(new HarnessHome(_root));
        store.Save(new FleetConfig([machine], Options.Deploy));
        return store;
    }

    private async Task<MachinesViewModel> CreateViewModelAsync(MachineSpec machine, FleetClient fleet)
    {
        var vm = new MachinesViewModel(new FleetWorkspaceServices(Store(machine), Options));
        vm.Load(machine.Name);
        vm.AttachFleet(fleet);
        await vm.WaitForNetworkOperationsAsync().WaitAsync(CompletionTimeout, Ct);
        return vm;
    }

    private static async Task<HarnessHostFixture> CreateHostAsync(string prefix)
    {
        var host = await HarnessHostFixture.CreateAsync(prefix, Ct);
        TempTree.AssertFitsMacOsSocketLimit(RemoteHostEndpoint.SocketPath(host.Root));
        TempTree.AssertFitsMacOsSocketLimit(Path.Combine(host.Root, "dsh.sock"));
        await host.ConnectAsync("fixture-ready", Ct);
        return host;
    }

    private static async Task<FleetClient> ConnectFleetAsync(MachineSpec machine)
    {
        var fleet = new FleetClient([machine], Options);
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

    private sealed class PausedContinuationContext : SynchronizationContext, IDisposable
    {
        private readonly SynchronizationContext _dispatcher = Current ?? new SynchronizationContext();
        private readonly object _gate = new();
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _pending = new();
        private readonly TaskCompletionSource _posted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _paused = true;

        public Task Invoke(Func<Task> operation)
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try { return operation(); }
            finally { SetSynchronizationContext(previous); }
        }

        public Task WaitForPostAsync() => _posted.Task;

        public override void Post(SendOrPostCallback callback, object? state)
        {
            lock (_gate)
            {
                if (_paused)
                {
                    _pending.Enqueue((callback, state));
                    _posted.TrySetResult();
                    return;
                }
            }
            _dispatcher.Post(callback, state);
        }

        public void Resume()
        {
            (SendOrPostCallback Callback, object? State)[] pending;
            lock (_gate)
            {
                _paused = false;
                pending = _pending.ToArray();
                _pending.Clear();
            }
            foreach (var item in pending)
                _dispatcher.Post(item.Callback, item.State);
        }

        public void Dispose() => Resume();
    }
}

