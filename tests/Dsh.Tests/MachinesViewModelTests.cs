using Dsh.Boot;
using Dsh.Fleet;
using Dsh.Fleet.Iroh;
using Dsh.Gui.ViewModels;
using Dsh.RemoteHost;

namespace Dsh.Tests;

/** 机器管理页的视图模型: 经真实 MachineStore(settings.yaml 的 fleet 段)增删改机器与部署设置, 对真实 host 测试连接与诊断。 */
public sealed class MachinesViewModelTests : IDisposable
{
    private readonly string _home = TempTree.CreateDirectory("machines-vm");

    public void Dispose() => TempTree.Delete(_home);

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

    [Fact(Timeout = 30000)]
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

    [Theory(Timeout = 10000)]
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

    [Fact(Timeout = 60000)]
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

        Assert.Equal(("已连接", RemoteHostServer.ReleaseVersion, true, (string?)null), tested);
        Assert.Contains(viewModel.OperationLog, line => line.StartsWith($"alpha: host {RemoteHostServer.ReleaseVersion}", StringComparison.Ordinal));
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
