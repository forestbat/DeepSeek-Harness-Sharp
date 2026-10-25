using Dsh.Boot;
using Dsh.Fleet;
using Dsh.Fleet.Iroh;
using Dsh.Gui.ViewModels;
using Dsh.RemoteHost;

namespace Dsh.Tests;

/** 机器管理页的视图模型: 经真实 MachineStore(settings.yaml 的 fleet 段)增删改机器与部署设置, 对真实 host 测试连接与诊断。 */
[Collection(SerialProcessCollection.Name)]
public sealed class MachinesViewModelTests : IDisposable
{
    [Fact]
    public void NewMachineButtons_HighlightOnlyTheActiveDraftKind()
    {
        var viewModel = new MachinesViewModel(new MachineStore(new HarnessHome(_home)), FleetConnectionTests.TestOptions);
        viewModel.Load();

        viewModel.NewSshCommand.Execute(null);
        Assert.True(viewModel.IsDraftingSsh);
        Assert.False(viewModel.IsDraftingIroh);
        Assert.False(viewModel.IsDraftingLocal);

        viewModel.NewLocalCommand.Execute(null);
        Assert.False(viewModel.IsDraftingSsh);
        Assert.False(viewModel.IsDraftingIroh);
        Assert.True(viewModel.IsDraftingLocal);
    }

    private readonly string _home = TempTree.CreateDirectory("machines-vm");
    private readonly IFleetIrohSetup? _previousIrohSetup = FleetService.IrohSetup;

    public MachinesViewModelTests()
        => FleetService.IrohSetup = null;

    public void Dispose()
    {
        FleetService.IrohSetup = _previousIrohSetup;
        TempTree.Delete(_home);
    }

    [Fact]
    public async Task Machines_AreAddedRenamedAndDeleted_AndEveryFieldRoundTripsThroughTheStore()
    {
        var store = new MachineStore(new HarnessHome(_home));
        var viewModel = new MachinesViewModel(store, new FleetOptions());
        var changes = 0;
        viewModel.MachinesChanged += () => changes++;
        viewModel.Load();
        var initial = (Assert.Single(viewModel.Machines).IsImplicit, viewModel.Editor, viewModel.DeleteCommand.CanExecute(null));

        viewModel.NewSshCommand.Execute(null);
        Fill(viewModel.Editor!, "build", SshAuth.Key);
        await viewModel.SaveCommand.ExecuteAsync(null);
        var saved = Assert.Single(store.Load().Machines);

        viewModel.Editor!.Name = "builder";
        viewModel.Editor.Port = "22";
        await viewModel.SaveCommand.ExecuteAsync(null);
        var renamed = store.Load().Machines.Select(machine => (machine.Name, machine.Port)).ToList();

        viewModel.NewSshCommand.Execute(null);
        Fill(viewModel.Editor!, "lab", SshAuth.Password);
        await viewModel.SaveCommand.ExecuteAsync(null);
        viewModel.Editor!.Name = "builder";
        await viewModel.SaveCommand.ExecuteAsync(null);
        var duplicate = (viewModel.StatusIsError, string.Join(",", store.Load().Machines.Select(machine => machine.Name)));

        viewModel.Selected = viewModel.Machines.Single(row => row.Name == "lab");
        viewModel.DeleteCommand.Execute(null);
        var beforeConfirming = store.Load().Machines.Count;
        await viewModel.Confirmation!.AcceptCommand.ExecuteAsync(null);

        Assert.Equal((true, (MachineEditorViewModel?)null, false), initial);
        Assert.Equal(("build", MachineKinds.Ssh, "build.internal", 2222, "dev", SshAuth.Key), (saved.Name, saved.Kind, saved.Host, saved.Port, saved.User, saved.Auth));
        Assert.Equal(("~/.ssh/id_test", null, "ssh -W %h:%p jump", "/srv/work", "~/.dsh-build"), (saved.KeyPath, saved.Password, saved.Proxy, saved.RemotePath, saved.HarnessHome));
        Assert.Equal(["ConnectTimeout=5", "ServerAliveInterval=10"], saved.SshOptions);
        Assert.Equal([("builder", 22)], renamed);
        Assert.Equal((true, "builder,lab"), duplicate);
        Assert.Equal(2, beforeConfirming);
        Assert.Equal(["local", "builder"], viewModel.Machines.Select(row => row.Name));
        Assert.Equal("builder", Assert.Single(store.Load().Machines).Name);
        Assert.Equal(4, changes);
    }

