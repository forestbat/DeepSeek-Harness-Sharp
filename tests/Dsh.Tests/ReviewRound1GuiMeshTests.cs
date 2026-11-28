using Avalonia.Media;
using Avalonia.Threading;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Fleet;
using Dsh.Gui.Services;
using Dsh.Gui.ViewModels;
using Dsh.RemoteHost;
using Dsh.Workspace;

namespace Dsh.Tests;

[Collection(GuiSerialCollection.CollectionName)]
public sealed class ReviewRound1GuiMeshTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WorkspaceViewModel_ReopeningMeshPageAfterNavigation_CreatesFreshActiveViewModelAndRefreshes()
        => await HeadlessGui.RunAsync(async () =>
        {
            var directory = TempTree.CreateDirectory("r1-f26");
            try
            {
                var harness = MeshHostAndGuiTests.MeshTestHarness.Create();
                await using var host = await MeshHostAndGuiTests.MeshServerFixture.StartAsync(harness.Ctx, token: null, Ct);
                await using var client = await host.ConnectAsync("mesh-f26", cancellationToken: Ct);

                var workspaceClient = new StubWorkspaceClient([new StubWorkspaceMachine(MachineSpec.Local(directory), client)]);
                var services = new StubWorkspaceServices(new HarnessHome(directory), () => workspaceClient);
                await using var fleet = new FleetWorkspace(services);
                await using var workspace = new WorkspaceViewModel(fleet, (_, _, _) => { });
                var icon = Geometry.Parse("M0,0 L10,10");
                workspace.RegisterHostPages(icon, icon, icon);
                workspace.RegisterMeshPage(icon);

                var meshEntry = Assert.Single(workspace.Pages.Available, page => page.Id == WorkspacePages.Mesh);
                var machinesEntry = Assert.Single(workspace.Pages.Available, page => page.Id == WorkspacePages.Machines);

                workspace.OpenPage(meshEntry);
                var first = Assert.IsType<AgentMeshViewModel>(workspace.PageContent);
                await first.RefreshCommand.ExecuteAsync(null);
                Assert.True(first.IsConnected);
                first.NewAgentName = "agent-before-nav";
                await first.CreateAgentCommand.ExecuteAsync(null);
                Assert.Single(first.Agents);

                workspace.ShowDashboard();
                Assert.Null(workspace.PageContent);

                workspace.OpenPage(meshEntry);
                var second = Assert.IsType<AgentMeshViewModel>(workspace.PageContent);
                Assert.NotSame(first, second);
                await second.RefreshCommand.ExecuteAsync(null);
                Assert.True(second.IsConnected);
                Assert.Single(second.Agents);
                Assert.Equal("agent-before-nav", second.Agents[0].Name);

                workspace.OpenPage(machinesEntry);
                Assert.IsType<MachinesPageViewModel>(workspace.PageContent);

                workspace.OpenPage(meshEntry);
                var third = Assert.IsType<AgentMeshViewModel>(workspace.PageContent);
                Assert.NotSame(second, third);
                await third.RefreshCommand.ExecuteAsync(null);
                third.NewAgentName = "agent-after-nav";
                await third.CreateAgentCommand.ExecuteAsync(null);
                Assert.Equal(2, third.Agents.Count);
            }
            finally
            {
                TempTree.Delete(directory);
            }
        });

    [Fact]
    public async Task AgentMeshViewModel_FollowsFleetClientAcrossReload_AndResumesWatch()
        => await HeadlessGui.RunAsync(async () =>
        {
            var directory = TempTree.CreateDirectory("r1-f27");
            try
            {
                var harness1 = MeshHostAndGuiTests.MeshTestHarness.Create();
                await using var host1 = await MeshHostAndGuiTests.MeshServerFixture.StartAsync(harness1.Ctx, token: null, Ct);
                await using var client1 = await host1.ConnectAsync("mesh-f27-1", cancellationToken: Ct);
                await client1.GetCapability<IWorkspaceMesh>()!.CreateAgentAsync(new AgentProfileRegistration("alpha-agent"), Ct);

                var harness2 = MeshHostAndGuiTests.MeshTestHarness.Create();
                await using var host2 = await MeshHostAndGuiTests.MeshServerFixture.StartAsync(harness2.Ctx, token: null, Ct);
                await using var client2 = await host2.ConnectAsync("mesh-f27-2", cancellationToken: Ct);
                await using var observer2 = await host2.ConnectAsync("mesh-f27-2-observer", cancellationToken: Ct);
                await client2.GetCapability<IWorkspaceMesh>()!.CreateAgentAsync(new AgentProfileRegistration("beta-agent"), Ct);

                var generation = 0;
                var services = new StubWorkspaceServices(new HarnessHome(directory), () =>
                {
                    generation++;
                    if (generation == 1)
                        return new StubWorkspaceClient([new StubWorkspaceMachine(MachineSpec.Local(directory), client1)]);
                    var remoteSpec = new MachineSpec("remote-b", MachineKinds.Ssh, "127.0.0.1", User: "user");
                    return new StubWorkspaceClient(
                    [
                        new StubWorkspaceMachine(MachineSpec.Local(directory), client2),
                        new StubWorkspaceMachine(remoteSpec, null),
                    ]);
                });

                await using var fleet = new FleetWorkspace(services);
                using var model = new AgentMeshViewModel(fleet);
                await model.RefreshCommand.ExecuteAsync(null);
                Assert.True(model.IsConnected);
                Assert.Contains(model.Agents, agent => agent.Name == "alpha-agent");
                Assert.DoesNotContain("remote-b", model.MachineNames);

                await fleet.ReloadAsync();
                await PumpUntilAsync(() =>
                    model.MachineNames.Contains("remote-b")
                    && model.Agents.Any(agent => agent.Name == "beta-agent"));

                Assert.DoesNotContain(model.Agents, agent => agent.Name == "alpha-agent");

                await observer2.GetCapability<IWorkspaceMesh>()!.CreateAgentAsync(new AgentProfileRegistration("gamma-pushed"), Ct);
                await PumpUntilAsync(() => model.Agents.Any(agent => agent.Name == "gamma-pushed"));
            }
            finally
            {
                TempTree.Delete(directory);
            }
        });

    [Fact]
    public async Task RemoteMeshClient_BroadcastsToConcurrentReaders_AndBoundsIdleAndSlowBuffering()
    {
        var harness = MeshHostAndGuiTests.MeshTestHarness.Create();
        await using var host = await MeshHostAndGuiTests.MeshServerFixture.StartAsync(harness.Ctx, token: null, Ct);
        await using var client = await host.ConnectAsync("mesh-f28-readers", cancellationToken: Ct);
        var mesh = client.GetCapability<IWorkspaceMesh>()!;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await using var reader1 = mesh.MeshChangesAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        await using var reader2 = mesh.MeshChangesAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        Assert.True(await reader1.MoveNextAsync());
        Assert.Equal("snapshot", reader1.Current.Area);
        Assert.True(await reader2.MoveNextAsync());
        Assert.Equal("snapshot", reader2.Current.Area);

        await mesh.CreateAgentAsync(new AgentProfileRegistration("broadcast-agent"), Ct);

        var seen1 = await ReadUntilAgentAsync(reader1, "broadcast-agent");
        var seen2 = await ReadUntilAgentAsync(reader2, "broadcast-agent");
        Assert.True(seen1);
        Assert.True(seen2);

        // Idle client with no active reader retains only the latest snapshot (O(1)).
        await using var idleClient = await host.ConnectAsync("mesh-f28-idle", cancellationToken: Ct);
        var idleMesh = idleClient.GetCapability<IWorkspaceMesh>()!;
        var baseAgents = await mesh.ListAgentsAsync(Ct);
        for (var i = 0; i < 25; i++)
        {
            var snapshot = new RemoteMeshSnapshot(
                [.. baseAgents, new AgentProfile(AgentId.Create($"idle-{i}"), $"idle-{i}", null, AgentBackendKind.Local, null, [], AgentLifecycleState.Suspended, null, null, null, null, null, i, i)],
                [], [], [], [], [], []);
            await idleClient.MeshChangedAsync(new RemoteMeshChange("agents", snapshot, $"idle-{i}"), Ct);
        }

        await using var idleReader = idleMesh.MeshChangesAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        Assert.True(await idleReader.MoveNextAsync());
        Assert.Equal("idle-24", idleReader.Current.EntityId);

        var nextSnapshot = new RemoteMeshSnapshot(
            [.. baseAgents, new AgentProfile(AgentId.Create("idle-next"), "idle-next", null, AgentBackendKind.Local, null, [], AgentLifecycleState.Suspended, null, null, null, null, null, 26, 26)],
            [], [], [], [], [], []);
        await idleClient.MeshChangedAsync(new RemoteMeshChange("agents", nextSnapshot, "idle-next"), Ct);
        Assert.True(await idleReader.MoveNextAsync());
        Assert.Equal("idle-next", idleReader.Current.EntityId);

        // Slow active reader uses a bounded 16-item DropOldest channel rather than unbounded buffering.
        await using var slowClient = await host.ConnectAsync("mesh-f28-slow", cancellationToken: Ct);
        var slowMesh = slowClient.GetCapability<IWorkspaceMesh>()!;
        await using var slowReader = slowMesh.MeshChangesAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        Assert.True(await slowReader.MoveNextAsync());

        for (var i = 0; i < 25; i++)
        {
            var snapshot = new RemoteMeshSnapshot(
                [.. baseAgents, new AgentProfile(AgentId.Create($"slow-{i}"), $"slow-{i}", null, AgentBackendKind.Local, null, [], AgentLifecycleState.Suspended, null, null, null, null, null, i, i)],
                [], [], [], [], [], []);
            await slowClient.MeshChangedAsync(new RemoteMeshChange("agents", snapshot, $"slow-{i}"), Ct);
        }

        List<string?> receivedIds = [];
        while (await slowReader.MoveNextAsync())
        {
            receivedIds.Add(slowReader.Current.EntityId);
            if (slowReader.Current.EntityId == "slow-24")
                break;
        }
        Assert.Equal(16, receivedIds.Count);
        Assert.Equal("slow-9", receivedIds[0]);
        Assert.Equal("slow-24", receivedIds[^1]);

        await mesh.CreateAgentAsync(new AgentProfileRegistration("server-pushed"), Ct);
        Assert.True(await ReadUntilAgentAsync(slowReader, "server-pushed"));
    }

    private static async Task<bool> ReadUntilAgentAsync(IAsyncEnumerator<RemoteMeshChange> reader, string agentName)
    {
        while (await reader.MoveNextAsync())
        {
            if (reader.Current.Snapshot?.Agents.Any(agent => agent.Name == agentName) == true)
                return true;
        }
        return false;
    }

    private static async Task PumpUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                Assert.Fail("Timed out waiting for Mesh view model state.");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20, Ct);
        }
    }

    private sealed class StubWorkspaceServices : IWorkspaceServices
    {
        private readonly Func<IWorkspaceClient> _factory;
        private readonly FleetWorkspaceServices _inner;

        public StubWorkspaceServices(HarnessHome home, Func<IWorkspaceClient> factory)
        {
            _factory = factory;
            var store = new MachineStore(home);
            _inner = new FleetWorkspaceServices(store, new FleetOptions { LocalHome = home, AutoReconnect = false, StartLocalHost = null });
        }

        public HarnessHome? LocalHome => _inner.LocalHome;
        public IMachineCatalog Store => _inner.Store;
        public IMachineAdministration Administration => _inner.Administration;
        public IWorkspaceClient CreateClient() => _factory();
        public IWorkspaceProxyProbe CreateProxyProbe() => _inner.CreateProxyProbe();
        public Task SelectBorrowedOutboundAsync(string? machine, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<ProxyProbeResult> ProbeBorrowedOutboundAsync(string machine, Guid operationId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class StubWorkspaceClient(IReadOnlyList<IWorkspaceMachine> machines) : IWorkspaceClient
    {
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WhenDisposed => _disposed.Task;
        public event Action? Changed { add { } remove { } }
        public IReadOnlyList<IWorkspaceMachine> Machines => machines;
        public IWorkspaceMachine? Find(string name) => machines.FirstOrDefault(m => string.Equals(m.Machine.Name, name, StringComparison.Ordinal));
        public void Start() { }
        public Task WaitSettledAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public IReadOnlyList<FleetMachineStatus> Statuses() => [];
        public IReadOnlyList<FleetSession> Sessions() => [];
        public IReadOnlyList<FleetMachineGroup> Groups() => [];
        public IReadOnlyList<FleetPendingItem> Pending() => [];

        public async ValueTask DisposeAsync()
        {
            foreach (var machine in machines)
                await machine.DisposeAsync();
            _disposed.TrySetResult();
        }
    }

    private sealed class StubWorkspaceMachine(MachineSpec machine, RemoteHostClient? client) : IWorkspaceMachine
    {
        private readonly CancellationTokenSource _lifetime = new();

        public MachineSpec Machine => machine;
        public MachineConnectionState State => client is null ? MachineConnectionState.Offline : MachineConnectionState.Connected;
        public string? Detail => null;
        public HostInfo? Info => null;
        public HostHelloResponse? Hello => client?.Hello;
        public IReadOnlyList<RemoteSessionSummary> Sessions => [];
        public RemotePendingList Pending => new([], []);
        public RemoteNotifyPage? Notifications => null;
        public int UnreadNotifications => 0;
        public IWorkspaceMachineNetwork Network => throw new NotSupportedException();

        public event Action<IWorkspaceMachine>? StateChanged { add { } remove { } }
        public event Action<IWorkspaceMachine>? SessionsChanged { add { } remove { } }
        public event Action<IWorkspaceMachine>? PendingChanged { add { } remove { } }
        public event Action<IWorkspaceMachine, RemoteNotificationChange?>? NotificationsChanged { add { } remove { } }

        public void Start() { }
        public Task WaitSettledAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<WorkspaceHostLease> WaitConnectedAsync(CancellationToken cancellationToken)
            => client is null
                ? throw new InvalidOperationException("offline")
                : Task.FromResult(new WorkspaceHostLease(client, _lifetime.Token, client.WhenClosed));
        public IAsyncEnumerable<RemoteSessionEvent> SubscribeAsync(string sessionId, long fromSeq, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task AttachPtyAsync(string ptyId, Stream input, Stream output, CancellationToken cancellationToken,
            Func<IWorkspaceHost, CancellationToken, Task>? prepare = null)
            => throw new NotSupportedException();

        public async ValueTask DisposeAsync()
        {
            await _lifetime.CancelAsync();
            _lifetime.Dispose();
            if (client is not null)
                await client.DisposeAsync();
        }
    }
}
