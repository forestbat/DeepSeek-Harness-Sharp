using Dsh.Boot;
using Dsh.Core;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Dsh.RemoteHost;
using Dsh.Fleet;
using Dsh.Runtime;
using Dsh.Interaction;
using Dsh.Transport;

namespace Dsh.Tests;

/// <summary>Real HTTP consumers, SSH streams and settings writes exercise the shared route's ownership boundary.</summary>
[Collection(SerialProcessCollection.Name)]
public sealed class BorrowedOutboundProxyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Probe = "http://example.com/route-validation";
    private const string InvalidBaselineProxyUrl = "gopher://invalid.example:70";
    private static readonly TimeSpan CliObservationTimeout = TimeSpan.FromSeconds(5);

    [Fact(Explicit = true)]
    public async Task CancellingTheInitialLease_DoesNotLetOwnerDisposalClearAnUnappliedSavedSelection()
    {
        await using var baseline = new TestForwardProxy();
        var root = TempTree.CreateSocketDirectory("b80i");
        try
        {
            var home = new HarnessHome(root);
            using var proxy = new OutboundProxy();
            HarnessSettings.SaveOutboundProxy(home, new OutboundProxySettings { Url = $"http://127.0.0.1:{baseline.Port}" }, proxy);
            HarnessSettings.SaveBorrowedOutboundMachine(home, "missing");
            proxy.SuspendBorrowed();
            await using var owner = new BorrowedOutboundRoute(home, proxy);
            using var canceled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await owner.AcquireAsync(canceled.Token));
            await owner.DisposeAsync();
            using var consumer = new HttpClient(proxy.CreateHandler());
            Assert.NotNull(await Record.ExceptionAsync(() => consumer.GetStringAsync("http://example.com/unapplied-selection", Ct)));
            Assert.Equal("missing", HarnessSettings.Load(home).Network?.BorrowedOutboundMachine);
            Assert.Empty(baseline.Requests);
            await using var replacement = new BorrowedOutboundRoute(home, proxy);
            await replacement.SelectAsync(null, Ct);
            Assert.Equal(TestForwardProxy.ProxiedBody, await consumer.GetStringAsync("http://example.com/explicit-direct", Ct));
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Fact(Explicit = true)]
    public async Task RetiredSshGeneration_ReleasesItsMachineCredentials_WhileOldConsumersRemainReachable()
    {
        FleetSshEndToEndTests.RequireEnvironment();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        await using var remote = new TestForwardProxy(requiredCredential: "retired-proxy-user:retired-proxy-secret");
        var root = TempTree.CreateSocketDirectory("b80c");
        try
        {
            var (tunnel, credentials) = CreateCredentialOwner(ssh, remote, root);
            await using (tunnel)
            {
                using var proxy = new OutboundProxy();
                Stream? retiredStream = null;
                async ValueTask<Stream> Connect(DnsEndPoint endpoint, CancellationToken token)
                {
                    var connected = await tunnel.ConnectAsync(endpoint, token);
                    retiredStream = connected;
                    return connected;
                }
                proxy.ConfigureBorrowed(tunnel.ProxyUri.OriginalString, Connect);
                var retiredSnapshot = proxy.CaptureRoute(new Uri("http://example.com/retired-credentials"));
                using var consumer = new HttpClient(proxy.CreateHandler());
                Assert.Equal(TestForwardProxy.ProxiedBody, await consumer.GetStringAsync("http://example.com/retired-credentials", Ct));
                Assert.NotNull(retiredStream);
                Assert.True(credentials.IsAlive, "The live SSH owner must retain its selected machine.");
                proxy.SuspendBorrowed();
                await tunnel.DisposeAsync();
                await AssertOwnedSshRetiredAsync(root, remote.Port);
                Assert.NotNull(await Record.ExceptionAsync(() => consumer.GetStringAsync("http://example.com/after-retirement", Ct)));
                await Task.Yield();
                for (var attempt = 0; attempt < 10 && credentials.IsAlive; attempt++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }
                Assert.False(credentials.IsAlive, "A retired SSH generation must release its machine credentials even while old consumers remain reachable.");
                GC.KeepAlive(retiredSnapshot);
                GC.KeepAlive(consumer);
                GC.KeepAlive(retiredStream);
                GC.KeepAlive(tunnel);
            }
        }
        finally
        {
            TempTree.Delete(root);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static (SshOutboundTunnel Tunnel, WeakReference Credentials) CreateCredentialOwner(TestSshServer ssh, TestForwardProxy remote, string root)
        {
            var machine = ObserveSsh(ssh.Machine("retired-credential-owner", root), root) with
            {
                OutboundTunnel = new("proxyCommand", $"http://retired-proxy-user:retired-proxy-secret@127.0.0.1:{remote.Port}"),
            };
            return (new SshOutboundTunnel(machine), new WeakReference(machine));
        }
    }

    [Fact(Explicit = true)]
    public async Task DirectOnlyOwnerDisposal_RetiresItsOldPool_WithoutDisablingTheIndependentBaseline()
    {
        await using var baseline = new TestForwardProxy(keepAlive: true, pauseResponses: true);
        var root = TempTree.CreateSocketDirectory("b80e");
        try
        {
            var home = new HarnessHome(root);
            using var proxy = new OutboundProxy();
            HarnessSettings.SaveOutboundProxy(home, new OutboundProxySettings { Url = $"http://127.0.0.1:{baseline.Port}" }, proxy);
            await using var route = new BorrowedOutboundRoute(home, proxy);
            using var consumer = new HttpClient(proxy.CreateHandler());
            var initial = consumer.GetStringAsync("http://example.com/before-owner-unload", TestContext.Current.CancellationToken);
            Assert.Equal("GET http://example.com/before-owner-unload HTTP/1.1", await baseline.WaitForRequestAsync(Ct));
            await using var lease = await route.AcquireAsync(Ct);
            baseline.ReleaseResponse();
            Assert.Equal(TestForwardProxy.ProxiedBody, await initial);
            await route.DisposeAsync();
            await FleetConnectionTests.WaitForAsync(() => baseline.ActiveConnections == 0);
            var independent = consumer.GetStringAsync("http://example.com/after-owner-unload", Ct);
            Assert.Equal("GET http://example.com/after-owner-unload HTTP/1.1", await baseline.WaitForRequestAsync(Ct));
            baseline.ReleaseResponse();
            Assert.Equal(TestForwardProxy.ProxiedBody, await independent);
            Assert.Equal(2, baseline.Connections);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Theory(Explicit = true)]
    [InlineData("missing", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("missing", true)]
    public async Task DisabledFleet_RejectsTheSavedBorrowedRoute_BeforeAnyConsumerCanUseTheBaseline(string selected, bool invalidBaseline)
    {
        await using var baseline = new TestForwardProxy();
        var root = TempTree.CreateSocketDirectory("b80b");
        try
        {
            var home = new HarnessHome(root);
            var settings = new HarnessSettings
            {
                Network = new NetworkSettings
                {
                    BorrowedOutboundMachine = selected,
                    OutboundProxy = new OutboundProxySettings { Url = invalidBaseline ? InvalidBaselineProxyUrl : $"http://127.0.0.1:{baseline.Port}" },
                },
            };
            settings.Plugins["@deepseek-ai/dsh-fleet"] = new PluginSetting { Enabled = false };
            settings.Save(home);
            settings.SavePlugins(home);
            using var app = await ConfigBoot.Compose(new HarnessOptions(home, Cwd: root));
            using var consumer = new HttpClient(OutboundProxy.FromContext(app.Ctx).CreateHandler());
            Assert.NotNull(await Record.ExceptionAsync(() => consumer.GetStringAsync("http://example.com/disabled-fleet", Ct)));
            var agent = (await app.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!.Create(new CreateAgentOptions(Cwd: root), TestContext.Current.CancellationToken)).Agent;
            var commands = app.Ctx.Get<CommandsService>(CommandsService.ServiceName)!;
            Assert.IsType<CommandResult.Error>((await commands.Execute(agent, "/proxy reload", Ct))!.Result);
            Assert.Empty(baseline.Requests);
            if (invalidBaseline)
                HarnessSettings.SaveOutboundProxy(home, new OutboundProxySettings { Url = $"http://127.0.0.1:{baseline.Port}" }, OutboundProxy.FromContext(app.Ctx));
            HarnessSettings.SaveBorrowedOutboundMachine(home, null);
            Assert.IsType<CommandResult.Success>((await commands.Execute(agent, "/proxy reload", Ct))!.Result);
            Assert.Equal(TestForwardProxy.ProxiedBody, await consumer.GetStringAsync("http://example.com/explicit-file-direct", Ct));
            Assert.Equal(["GET http://example.com/explicit-file-direct HTTP/1.1"], baseline.Requests);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Fact(Explicit = true)]
    public async Task DirectOnlyStartup_WithAnInvalidBaseline_KeepsFleetAndHttpConsumersAvailable()
    {
        await using var endpoint = new TestForwardProxy();
        var root = TempTree.CreateSocketDirectory("b80d");
        try
        {
            var home = new HarnessHome(root);
            var settings = new HarnessSettings
            {
                Network = new NetworkSettings { OutboundProxy = new OutboundProxySettings { Url = InvalidBaselineProxyUrl } },
            };
            settings.Plugins["@deepseek-ai/dsh-fleet"] = new PluginSetting { Enabled = true };
            settings.Save(home);
            settings.SavePlugins(home);
            using var app = await ConfigBoot.Compose(new HarnessOptions(home, Cwd: root));
            var fleet = Assert.IsType<FleetService>(app.Ctx.Get<FleetService>(FleetService.ServiceName));
            await using var frontend = fleet.CreateClient(new FleetOptions());
            using var consumer = new HttpClient(fleet.OutboundRoute.Proxy.CreateHandler());
            Assert.Equal(TestForwardProxy.ProxiedBody, await consumer.GetStringAsync($"http://127.0.0.1:{endpoint.Port}/startup", TestContext.Current.CancellationToken));
            Assert.Equal(InvalidBaselineProxyUrl, HarnessSettings.Load(home).Network?.OutboundProxy?.Url);
            Assert.Null(HarnessSettings.Load(home).Network?.BorrowedOutboundMachine);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Fact(Explicit = true)]
    public async Task DirectOnlyReload_WithAnInvalidBaseline_PreservesTheExistingConsumerAndSavedError()
    {
        await using var baseline = new TestForwardProxy(keepAlive: true);
        var root = TempTree.CreateSocketDirectory("b80r");
        try
        {
            var home = new HarnessHome(root);
            using var proxy = new OutboundProxy();
            HarnessSettings.SaveOutboundProxy(home, new OutboundProxySettings { Url = $"http://127.0.0.1:{baseline.Port}" }, proxy);
            await using var route = new BorrowedOutboundRoute(home, proxy);
            await using var lease = await route.AcquireAsync(TestContext.Current.CancellationToken);
            using var consumer = new HttpClient(proxy.CreateHandler());
            Assert.Equal(TestForwardProxy.ProxiedBody, await consumer.GetStringAsync("http://example.com/before-invalid-baseline", Ct));
            new HarnessSettings
            {
                Network = new NetworkSettings { OutboundProxy = new OutboundProxySettings { Url = InvalidBaselineProxyUrl } },
            }.Save(home);
            await Assert.ThrowsAsync<ArgumentException>(() => route.ReloadAsync(Ct));
            Assert.Equal(TestForwardProxy.ProxiedBody, await consumer.GetStringAsync("http://example.com/after-invalid-baseline", Ct));
            Assert.Equal(InvalidBaselineProxyUrl, HarnessSettings.Load(home).Network?.OutboundProxy?.Url);
            Assert.Null(HarnessSettings.Load(home).Network?.BorrowedOutboundMachine);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Fact(Explicit = true)]
    public async Task PluginOwnerDisposal_CancelsItsPendingDraft_AndAwaitsAllClientRouteLeases()
    {
        FleetSshEndToEndTests.RequireEnvironment();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        await using var first = new TestForwardProxy(keepAlive: true);
        await using var draft = new TestForwardProxy(pauseResponses: true);
        var root = TempTree.CreateSocketDirectory("b80u");
        try
        {
            var home = Configure(root, ssh, first, "A");
            new MachineStore(home).Upsert(ssh.Machine("B", root) with { OutboundTunnel = new("proxyCommand", $"http://127.0.0.1:{draft.Port}") });
            using var fleet = new FleetService(home);
            await using var firstClient = fleet.CreateClient(new FleetOptions());
            await using var secondClient = fleet.CreateClient(new FleetOptions());
            using var consumer = new HttpClient(fleet.OutboundRoute.Proxy.CreateHandler());
            Assert.Equal(TestForwardProxy.ProxiedBody, await consumer.GetStringAsync("http://example.com/before-unload", Ct));
            var saved = await File.ReadAllTextAsync(home.SettingsFile, Ct);
            var selecting = fleet.OutboundRoute.SelectAsync("B", Ct);
            Assert.Equal($"GET {Probe} HTTP/1.1", await draft.WaitForRequestAsync(Ct));
            await Task.Run(fleet.Dispose, Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await selecting);
            draft.ReleaseResponse();
            await FleetConnectionTests.WaitForAsync(() => first.ActiveConnections == 0 && draft.ActiveConnections == 0);
            fleet.Dispose();
            Assert.True(saved == await File.ReadAllTextAsync(home.SettingsFile, Ct), "Unloading must cancel, not save, an unfinished selection.");
            Assert.NotNull(await Record.ExceptionAsync(() => consumer.GetStringAsync("http://example.com/after-unload", Ct)));
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Theory(Explicit = true)]
    [InlineData(DownloadVia.Client)]
    [InlineData(DownloadVia.Auto)]
    public async Task LocalArchiveDownload_RejectsAnInvalidBorrowedSelection_WithoutUsingTheBaseline(string via)
    {
        FleetSshEndToEndTests.RequireEnvironment();
        if (via == DownloadVia.Auto && !File.Exists("/usr/bin/curl") && !File.Exists("/usr/bin/wget"))
            Assert.Skip("automatic remote-download coverage needs curl or wget on the loopback SSH server");
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        await using var baseline = new TestForwardProxy();
        var root = TempTree.CreateSocketDirectory("b80d");
        using var releases = new TcpListener(IPAddress.Loopback, 0);
        releases.Start();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        try
        {
            var home = new HarnessHome(root);
            using var proxy = new OutboundProxy();
            HarnessSettings.SaveOutboundProxy(home, new OutboundProxySettings { Url = $"http://127.0.0.1:{baseline.Port}" }, proxy);
            HarnessSettings.SaveBorrowedOutboundMachine(home, "missing");
            var before = await File.ReadAllTextAsync(home.SettingsFile, Ct);
            var serving = via == DownloadVia.Auto
                ? FleetSshEndToEndTests.ServeReleaseAsync(releases, null, cancellation.Token)
                : Task.CompletedTask;
            var port = ((IPEndPoint)releases.LocalEndpoint).Port;
            var progress = new List<string>();
            var installer = new RemoteDsharpInstaller(new SshTransport(ssh.Machine("download-target", root)));
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => installer.EnsureAsync(
                new DeploySettings { Via = via, ReleaseBaseUrl = $"http://127.0.0.1:{port}/releases" },
                progress.Add, Ct, localHome: home));
            Assert.Equal("The selected outbound machine does not exist.", failure.Message);
            await serving;
            Assert.Equal(via == DownloadVia.Auto, progress.Any(message => message.Contains("改由本地下发", StringComparison.Ordinal)));
            Assert.False(releases.Pending(), "A rejected local archive route must never make a direct HTTP request.");
            Assert.Empty(baseline.Requests);
            Assert.Equal(before, await File.ReadAllTextAsync(home.SettingsFile, Ct));
            Assert.Null((await installer.InspectAsync(Ct)).CommandPath);
            Assert.False(File.Exists(Path.Combine(ssh.Home, ".dsharp", RuntimeInformation.RuntimeIdentifier, ".dsharp-version")));
        }
        finally
        {
            cancellation.Cancel();
            TempTree.Delete(root);
        }
    }

    [Fact(Explicit = true)]
    public async Task CancellingClientDownload_ReleasesOnlyItsShortRoute_WhileAnExistingConsumerKeepsItsConnection()
    {
        FleetSshEndToEndTests.RequireEnvironment();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        await using var forwarding = new TestForwardProxy(keepAlive: true, pauseResponses: true);
        var root = TempTree.CreateSocketDirectory("b80q");
        try
        {
            var home = Configure(root, ssh, forwarding, "A");
            using var proxy = new OutboundProxy();
            await using var route = new BorrowedOutboundRoute(home, proxy);
            await using var lease = await route.AcquireAsync(Ct);
            using var consumer = new HttpClient(proxy.CreateHandler());
            var warm = consumer.GetStringAsync("http://example.com/existing-consumer", Ct);
            Assert.Equal("GET http://example.com/existing-consumer HTTP/1.1", await forwarding.WaitForRequestAsync(Ct));
            forwarding.ReleaseResponse();
            Assert.Equal(TestForwardProxy.ProxiedBody, await warm);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            var progress = new List<string>();
            var download = new RemoteDsharpInstaller(new SshTransport(ssh.Machine("download-target", root))).EnsureAsync(
                new DeploySettings { Via = DownloadVia.Client, ReleaseBaseUrl = "http://releases.example.test/releases" },
                progress.Add, cancellation.Token, force: true, localHome: home);
            var request = await forwarding.WaitForRequestAsync(Ct);
            Assert.StartsWith("GET http://releases.example.test/releases/latest/download/", request, StringComparison.Ordinal);
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await download);
            forwarding.ReleaseResponse();
            await FleetConnectionTests.WaitForAsync(() => forwarding.ActiveConnections == 1);
            var surviving = consumer.GetStringAsync("http://example.com/survives-download-cancel", Ct);
            Assert.Equal("GET http://example.com/survives-download-cancel HTTP/1.1", await forwarding.WaitForRequestAsync(Ct));
            forwarding.ReleaseResponse();
            Assert.Equal(TestForwardProxy.ProxiedBody, await surviving);
            Assert.Equal(2, forwarding.Connections);
            Assert.Equal("A", HarnessSettings.Load(home).Network?.BorrowedOutboundMachine);
            Assert.DoesNotContain(progress, value => value.Contains("上传", StringComparison.Ordinal));
            Assert.False(Directory.Exists(Path.Combine(ssh.Home, ".dsharp")), "Cancellation before archive headers must never upload or deploy a stand-in archive.");
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Theory(Explicit = true)]
    [InlineData("missing")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task InvalidReloadFromDirect_RemainsUnavailableAfterLastReleaseAndBaselineSave_UntilExplicitDirect(string invalidSelection)
    {
        await using var baseline = new TestForwardProxy(keepAlive: true);
        await using var replacement = new TestForwardProxy(keepAlive: true);
        var root = TempTree.CreateSocketDirectory("b80x");
        try
        {
            var home = new HarnessHome(root);
            using var proxy = new OutboundProxy();
            HarnessSettings.SaveOutboundProxy(home, new OutboundProxySettings { Url = $"http://127.0.0.1:{baseline.Port}" }, proxy);
            await using var route = new BorrowedOutboundRoute(home, proxy);
            var lease = await route.AcquireAsync(TestContext.Current.CancellationToken);
            using var consumer = new HttpClient(proxy.CreateHandler());
            Assert.Equal(TestForwardProxy.ProxiedBody, await consumer.GetStringAsync("http://example.com/original-direct", Ct));
            HarnessSettings.SaveBorrowedOutboundMachine(home, invalidSelection);
            await Assert.ThrowsAsync<InvalidOperationException>(() => route.ReloadAsync(Ct));
            await FleetConnectionTests.WaitForAsync(() => baseline.ActiveConnections == 0);
            await lease.DisposeAsync();
            HarnessSettings.SaveOutboundProxy(home, new OutboundProxySettings { Url = $"http://127.0.0.1:{replacement.Port}" }, proxy);
            Assert.NotNull(await Record.ExceptionAsync(() => consumer.GetStringAsync("http://example.com/still-unavailable", Ct)));
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await route.AcquireAsync(Ct));
            Assert.Equal(invalidSelection, HarnessSettings.Load(home).Network?.BorrowedOutboundMachine);
            Assert.Single(baseline.Requests);
            Assert.Empty(replacement.Requests);

            await route.SelectAsync(null, Ct);
            await using var repaired = await route.AcquireAsync(Ct);
            Assert.Equal(TestForwardProxy.ProxiedBody, await consumer.GetStringAsync("http://example.com/explicit-direct", Ct));
            Assert.Equal(["GET http://example.com/explicit-direct HTTP/1.1"], replacement.Requests);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Fact(Explicit = true)]
    public async Task UnreachableSavedSelection_ReportsTheTransportFailure_WithoutClearingItOrUsingTheBaseline()
    {
        FleetSshEndToEndTests.RequireEnvironment();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        await using var unused = new TestForwardProxy();
        await using var baseline = new TestForwardProxy();
        var root = TempTree.CreateSocketDirectory("b80n");
        try
        {
            var home = Configure(root, ssh, unused, "A", baseline: baseline);
            using var endpoint = new TcpListener(IPAddress.Loopback, 0);
            endpoint.Start();
            var port = ((IPEndPoint)endpoint.LocalEndpoint).Port;
            endpoint.Stop();
            var store = new MachineStore(home);
            store.Upsert(store.Load().Find("A")! with { OutboundTunnel = new("proxyCommand", $"http://127.0.0.1:{port}") });
            using var proxy = new OutboundProxy();
            await using var route = new BorrowedOutboundRoute(home, proxy);
            await using var lease = await route.AcquireAsync(Ct);
            using var consumer = new HttpClient(proxy.CreateHandler()) { Timeout = TimeSpan.FromSeconds(5) };
            var failure = await Record.ExceptionAsync(() => consumer.GetStringAsync("http://example.com/unreachable-selection", Ct));
            Assert.True(failure is HttpRequestException or IOException or OperationCanceledException, "The real borrowed transport must fail explicitly.");
            Assert.Equal("A", HarnessSettings.Load(home).Network?.BorrowedOutboundMachine);
            Assert.Empty(unused.Requests);
            Assert.Empty(baseline.Requests);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Fact(Explicit = true)]
    public async Task RealCli_UseAndDirect_PersistTheSelection_WithoutChangingTheBaselineOrDeployingTheSelectedMachine()
    {
        FleetSshEndToEndTests.RequireEnvironment();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        await using var first = new TestForwardProxy(keepAlive: true);
        await using var second = new TestForwardProxy(keepAlive: true);
        await using var baseline = new TestForwardProxy(keepAlive: true);
        var root = TempTree.CreateSocketDirectory("b80u");
        try
        {
            var home = Configure(root, ssh, first, "A", baseline: baseline);
            new MachineStore(home).Upsert(ssh.Machine("B", root) with { OutboundTunnel = new("proxyCommand", $"http://127.0.0.1:{second.Port}") });
            var baselineUrl = HarnessSettings.Load(home).Network?.OutboundProxy?.Url;
            var selected = await FleetRemoteProxyTests.RunCliAsync(root, null, Ct, "machine", "tunnel", "B", "use");
            Assert.True(selected.ExitCode == 0, "The actual CLI must validate and save its borrowed selection without a remote dsharp.");
            Assert.Equal("B", HarnessSettings.Load(home).Network?.BorrowedOutboundMachine);
            Assert.Equal(baselineUrl, HarnessSettings.Load(home).Network?.OutboundProxy?.Url);
            Assert.Equal([$"GET {Probe} HTTP/1.1"], second.Requests);
            using var proxy = new OutboundProxy();
            await using var route = new BorrowedOutboundRoute(home, proxy);
            await using var lease = await route.AcquireAsync(Ct);
            using var consumer = new HttpClient(proxy.CreateHandler());
            Assert.Equal(TestForwardProxy.ProxiedBody, await consumer.GetStringAsync("http://example.com/cli-selected", Ct));
            var saved = await File.ReadAllTextAsync(home.SettingsFile, Ct);
            var invalid = await FleetRemoteProxyTests.RunCliAsync(root, null, Ct, "machine", "tunnel", "missing", "use");
            Assert.NotEqual(0, invalid.ExitCode);
            Assert.True(saved == await File.ReadAllTextAsync(home.SettingsFile, Ct), "An invalid selection must not change settings.");
            Assert.Equal(TestForwardProxy.ProxiedBody, await consumer.GetStringAsync("http://example.com/after-invalid-cli", Ct));
            var connections = second.Connections;
            var direct = await FleetRemoteProxyTests.RunCliAsync(root, null, Ct, "machine", "tunnel", "direct");
            Assert.Equal(0, direct.ExitCode);
            Assert.Null(HarnessSettings.Load(home).Network?.BorrowedOutboundMachine);
            Assert.Equal(connections, second.Connections);
            await route.ReloadAsync(Ct);
            await FleetConnectionTests.WaitForAsync(() => second.ActiveConnections == 0);
            Assert.Equal(TestForwardProxy.ProxiedBody, await consumer.GetStringAsync("http://example.com/cli-baseline", Ct));
            Assert.Empty(first.Requests);
            Assert.Single(baseline.Requests);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Theory(Explicit = true)]
    [InlineData("proxyCommand")]
    [InlineData("localForward")]
    public async Task RealCliTunnelTest_ConsumesPublicHttps200_AndRetiresOnlyItsIndependentTransport(string mode)
    {
        var executable = FleetSshEndToEndTests.RequireEnvironment();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        await using var selected = new TestForwardProxy(forward: true, pauseResponses: true);
        await using var other = new TestForwardProxy();
        await using var baseline = new TestForwardProxy(keepAlive: true);
        var root = TempTree.CreateSocketDirectory("b83c");
        var remote = RealHostProcesses.CreateHome("b83h");
        Process? owned = null;
        try
        {
            var home = Configure(root, ssh, selected, "A", baseline: baseline);
            var store = new MachineStore(home);
            store.Upsert(ObserveSsh(ssh.Machine("A", remote), root) with
            {
                OutboundTunnel = new(mode, $"http://127.0.0.1:{selected.Port}"),
            });
            store.Upsert(ssh.Machine("B", remote) with
            {
                OutboundTunnel = new(mode, $"http://localhost:{other.Port}"),
            });
            var saved = await File.ReadAllBytesAsync(home.SettingsFile, Ct);
            var savedBaseline = HarnessSettings.Load(home).Network!.OutboundProxy!.Url;

            await using var host = await SshHostLauncher.ConnectAsync(
                ssh.Machine("independent-host", remote), executable, "cli-tunnel-survivor", Ct);
            var hostIdentity = LocalEndpointToken.Read(RemoteHostEndpoint.EndpointFile(remote));
            var remoteSettings = Path.Combine(remote, "settings.yaml");
            var savedRemote = await File.ReadAllBytesAsync(remoteSettings, Ct);
            using var independentProxy = new OutboundProxy();
            independentProxy.Configure(savedBaseline, []);
            using var independent = new HttpClient(independentProxy.CreateHandler());
            Assert.Equal(TestForwardProxy.ProxiedBody,
                await independent.GetStringAsync("http://example.com/cli-survivor-before", Ct));
            Assert.Equal("GET http://example.com/cli-survivor-before HTTP/1.1",
                await baseline.WaitForRequestAsync(Ct));
            var baselineConnections = baseline.Connections;

            using var signal = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            signal.CancelAfter(CliObservationTimeout);
            var cli = FleetCliProcess.RunAsync(executable, root,
                "machine", "tunnel", "A", "test", "--probe-url", "https://example.com/");
            int? listenerPort = null;
            Task exited;
            try
            {
                Assert.Equal("CONNECT example.com:443 HTTP/1.1",
                    await selected.WaitForRequestAsync(signal.Token));
                Assert.False(cli.IsCompleted, "The real proxy barrier must hold the independent CLI before TLS begins.");
                var commands = await File.ReadAllLinesAsync(
                    Path.Combine(root, "owned-ssh-commands.txt"), signal.Token);
                var resources = await File.ReadAllLinesAsync(
                    Path.Combine(root, "owned-ssh-resources.txt"), signal.Token);
                Assert.True(commands.Length == 1 && resources.Length == 1,
                    "The independent CLI must open exactly one owned SSH transport.");
                var command = commands[0];
                var resource = resources[0].Split('|', 2);
                Assert.Equal(2, resource.Length);
                Assert.False(File.Exists(resource[1]), "The authenticated CLI transport must already have removed its askpass.");
                owned = Process.GetProcessById(int.Parse(resource[0], System.Globalization.CultureInfo.InvariantCulture));
                Assert.False(owned.HasExited);
                exited = owned.WaitForExitAsync(Ct);
                if (mode == "proxyCommand")
                    Assert.True(command.Contains($"-W 127.0.0.1:{selected.Port}", StringComparison.Ordinal),
                        "The CLI must own the selected SSH stdio command, not a Host or Network RPC.");
                else
                {
                    var arguments = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var index = Array.IndexOf(arguments, "-L");
                    Assert.True(index >= 0 && index + 1 < arguments.Length,
                        "The CLI must own an actual SSH local-forward command.");
                    var fields = arguments[index + 1].Split(':');
                    Assert.Equal("127.0.0.1", fields[0]);
                    Assert.Equal("127.0.0.1", fields[^2]);
                    Assert.Equal(selected.Port, int.Parse(fields[^1], System.Globalization.CultureInfo.InvariantCulture));
                    listenerPort = int.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture);
                    FleetLocalForwardEgressTests.AssertOnlyLoopbackListener(listenerPort.Value);
                }
                Assert.Single(baseline.Requests);
                Assert.Equal(baselineConnections, baseline.Connections);
                Assert.Empty(other.Requests);
            }
            finally
            {
                selected.ReleaseResponse();
            }

            var result = await cli;
            Assert.Equal(0, result.ExitCode);
            Assert.True(result.Output.StartsWith("HTTP 200 ", StringComparison.Ordinal),
                "The production CLI must report the actual target HTTP 200.");
            using var retired = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            retired.CancelAfter(CliObservationTimeout);
            await exited.WaitAsync(retired.Token);
            await selected.WaitForClosedConnectionAsync(retired.Token);
            Assert.Equal(0, selected.ActiveConnections);
            await FleetLocalForwardEgressTests.AssertOwnedSshRetiredAsync(root, mode, selected.Port);
            if (listenerPort is { } port)
                FleetLocalForwardEgressTests.AssertPortCanRebind(port);
            Assert.Equal("CONNECT example.com:443 HTTP/1.1", Assert.Single(selected.Requests));
            Assert.Empty(other.Requests);
            Assert.Single(baseline.Requests);
            Assert.Equal(savedBaseline, HarnessSettings.Load(home).Network?.OutboundProxy?.Url);
            Assert.Equal("A", HarnessSettings.Load(home).Network?.BorrowedOutboundMachine);
            var current = await File.ReadAllBytesAsync(home.SettingsFile, Ct);
            var currentRemote = await File.ReadAllBytesAsync(remoteSettings, Ct);
            Assert.True(saved.AsSpan().SequenceEqual(current),
                "The independent CLI test must leave the entire local settings file unchanged.");
            Assert.True(savedRemote.AsSpan().SequenceEqual(currentRemote),
                "The independent CLI test must leave remote Host settings unchanged.");
            Assert.True(await host.Client.PingAsync(Ct) > 0);
            Assert.True(hostIdentity == LocalEndpointToken.Read(RemoteHostEndpoint.EndpointFile(remote)),
                "CLI completion must preserve the independent remote Host identity.");
            Assert.Equal(TestForwardProxy.ProxiedBody,
                await independent.GetStringAsync("http://example.com/cli-survivor-after", Ct));
            Assert.Equal("GET http://example.com/cli-survivor-after HTTP/1.1",
                await baseline.WaitForRequestAsync(Ct));
            Assert.Equal(baselineConnections, baseline.Connections);
            Assert.Equal(2, baseline.Requests.Count);
        }
        finally
        {
            owned?.Dispose();
            RealHostProcesses.KillAll(remote);
            TempTree.Delete(remote);
            TempTree.Delete(root);
        }
    }

    [Fact(Explicit = true)]
    public async Task SharedConsumers_SwitchReloadAndLastRelease_RetireOwnedSshButPreserveTheTargetHostAndPty()
    {
        var executable = FleetSshEndToEndTests.RequireEnvironment();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        await using var first = new TestForwardProxy(keepAlive: true);
        await using var second = new TestForwardProxy(keepAlive: true);
        var root = TempTree.CreateSocketDirectory("b80l");
        var remote = RealHostProcesses.CreateHome("b80h");
        try
        {
            var home = Configure(root, ssh, first, "A");
            var store = new MachineStore(home);
            store.Upsert(ObserveSsh(store.Load().Find("A")!, root));
            store.Upsert(ObserveSsh(ssh.Machine("B", root), root) with { OutboundTunnel = new("proxyCommand", $"http://127.0.0.1:{second.Port}") });
            var machine = ssh.Machine("live-target", remote);
            await using var host = await SshHostLauncher.ConnectAsync(machine, executable, "borrowed-lease-survival", Ct);
            var identity = LocalEndpointToken.Read(RemoteHostEndpoint.EndpointFile(remote));
            var shell = await host.Client.Pty.StartPtyAsync(new RemotePtyStartRequest("/bin/sh", [], remote, Title: "lease-survival"), Ct);
            await using var terminal = RemoteHostPtyTests.TerminalCapture.Attach(host.Client, shell);
            await terminal.SendAsync("echo $$ > original.pid; echo lease-before-$((20+22))\n");
            await terminal.WaitForAsync("lease-before-42", TimeSpan.FromSeconds(5));
            var shellPid = await File.ReadAllTextAsync(Path.Combine(remote, "original.pid"), Ct);
            using var proxy = new OutboundProxy();
            await using var route = new BorrowedOutboundRoute(home, proxy);
            var firstLease = await route.AcquireAsync(Ct);
            var secondLease = await route.AcquireAsync(Ct);
            using var consumer = new HttpClient(proxy.CreateHandler());
            Assert.Equal(TestForwardProxy.ProxiedBody, await consumer.GetStringAsync("http://example.com/lease-A", Ct));
            await route.SelectAsync("B", Ct);
            await FleetConnectionTests.WaitForAsync(() => first.ActiveConnections == 0);
            Assert.Equal(TestForwardProxy.ProxiedBody, await consumer.GetStringAsync("http://example.com/lease-B", Ct));
            await route.ReloadAsync(Ct);
            await FleetConnectionTests.WaitForAsync(() => second.ActiveConnections == 0);
            await firstLease.DisposeAsync();
            Assert.Equal(TestForwardProxy.ProxiedBody, await consumer.GetStringAsync("http://example.com/only-survivor", Ct));
            await secondLease.DisposeAsync();
            await secondLease.DisposeAsync();
            await route.DisposeAsync();
            await route.DisposeAsync();
            await FleetConnectionTests.WaitForAsync(() => second.ActiveConnections == 0);
            await AssertOwnedSshRetiredAsync(root, first.Port, second.Port);
            Assert.True(await host.Client.PingAsync(Ct) > 0);
            Assert.True(identity == LocalEndpointToken.Read(RemoteHostEndpoint.EndpointFile(remote)), "Route retirement must preserve the independent target Host.");
            Assert.Contains(await host.Client.ListHostSessionsAsync(Ct), value => value.SessionId == shell && value.Status == HostSessionStatuses.Running);
            await terminal.SendAsync("echo $$ > surviving.pid; echo lease-after-$((50+5))\n");
            await terminal.WaitForAsync("lease-after-55", TimeSpan.FromSeconds(5));
            Assert.Equal(shellPid, await File.ReadAllTextAsync(Path.Combine(remote, "surviving.pid"), Ct));
            Assert.Equal(1, first.Requests.Count(value => value.Contains("lease-A", StringComparison.Ordinal)));
            Assert.DoesNotContain(second.Requests, value => value.Contains("lease-A", StringComparison.Ordinal));
        }
        finally
        {
            RealHostProcesses.KillAll(remote);
            TempTree.Delete(remote);
            TempTree.Delete(root);
        }
    }

    [Theory(Explicit = true)]
    [InlineData(DownloadVia.Client)]
    [InlineData(DownloadVia.Server)]
    public async Task RealBuildArchive_DeploysThroughTheSelectedDownloadOwner_AndPingsItsHost(string via)
    {
        var executable = FleetSshEndToEndTests.RequireEnvironment();
        var guard = FleetSshEndToEndTests.RealHomeGuard.Capture();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        var root = TempTree.CreateSocketDirectory("b80a");
        var remote = TempTree.CreateSocketDirectory("b80r");
        try
        {
            TempTree.AssertFitsMacOsSocketLimit(Path.Combine(remote, "run", "host.sock"));
            var archive = await FleetSshEndToEndTests.BuildArchiveAsync(executable, RuntimeInformation.RuntimeIdentifier, ssh.Root, Ct);
            await using var release = new ReleaseArchiveProxy(archive);
            var home = new HarnessHome(root);
            var borrowed = ObserveSsh(ssh.Machine("archive-route", root), root) with
            {
                OutboundTunnel = new("proxyCommand", $"http://127.0.0.1:{release.Port}"),
            };
            new MachineStore(home).Upsert(borrowed);
            HarnessSettings.SaveBorrowedOutboundMachine(home, via == DownloadVia.Client ? borrowed.Name : "invalid-local-selection");
            var saved = await File.ReadAllTextAsync(home.SettingsFile, Ct);
            var settings = new DeploySettings
            {
                Via = via,
                ReleaseBaseUrl = "http://releases.example.test/releases",
                ServerProxy = via == DownloadVia.Server ? $"http://127.0.0.1:{release.Port}" : null,
            };
            var target = ssh.Machine("archive-target", remote);
            var progress = new List<string>();

            var deployed = await new RemoteDsharpInstaller(new SshTransport(target)).EnsureAsync(
                settings, progress.Add, Ct, force: true, localHome: home);

            await release.Transferred.WaitAsync(Ct);
            Assert.False(deployed.AlreadyPresent);
            Assert.Equal(new FileInfo(archive).Length, release.BytesTransferred);
            Assert.Equal($"GET http://releases.example.test/releases/latest/download/{RemoteDsharpInstaller.AssetName(RuntimeInformation.RuntimeIdentifier)} HTTP/1.1", release.Request);
            Assert.Equal(via == DownloadVia.Client, progress.Any(value => value.Contains("上传", StringComparison.Ordinal)));
            Assert.True(saved == await File.ReadAllTextAsync(home.SettingsFile, Ct), "Deployment must not change the local network settings.");
            if (via == DownloadVia.Client)
                await AssertOwnedSshRetiredAsync(root, release.Port);
            else
                Assert.False(File.Exists(Path.Combine(root, "owned-ssh-commands.txt")), "Server downloads must never open the local borrowed selection.");
            await using var host = await SshHostLauncher.ConnectAsync(target, deployed.CommandPath, "archive-download-acceptance", Ct);
            Assert.Equal(remote, (await host.Client.InfoAsync(Ct)).HomeRoot);
            Assert.True(await host.Client.PingAsync(Ct) > 0);
        }
        finally
        {
            RealHostProcesses.KillAll(remote);
            TempTree.Delete(remote);
            TempTree.Delete(root);
            guard.AssertUnchanged();
        }
    }

    [Fact(Explicit = true)]
    public async Task FleetClients_ShareTheContextRoute_UntilTheLastAsyncConsumerDisposes()
    {
        FleetSshEndToEndTests.RequireEnvironment();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        await using var forwarding = new TestForwardProxy(keepAlive: true);
        var root = TempTree.CreateSocketDirectory("b80f");
        try
        {
            var home = Configure(root, ssh, forwarding, "A");
            using var fleet = new FleetService(home);
            await using var first = fleet.CreateClient(new FleetOptions());
            await using var second = fleet.CreateClient(new FleetOptions());
            using var consumer = new HttpClient(fleet.OutboundRoute.Proxy.CreateHandler());
            Assert.Equal(TestForwardProxy.ProxiedBody, await consumer.GetStringAsync("http://example.com/first-fleet", Ct));
            await first.DisposeAsync();
            await first.DisposeAsync();
            Assert.Equal(TestForwardProxy.ProxiedBody, await consumer.GetStringAsync("http://example.com/second-fleet", Ct));
            Assert.Equal(1, forwarding.Connections);
            await second.DisposeAsync();
            await second.DisposeAsync();
            await FleetConnectionTests.WaitForAsync(() => forwarding.ActiveConnections == 0);
            Assert.NotNull(await Record.ExceptionAsync(() => consumer.GetStringAsync("http://example.com/disposed-fleet", Ct)));
            Assert.Equal(["GET http://example.com/first-fleet HTTP/1.1", "GET http://example.com/second-fleet HTTP/1.1"], forwarding.Requests);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Fact(Explicit = true)]
    public async Task CancellingASelectionDraft_DoesNotRetireAnotherConsumersRouteOrPersistTheDraft()
    {
        FleetSshEndToEndTests.RequireEnvironment();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        await using var first = new TestForwardProxy(keepAlive: true);
        await using var draft = new TestForwardProxy(pauseResponses: true);
        var root = TempTree.CreateSocketDirectory("b80d");
        try
        {
            var home = Configure(root, ssh, first, "A");
            new MachineStore(home).Upsert(ssh.Machine("B", root) with { OutboundTunnel = new("proxyCommand", $"http://127.0.0.1:{draft.Port}") });
            var saved = await File.ReadAllTextAsync(home.SettingsFile, Ct);
            using var proxy = new OutboundProxy();
            await using var route = new BorrowedOutboundRoute(home, proxy);
            await using var lease = await route.AcquireAsync(Ct);
            using var consumer = new HttpClient(proxy.CreateHandler());
            Assert.Equal(TestForwardProxy.ProxiedBody, await consumer.GetStringAsync("http://example.com/before-draft", Ct));
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            var selection = route.SelectAsync("B", cancellation.Token);
            Assert.Equal($"GET {Probe} HTTP/1.1", await draft.WaitForRequestAsync(Ct));
            Assert.Equal(TestForwardProxy.ProxiedBody, await consumer.GetStringAsync("http://example.com/during-draft", Ct));
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await selection);
            draft.ReleaseResponse();
            await FleetConnectionTests.WaitForAsync(() => draft.ActiveConnections == 0);
            Assert.True(saved == await File.ReadAllTextAsync(home.SettingsFile, Ct), "Cancelling a draft must not persist it.");
            Assert.Equal(TestForwardProxy.ProxiedBody, await consumer.GetStringAsync("http://example.com/after-draft", Ct));
            Assert.Equal(1, first.Connections);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Fact(Explicit = true)]
    public async Task Consumers_CancelIndependently_AndLastLeaseClosesTheSharedRoute()
    {
        FleetSshEndToEndTests.RequireEnvironment();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        await using var forwarding = new TestForwardProxy(keepAlive: true, pauseResponses: true);
        var root = TempTree.CreateSocketDirectory("b80c");
        try
        {
            var home = Configure(root, ssh, forwarding, "A");
            using var proxy = new OutboundProxy();
            await using var route = new BorrowedOutboundRoute(home, proxy);
            var firstLease = await route.AcquireAsync(Ct);
            var secondLease = await route.AcquireAsync(Ct);
            using var first = new HttpClient(proxy.CreateHandler());
            using var second = new HttpClient(proxy.CreateHandler());
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            var cancelled = first.GetStringAsync("http://example.com/cancel-one", cancellation.Token);
            Assert.Equal("GET http://example.com/cancel-one HTTP/1.1", await forwarding.WaitForRequestAsync(Ct));
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await cancelled);
            forwarding.ReleaseResponse();
            await firstLease.DisposeAsync();
            await firstLease.DisposeAsync();

            var surviving = second.GetStringAsync("http://example.com/survivor", Ct);
            Assert.Equal("GET http://example.com/survivor HTTP/1.1", await forwarding.WaitForRequestAsync(Ct));
            forwarding.ReleaseResponse();
            Assert.Equal(TestForwardProxy.ProxiedBody, await surviving);
            await secondLease.DisposeAsync();
            await secondLease.DisposeAsync();
            await FleetConnectionTests.WaitForAsync(() => forwarding.ActiveConnections == 0);
            Assert.NotNull(await Record.ExceptionAsync(() => second.GetStringAsync("http://example.com/no-lease", Ct)));
            Assert.DoesNotContain(forwarding.Requests, value => value.Contains("no-lease", StringComparison.Ordinal));
            Assert.Equal("A", HarnessSettings.Load(home).Network?.BorrowedOutboundMachine);

            await using var nextLease = await route.AcquireAsync(Ct);
            var next = second.GetStringAsync("http://example.com/next-consumer", Ct);
            Assert.Equal("GET http://example.com/next-consumer HTTP/1.1", await forwarding.WaitForRequestAsync(Ct));
            forwarding.ReleaseResponse();
            Assert.Equal(TestForwardProxy.ProxiedBody, await next);
            Assert.Equal(1, forwarding.Requests.Count(value => value.Contains("cancel-one", StringComparison.Ordinal)));
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Fact(Explicit = true)]
    public async Task SelectAndReload_RetireOldBodiesAndCredentials_WithoutReplayingRequests()
    {
        FleetSshEndToEndTests.RequireEnvironment();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        await using var first = new TestForwardProxy(requiredCredential: "first:secret-a", keepAlive: true, pauseResponses: true);
        await using var second = new TestForwardProxy(requiredCredential: "second:secret-b", keepAlive: true);
        await using var baseline = new TestForwardProxy(keepAlive: true);
        var root = TempTree.CreateSocketDirectory("b80s");
        try
        {
            var home = Configure(root, ssh, first, "A", "first:secret-a", baseline);
            var store = new MachineStore(home);
            store.Upsert(ssh.Machine("B", root) with { OutboundTunnel = new("proxyCommand", $"http://second:secret-b@127.0.0.1:{second.Port}") });
            using var proxy = new OutboundProxy();
            await using var route = new BorrowedOutboundRoute(home, proxy);
            await using var lease = await route.AcquireAsync(Ct);
            using var client = new HttpClient(proxy.CreateHandler());
            var old = client.GetStringAsync("http://example.com/old-in-flight", Ct);
            Assert.Equal("GET http://example.com/old-in-flight HTTP/1.1", await first.WaitForRequestAsync(Ct));

            await route.SelectAsync("B", Ct);

            Assert.Equal("B", HarnessSettings.Load(home).Network?.BorrowedOutboundMachine);
            Assert.NotNull(await Record.ExceptionAsync(async () => await old));
            first.ReleaseResponse();
            await FleetConnectionTests.WaitForAsync(() => first.ActiveConnections == 0);
            Assert.Equal(TestForwardProxy.ProxiedBody, await client.GetStringAsync("http://example.com/new-request", Ct));
            Assert.All(second.Credentials, value => Assert.Equal("second:secret-b", value));
            Assert.DoesNotContain(second.Requests, value => value.Contains("old-in-flight", StringComparison.Ordinal));
            var beforeReload = second.Connections;
            await route.ReloadAsync(Ct);
            await FleetConnectionTests.WaitForAsync(() => second.ActiveConnections == 0);
            Assert.Equal(TestForwardProxy.ProxiedBody, await client.GetStringAsync("http://example.com/after-reload", Ct));
            Assert.True(second.Connections > beforeReload);
            Assert.Empty(baseline.Requests);

            await route.SelectAsync(null, Ct);
            await FleetConnectionTests.WaitForAsync(() => second.ActiveConnections == 0);
            Assert.Null(HarnessSettings.Load(home).Network?.BorrowedOutboundMachine);
            Assert.Equal(TestForwardProxy.ProxiedBody, await client.GetStringAsync("http://example.com/baseline", Ct));
            Assert.Equal(["GET http://example.com/old-in-flight HTTP/1.1"], first.Requests);
            Assert.Single(baseline.Requests);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Fact(Explicit = true)]
    public async Task CandidatePublicationFailure_RetainsTheActiveGeneration_AndDisposesTheDraft()
    {
        FleetSshEndToEndTests.RequireEnvironment();
        var cancellation = TestContext.Current.CancellationToken;
        await using var ssh = await TestSshServer.StartAsync(cancellation);
        await using var first = new TestForwardProxy(keepAlive: true);
        await using var second = new TestForwardProxy(keepAlive: true, pauseResponses: true);
        var root = TempTree.CreateSocketDirectory("b80w");
        try
        {
            var home = Configure(root, ssh, first, "A");
            new MachineStore(home).Upsert(ssh.Machine("B", root) with { OutboundTunnel = new("proxyCommand", $"http://127.0.0.1:{second.Port}") });
            var original = await File.ReadAllTextAsync(home.SettingsFile, cancellation);
            var preserved = Path.Combine(root, "preserved-settings.yaml");
            using var proxy = new OutboundProxy();
            await using var route = new BorrowedOutboundRoute(home, proxy);
            await using var lease = await route.AcquireAsync(cancellation);
            using var client = new HttpClient(proxy.CreateHandler());
            Assert.Equal(TestForwardProxy.ProxiedBody, await client.GetStringAsync("http://example.com/before-save", cancellation));
            var failure = Record.ExceptionAsync(() => route.SelectAsync("B", cancellation));
            try
            {
                Assert.Contains("route-validation", await second.WaitForRequestAsync(cancellation), StringComparison.Ordinal);
                File.Move(home.SettingsFile, preserved);
                Directory.CreateDirectory(home.SettingsFile);
            }
            finally
            {
                second.ReleaseResponse();
            }
            var error = await failure;
            Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
            Assert.Equal(original, await File.ReadAllTextAsync(preserved, cancellation));
            Directory.Delete(home.SettingsFile);
            File.Move(preserved, home.SettingsFile);
            Assert.Equal("A", HarnessSettings.Load(home).Network?.BorrowedOutboundMachine);
            Assert.Equal(TestForwardProxy.ProxiedBody, await client.GetStringAsync("http://example.com/after-failed-save", cancellation));
            Assert.Equal(1, first.Connections);
            await second.WaitForClosedConnectionAsync(cancellation);
            Assert.Equal(0, second.ActiveConnections);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Fact(Explicit = true)]
    public async Task InvalidSavedSelection_FailsClosed_AndNeverClearsOrFallsBackToBaseline()
    {
        FleetSshEndToEndTests.RequireEnvironment();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        await using var forwarding = new TestForwardProxy(keepAlive: true);
        await using var baseline = new TestForwardProxy(keepAlive: true);
        var root = TempTree.CreateSocketDirectory("b80i");
        try
        {
            var home = Configure(root, ssh, forwarding, "A", baseline: baseline);
            using var proxy = new OutboundProxy();
            await using var route = new BorrowedOutboundRoute(home, proxy);
            await using var lease = await route.AcquireAsync(Ct);
            using var client = new HttpClient(proxy.CreateHandler());
            Assert.Equal(TestForwardProxy.ProxiedBody, await client.GetStringAsync("http://example.com/before-invalid", Ct));
            HarnessSettings.SaveBorrowedOutboundMachine(home, "does-not-exist");
            var saved = await File.ReadAllTextAsync(home.SettingsFile, Ct);
            await Assert.ThrowsAsync<InvalidOperationException>(() => route.ReloadAsync(Ct));
            await FleetConnectionTests.WaitForAsync(() => forwarding.ActiveConnections == 0);
            Assert.NotNull(await Record.ExceptionAsync(() => client.GetStringAsync("http://example.com/must-fail", Ct)));
            Assert.True(saved == await File.ReadAllTextAsync(home.SettingsFile, Ct), "Reload must not clear or replace an invalid saved selection.");
            Assert.Empty(baseline.Requests);
            Assert.Single(forwarding.Requests);
            await using var cold = new BorrowedOutboundRoute(home, new OutboundProxy());
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await cold.AcquireAsync(Ct));
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    internal static MachineSpec ObserveSsh(MachineSpec machine, string root)
    {
        var commands = RemoteShell.Quote(Path.Combine(root, "owned-ssh-commands.txt"));
        var resources = RemoteShell.Quote(Path.Combine(root, "owned-ssh-resources.txt"));
        var options = string.Join(' ', (machine.SshOptions ?? []).Select(value => $"-o {RemoteShell.Quote(value)}"));
        var jump = $"exec ssh -T -p {machine.Port} -i {RemoteShell.Quote(machine.KeyPath!)} {options} -o StrictHostKeyChecking=accept-new -o BatchMode=yes -o IdentitiesOnly=yes -o IdentityAgent=none -W %h:%p {RemoteShell.Quote($"{machine.User}@{machine.Host}")}";
        var command = $"ps -p \"$PPID\" -o args= >> {commands}; echo \"$PPID|$SSH_ASKPASS\" >> {resources}; {jump}";
        return machine with
        {
            Auth = SshAuth.Password,
            Password = $"fixture-{Guid.NewGuid():N}",
            Proxy = $"/bin/sh -c {RemoteShell.Quote(command)}",
            SshOptions = [.. machine.SshOptions ?? [], $"IdentityFile={machine.KeyPath}", "PreferredAuthentications=publickey", "IdentitiesOnly=yes", "IdentityAgent=none"],
        };
    }

    internal static async Task AssertOwnedSshRetiredAsync(string root, params int[] proxyPorts)
    {
        var commandFile = Path.Combine(root, "owned-ssh-commands.txt");
        Assert.True(File.Exists(commandFile), "The actual SSH process must record its command at authentication.");
        var commands = await File.ReadAllLinesAsync(commandFile, Ct);
        Assert.NotEmpty(commands);
        Assert.True(commands.All(value => proxyPorts.Any(port => value.Contains($"-W 127.0.0.1:{port}", StringComparison.Ordinal))),
            "Every recorded process must be the owned stdio proxy command, not a remote dsharp or Network RPC.");
        foreach (var resource in await File.ReadAllLinesAsync(Path.Combine(root, "owned-ssh-resources.txt"), Ct))
        {
            var fields = resource.Split('|', 2);
            Assert.True(int.TryParse(fields[0], out var pid) && pid > 0, "The fixture must record its real SSH process identity.");
            await FleetConnectionTests.WaitForAsync(() =>
            {
                try
                {
                    using var process = Process.GetProcessById(pid);
                    return process.HasExited;
                }
                catch (ArgumentException)
                {
                    return true;
                }
            });
            Assert.True(fields.Length == 2 && fields[1].Length > 0, "Each owned password-authentication transport must identify its askpass file.");
            Assert.False(File.Exists(fields[1]), "The owned SSH askpass must be absent after retirement.");
        }
    }

    private sealed class ReleaseArchiveProxy : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serve;

        public ReleaseArchiveProxy(string archive)
        {
            _listener.Start();
            _serve = ServeAsync(archive);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public string? Request { get; private set; }
        public long BytesTransferred { get; private set; }
        public Task Transferred => _serve;

        private async Task ServeAsync(string archive)
        {
            using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            Request = await reader.ReadLineAsync(_stop.Token);
            while (await reader.ReadLineAsync(_stop.Token) is { Length: > 0 }) { }
            await using var file = File.OpenRead(archive);
            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: {file.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header, _stop.Token);
            await file.CopyToAsync(stream, _stop.Token);
            BytesTransferred = file.Position;
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            try
            {
                await _serve;
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            finally
            {
                _stop.Dispose();
            }
        }
    }

    private static HarnessHome Configure(string root, TestSshServer ssh, TestForwardProxy forwarding, string selected,
        string? credential = null, TestForwardProxy? baseline = null)
    {
        TempTree.AssertFitsMacOsSocketLimit(Path.Combine(root, "run", "host.sock"));
        var home = new HarnessHome(root);
        var settings = new HarnessSettings
        {
            Network = new NetworkSettings
            {
                OutboundProxy = new OutboundProxySettings { ProbeUrl = Probe, Url = baseline is null ? null : $"http://127.0.0.1:{baseline.Port}" },
            },
        };
        settings.Save(home);
        new MachineStore(home).Upsert(ssh.Machine(selected, root) with
        {
            OutboundTunnel = new("proxyCommand", $"http://{(credential is null ? "" : credential + "@")}127.0.0.1:{forwarding.Port}"),
        });
        HarnessSettings.SaveBorrowedOutboundMachine(home, selected);
        return home;
    }
}