    [Fact]
    public async Task MissingIrohCapability_RejectsConvertingAnSshMachineWithoutChangingTheStore()
    {
        var machine = MachineSpec.Ssh("target", "server.internal", MachineSpec.DefaultSshPort, "fixture-user", SshAuth.Agent);
        var store = new MachineStore(new HarnessHome(_home));
        store.Upsert(machine);
        var viewModel = new MachinesViewModel(store, new FleetOptions());
        viewModel.Load("target");

        Assert.False(viewModel.NewIrohCommand.CanExecute(null));
        Assert.False(viewModel.EnableIrohCommand.CanExecute(null));
        Assert.DoesNotContain(MachineKinds.Iroh, viewModel.Editor!.Kinds);
        viewModel.Editor.Kind = MachineKinds.Iroh;
        viewModel.Editor.EndpointId = new string('a', MachineSpec.IrohEndpointIdLength);
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.True(viewModel.StatusIsError);
        Assert.Equal(machine, Assert.Single(store.Load().Machines));
    }

    [Fact]
    public void IrohEditor_AutofillsPathAndKeepsTargetAndArguments()
    {
        var machine = MachineSpec.Ssh("target", "bootstrap.invalid", 22, "fixture-user", SshAuth.Agent) with
        {
            Kind = MachineKinds.Iroh,
            EndpointId = new string('a', MachineSpec.IrohEndpointIdLength),
            IrohArguments = "-i ~/.ssh/id_ed25519",
        };
        var editor = MachineEditorViewModel.From(machine, true, "/opt/iroh-ssh");

        Assert.Equal("/opt/iroh-ssh", editor.IrohExecutablePath);
        Assert.Equal($"fixture-user@{new string('a', MachineSpec.IrohEndpointIdLength)}", editor.IrohTarget);
        Assert.Equal("-i ~/.ssh/id_ed25519", editor.IrohArguments);
    }

    [Theory]
    [InlineData(MachineKinds.Ssh)]
    [InlineData(MachineKinds.Iroh)]
    public async Task EditingAnExistingMachine_PreservesItsCliConfiguredOutboundTunnel(string kind)
    {
        var tunnel = new OutboundTunnelSpec("proxyCommand", "http://fixture-user:fixture-password@proxy.internal:3128");
        var machine = MachineSpec.Ssh("target", "server.internal", 22, "fixture-user", SshAuth.Agent) with
        {
            Kind = kind,
            EndpointId = kind == MachineKinds.Iroh ? new string('a', 64) : null,
            OutboundTunnel = tunnel,
        };
        var store = new MachineStore(new HarnessHome(_home));
        store.Upsert(machine);
        var viewModel = new MachinesViewModel(store, new FleetOptions());
        viewModel.Load("target");
        Assert.Equal(kind == MachineKinds.Iroh, viewModel.Editor!.Kinds.Contains(MachineKinds.Iroh));
        viewModel.Editor!.Name = "renamed";
        viewModel.Editor.RemotePath = "/srv/updated";

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.False(viewModel.StatusIsError, viewModel.Status);
        var saved = Assert.Single(store.Load().Machines);
        Assert.Equal(("renamed", "/srv/updated", kind), (saved.Name, saved.RemotePath, saved.Kind));
        Assert.Equal(tunnel, saved.OutboundTunnel);
    }

    [Theory]
    [InlineData(MachineKinds.Ssh, "proxyCommand", null)]
    [InlineData(MachineKinds.Ssh, "localForward", 43210)]
    [InlineData(MachineKinds.Iroh, "proxyCommand", null)]
    [InlineData(MachineKinds.Iroh, "localForward", null)]
    public async Task OutboundTunnel_ExplicitEditsPreserveEveryOtherSavedMachineField(string kind, string mode, int? localPort)
    {
        var original = MachineSpec.Ssh("target", "server.invalid", 2222, "fixture-user", SshAuth.Key) with
        {
            KeyPath = "/fixture/id_ed25519",
            Proxy = kind == MachineKinds.Ssh ? "ssh -W %h:%p jump.invalid" : null,
            RemotePath = "/fixture/work",
            HarnessHome = "/fixture/host-home",
            SshOptions = ["ConnectTimeout=5", "ServerAliveInterval=10"],
            Kind = kind,
            EndpointId = kind == MachineKinds.Iroh ? new string('a', 64) : null,
            RelayUrls = ["https://relay.invalid"],
            OutboundTunnel = new("proxyCommand", "http://fixture-user:fixture-secret@proxy.invalid:3128"),
        };
        var store = new MachineStore(new HarnessHome(_home));
        store.Upsert(original);
        var viewModel = new MachinesViewModel(store, new FleetOptions());
        viewModel.Load("target");
        var editor = viewModel.Editor!;
        Assert.Equal("http://***@proxy.invalid:3128", editor.OutboundTunnelProxyUrl);
        editor.OutboundTunnelMode = mode;
        editor.OutboundTunnelProxyUrl = "socks5://new-user:new-secret@new-proxy.invalid:1080";
        editor.OutboundTunnelLocalPort = localPort?.ToString() ?? "";
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.False(viewModel.StatusIsError, viewModel.Status);
        var saved = Assert.Single(store.Load().Machines);
        Assert.Equal(new OutboundTunnelSpec(mode, "socks5://new-user:new-secret@new-proxy.invalid:1080", localPort), saved.OutboundTunnel);
        Assert.Equal(original with { OutboundTunnel = saved.OutboundTunnel, SshOptions = saved.SshOptions, RelayUrls = saved.RelayUrls }, saved);
        Assert.Equal(original.SshOptions, saved.SshOptions);
        Assert.Equal(original.RelayUrls, saved.RelayUrls);
        Assert.Equal("socks5://***@new-proxy.invalid:1080", viewModel.Editor!.OutboundTunnelProxyUrl);
    }

    [Fact]
    public async Task MaskedTunnelAndSshCredentials_AreNeverPersistedAsPlaceholders()
    {
        var original = MachineSpec.Ssh("target", "server.invalid", 22, "fixture-user", SshAuth.Agent) with
        {
            Proxy = "http://ssh-user:ssh-secret@ssh-proxy.invalid:3128",
            OutboundTunnel = new("localForward", "http://proxy-user:proxy-secret@proxy.invalid:3128", 43210),
        };
        var store = new MachineStore(new HarnessHome(_home));
        store.Upsert(original);
        var viewModel = new MachinesViewModel(store, new FleetOptions());
        viewModel.Load("target");
        Assert.Equal("http://***@ssh-proxy.invalid:3128", viewModel.Editor!.Proxy);
        Assert.Equal("http://***@proxy.invalid:3128", viewModel.Editor.OutboundTunnelProxyUrl);

        viewModel.Editor.RemotePath = "/fixture/updated";
        await viewModel.SaveCommand.ExecuteAsync(null);
        var saved = Assert.Single(store.Load().Machines);
        Assert.Equal(original.Proxy, saved.Proxy);
        Assert.Equal(original.OutboundTunnel, saved.OutboundTunnel);
        Assert.DoesNotContain("***", File.ReadAllText(store.FilePath), StringComparison.Ordinal);

        viewModel.Editor!.OutboundTunnelEnabled = false;
        await viewModel.SaveCommand.ExecuteAsync(null);
        Assert.Null(Assert.Single(store.Load().Machines).OutboundTunnel);
        Assert.Equal(original.Proxy, store.Load().Machines[0].Proxy);
    }

    [Fact]
    public async Task DeploySettings_AreSavedSeparately_AndSurviveSavingAMachine()
    {
        var store = new MachineStore(new HarnessHome(_home));
        var viewModel = new MachinesViewModel(store, new FleetOptions());
        viewModel.Load();
        var deploy = viewModel.Deploy!;

        (deploy.AutoDeploy, deploy.ReleaseVersion, deploy.Via, deploy.ServerProxy) = (false, "v1.2.3", DownloadVia.Client, " ");
        await viewModel.SaveDeploySettingsCommand.ExecuteAsync(null);
        deploy.ReleaseVersion = "v9.9.9";
        viewModel.NewSshCommand.Execute(null);
        Fill(viewModel.Editor!, "build", SshAuth.Agent);
        await viewModel.SaveCommand.ExecuteAsync(null);
        var config = store.Load();

        Assert.Equal((false, "v1.2.3", DownloadVia.Client, (string?)null), (config.Deploy.AutoDeploy, config.Deploy.ReleaseVersion, config.Deploy.Via, config.Deploy.ServerProxy));
        Assert.Equal(DeploySettings.DefaultReleaseBaseUrl, config.Deploy.ReleaseBaseUrl);
        Assert.Same(deploy, viewModel.Deploy);
        Assert.Equal("v9.9.9", viewModel.Deploy!.ReleaseVersion);
        Assert.Equal(("build", (string?)null), (Assert.Single(config.Machines).Name, config.Machines[0].KeyPath));
    }

    [Theory]
    [InlineData(SshAuth.Agent)]
    [InlineData(SshAuth.Key)]
    [InlineData(SshAuth.Password)]
    public async Task IrohMachines_RoundTripAndRemainIrohWhenEdited(string auth)
    {
        var store = new MachineStore(new HarnessHome(_home));
        var viewModel = new MachinesViewModel(store, new FleetOptions { SshTransportAdapter = new IrohSshTransport() });
        viewModel.Load();
        viewModel.NewIrohCommand.Execute(null);
        var editor = viewModel.Editor!;
        Assert.True(editor.IsIroh);
        Assert.True(editor.IsRemote);
        Assert.False(editor.IsSsh);
        Fill(editor, "mesh", auth);
        editor.Proxy = "";
        editor.EndpointId = new string('a', 64);
        editor.RelayUrls = " https://relay.example.com \r\n\r\nhttps://relay2.example.com\n";
        await viewModel.SaveCommand.ExecuteAsync(null);
        Assert.False(viewModel.StatusIsError, viewModel.Status);

        viewModel.Load("mesh");
        Assert.Equal(editor.EndpointId, viewModel.Editor!.EndpointId);
        Assert.Equal(MachineKinds.Iroh, viewModel.Editor.Kind);
        viewModel.Editor.Name = "mesh-renamed";
        await viewModel.SaveCommand.ExecuteAsync(null);
        Assert.False(viewModel.StatusIsError, viewModel.Status);
        var saved = Assert.Single(store.Load().Machines);
        Assert.Equal(("mesh-renamed", MachineKinds.Iroh, "mesh.internal", new string('a', 64)), (saved.Name, saved.Kind, saved.Host, saved.EndpointId));
        Assert.Equal(["https://relay.example.com", "https://relay2.example.com"], saved.RelayUrls);
        Assert.Equal(["ConnectTimeout=5", "ServerAliveInterval=10"], saved.SshOptions);
        Assert.Equal((auth, auth == SshAuth.Key ? "~/.ssh/id_test" : null, auth == SshAuth.Password ? "secret" : null), (saved.Auth, saved.KeyPath, saved.Password));
        Assert.Equal(("/srv/work", "~/.dsh-build", (string?)null), (saved.RemotePath, saved.HarnessHome, saved.Proxy));
    }

    [Fact]
    public async Task InvalidIrohEdit_PreservesTheSavedMachineAndDoesNotRefreshFleet()
    {
        var store = new MachineStore(new HarnessHome(_home));
        var machine = MachineSpec.Ssh("box", "box.internal", 22, "dev", SshAuth.Agent) with { Kind = MachineKinds.Iroh, EndpointId = new string('b', 64) };
        store.Upsert(machine);
        var viewModel = new MachinesViewModel(store, new FleetOptions());
        var changes = 0;
        viewModel.MachinesChanged += () => changes++;
        viewModel.Load("box");
        viewModel.Editor!.EndpointId = "not-an-endpoint";
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.True(viewModel.StatusIsError, viewModel.Status);
        Assert.Equal(machine, Assert.Single(store.Load().Machines));
        Assert.Equal(0, changes);
        Assert.True(viewModel.Editor.IsIroh);
        Assert.Equal("not-an-endpoint", viewModel.Editor.EndpointId);
    }

    [Fact(Timeout = 5000)]
    public async Task FailedSshBootstrap_DoesNotPersistOrSwitchTheEditor()
    {
        using var port = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        port.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        var machine = MachineSpec.Ssh("box", "127.0.0.1", ((System.Net.IPEndPoint)port.LocalEndPoint!).Port, "dev", SshAuth.Agent)
            with { SshOptions = ["ConnectTimeout=1", "ConnectionAttempts=1", "BatchMode=yes"] };
        var store = new MachineStore(new HarnessHome(_home));
        store.Upsert(machine);
        var original = File.ReadAllText(store.FilePath);
        var viewModel = new MachinesViewModel(store, new FleetOptions());
        var changes = 0;
        viewModel.MachinesChanged += () => changes++;
        viewModel.Load("box");
        using var cancellation = TestContext.Current.CancellationToken.Register(() => viewModel.CancelOperationCommand.Execute(null));
        viewModel.Editor!.User = "unsaved-user";
        await viewModel.EnableIrohCommand.ExecuteAsync(null);

        Assert.True(viewModel.StatusIsError, viewModel.Status);
        Assert.Equal(original, File.ReadAllText(store.FilePath));
        Assert.Equal(0, changes);
        Assert.True(viewModel.Editor.IsSsh);
        Assert.Equal("unsaved-user", viewModel.Editor.User);
        Assert.False(viewModel.Busy);
    }

    [Theory(Timeout = 5000)]
    [InlineData("local")]
    [InlineData("box/child")]
    [InlineData("taken")]
    public async Task InvalidBootstrapName_CannotReachTheTargetOrChangeSavedConfiguration(string name)
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var machine = MachineSpec.Ssh("box", "127.0.0.1", ((System.Net.IPEndPoint)listener.LocalEndpoint).Port, "dev", SshAuth.Agent)
            with { SshOptions = ["ConnectTimeout=1", "ConnectionAttempts=1", "BatchMode=yes"] };
        var store = new MachineStore(new HarnessHome(_home));
        store.Save(new FleetConfig([machine, MachineSpec.Ssh("taken", "other", 22, "dev", SshAuth.Agent)], new DeploySettings()));
        var original = await File.ReadAllBytesAsync(store.FilePath, TestContext.Current.CancellationToken);
        var viewModel = new MachinesViewModel(store, new FleetOptions());
        viewModel.Load("box");
        viewModel.Editor!.Name = name;
        await viewModel.EnableIrohCommand.ExecuteAsync(null);
        Assert.True(viewModel.StatusIsError, viewModel.Status);
        Assert.False(listener.Pending());
        Assert.Equal(original, await File.ReadAllBytesAsync(store.FilePath, TestContext.Current.CancellationToken));
        Assert.True(viewModel.Editor.IsSsh);
    }

    [Fact(Timeout = 5000)]
    public async Task TestConnection_ShowsHostVersionAndPlatform_AndDoctorListsTheChecks()
    {
        await using var host = await HarnessHostFixture.CreateAsync("mvt", TestContext.Current.CancellationToken);
        // The fixture's listener starts in the background; a probe connection (which retries) waits until it accepts, so the single test attempt below meets a ready host.
        await host.ConnectAsync("probe", TestContext.Current.CancellationToken);
        var viewModel = new MachinesViewModel(new MachineStore(new HarnessHome(_home)), FleetConnectionTests.TestOptions);
        viewModel.Load();
        viewModel.NewLocalCommand.Execute(null);
        (viewModel.Editor!.Name, viewModel.Editor.HarnessHome) = ("alpha", host.Root);
        await viewModel.SaveCommand.ExecuteAsync(null);

        await viewModel.TestConnectionCommand.ExecuteAsync(null);
        var row = viewModel.Machines.Single(machine => machine.Name == "alpha");
        var tested = (row.StateText, row.HostVersion, row.Platform is { Length: > 0 }, viewModel.StatusIsError ? viewModel.Status : null);
        await viewModel.RunDoctorCommand.ExecuteAsync(null);
        var checks = viewModel.DoctorChecks.ToDictionary(check => check.Name, check => check.Outcome);

        viewModel.NewLocalCommand.Execute(null);
        (viewModel.Editor!.Name, viewModel.Editor.HarnessHome) = ("nowhere", Path.Combine(_home, "no-host"));
        await viewModel.TestConnectionCommand.ExecuteAsync(null);

        Assert.Equal(("已连接", HostProtocol.ReleaseVersion, true, (string?)null), tested);
        Assert.Contains(viewModel.OperationLog, line => line.StartsWith($"alpha: host {HostProtocol.ReleaseVersion}", StringComparison.Ordinal));
        Assert.Equal(DoctorOutcome.Ok, checks["platform"]);
        Assert.Equal(DoctorOutcome.Ok, checks["connection"]);
        Assert.True(viewModel.StatusIsError, viewModel.Status);
        Assert.Contains("nowhere", viewModel.Status, StringComparison.Ordinal);
    }

    private static void Fill(MachineEditorViewModel editor, string name, string auth)
    {
        (editor.Name, editor.Host, editor.Port, editor.User, editor.Auth) = (name, $"{name}.internal", "2222", "dev", auth);
        (editor.KeyPath, editor.Password, editor.Proxy) = ("~/.ssh/id_test", "secret", "ssh -W %h:%p jump");
        (editor.RemotePath, editor.HarnessHome, editor.SshOptions) = ("/srv/work", "~/.dsh-build", "ConnectTimeout=5\r\n\r\n ServerAliveInterval=10 \n");
    }
}
