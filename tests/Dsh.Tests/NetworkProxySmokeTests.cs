using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Dsh.Boot;
using Dsh.Fleet;
using Dsh.Fleet.Iroh;
using Dsh.Runtime;
using Dsh.Transport;
using Dsh.Core;
using Dsh.Persistence;
using Dsh.RemoteHost;
using Dsh.Workspace;
using Dsh.Web;
using Dsh.Llm;
using System.Diagnostics;
using System.IO.Pipes;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dsh.Gui.ViewModels;
using Dsh.Gui.Views;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Dsh.Gui.Services;
using Dsh.Gui.Terminal;
using TextBlock = Dsh.Llm.TextBlock;
using UiTextBlock = Avalonia.Controls.TextBlock;
using System.Threading.Channels;

namespace Dsh.Tests
{

#if !NETWORK_GUI_OBSERVER
/** Explicit real-network and real-surface acceptance using this worktree's production executable. */
[Collection(SerialProcessCollection.Name)]
public sealed class NetworkProxySmokeTests
{
    private const string HttpTarget = "http://example.com/";
    private const string HttpsTarget = "https://example.com/";
    private const string TlsMachineName = "tls-proxy";
    private const int SharedSettingsTimeoutMilliseconds = 5000;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory(Explicit = true, Timeout = 5000)]
    [InlineData("proxyCommand", "http", HttpTarget, 1)]
    [InlineData("proxyCommand", "http", HttpsTarget, 1)]
    [InlineData("proxyCommand", "http", HttpsTarget, 2)]
    [InlineData("proxyCommand", "socks5", HttpTarget, 1)]
    [InlineData("proxyCommand", "socks5", HttpsTarget, 1)]
    [InlineData("proxyCommand", "socks5", HttpsTarget, 2)]
    [InlineData("localForward", "http", HttpTarget, 1)]
    [InlineData("localForward", "http", HttpsTarget, 1)]
    [InlineData("localForward", "http", HttpsTarget, 2)]
    [InlineData("localForward", "socks5", HttpTarget, 1)]
    [InlineData("localForward", "socks5", HttpsTarget, 1)]
    [InlineData("localForward", "socks5", HttpsTarget, 2)]
    public async Task SshBorrowedProxy_ForwardsAuthenticatedPublicHttpAndTls(string mode, string scheme, string target, int version)
    {
        FleetSshEndToEndTests.RequireEnvironment();
        var guard = FleetSshEndToEndTests.RealHomeGuard.Capture();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        var root = TempTree.CreateSocketDirectory("n79s");
        try
        {
            TempTree.AssertFitsMacOsSocketLimit(Path.Combine(root, "run", "host.sock"));
            var user = $"u{Guid.NewGuid():N}";
            var password = $"p{Guid.NewGuid():N}";
            var credential = $"{user}:{password}";
            await using var forwarding = new TestForwardProxy(socks: scheme == "socks5", requiredCredential: credential, forward: true);
            var machine = ssh.Machine("public-proxy", root) with
            {
                OutboundTunnel = new(mode, $"{scheme}://{user}:{password}@127.0.0.1:{forwarding.Port}"),
            };
            await using var tunnel = new SshOutboundTunnel(machine);
            var commands = new ConcurrentQueue<string>();
            using var outbound = new OutboundProxy();
            outbound.ConfigureBorrowed(tunnel.ProxyUri.AbsoluteUri, (endpoint, token) => ConnectObservedAsync(tunnel, endpoint, token, commands));
            using var client = new HttpClient(outbound.CreateHandler(allowAutoRedirect: false));

            await AssertExampleDomainAsync(client, target, version, TestContext.Current.CancellationToken);

            AssertProxyRequest(forwarding, scheme, target);
            AssertCommandStreams(forwarding, commands.ToArray(), "127.0.0.1", forwarding.Port, mode);
            Assert.True(forwarding.Credentials.Count > 0 && forwarding.Credentials.All(value => value == credential),
                "The real forwarding proxy must authenticate only this fixture's credential.");
            AssertPrivateOutput(outbound.Describe(), user, password);
        }
        finally
        {
            try
            {
                RealHostProcesses.KillAll(root);
            }
            finally
            {
                TempTree.Delete(root);
                guard.AssertUnchanged();
            }
        }
    }

    [Theory(Explicit = true, Timeout = 5000)]
    [InlineData("proxyCommand")]
    [InlineData("localForward")]
    public async Task IrohBorrowedProxy_ForwardsPublicHttpAndTlsThroughTheBuiltInProxyCommand(string mode)
    {
        var executable = FleetSshEndToEndTests.RequireEnvironment();
        var guard = FleetSshEndToEndTests.RealHomeGuard.Capture();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        var root = TempTree.CreateSocketDirectory("n79i");
        try
        {
            TempTree.AssertFitsMacOsSocketLimit(Path.Combine(root, "run", "host.sock"));
            var source = ssh.Machine("iroh-public-proxy", root);
            var receiver = IrohRemoteInstaller.ParseStatus(await new SshTransport(source).RunAsync(
                $"{RemoteShell.Prelude(source)}{RemoteShell.Quote(executable)} iroh-remote enable --ssh-port {source.Port}", Ct));
            Assert.True(receiver.Running, "The fixture's real iroh receiver must be running.");
            var user = $"u{Guid.NewGuid():N}";
            var password = $"p{Guid.NewGuid():N}";
            var credential = $"{user}:{password}";
            await using var forwarding = new TestForwardProxy(requiredCredential: credential, forward: true);
            var machine = source with
            {
                Kind = MachineKinds.Iroh,
                Host = "192.0.2.1",
                EndpointId = receiver.EndpointId,
                OutboundTunnel = new(mode, $"http://{user}:{password}@127.0.0.1:{forwarding.Port}"),
            };
            await using var tunnel = new SshOutboundTunnel(machine, new IrohSshTransport());
            var commands = new ConcurrentQueue<string>();
            using var outbound = new OutboundProxy();
            outbound.ConfigureBorrowed(tunnel.ProxyUri.AbsoluteUri, (endpoint, token) => ConnectObservedAsync(tunnel, endpoint, token, commands));
            using var client = new HttpClient(outbound.CreateHandler(allowAutoRedirect: false));

            await AssertExampleDomainAsync(client, HttpTarget, 1, TestContext.Current.CancellationToken);
            await AssertExampleDomainAsync(client, HttpsTarget, 2, TestContext.Current.CancellationToken);

            Assert.Equal(2, forwarding.Requests.Count);
            Assert.True(forwarding.Requests.Contains($"GET {HttpTarget} HTTP/1.1"), "The iroh route must carry a real absolute-form HTTP request.");
            Assert.True(forwarding.Requests.Contains("CONNECT example.com:443 HTTP/1.1"), "The iroh route must carry the real TLS CONNECT request.");
            AssertCommandStreams(forwarding, commands.ToArray(), "127.0.0.1", forwarding.Port, mode);
            Assert.True(forwarding.Credentials.Count >= 2 && forwarding.Credentials.All(value => value == credential),
                "Both public requests must authenticate at this fixture's real forwarding proxy.");
            AssertPrivateOutput(outbound.Describe(), user, password);
        }
        finally
        {
            try
            {
                await IrohReceiver.DisableAsync(new HarnessHome(root), null, CancellationToken.None);
            }
            finally
            {
                try
                {
                    RealHostProcesses.KillAll(root);
                }
                finally
                {
                    TempTree.Delete(root);
                    guard.AssertUnchanged();
                }
            }
        }
    }

    [Theory(Explicit = true, Timeout = 5000)]
    [InlineData("proxyCommand")]
    [InlineData("localForward")]
    public async Task HttpsBorrowedProxy_PreservesLocalhostCertificateIdentityInAnIsolatedProcess(string mode)
    {
        FleetSshEndToEndTests.RequireEnvironment();
        var guard = FleetSshEndToEndTests.RealHomeGuard.Capture();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        var root = TempTree.CreateSocketDirectory("n79t");
        try
        {
            TempTree.AssertFitsMacOsSocketLimit(Path.Combine(root, "run", "host.sock"));
            var user = $"u{Guid.NewGuid():N}";
            var password = $"p{Guid.NewGuid():N}";
            var credential = $"{user}:{password}";
            await using var forwarding = new TestForwardProxy(requiredCredential: credential, forward: true);
            await using var tls = await HttpsForwardProxyFixture.StartAsync(root, forwarding, Ct);
            var machine = ssh.Machine(TlsMachineName, root) with
            {
                OutboundTunnel = new(mode, $"https://{user}:{password}@localhost:{tls.Port}"),
            };
            new MachineStore(new HarnessHome(root)).Upsert(machine);

            var child = await OutboundProxyProcess.RunAsync(
                $"{typeof(NetworkProxySmokeTests).FullName}.{nameof(HttpsBorrowedProxy_ChildUsesFixtureTrustAndSavedMachine)}",
                null, Ct, certificateFile: tls.CertificateFile);

            AssertPrivateOutput(child.StandardOutput + child.StandardError, user, password);
            Assert.True(child.ExitCode == 0, "The isolated real HTTPS-proxy HTTP/TLS smoke failed.");
            var commands = await File.ReadAllLinesAsync(Path.Combine(root, "ssh-command-evidence.txt"), Ct);
            AssertCommandStreams(forwarding, commands, "localhost", tls.Port, mode);
            Assert.Equal(2, forwarding.Requests.Count);
            Assert.True(forwarding.Requests.Contains($"GET {HttpTarget} HTTP/1.1"), "The HTTPS proxy must forward the public absolute-form HTTP request.");
            Assert.True(forwarding.Requests.Contains("CONNECT example.com:443 HTTP/1.1"), "The HTTPS proxy must forward the public TLS CONNECT request.");
            Assert.True(forwarding.Credentials.Count >= 2 && forwarding.Credentials.All(value => value == credential),
                "The TLS-wrapped real proxy must receive only this fixture's credential.");
        }
        finally
        {
            try
            {
                RealHostProcesses.KillAll(root);
            }
            finally
            {
                TempTree.Delete(root);
                guard.AssertUnchanged();
            }
        }
    }

    // SSL_CERT_FILE is an existing process-local technical acceptance input, not a product configuration variable.
    [Fact(Explicit = true, Timeout = 5000)]
    public async Task HttpsBorrowedProxy_ChildUsesFixtureTrustAndSavedMachine()
    {
        var certificateFile = Environment.GetEnvironmentVariable("SSL_CERT_FILE");
        if (string.IsNullOrWhiteSpace(certificateFile) || !File.Exists(certificateFile))
        {
            Assert.Skip("This child requires the parent HTTPS-proxy fixture and its isolated CA file.");
            return;
        }
        var root = Path.GetDirectoryName(Path.GetFullPath(certificateFile))!;
        var home = new HarnessHome(root);
        if (!File.Exists(home.SettingsFile))
        {
            Assert.Skip("This child requires the parent's saved fixture machine next to its CA file.");
            return;
        }
        var machine = new MachineStore(home).Load().Machines.SingleOrDefault(value => value.Name == TlsMachineName);
        if (machine is null)
        {
            Assert.Skip("The parent HTTPS-proxy fixture machine is not present.");
            return;
        }
        await using var tunnel = new SshOutboundTunnel(machine);
        var commands = new ConcurrentQueue<string>();
        Assert.Equal(Uri.UriSchemeHttps, tunnel.ProxyUri.Scheme);
        Assert.Equal("localhost", tunnel.ProxyUri.Host);
        using var outbound = new OutboundProxy();
        outbound.ConfigureBorrowed(tunnel.ProxyUri.AbsoluteUri, (endpoint, token) => ConnectObservedAsync(tunnel, endpoint, token, commands));
        using var client = new HttpClient(outbound.CreateHandler(allowAutoRedirect: false));

        await AssertExampleDomainAsync(client, HttpTarget, 1, TestContext.Current.CancellationToken);
        await AssertExampleDomainAsync(client, HttpsTarget, 2, TestContext.Current.CancellationToken);
        OwnerOnlyFiles.Write(Path.Combine(root, "ssh-command-evidence.txt"), string.Join(Environment.NewLine, commands));
    }

    [Theory(Explicit = true, Timeout = 5000)]
    [InlineData("proxyCommand", "A")]
    [InlineData("proxyCommand", "A with spaces")]
    [InlineData("localForward", "A")]
    [InlineData("localForward", "A with spaces")]
    public async Task RealTuiSelection_PersistsTheSharedRoute_ForAnActualHttpFetchConsumer(string mode, string selectedMachine)
    {
        var executable = FleetSshEndToEndTests.RequireEnvironment();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        await using var selected = new TestForwardProxy(forward: true);
        await using var other = new TestForwardProxy(forward: true);
        await using var baseline = new TestForwardProxy(forward: true);
        var root = RealHostProcesses.CreateHome("b80t");
        try
        {
            var home = PrepareTuiHome(root, ssh, selected, other, baseline, mode);
            var store = new MachineStore(home);
            store.Upsert(store.Load().Find("A")! with { Name = selectedMachine });
            var before = HarnessSettings.Load(home).Network!.OutboundProxy!.Url;
            using var terminal = new PtyHost();
            var session = await StartTuiAsync(terminal, executable, root);
            await WaitUntilAsync(() => TuiReady(session), "The real TUI must render its input before commands are sent.");
            await session.WriteAsync(Encoding.UTF8.GetBytes($"/proxy via {selectedMachine}\r"), Ct);
            await WaitUntilAsync(() => HarnessSettings.Load(home).Network?.BorrowedOutboundMachine == selectedMachine, "The real TUI must persist its validated selection.");

            using (var outbound = new OutboundProxy())
            {
                await using var route = new BorrowedOutboundRoute(home, outbound);
                await using var lease = await route.AcquireAsync(Ct);
                var result = await new HttpFetchProvider(proxy: outbound).Fetch(new WebFetchRequest(HttpsTarget), Ct);
                Assert.Equal(200, result.StatusCode);
                Assert.Contains("Example Domain", result.Body.Content);
            }
            Assert.Contains(selected.Requests, request => request.StartsWith("CONNECT ", StringComparison.Ordinal));
            Assert.Empty(other.Requests);
            Assert.Equal(before, HarnessSettings.Load(home).Network?.OutboundProxy?.Url);
            var saved = await File.ReadAllTextAsync(home.SettingsFile, Ct);
            await session.WriteAsync(Encoding.UTF8.GetBytes("/proxy via missing-machine\r"), Ct);
            await WaitUntilAsync(() => TuiText(session, out _).Contains("selected outbound machine does not exist", StringComparison.Ordinal),
                "An invalid TUI selection must produce an explicit command error.");
            Assert.True(saved == await File.ReadAllTextAsync(home.SettingsFile, Ct), "An invalid TUI selection must leave settings unchanged.");
            await session.WriteAsync(Encoding.UTF8.GetBytes("/proxy direct\r"), Ct);
            await WaitUntilAsync(() => HarnessSettings.Load(home).Network?.BorrowedOutboundMachine is null, "Direct must clear only the borrowed selection.");
            await session.WriteAsync(Encoding.UTF8.GetBytes("/exit\r"), Ct);
            await session.WaitForExitAsync(Ct);
            Assert.Equal(0, session.ExitCode);
            await FleetLocalForwardEgressTests.AssertOwnedSshRetiredAsync(root, mode, selected.Port);
            Assert.Empty(other.Requests);
        }
        finally
        {
            RealHostProcesses.KillAll(root);
            TempTree.Delete(root);
        }
    }

    [Theory(Explicit = true, Timeout = 5000)]
    [InlineData("proxyCommand")]
    [InlineData("localForward")]
    [Trait("Category", "LlmSmoke")]
    public async Task RealTuiModel_WebFetchAndSearch_AndLocalHostReload_UseTheSameSavedBorrowedSelection(string mode)
    {
        var executable = FleetSshEndToEndTests.RequireEnvironment();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        await using var selected = new TestForwardProxy(forward: true);
        await using var other = new TestForwardProxy(forward: true);
        await using var baseline = new TestForwardProxy(forward: true);
        var root = RealHostProcesses.CreateHome("b80m");
        try
        {
            var home = new HarnessHome(root);
            var (model, providerHost) = PrepareModelHome(home);
            PrepareTuiHome(root, ssh, selected, other, baseline, mode);
            using var hostProcess = RealHostProcesses.StartHost(executable, root);
            await using var host = await RemoteHostClient.ConnectAsync(await RemoteHostConnector.ConnectAsync(root, Ct,
                startupLifetime: hostProcess.WaitForExitAsync(Ct)), null, "borrowed-route-acceptance", Ct);
            using var terminal = new PtyHost();
            var tui = await StartTuiAsync(terminal, executable, root);
            await WaitForTuiReadyAsync(tui);
            await tui.WriteAsync(Encoding.UTF8.GetBytes("/proxy via A\r"), Ct);
            await WaitUntilAsync(() => HarnessSettings.Load(home).Network?.BorrowedOutboundMachine == "A", "The real TUI must select A.");
            var tuiFetchAddresses = await WebFetchHttpNetwork.ResolvePublicAddresses(new Uri(HttpsTarget).Host, Ct);
            var tuiTrafficStart = selected.Requests.ToArray().Length;
            var persistedTurn = WaitForPersistedTurnAsync(home);
            await tui.WriteAsync(Encoding.UTF8.GetBytes(ModelWebPrompt + "\r"), Ct);
            var completed = await persistedTurn;
            await CaptureCompletedTuiAsync(tui);
            await AssertModelWebTurnAsync(completed, selected.Requests.ToArray()[tuiTrafficStart..], providerHost, tuiFetchAddresses, home);

            await host.Network.ReloadAsync(Ct);
            var hostFetchAddresses = await WebFetchHttpNetwork.ResolvePublicAddresses(new Uri(HttpsTarget).Host, Ct);
            var hostTrafficStart = selected.Requests.ToArray().Length;
        var hostSession = await host.CreateSessionAsync(root, cancellationToken: Ct);
            var hostEvents = ReadHostTurnAsync(host, hostSession.Id);
            await host.SendMessageAsync(hostSession.Id, "Use web_fetch to read https://example.com/ and briefly summarize its contents without using other tools.", [], Ct);
            var events = await hostEvents;
            Assert.IsType<TurnEndReason.Completed>(Assert.IsType<TurnEndPayload>(events[^1].Data).Reason);
            var hostFetchCalls = events.Select(value => value.Data).OfType<ToolCallPayload>()
                .Where(call => call.Name == "web_fetch").Select(call => call.CallId).ToHashSet();
            Assert.NotEmpty(hostFetchCalls);
            Assert.Contains(events.Select(value => value.Data).OfType<ToolResultPayload>(),
                result => hostFetchCalls.Contains(result.Message.Block.ToolCallId) && result.Error is null
                    && WebFetchTool.FetchMetaFromResult(result.Meta) is { StatusCode: 200, Url: HttpsTarget }
                    && result.Message.Block.Content.OfType<TextBlock>().Any(block => block.Text.Contains("Example Domain", StringComparison.Ordinal)));
            var hostTraffic = selected.Requests.ToArray()[hostTrafficStart..];
            Assert.Contains(hostTraffic, request => request == $"CONNECT {providerHost}:443 HTTP/1.1");
            AssertPinnedFetchTraffic(hostTraffic, hostFetchAddresses);
            Assert.Empty(other.Requests);
            await tui.WriteAsync(Encoding.UTF8.GetBytes("/exit\r"), Ct);
            await tui.WaitForExitAsync(Ct);
            Assert.Equal(0, tui.ExitCode);
            Assert.True(await host.PingAsync(Ct) > 0, "Exiting TUI must leave the independent local Host alive.");
            await host.Network.ReloadAsync(Ct);
            HarnessSettings.SaveBorrowedOutboundMachine(home, null);
            await host.Network.ReloadAsync(Ct);
            await FleetLocalForwardEgressTests.AssertOwnedSshRetiredAsync(root, mode, selected.Port);
        }
        finally
        {
            RealHostProcesses.KillAll(root);
            TempTree.Delete(root);
        }
    }

    private const string ModelWebPrompt =
        "Use web_fetch to read https://example.com/ and web_search with engine github-repos and query 'runtime in:name org:dotnet' to find the official dotnet runtime repository. " +
        "Do both actual tool calls, then summarize Example Domain and cite at least one usable search source URL. " +
        "Do not use other tools.";

    [Fact(Explicit = true, Timeout = SharedSettingsTimeoutMilliseconds)]
    [Trait("Category", "OnlyGpu")]
    [Trait("Category", "LlmSmoke")]
    public async Task RealGuiAndTui_BidirectionalSavesAndReload_UseTheSameBaseline()
    {
        var executable = FleetSshEndToEndTests.RequireEnvironment();
        var guard = FleetSshEndToEndTests.RealHomeGuard.Capture();
        await using var first = new TestForwardProxy(forward: true);
        await using var second = new TestForwardProxy(forward: true);
        var root = RealHostProcesses.CreateHome("ngst");
        var phase = "prepare";
        try
        {
            var home = new HarnessHome(root);
            var (_, providerHost) = PrepareModelHome(home);
            var firstUrl = $"http://127.0.0.1:{first.Port}";
            var secondUrl = $"http://127.0.0.1:{second.Port}";
            SaveSurfaceSettings(home, firstUrl, HttpsTarget);
            using var terminal = new PtyHost();
            var tui = await StartTuiAsync(terminal, executable, root);
            phase = "tui-ready";
            await WaitForTuiReadyAsync(tui);
            phase = "gui-save";
            await using (var gui = await NetworkGuiProcess.StartAsync(executable, root, SurfaceEvidence("shared-save"), TestContext.Current.CancellationToken))
            {
                await gui.ClickAsync("text", "设置", Ct);
                await gui.ClickAsync("text", "网络", Ct);
                await gui.ClickAsync("id", "LocalProxyEdit", Ct);
                await gui.TextAsync("id", "LocalProxyUrl", secondUrl, Ct);
                await gui.ClickAsync("id", "LocalProxySave", Ct);
                await WaitUntilAsync(() => HarnessSettings.Load(home).Network?.OutboundProxy?.Url == secondUrl, "The actual GUI must save the shared baseline.");
                phase = "tui-reload";
                await Console.Out.WriteLineAsync($"SHARED_SETTINGS phase={phase}");
                await tui.WriteAsync("/proxy reload\r"u8.ToArray(), Ct);
                await WaitForTuiTextAsync(tui, secondUrl);
                phase = "tui-query";
                await Console.Out.WriteLineAsync($"SHARED_SETTINGS phase={phase}");
                await tui.WriteAsync("/proxy"u8.ToArray(), Ct);
                await WaitForTuiTextAsync(tui, "Commands -");
                await tui.WriteAsync("\x1b"u8.ToArray(), Ct);
                await WaitForTuiTextAsync(tui, "ready - Enter to send");
                await tui.WriteAsync("\r"u8.ToArray(), Ct);
                await WaitForTuiTextAsync(tui, $"本机出口代理: 当前生效 {secondUrl}");
                phase = "tui-reloaded-model";
                var addresses = await WebFetchHttpNetwork.ResolvePublicAddresses(new Uri(HttpsTarget).Host, Ct);
                var traffic = second.Requests.Count;
                var turn = WaitForPersistedTurnAsync(home);
                await tui.WriteAsync(Encoding.UTF8.GetBytes(ModelWebPrompt + "\r"), Ct);
                await AssertModelWebTurnAsync(await turn, second.Requests.ToArray()[traffic..], providerHost, addresses, home);
                await CaptureCompletedTuiAsync(tui);
                phase = "tui-reverse-save";
                await tui.WriteAsync(Encoding.UTF8.GetBytes($"/proxy set {firstUrl}\r"), Ct);
                await WaitUntilAsync(() => HarnessSettings.Load(home).Network?.OutboundProxy?.Url == firstUrl, "The actual TUI must save its reverse update in the shared file.");
                phase = "tui-exit";
                await tui.WriteAsync("/exit\r"u8.ToArray(), Ct);
                await tui.WaitForExitAsync(Ct);
                Assert.Equal(0, tui.ExitCode);
            }
            phase = "gui-reread";
            await using (var gui = await NetworkGuiProcess.StartAsync(executable, root, SurfaceEvidence("shared-read"), Ct))
            {
                await using var host = await RemoteHostClient.ConnectAsync(await RemoteHostConnector.ConnectAsync(root, Ct),
                    null, "shared-settings-reader", Ct);
                await gui.ClickAsync("text", "设置", Ct);
                await gui.ClickAsync("text", "网络", Ct);
                await gui.ClickAsync("id", "LocalProxySave", Ct);
                await gui.FindAsync("enabled", "id|LocalProxySave", Ct);
                phase = "gui-reverse-model";
                await RunGuiWebSessionAsync(gui, host, home, Path.Combine(root, "reverse-gui"), 0, first, providerHost, first.Requests.Count);
                Assert.Equal(firstUrl, HarnessSettings.Load(home).Network?.OutboundProxy?.Url);
            }
        }
        finally
        {
            await Console.Out.WriteLineAsync($"SHARED_SETTINGS phase={phase}");
            await DeleteSurfaceHomeAsync(root);
            guard.AssertUnchanged();
        }
    }

    [Fact(Explicit = true, Timeout = 5000)]
    [Trait("Category", "OnlyGpu")]
    public async Task RealGuiSettings_ThreeLayers_DraftCancellationAndMachineIsolation_AtBothSizesAndThemes()
    {
        var executable = FleetSshEndToEndTests.RequireEnvironment();
        var guard = FleetSshEndToEndTests.RealHomeGuard.Capture();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        await using var legacy = await HarnessHostFixture.CreateAsync("ngol", Ct,
            backend => new FleetProxyProbeTests.LegacyProbeBackend(backend, oldSchema: false));
        await using var notFound = new TestForwardProxy(statusCode: HttpStatusCode.NotFound);
        await using var authentication = new TestForwardProxy(requiredCredential: "fixture:required");
        await using var paused = new TestForwardProxy(pauseResponses: true, observePeerCloseWhilePaused: true);
        var localRoot = RealHostProcesses.CreateHome("ngui");
        var aRoot = RealHostProcesses.CreateHome("nga");
        var bRoot = RealHostProcesses.CreateHome("ngb");
        try
        {
            var local = new HarnessHome(localRoot);
            SaveSurfaceSettings(local, $"http://127.0.0.1:{notFound.Port}", HttpTarget);
            SaveSurfaceSettings(new(aRoot), $"http://127.0.0.1:{paused.Port}", HttpTarget);
            SaveSurfaceSettings(new(bRoot), $"http://127.0.0.1:{notFound.Port}", HttpTarget);
            var a = ssh.Machine("A", aRoot);
            var b = ssh.Machine("B", bRoot);
            new MachineStore(local).Upsert(a);
            new MachineStore(local).Upsert(b);
            new MachineStore(local).Upsert(MachineSpec.Local(legacy.Root, "legacy"));
            await InstallSurfaceBuildAsync(executable, ssh, a);
            using var aProcess = RealHostProcesses.StartHost(executable, aRoot);
            using var bProcess = RealHostProcesses.StartHost(executable, bRoot);
            await using var aHost = await ConnectSurfaceHostAsync(aRoot, aProcess);
            await using var bHost = await ConnectSurfaceHostAsync(bRoot, bProcess);
            await using var gui = await NetworkGuiProcess.StartAsync(executable, localRoot, SurfaceEvidence("settings"), Ct);
            await gui.ClickAsync("text", "设置", Ct);
            await gui.ClickAsync("text", "网络", Ct);
            var saved = await File.ReadAllTextAsync(local.SettingsFile, Ct);
            await gui.ClickAsync("id", "LocalProxyProbe", Ct);
            await AssertGuiProbeAsync(gui, "LocalProxyProbe", ProxyProbeOutcome.Passed, ProxyProbeStage.HttpTarget, 404);
            await gui.ClickAsync("id", "LocalProxyEdit", Ct);
            await gui.TextAsync("id", "LocalProxyUrl", $"http://127.0.0.1:{authentication.Port}", Ct);
            await gui.ClickAsync("id", "LocalProxyProbe", Ct);
            await AssertGuiProbeAsync(gui, "LocalProxyProbe", ProxyProbeOutcome.Failed, ProxyProbeStage.ProxyAuth, 407);
            await gui.TextAsync("id", "LocalProxyUrl", "http://127.0.0.1:1", Ct);
            await gui.ClickAsync("id", "LocalProxyProbe", Ct);
            await AssertGuiProbeAsync(gui, "LocalProxyProbe", ProxyProbeOutcome.Failed, ProxyProbeStage.ProxyConnect);
            await gui.TextAsync("id", "LocalProxyUrl", $"http://127.0.0.1:{paused.Port}", Ct);
            await gui.ClickAsync("id", "LocalProxyProbe", Ct);
            await paused.WaitForRequestAsync(Ct);
            await gui.ClickAsync("id", "LocalProxyCancel", Ct);
            await AssertGuiProbeAsync(gui, "LocalProxyProbe", ProxyProbeOutcome.Cancelled);
            await WaitForProbeDisconnectAsync(paused);
            Assert.Equal(saved, await File.ReadAllTextAsync(local.SettingsFile, Ct));

            // Leaving the page cancels a real accepted request, rather than merely hiding its status.
            await gui.ClickAsync("id", "LocalProxyProbe", Ct);
            await paused.WaitForRequestAsync(Ct);
            await gui.ClickAsync("text", "机器", Ct);
            await WaitForProbeDisconnectAsync(paused);
            await gui.ClickAsync("label", "A", Ct);
            await gui.ClickAsync("id", "SshProbe", Ct);
            await AssertGuiProbeAsync(gui, "SshProbe", ProxyProbeOutcome.Passed);

            await gui.TextAsync("id", "SshProxy", "false", Ct);
            await gui.ClickAsync("id", "SshProbe", Ct);
            await AssertGuiProbeAsync(gui, "SshProbe", ProxyProbeOutcome.Failed, ProxyProbeStage.Ssh);
            var marker = Path.Combine(localRoot, "ssh-entered");
            var entered = WaitForFileAsync(marker);
            await gui.TextAsync("id", "SshProxy", $"sh -c {RemoteShell.Quote($"printf ready > {RemoteShell.Quote(marker)}; exec 3>&1; exec cat >/dev/null")}", Ct);
            await gui.ClickAsync("id", "SshProbe", Ct);
            await entered;
            await gui.ClickAsync("id", "SshProbeCancel", Ct);
            await AssertGuiProbeAsync(gui, "SshProbe", ProxyProbeOutcome.Cancelled);
            Assert.Equal(saved, await File.ReadAllTextAsync(local.SettingsFile, Ct));

            await gui.ClickAsync("id", "RemoteRead", Ct);
            await gui.FindAsync("id", "RemoteHome", Ct, aRoot);
            var remoteSaved = await File.ReadAllTextAsync(new HarnessHome(aRoot).SettingsFile, Ct);

            await gui.TextAsync("id", "RemoteProxyUrl", $"http://127.0.0.1:{authentication.Port}", Ct);
            await gui.ClickAsync("id", "RemoteProbe", Ct);
            await AssertGuiProbeAsync(gui, "RemoteProbe", ProxyProbeOutcome.Failed, ProxyProbeStage.ProxyAuth, 407);
            await gui.TextAsync("id", "RemoteProxyUrl", "http://127.0.0.1:1", Ct);
            await gui.ClickAsync("id", "RemoteProbe", Ct);
            await AssertGuiProbeAsync(gui, "RemoteProbe", ProxyProbeOutcome.Failed, ProxyProbeStage.ProxyConnect);
            await gui.TextAsync("id", "RemoteProxyUrl", $"http://127.0.0.1:{paused.Port}", Ct);
            await gui.ClickAsync("id", "RemoteProbe", Ct);
            await paused.WaitForRequestAsync(Ct);
            await gui.ClickAsync("id", "RemoteProbeCancel", Ct);
            await AssertGuiProbeAsync(gui, "RemoteProbe", ProxyProbeOutcome.Cancelled);
            await WaitForProbeDisconnectAsync(paused);
            Assert.Equal(remoteSaved, await File.ReadAllTextAsync(new HarnessHome(aRoot).SettingsFile, Ct));
            await gui.ClickAsync("id", "RemoteProbe", Ct);
            await paused.WaitForRequestAsync(Ct);
            await gui.ClickAsync("label", "B", Ct);
            await WaitForProbeDisconnectAsync(paused);
            paused.ReleaseResponse();
            await gui.ClickAsync("id", "RemoteRead", Ct);
            await gui.FindAsync("id", "RemoteHome", Ct, bRoot);
            await gui.ClickAsync("id", "RemoteProbe", Ct);
            var current = await AssertGuiProbeAsync(gui, "RemoteProbe", ProxyProbeOutcome.Passed, ProxyProbeStage.HttpTarget, 404);
            Assert.Contains("|B|", current.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(aRoot, current.Text, StringComparison.Ordinal);
            await gui.ClickAsync("id", "RemoteClear", Ct);
            await gui.FindAsync("remote-state", "", Ct);
            Assert.Null((await bHost.Network.GetOutboundAsync(Ct)).Settings.Url);
            await SetGuiRemoteProxyAsync(gui, "B", bRoot, $"http://127.0.0.1:{notFound.Port}", HttpTarget);
            Assert.Equal($"http://127.0.0.1:{notFound.Port}", (await bHost.Network.GetOutboundAsync(Ct)).Settings.Url);
            Assert.Equal(saved, await File.ReadAllTextAsync(local.SettingsFile, Ct));
            // The original A file still contains the saved endpoint, not either failed or cancelled draft.
            Assert.Equal(remoteSaved, await File.ReadAllTextAsync(new HarnessHome(aRoot).SettingsFile, Ct));
            await gui.ClickAsync("label", MachineSpec.LocalName, Ct);
            await gui.ClickAsync("id", "SshProbe", Ct);
            await AssertGuiProbeAsync(gui, "SshProbe", ProxyProbeOutcome.Unsupported);
            await gui.ClickAsync("label", "legacy", Ct);
            await gui.ClickAsync("id", "RemoteProbe", Ct);
            await AssertGuiProbeAsync(gui, "RemoteProbe", ProxyProbeOutcome.Unsupported);

            foreach (var theme in new[] { "Dark", "Light" })
            {
                await gui.ClickAsync("text", "外观", Ct);
                await gui.ClickAsync("id", "Theme" + theme, Ct);
                await gui.ClickAsync("text", "机器", Ct);
                await gui.ClickAsync("label", "B", Ct);
                foreach (var size in new[] { (1280, 900), (960, 720) })
                {
                    await gui.ResizeAsync(size.Item1, size.Item2, Ct);
                    await gui.ClickAsync("label", "B", Ct);
                    await gui.ClickAsync("id", "RemoteProbe", Ct);
                    await AssertGuiProbeAsync(gui, "RemoteProbe", ProxyProbeOutcome.Passed, ProxyProbeStage.HttpTarget, 404);
                    await gui.ScreenshotAsync($"remote-{theme.ToLowerInvariant()}-{size.Item1}x{size.Item2}.png", Ct);
                    await gui.ClickAsync("text", "网络", Ct);
                    await gui.ClickAsync("id", "LocalProxyEdit", Ct);
                    await gui.TextAsync("id", "LocalProxyUrl", "http://127.0.0.1:1", Ct);
                    await gui.ClickAsync("id", "LocalProxyProbe", Ct);
                    await AssertGuiProbeAsync(gui, "LocalProxyProbe", ProxyProbeOutcome.Failed, ProxyProbeStage.ProxyConnect);
                    await gui.ScreenshotAsync($"network-{theme.ToLowerInvariant()}-{size.Item1}x{size.Item2}.png", Ct);
                    await gui.ClickAsync("text", "机器", Ct);
                }
            }
            await gui.ExitAsync(Ct);
            Assert.True(await aHost.PingAsync(Ct) > 0 && await bHost.PingAsync(Ct) > 0);
        }
        finally
        {
            foreach (var root in new[] { localRoot, aRoot, bRoot })
            {
                await DeleteSurfaceHomeAsync(root);
            }
            guard.AssertUnchanged();
        }
    }

    [Theory(Explicit = true, Timeout = 5000)]
    [InlineData("proxyCommand")]
    [InlineData("localForward")]
    [Trait("Category", "OnlyGpu")]
    [Trait("Category", "LlmSmoke")]
    public async Task RealGuiModel_LocalBorrowAndRemoteWorkspaces_PreserveIndependentConsumersAcrossReloadAndExit(string mode)
    {
        var executable = FleetSshEndToEndTests.RequireEnvironment();
        var guard = FleetSshEndToEndTests.RealHomeGuard.Capture();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        await using var aProxy = new TestForwardProxy(forward: true);
        await using var bProxy = new TestForwardProxy(forward: true);
        await using var baseline = new TestForwardProxy(forward: true);
        await using var rejected = new TestForwardProxy(requiredCredential: "fixture:required");
        var localRoot = RealHostProcesses.CreateHome("nglm");
        var aRoot = RealHostProcesses.CreateHome("ngma");
        var bRoot = RealHostProcesses.CreateHome("ngmb");
        try
        {
            var home = new HarnessHome(localRoot);
            var (model, providerHost) = PrepareModelHome(home);
            PrepareModelHome(new(aRoot));
            PrepareModelHome(new(bRoot));
            SaveSurfaceSettings(home, $"http://127.0.0.1:{baseline.Port}", HttpsTarget);
            SaveSurfaceSettings(new(aRoot), $"http://127.0.0.1:{baseline.Port}", HttpsTarget);
            SaveSurfaceSettings(new(bRoot), $"http://127.0.0.1:{baseline.Port}", HttpsTarget);
            var a = BorrowedOutboundProxyTests.ObserveSsh(ssh.Machine("A", aRoot), localRoot) with
            {
                OutboundTunnel = new(mode, $"http://127.0.0.1:{aProxy.Port}"),
            };
            var b = ssh.Machine("B", bRoot) with { OutboundTunnel = new(mode, $"http://127.0.0.1:{bProxy.Port}") };
            new MachineStore(home).Upsert(a);
            new MachineStore(home).Upsert(b);
            await InstallSurfaceBuildAsync(executable, ssh, a);
            await SaveRemoteSurfaceProxiesThroughCliAsync(executable, home, aRoot, bRoot, aProxy, bProxy);
            using var localProcess = RealHostProcesses.StartHost(executable, localRoot);
            using var aProcess = RealHostProcesses.StartHost(executable, aRoot);
            using var bProcess = RealHostProcesses.StartHost(executable, bRoot);
            await using var localHost = await ConnectSurfaceHostAsync(localRoot, localProcess);
            await using var aHost = await ConnectSurfaceHostAsync(aRoot, aProcess);
            await using var bHost = await ConnectSurfaceHostAsync(bRoot, bProcess);
            var restartedA = await aHost.Network.GetOutboundAsync(Ct);
            var restartedB = await bHost.Network.GetOutboundAsync(Ct);
            Assert.Equal((aRoot, $"http://127.0.0.1:{aProxy.Port}"), (restartedA.HostHome, restartedA.Settings.Url));
            Assert.Equal((bRoot, $"http://127.0.0.1:{bProxy.Port}"), (restartedB.HostHome, restartedB.Settings.Url));
            var aPty = await aHost.Pty.StartPtyAsync(new RemotePtyStartRequest("/bin/sh", [], aRoot, Title: "original-a"), Ct);
            var bPty = await bHost.Pty.StartPtyAsync(new RemotePtyStartRequest("/bin/sh", [], bRoot, Title: "original-b"), Ct);
            await using (var gui = await NetworkGuiProcess.StartAsync(executable, localRoot, SurfaceEvidence("model-" + mode), Ct))
            {
                await gui.ClickAsync("text", "设置", Ct);
                await gui.ClickAsync("text", "网络", Ct);
                await gui.ClickAsync("id", "BorrowedMachine", Ct);
                await gui.KeyAsync(Ct, NetworkGuiKey.Down);
                await gui.KeyAsync(Ct, NetworkGuiKey.Enter);
                await gui.FindAsync("label", "A", Ct);
                await gui.ClickAsync("id", "BorrowedSelect", Ct);
                await WaitUntilAsync(() => HarnessSettings.Load(home).Network?.BorrowedOutboundMachine == "A", "The actual GUI must finish saving its validated borrowed selection.");
                await gui.ClickAsync("id", "BorrowedProbe", Ct);
                await AssertGuiProbeAsync(gui, "BorrowedProbe", ProxyProbeOutcome.Passed);
                Assert.Equal("A", HarnessSettings.Load(home).Network!.BorrowedOutboundMachine);
                Assert.Equal($"http://127.0.0.1:{baseline.Port}", HarnessSettings.Load(home).Network!.OutboundProxy!.Url);

                var localSaved = await File.ReadAllTextAsync(home.SettingsFile, Ct);
                await ReadGuiRemoteProxyAsync(gui, "A", aRoot);
                await gui.FindAsync("remote-state", $"http://127.0.0.1:{aProxy.Port}", Ct);
                await ReadGuiRemoteProxyAsync(gui, "B", bRoot);
                await gui.FindAsync("remote-state", $"http://127.0.0.1:{bProxy.Port}", Ct);
                await SetGuiRemoteProxyAsync(gui, "A", aRoot, $"http://127.0.0.1:{aProxy.Port}");
                Assert.Equal($"http://127.0.0.1:{aProxy.Port}", (await aHost.Network.GetOutboundAsync(Ct)).Settings.Url);
                Assert.True(localSaved == await File.ReadAllTextAsync(home.SettingsFile, Ct), "Saving a remote Host's proxy must not change the local settings file.");

                var localTurn = WaitForNewHostTurnAsync(localHost, localRoot);
                var localTraffic = aProxy.Requests.Count;
                var addresses = await WebFetchHttpNetwork.ResolvePublicAddresses("example.com", Ct);
                await gui.ClickAsync("text", "返回对话", Ct);
                await gui.TextAsync("id", "InputBox", ModelWebPrompt, Ct);
                await gui.KeyAsync(Ct, NetworkGuiKey.Enter);
                var localCitation = await AssertModelWebTurnAsync(await localTurn, aProxy.Requests.ToArray()[localTraffic..], providerHost, addresses, home);
                await gui.FindAsync("answer", localCitation, Ct);
                await gui.ScreenshotAsync("answer-local-borrow.png", Ct);

                // Each action goes through the real dashboard, new-session form and selected machine's backend.
                var aTraffic = aProxy.Requests.Count;
                var bTraffic = bProxy.Requests.Count;
                // Workspaces reuse their Host's existing provider and search CONNECT tunnels.
                foreach (var target in new[] { (Machine: "A", Host: aHost, Root: aRoot, Proxy: aProxy, Traffic: aTraffic), (Machine: "B", Host: bHost, Root: bRoot, Proxy: bProxy, Traffic: bTraffic) })
                {
                    foreach (var workspace in new[] { "one", "two" })
                    {
                        await RunGuiWebSessionAsync(gui, target.Host, new(target.Root), Path.Combine(target.Root, workspace),
                            target.Machine == "A" ? 1 : 2, target.Proxy, providerHost, target.Traffic);
                    }
                }
                // Only A's Host-owned outbound settings change; the local borrowed tunnel still targets its healthy proxy.
                await gui.ClickAsync("text", "设置", Ct);
                await SetGuiRemoteProxyAsync(gui, "A", aRoot, $"http://127.0.0.1:{rejected.Port}");
                Assert.Equal($"http://127.0.0.1:{rejected.Port}", (await aHost.Network.GetOutboundAsync(Ct)).Settings.Url);
                foreach (var workspace in new[] { "bad-one", "bad-two" })
                {
                    var project = Path.Combine(aRoot, workspace);
                    Directory.CreateDirectory(project);
                    var turn = WaitForNewHostTurnAsync(aHost, project);
                    var traffic = rejected.HttpAttempts.Count;
                    await StartGuiSessionAsync(gui, 1, project);
                    var events = await turn;
                    Assert.Contains(events, value => value.Data is TurnStartPayload);
                    var failure = Assert.IsType<TurnEndReason.Error>(events.Select(value => value.Data)
                        .OfType<TurnEndPayload>().Last().Reason).Failure;
                    Assert.False(string.IsNullOrWhiteSpace(failure.Code));
                    Assert.Contains("407", failure.Message, StringComparison.Ordinal);
                    Assert.Contains(rejected.HttpAttempts.ToArray()[traffic..],
                        request => request == $"CONNECT {providerHost}:443 HTTP/1.1");
                    await gui.FindAsync("turn-error", failure.Code, Ct);
                }
                await RunGuiWebSessionAsync(gui, bHost, new(bRoot), Path.Combine(bRoot, "isolation-b"), 2, bProxy, providerHost, bTraffic);
                await RunGuiWebSessionAsync(gui, localHost, home, Path.Combine(localRoot, "isolation-local"), 0, aProxy, providerHost, localTraffic);
                Assert.Equal($"http://127.0.0.1:{rejected.Port}", (await aHost.Network.GetOutboundAsync(Ct)).Settings.Url);
                Assert.Equal($"http://127.0.0.1:{bProxy.Port}", (await bHost.Network.GetOutboundAsync(Ct)).Settings.Url);
                Assert.Equal("A", HarnessSettings.Load(home).Network!.BorrowedOutboundMachine);
                Assert.Equal($"http://127.0.0.1:{baseline.Port}", HarnessSettings.Load(home).Network!.OutboundProxy!.Url);
                await gui.ClickAsync("text", "设置", Ct);
                await SetGuiRemoteProxyAsync(gui, "A", aRoot, $"http://127.0.0.1:{aProxy.Port}");
                Assert.Equal($"http://127.0.0.1:{aProxy.Port}", (await aHost.Network.GetOutboundAsync(Ct)).Settings.Url);
                await RunGuiWebSessionAsync(gui, aHost, new(aRoot), Path.Combine(aRoot, "restored"), 1, aProxy, providerHost, aProxy.Requests.Count);
                var guiShell = await AddGuiOwnedShellAsync(gui, aHost, Path.Combine(aRoot, "restored"), mode);
                await AssertIndependentHostTurnAsync(localHost, home, model, aProxy, providerHost, localTraffic);
                await gui.ClickAsync("text", "设置", Ct);
                await gui.ClickAsync("text", "机器", Ct);
                await gui.ClickAsync("label", "A", Ct);
                await gui.FindAsync("fleet-baseline", "", Ct);
                await gui.ClickAsync("text", "保存", Ct); // The production machine-save callback reloads FleetWorkspace.
                await gui.FindAsync("fleet-reloaded", "", Ct);
                Assert.Equal(HostSessionStatuses.Exited, Assert.Single(await aHost.ListHostSessionsAsync(Ct),
                    session => session.SessionId == guiShell).Status);
                Assert.True(await aHost.PingAsync(Ct) > 0 && await bHost.PingAsync(Ct) > 0);
                Assert.Equal(HostSessionStatuses.Running, Assert.Single(await aHost.ListHostSessionsAsync(Ct),
                    session => session.SessionId == aPty).Status);
                Assert.Equal(HostSessionStatuses.Running, Assert.Single(await bHost.ListHostSessionsAsync(Ct),
                    session => session.SessionId == bPty).Status);
                await RunGuiWebSessionAsync(gui, bHost, new(bRoot), Path.Combine(bRoot, "after-reload"), 2, bProxy, providerHost, bTraffic);
                await AssertIndependentHostTurnAsync(localHost, home, model, aProxy, providerHost, localTraffic);
                await gui.ExitAsync(Ct);
                await AssertIndependentHostTurnAsync(localHost, home, model, aProxy, providerHost, localTraffic);
            }
            await AssertRemoteSurvivorsAsync(aHost, aPty, bHost, bPty);
            // Ending only the last owned local route consumer must retire its tunnels, not the target Host/PTY.
            await EndSurfaceHostAsync(localProcess);
            await AssertSurfaceSshRetiredAsync(localRoot, mode);
            await AssertRemoteSurvivorsAsync(aHost, aPty, bHost, bPty);
        }
        finally
        {
            foreach (var root in new[] { localRoot, aRoot, bRoot })
            {
                await DeleteSurfaceHomeAsync(root);
            }
            guard.AssertUnchanged();
        }
    }

    [Fact(Explicit = true, Timeout = 5000)]
    [Trait("Category", "LlmSmoke")]
    public async Task RealSshTuiModel_RemoteAAndB_CompleteWebFetchAndSearchThroughTheirOwnHostSettings()
    {
        var executable = FleetSshEndToEndTests.RequireEnvironment();
        var guard = FleetSshEndToEndTests.RealHomeGuard.Capture();
        await using var ssh = await TestSshServer.StartAsync(TestContext.Current.CancellationToken);
        await using var aProxy = new TestForwardProxy(forward: true);
        await using var bProxy = new TestForwardProxy(forward: true);
        var aRoot = RealHostProcesses.CreateHome("ntra");
        var bRoot = RealHostProcesses.CreateHome("ntrb");
        try
        {
            foreach (var target in new[] { (Name: "A", Root: aRoot, Proxy: aProxy), (Name: "B", Root: bRoot, Proxy: bProxy) })
            {
                var elapsed = Stopwatch.StartNew();
                await Console.Out.WriteLineAsync($"REAL_SSH_TUI {target.Name} prepare {elapsed.ElapsedMilliseconds}ms");
                var home = new HarnessHome(target.Root);
                var (_, providerHost) = PrepareModelHome(home);
                SaveSurfaceSettings(home, $"http://127.0.0.1:{target.Proxy.Port}", HttpsTarget);
                var machine = ssh.Machine(target.Name, target.Root);
                var arguments = new List<string> { "-tt", "-p", machine.Port.ToString(CultureInfo.InvariantCulture),
                    "-i", machine.KeyPath!, "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=accept-new",
                    "-o", "IdentitiesOnly=yes", "-o", "IdentityAgent=none" };
                foreach (var option in machine.SshOptions ?? [])
                {
                    arguments.Add("-o");
                    arguments.Add(option);
                }
                arguments.Add($"{machine.User}@{machine.Host}");
                arguments.Add($"{RemoteShell.Prelude(machine)}cd {RemoteShell.Quote(target.Root)} && exec {RemoteShell.Quote(executable)} tui");
                using var terminal = new PtyHost();
                var tui = await terminal.StartAsync(new PtyStartInfo
                {
                    FileName = "ssh", Arguments = arguments, Home = target.Root, WorkingDirectory = target.Root, Columns = 140, Rows = 40,
                    Environment = new Dictionary<string, string?>
                    {
                        ["TERM"] = "xterm-256color", ["LC_ALL"] = "C",
                        [HarnessHome.HomeEnv] = target.Root, [HarnessHome.ChildHomeEnv] = target.Root,
                        ["SSH_AUTH_SOCK"] = null, ["SSH_ASKPASS"] = null, ["SSH_ASKPASS_REQUIRE"] = null, ["GIT_ASKPASS"] = null,
                    },
                }, cancellationToken: Ct);
                await Console.Out.WriteLineAsync($"REAL_SSH_TUI {target.Name} pty-started {elapsed.ElapsedMilliseconds}ms");
                await WaitForTuiReadyAsync(tui);
                await Console.Out.WriteLineAsync($"REAL_SSH_TUI {target.Name} ready {elapsed.ElapsedMilliseconds}ms");
                var completed = WaitForPersistedTurnAsync(home);
                var traffic = target.Proxy.Requests.Count;
                var addresses = await WebFetchHttpNetwork.ResolvePublicAddresses("example.com", Ct);
                await tui.WriteAsync(Encoding.UTF8.GetBytes(ModelWebPrompt + "\r"), Ct);
                await Console.Out.WriteLineAsync($"REAL_SSH_TUI {target.Name} prompt-written {elapsed.ElapsedMilliseconds}ms");
                var turn = await completed;
                await Console.Out.WriteLineAsync($"REAL_SSH_TUI {target.Name} turn-persisted {elapsed.ElapsedMilliseconds}ms");
                await AssertModelWebTurnAsync(turn, target.Proxy.Requests.ToArray()[traffic..], providerHost, addresses, home);
                await Console.Out.WriteLineAsync($"REAL_SSH_TUI {target.Name} tools-and-routes-proved {elapsed.ElapsedMilliseconds}ms");
                await CaptureCompletedTuiAsync(tui);
                await Console.Out.WriteLineAsync($"REAL_SSH_TUI {target.Name} completed-screen-captured {elapsed.ElapsedMilliseconds}ms");
                await tui.WriteAsync("/exit\r"u8.ToArray(), Ct);
                await tui.WaitForExitAsync(Ct);
                Assert.Equal(0, tui.ExitCode);
                await Console.Out.WriteLineAsync($"REAL_SSH_TUI {target.Name} exited {elapsed.ElapsedMilliseconds}ms");
            }
        }
        finally
        {
            foreach (var root in new[] { aRoot, bRoot })
            {
                await DeleteSurfaceHomeAsync(root);
            }
            guard.AssertUnchanged();
        }
    }

    private static async Task WaitForTuiReadyAsync(PtySession tui)
    {
        using var subscription = tui.Subscribe();
        await foreach (var _ in subscription.Output.ReadAllAsync(Ct))
            if (TuiReady(tui))
                return;
        throw new IOException("The real SSH TUI exited before rendering its input.");
    }

    private static async Task CaptureCompletedTuiAsync(PtySession tui)
    {
        await WaitForTuiTextAsync(tui, "ready - Enter to send");
        var evidence = SurfaceEvidence("tui-answer");
        Directory.CreateDirectory(evidence);
        await File.WriteAllTextAsync(Path.Combine(evidence, "visible-tui.txt"), TuiText(tui, out _), Ct);
        await Console.Out.WriteLineAsync($"TUI_COMPLETED_SCREEN=.artifacts/network-gui/{Path.GetFileName(evidence)}/visible-tui.txt");
    }

    private static async Task WaitForTuiTextAsync(PtySession tui, string marker)
    {
        using var subscription = tui.Subscribe();
        await foreach (var chunk in subscription.Output.ReadAllAsync(Ct))
            if (TuiText(tui, out _).Contains(marker, StringComparison.Ordinal))
                return;
        throw new IOException("The real TUI exited before displaying the completed model answer.");
    }

    private static async Task<IReadOnlyList<SessionEvent>> WaitForPersistedTurnAsync(HarnessHome home)
    {
        Directory.CreateDirectory(home.SessionsPath);
        var changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
        using var watcher = new FileSystemWatcher(home.SessionsPath) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
        watcher.Created += (_, _) => changes.Writer.TryWrite(true);
        watcher.Changed += (_, _) => changes.Writer.TryWrite(true);
        watcher.Renamed += (_, _) => changes.Writer.TryWrite(true);
        watcher.Error += (_, args) => changes.Writer.TryComplete(args.GetException());
        watcher.EnableRaisingEvents = true;
        while (true)
        {
            var events = ReadTuiTurn(home);
            if (events.Any(value => value.Data is TurnEndPayload))
                return events;
            await changes.Reader.ReadAsync(Ct);
        }
    }

    private static ((string Provider, string Model) Model, string ProviderHost) PrepareModelHome(HarnessHome home)
    {
        var source = HarnessSettings.Load(HarnessHome.Default());
        var model = source.ResolveDefaultModel() ?? throw new InvalidOperationException("The isolated acceptance home needs an authorized provider/model.");
        var provider = source.Providers[model.Provider];
        var options = provider.Options ?? throw new InvalidOperationException("The real model requires an HTTP provider.");
        var credential = options.ApiKey ?? (options.ApiKeyEnv is { } name ? new EnvCredentials(HarnessHome.Default()).Get(name) : null);
        Assert.False(string.IsNullOrWhiteSpace(credential), "The isolated provider requires its authorized credential.");
        var endpoint = new Uri(options.BaseUrl ?? throw new InvalidOperationException("The provider endpoint is required."));
        Assert.Equal(Uri.UriSchemeHttps, endpoint.Scheme);
        var settings = new HarnessSettings
        {
            GlobalDefaultModel = $"{model.Provider}/{model.Model}",
            CompactionModel = source.CompactionModel,
            Subagent = source.Subagent,
            Plugins = source.Plugins,
            Providers = new()
            {
                [model.Provider] = new ProviderSettings
                {
                    Type = provider.Type,
                    Options = new ProviderOptions { BaseUrl = options.BaseUrl, ApiKey = credential },
                    Models = provider.Models.TryGetValue(model.Model, out var description) ? new() { [model.Model] = description } : [],
                },
            },
            Logging = new LoggingSettings { File = false },
            Storage = new StorageSettings { Root = home.Root },
            Safety = new SafetySettings { AutoApprove = true },
        };
        settings.Save(home);
        settings.SavePlugins(home);
        return (model, endpoint.Host);
    }

    private static void SaveSurfaceSettings(HarnessHome home, string proxy, string probe)
    {
        var settings = HarnessSettings.Load(home);
        settings.Network = new() { OutboundProxy = new() { Url = proxy, ProbeUrl = probe } };
        settings.Save(home);
        new GuiSettings(home).Save(new GuiSettingsSnapshot { CloseAction = GuiSettings.CloseQuit, WindowWidth = 1280, WindowHeight = 900 });
    }

    private static Task<RemoteDsharpInstaller.Result> InstallSurfaceBuildAsync(string executable, TestSshServer ssh, MachineSpec machine)
        => new RemoteDsharpInstaller(new SshTransport(machine)).EnsureAsync(new DeploySettings { Via = DownloadVia.Client }, null, Ct,
            (_, rid, token) => FleetSshEndToEndTests.BuildArchiveAsync(executable, rid, ssh.Root, token));

    private static async Task<RemoteHostClient> ConnectSurfaceHostAsync(string root, Process process)
        => await RemoteHostClient.ConnectAsync(await RemoteHostConnector.ConnectAsync(root, Ct,
            startupLifetime: process.WaitForExitAsync(Ct)), null, "network-real-surface", Ct);

    private static string SurfaceEvidence(string name)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        return Path.Combine(root, ".artifacts", "network-gui", $"{name}-{Guid.NewGuid():N}");
    }

    private static async Task SetGuiRemoteProxyAsync(NetworkGuiProcess gui, string machine, string home, string proxy, string probe = HttpsTarget)
    {
        await ReadGuiRemoteProxyAsync(gui, machine, home);

        await gui.TextAsync("id", "RemoteProxyUrl", proxy, Ct);
        await gui.TextAsync("id", "RemoteProbeUrl", probe, Ct);
        await gui.ClickAsync("id", "RemoteSave", Ct);
        await gui.FindAsync("remote-state", proxy, Ct);
    }

    private static async Task ReadGuiRemoteProxyAsync(NetworkGuiProcess gui, string machine, string home)
    {
        await gui.ClickAsync("text", "机器", Ct);
        await gui.ClickAsync("label", machine, Ct);
        await gui.ClickAsync("id", "RemoteRead", Ct);
        await gui.FindAsync("id", "RemoteHome", Ct, home);
    }

    private static async Task SaveRemoteSurfaceProxiesThroughCliAsync(string executable, HarnessHome local, string aRoot,
        string bRoot, TestForwardProxy aProxy, TestForwardProxy bProxy)
    {
        using var aProcess = RealHostProcesses.StartHost(executable, aRoot);
        using var bProcess = RealHostProcesses.StartHost(executable, bRoot);
        try
        {
            await using var aHost = await ConnectSurfaceHostAsync(aRoot, aProcess);
            await using var bHost = await ConnectSurfaceHostAsync(bRoot, bProcess);
            var original = await File.ReadAllTextAsync(local.SettingsFile, Ct);
            await SaveSurfaceProxyThroughCliAsync(local, aHost, "A", aRoot, $"http://127.0.0.1:{aProxy.Port}");
            await SaveSurfaceProxyThroughCliAsync(local, bHost, "B", bRoot, $"http://127.0.0.1:{bProxy.Port}");
            Assert.True(original == await File.ReadAllTextAsync(local.SettingsFile, Ct), "Remote CLI saves must not change the local settings file.");
        }
        finally
        {
            if (!aProcess.HasExited)
                aProcess.Kill(entireProcessTree: true);
            if (!bProcess.HasExited)
                bProcess.Kill(entireProcessTree: true);
            await aProcess.WaitForExitAsync(Ct);
            await bProcess.WaitForExitAsync(Ct);
        }
    }

    private static async Task SaveSurfaceProxyThroughCliAsync(HarnessHome local, RemoteHostClient host, string machine,
        string targetHome, string proxy)
    {
        var before = await host.Network.GetOutboundAsync(Ct);
        var input = JsonSerializer.Serialize(new RemoteOutboundProxySettings(proxy, ProbeUrl: HttpsTarget),
            NetworkProtocolJsonContext.Default.RemoteOutboundProxySettings);
        var saved = await FleetRemoteProxyTests.RunCliAsync(local.Root, input, Ct, "machine", "proxy", machine, "set", "--json-stdin");
        Assert.True(saved.ExitCode == 0, "The real SSH machine-proxy CLI must save its target settings.");
        var fromCli = JsonSerializer.Deserialize(saved.Output, NetworkProtocolJsonContext.Default.RemoteOutboundProxyState)!;
        var current = await host.Network.GetOutboundAsync(Ct);
        Assert.Equal((before.OwnerId, targetHome, fromCli.Revision, proxy),
            (current.OwnerId, current.HostHome, current.Revision, current.Settings.Url));
    }

    private static async Task WaitForProbeDisconnectAsync(TestForwardProxy proxy)
    {
        while (proxy.ActiveConnections != 0)
            await proxy.WaitForClosedConnectionAsync(Ct);
    }

    private static async Task<NetworkGuiNode> AssertGuiProbeAsync(NetworkGuiProcess gui, string button, ProxyProbeOutcome outcome,
        ProxyProbeStage? stage = null, int? status = null)
    {
        var result = await gui.FindAsync("probe", button, Ct, outcome + "|");
        var fields = result.Text.Split('|');
        Assert.Equal(outcome.ToString(), fields[0]);
        if (stage is not null)
            Assert.Equal(stage.ToString(), fields[1]);
        if (status is not null)
            Assert.Equal(status.Value.ToString(CultureInfo.InvariantCulture), fields[2]);
        return result;
    }

    private static async Task StartGuiSessionAsync(NetworkGuiProcess gui, int machineIndex, string project)
    {
        await gui.ClickAsync("text", "工作台", Ct);
        await gui.ClickAsync("text", "新建会话", Ct);
        await gui.ClickAsync("id", "MachineBox", Ct);
        await gui.KeyAsync(Ct, NetworkGuiKey.Home);
        for (var index = 0; index < machineIndex; index++)
            await gui.KeyAsync(Ct, NetworkGuiKey.Down);
        await gui.KeyAsync(Ct, NetworkGuiKey.Enter);
        await gui.TextAsync("id", "ProjectBox", project, Ct);
        await gui.TextAsync("placeholder", "可选：启动后立即发送", ModelWebPrompt, Ct);
        await gui.ClickAsync("id", "StartButton", Ct);
    }

    private static async Task RunGuiWebSessionAsync(NetworkGuiProcess gui, RemoteHostClient host, HarnessHome home,
        string project, int machineIndex, TestForwardProxy proxy, string providerHost, int routeTrafficStart)
    {
        Directory.CreateDirectory(project);
        var turn = WaitForNewHostTurnAsync(host, project);
        var addresses = await WebFetchHttpNetwork.ResolvePublicAddresses("example.com", Ct);
        await StartGuiSessionAsync(gui, machineIndex, project);
        var citation = await AssertModelWebTurnAsync(await turn, proxy.Requests.ToArray()[routeTrafficStart..], providerHost, addresses, home);
        await gui.FindAsync("answer", citation, Ct);
        await gui.ScreenshotAsync($"answer-{machineIndex}-{Path.GetFileName(project)}.png", Ct);
    }

    private static async Task<IReadOnlyList<SessionEvent>> WaitForNewHostTurnAsync(RemoteHostClient host, string project)
    {
        await foreach (var change in host.HostSessionChangesAsync(Ct))
        {
            var candidates = change.Snapshot ?? (change.Session is { } session ? [session] : []);
            var current = candidates.FirstOrDefault(value => value.ProjectPath == project && value.Kind == HostSessionKinds.Harness);
            if (current is not null)
                return await ReadHostTurnAsync(host, current.SessionId);
        }
        throw new IOException("The real GUI did not create its requested Host session.");
    }

    private static async Task<string> AssertModelWebTurnAsync(IReadOnlyList<SessionEvent> events, string[] traffic, string providerHost,
        IReadOnlyList<PublicAddress> addresses, HarnessHome home)
    {
        Assert.IsType<TurnEndReason.Completed>(events.Select(value => value.Data).OfType<TurnEndPayload>().Last().Reason);
        var calls = events.Select(value => value.Data).OfType<ToolCallPayload>().ToArray();
        var fetch = calls.Where(call => call.Name == "web_fetch").Select(call => call.CallId).ToHashSet();
        var search = calls.Where(call => call.Name == "web_search").Select(call => call.CallId).ToHashSet();
        if (fetch.Count == 0 || search.Count == 0)
            Assert.Fail($"The actual model must invoke both web_fetch and web_search. Observed tools: {string.Join(", ", calls.Select(call => call.Name))}; advertised schemas: {string.Join(", ", events.Select(value => value.Data).OfType<RequestHeaderPayload>().SelectMany(value => value.Header.Tools ?? []).Select(tool => tool.Name).Distinct())}; assistant block types: {string.Join(", ", events.Select(value => value.Data).OfType<AssistantMessagePayload>().SelectMany(value => value.Message.Content).Select(block => block.GetType().Name).Distinct())}; event payloads: {string.Join(", ", events.Select(value => value.Data.GetType().Name).Distinct())}.");
        var results = events.Select(value => value.Data).OfType<ToolResultPayload>().ToArray();
        Assert.Contains(results, result => fetch.Contains(result.Message.Block.ToolCallId) && result.Error is null
            && WebFetchTool.FetchMetaFromResult(result.Meta) is { StatusCode: 200, Url: HttpsTarget }
            && result.Message.Block.Content.OfType<TextBlock>().Any(block => block.Text.Contains("Example Domain", StringComparison.Ordinal)));
        var sources = results.Where(result => result.Error is null && search.Contains(result.Message.Block.ToolCallId))
            .SelectMany(result => WebSearchTool.SearchMetaFromResult(result.Meta)?.Sources ?? []).ToArray();
        if (sources.Length == 0)
            Assert.Fail($"The actual web_search must return a usable source. Search outcomes: {string.Join("; ", results.Where(result => search.Contains(result.Message.Block.ToolCallId)).Select(result => $"error={result.Error?.Name ?? "none"}/{result.Error?.Code ?? "none"}, metadata={result.Meta?.ValueKind}, sources={WebSearchTool.SearchMetaFromResult(result.Meta)?.Sources.Count.ToString() ?? "missing"}"))}.");
        var answer = events.Select(value => value.Data).OfType<AssistantMessagePayload>()
            .Last().Message.Content.OfType<TextBlock>().ToArray();
        var citation = sources.SelectMany(source => new[] { source.Url, new Uri(source.Url).Host })
            .FirstOrDefault(value => answer.Any(block => block.Text.Contains(value, StringComparison.Ordinal)));
        Assert.NotNull(citation);
        Assert.Contains(traffic, request => request == $"CONNECT {providerHost}:443 HTTP/1.1");
        Assert.Contains(traffic, request => request.StartsWith("CONNECT api.github.com:443 ", StringComparison.Ordinal));
        AssertPinnedFetchTraffic(traffic, addresses);
        using var outbound = new OutboundProxy();
        await using var route = new BorrowedOutboundRoute(home, outbound);
        await using var lease = await route.AcquireAsync(Ct);
        if (HarnessSettings.Load(home).Network?.BorrowedOutboundMachine is null)
            outbound.Configure(HarnessSettings.Load(home).Network?.OutboundProxy?.Url, HarnessSettings.Load(home).Network?.OutboundProxy?.Bypass);
        using var client = new HttpClient(outbound.CreateHandler());
        using var response = await client.GetAsync(sources[0].Url, HttpCompletionOption.ResponseHeadersRead, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return citation;
    }

    private static async Task AssertIndependentHostTurnAsync(RemoteHostClient host, HarnessHome home,
        (string Provider, string Model) model, TestForwardProxy proxy, string providerHost, int routeTrafficStart)
    {
        Assert.True(await host.PingAsync(Ct) > 0, "GUI reload/exit must leave its independent local Host alive.");
        var addresses = await WebFetchHttpNetwork.ResolvePublicAddresses("example.com", Ct);
        var session = await host.CreateSessionAsync(home.Root, cancellationToken: Ct);
        var turn = ReadHostTurnAsync(host, session.Id);
        await host.SendMessageAsync(session.Id, ModelWebPrompt, [], Ct);
        await AssertModelWebTurnAsync(await turn, proxy.Requests.ToArray()[routeTrafficStart..], providerHost, addresses, home);
    }

    private static async Task<string> AddGuiOwnedShellAsync(NetworkGuiProcess gui, RemoteHostClient host, string project, string mode)
    {
        await gui.ResizeAsync(1280, 900, Ct);
        await gui.ClickAsync("tip", "在项目目录打开一个终端", Ct);
        var terminal = await gui.FindAsync("owned-terminal", "", Ct);
        var registered = Assert.Single(await host.ListHostSessionsAsync(Ct), session => session.SessionId == terminal.Text);
        Assert.Equal(HostSessionKinds.Terminal, registered.Kind);
        Assert.Equal(project, registered.ProjectPath);
        Assert.Equal(HostSessionStatuses.Running, registered.Status);
        const string command = "PS1='surface> '; printf '\\033[2J\\033[HGUI-OWNED-%s\\n' \"$((19*23))\"";
        const string output = "GUI-OWNED-437";
        Assert.DoesNotContain(output, command, StringComparison.Ordinal);
        await gui.PasteTerminalAsync(command, Ct);
        // The real terminal's rendered command echo acknowledges asynchronous clipboard delivery before Enter.
        await gui.FindAsync("terminal-screen", "$((19*23))", Ct);
        await gui.KeyAsync(Ct, NetworkGuiKey.Enter);
        await gui.FindAsync("terminal-screen", output, Ct);
        await gui.ScreenshotAsync($"extra-shell-{mode}.png", Ct);
        return terminal.Text;
    }

    private static async Task AssertRemoteSurvivorsAsync(RemoteHostClient aHost, string aPty, RemoteHostClient bHost, string bPty)
    {
        Assert.True(await aHost.PingAsync(Ct) > 0 && await bHost.PingAsync(Ct) > 0);
        foreach (var target in new[] { (Host: aHost, Pty: aPty), (Host: bHost, Pty: bPty) })
        {
            Assert.Contains(await target.Host.ListHostSessionsAsync(Ct),
                session => session.SessionId == target.Pty && session.Status == HostSessionStatuses.Running);
            await using var capture = RemoteHostPtyTests.TerminalCapture.Attach(target.Host, target.Pty);
            await capture.SendAsync("printf 'SURVIVOR-%s\\n' \"$((6*7))\"\n");
            await capture.WaitForAsync("SURVIVOR-42", TimeSpan.FromSeconds(5));
        }
    }

    private static async Task AssertSurfaceSshRetiredAsync(string root, string mode)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var commands = await File.ReadAllLinesAsync(Path.Combine(root, "owned-ssh-commands.txt"), deadline.Token);
        Assert.NotEmpty(commands);
        var ports = new HashSet<int>();
        foreach (var command in commands)
        {
            var arguments = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var forwarding = Array.IndexOf(arguments, "-L");
            if (forwarding >= 0)
                ports.Add(int.Parse(arguments[forwarding + 1].Split(':')[1], CultureInfo.InvariantCulture));
        }
        if (mode == "localForward")
            Assert.NotEmpty(ports);
        var resources = await File.ReadAllLinesAsync(Path.Combine(root, "owned-ssh-resources.txt"), deadline.Token);
        Assert.NotEmpty(resources);
        foreach (var resource in resources.Distinct(StringComparer.Ordinal))
        {
            var fields = resource.Split('|', 2);
            Assert.Equal(2, fields.Length);
            var pid = int.Parse(fields[0], CultureInfo.InvariantCulture);
            try
            {
                using var process = Process.GetProcessById(pid);
                await process.WaitForExitAsync(deadline.Token);
            }
            catch (ArgumentException)
            {
                // An already-reaped owned process has released its operating-system resources.
            }
            Assert.False(string.IsNullOrEmpty(fields[1]));
            Assert.False(File.Exists(fields[1]), "No GUI or independent Host route may retain its owned askpass file.");
        }
        foreach (var port in ports)
        {
            using var rebound = new System.Net.Sockets.TcpListener(IPAddress.Loopback, port);
            rebound.Start();
            Assert.Equal(port, ((IPEndPoint)rebound.LocalEndpoint).Port);
        }
    }

    private static async Task EndSurfaceHostAsync(Process host)
    {
        using var signal = TestProcessScope.Start(new ProcessStartInfo("kill", ["-TERM", host.Id.ToString(CultureInfo.InvariantCulture)])
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        }) ?? throw new IOException("Could not terminate the owned independent Host.");
        var output = signal.StandardOutput.ReadToEndAsync(Ct);
        var errors = signal.StandardError.ReadToEndAsync(Ct);
        await signal.WaitForExitAsync(Ct);
        await output;
        await errors;
        Assert.Equal(0, signal.ExitCode);
        await host.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(0, host.ExitCode);
    }

    private static async Task DeleteSurfaceHomeAsync(string root)
    {
        var processes = new List<Process>();
        try
        {
            if (Directory.Exists("/proc"))
            {
                var marker = Encoding.UTF8.GetBytes($"{HarnessHome.HomeEnv}={root}\0");
                foreach (var directory in Directory.EnumerateDirectories("/proc"))
                {
                    if (!int.TryParse(Path.GetFileName(directory), out var pid) || pid == Environment.ProcessId)
                        continue;
                    try
                    {
                        if (File.ReadAllBytes(Path.Combine(directory, "environ")).AsSpan().IndexOf(marker) >= 0)
                            processes.Add(Process.GetProcessById(pid));
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
                    {
                        // Unrelated process environments may be unreadable; an owned process may already have exited.
                    }
                }
            }
            else
            {
                var marker = $"{HarnessHome.HomeEnv}={root}";
                foreach (var line in RealHostProcesses.ProcessListing("ps", "-axwwE", "-o", "pid=", "-o", "command="))
                {
                    var fields = line.Trim().Split(' ', 2);
                    if (fields is [var id, var command] && int.TryParse(id, out var pid)
                        && (command.Contains(marker + " ", StringComparison.Ordinal) || command.EndsWith(marker, StringComparison.Ordinal)))
                    {
                        try { processes.Add(Process.GetProcessById(pid)); }
                        catch (ArgumentException) { }
                    }
                }
            }
            RealHostProcesses.KillAll(root);
            await Task.WhenAll(processes.Select(process => process.WaitForExitAsync())).WaitAsync(TimeSpan.FromSeconds(5));
            TempTree.Delete(root);
            Assert.False(Directory.Exists(root), "The owned test home must be absent after all its processes have exited.");
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    private static async Task WaitForFileAsync(string path)
    {
        using var watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path));
        var created = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Created += (_, _) => created.TrySetResult();
        watcher.EnableRaisingEvents = true;
        if (File.Exists(path))
            created.TrySetResult();
        await created.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    private static void AssertPinnedFetchTraffic(IEnumerable<string> traffic, IReadOnlyList<PublicAddress> targetAddresses)
    {
        Assert.NotEmpty(targetAddresses);
        Assert.All(targetAddresses, address => Assert.True(WebFetchHttpNetwork.IsPublicIpAddress(address.Address)));
        var expected = targetAddresses.Select(address =>
            $"CONNECT {(address.Family == 6 ? $"[{address.Address}]" : address.Address)}:443 HTTP/1.1").ToHashSet();
        Assert.Contains(traffic, expected.Contains);
    }

    private static HarnessHome PrepareTuiHome(string root, TestSshServer ssh, TestForwardProxy selected, TestForwardProxy other, TestForwardProxy baseline, string mode)
    {
        TempTree.AssertFitsMacOsSocketLimit(Path.Combine(root, "run", "host.sock"));
        var home = new HarnessHome(root);
        var settings = HarnessSettings.Load(home);
        settings.Network = new NetworkSettings { OutboundProxy = new OutboundProxySettings { Url = $"http://127.0.0.1:{baseline.Port}", ProbeUrl = HttpsTarget } };
        settings.Fleet = null;
        settings.Save(home);
        var store = new MachineStore(home);
        store.Upsert(BorrowedOutboundProxyTests.ObserveSsh(ssh.Machine("A", root), root) with { OutboundTunnel = new(mode, $"http://127.0.0.1:{selected.Port}") });
        store.Upsert(ssh.Machine("B", root) with { OutboundTunnel = new(mode, $"http://127.0.0.1:{other.Port}") });
        return home;
    }

    private static Task<PtySession> StartTuiAsync(PtyHost host, string executable, string root)
        => host.StartAsync(new PtyStartInfo
        {
            FileName = executable,
            Arguments = ["tui"],
            Home = root,
            WorkingDirectory = root,
            Columns = 140,
            Rows = 40,
            Environment = new Dictionary<string, string?>
            {
                [HarnessHome.HomeEnv] = root,
                [HarnessHome.ChildHomeEnv] = root,
                ["DSH_PTY_CHILD"] = "1",
                ["TERM"] = "xterm-256color",
                ["LC_ALL"] = "C",
            },
        }, cancellationToken: Ct);

    private static bool TuiReady(PtySession session)
    {
        var text = TuiText(session, out var alternateScreen);
        return alternateScreen && text.Contains("快捷键", StringComparison.Ordinal);
    }

    private static string TuiText(PtySession session, out bool alternateScreen)
    {
        var screen = new VtScreen(140, 40);
        screen.Feed(session.Snapshot());
        alternateScreen = screen.IsAlternateScreen;
        var text = new StringBuilder(140 * 41);
        for (var row = 0; row < screen.Height; row++)
        {
            foreach (var cell in screen.Row(row))
                if (cell.Character != '\0')
                    text.Append(cell.Character);
            text.Append('\n');
        }
        return text.ToString();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string failure, Func<string>? describeState = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            if (deadline.IsCancellationRequested)
                Assert.Fail(describeState is null ? failure : $"{failure} {describeState()}");
            await Task.Delay(50, Ct);
        }
    }

    private static IReadOnlyList<SessionEvent> ReadTuiTurn(HarnessHome home)
    {
        using var persistence = new JsonlSessionPersistence(home.SessionsPath);
        foreach (var session in persistence.List())
        {
            using var handle = persistence.Open(session.Header.Id, SessionAccess.Read);
            var events = handle.Read();
            var first = 0;
            TurnStartPayload? start = null;
            var matches = false;
            for (var index = 0; index < events.Count; index++)
                switch (events[index].Data)
                {
                    case TurnStartPayload turn:
                        start = turn;
                        first = index;
                        matches = false;
                        break;
                    case UserMessagePayload user when start is not null && user.Message.Content.OfType<TextBlock>().Any(block => block.Text == ModelWebPrompt):
                        matches = true;
                        break;
                    case TurnEndPayload end when matches && end.Turn == start?.Turn:
                        return events.Skip(first).Take(index - first + 1).ToArray();
                }
        }
        return [];
    }

    private static async Task<IReadOnlyList<SessionEvent>> ReadHostTurnAsync(RemoteHostClient host, string sessionId)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var events = new List<SessionEvent>();
        await foreach (var item in host.SubscribeAsync(sessionId, 0, deadline.Token))
        {
            if (item.Event is not { } value)
                continue;
            events.Add(value);
            if (value.Data is TurnEndPayload)
                return events;
        }
        throw new IOException("The Host event stream ended before its real model turn completed.");
    }

    private static async ValueTask<Stream> ConnectObservedAsync(SshOutboundTunnel tunnel, DnsEndPoint endpoint,
        CancellationToken token, ConcurrentQueue<string> commands)
    {
        var stream = (SshProxyStream)await tunnel.ConnectAsync(endpoint, token);
        var arguments = stream.Process.StartInfo.ArgumentList;
        var forwarding = arguments.IndexOf("-W");
        if (forwarding < 0)
        {
            forwarding = arguments.IndexOf("-L");
            Assert.NotNull(tunnel.LocalPort);
            FleetLocalForwardEgressTests.AssertOnlyLoopbackListener(tunnel.LocalPort.Value);
        }
        Assert.True(forwarding >= 0 && forwarding + 1 < arguments.Count, "The actual SSH must own the forwarding connection.");
        commands.Enqueue(arguments[forwarding + 1]);
        return stream;
    }

    private static void AssertCommandStreams(TestForwardProxy forwarding, string[] commands, string host, int port, string mode)
    {
        Assert.Equal(forwarding.Connections, commands.Length);
        Assert.All(commands, target =>
        {
            if (mode == "proxyCommand")
                Assert.Equal($"{host}:{port}", target);
            else
            {
                Assert.StartsWith("127.0.0.1:", target, StringComparison.Ordinal);
                Assert.EndsWith($":{host}:{port}", target, StringComparison.Ordinal);
            }
        });
    }

    private static async Task AssertExampleDomainAsync(HttpClient client, string target, int version, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, target)
        {
            Version = version == 2 ? HttpVersion.Version20 : HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(request.Version, response.Version);
        Assert.Equal(target, response.RequestMessage!.RequestUri!.AbsoluteUri);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        Assert.Contains("<title>Example Domain</title>", body, StringComparison.Ordinal);
    }

    private static void AssertProxyRequest(TestForwardProxy forwarding, string scheme, string target)
    {
        var expected = scheme == "socks5"
            ? $"SOCKS example.com:{new Uri(target).Port}"
            : target == HttpsTarget ? "CONNECT example.com:443 HTTP/1.1" : $"GET {target} HTTP/1.1";
        Assert.True(forwarding.Requests.Count == 1 && forwarding.Requests.TryPeek(out var actual) && actual == expected,
            "The real forwarding proxy must record this request's expected protocol and public destination.");
    }

    private static void AssertPrivateOutput(string output, string user, string password)
    {
        var credential = $"{user}:{password}";
        Assert.True(!output.Contains(user, StringComparison.Ordinal) && !output.Contains(password, StringComparison.Ordinal)
            && !output.Contains(Uri.EscapeDataString(credential), StringComparison.Ordinal)
            && !output.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes(credential)), StringComparison.Ordinal)
            && !output.Contains("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase),
            "Diagnostics must not expose fixture credentials, their encoded forms or proxy authentication headers.");
    }
}
#endif

internal sealed record NetworkGuiNode(string Name, string Text, int X, int Y, int Width, int Height, bool Enabled, long Window)
{
    public static NetworkGuiNode Parse(string line)
    {
        var values = line.Split('\t');
        if (values.Length == 2 && values[0] == "ERROR")
            throw new IOException($"The real GUI observation failed: {Decode(values[1])}");
        if (values.Length != 8)
            throw new IOException("The real GUI observer returned an invalid frame.");
        return new(Decode(values[0]), Decode(values[1]), int.Parse(values[2], CultureInfo.InvariantCulture),
            int.Parse(values[3], CultureInfo.InvariantCulture), int.Parse(values[4], CultureInfo.InvariantCulture),
            int.Parse(values[5], CultureInfo.InvariantCulture), values[6] == "1", long.Parse(values[7], CultureInfo.InvariantCulture));
    }

    internal static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    internal static string Decode(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(value));
}

#if NETWORK_GUI_OBSERVER
internal static class NetworkGuiObservation
{
    public static void Start(string pipe)
    {
        var firstWindow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstOpened = Window.WindowOpenedEvent.AddClassHandler<Window>((_, _) => firstWindow.TrySetResult());
        _ = Task.Run(async () =>
        {
            using var connection = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
            await connection.ConnectAsync(30000);
            // Never initialize the dispatcher before the production AppBuilder has selected its platform.
            await firstWindow.Task.WaitAsync(TimeSpan.FromSeconds(5));
            firstOpened.Dispose();
            using var input = new StreamReader(connection, Encoding.UTF8, leaveOpen: true);
            using var output = new StreamWriter(connection, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var reload = new FleetReloadObservation();
            while (await input.ReadLineAsync() is { } request)
            {
                var fields = request.Split('\t');
                if (fields.Length != 3)
                    throw new IOException("Invalid GUI observation request.");
                var kind = fields[0];
                var selector = NetworkGuiNode.Decode(fields[1]);
                var contains = NetworkGuiNode.Decode(fields[2]);
                if (kind == "fleet-baseline")
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
                        reload.Arm(desktop.Windows.Select(window => window.DataContext).OfType<MainViewModel>().Single().Workspace.Fleet);
                    });
                    await output.WriteLineAsync("\t\t0\t0\t0\t0\t1\t0");
                    continue;
                }
                if (kind == "fleet-reloaded")
                {
                    using var retirement = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await reload.WaitAsync(retirement.Token);
                    await Dispatcher.UIThread.InvokeAsync(reload.ValidateReplacement);
                    reload.Dispose();
                    await output.WriteLineAsync("\t\t0\t0\t0\t0\t1\t0");
                    continue;
                }
                var reveal = kind == "reveal";
                var enabled = kind == "enabled";
                if (reveal || enabled)
                {
                    var parts = selector.Split('|', 2);
                    kind = parts[0];
                    selector = parts[1];
                }
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                var watched = new HashSet<Window>();
                var controls = new HashSet<Control>();
                var terminalChanges = new Dictionary<TerminalSession, Action>();
                var terminalFrameScheduled = false;
                var framePending = false;
                var frameSettled = false;
                IDisposable? opened = null;
                (bool Visible, bool Enabled, Rect Bounds, bool? Running, ProxyProbeOutcome? Outcome)? firstState = null, lastState = null;
                void Observe(object? sender, EventArgs args)
                {
                    if (ready.Task.IsCompleted || framePending || deadline.IsCancellationRequested)
                        return;
                    if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
                        return;
                    foreach (var window in desktop.Windows)
                    {
                        if (watched.Add(window))
                            window.LayoutUpdated += Observe;
                        foreach (var control in window.GetVisualDescendants().OfType<Control>().Prepend(window))
                        {
                            if (controls.Add(control))
                                control.PropertyChanged += Observe;
                            if (control.Name == selector)
                            {
                                var probeState = control.DataContext is MachinesViewModel machine
                                    ? selector.StartsWith("Ssh", StringComparison.Ordinal) ? machine.SshProbe : machine.RemoteProbe
                                    : Probe(control);
                                lastState = (control.IsEffectivelyVisible, control.IsEffectivelyEnabled, control.Bounds,
                                    probeState?.IsRunning, probeState?.Result?.Outcome);
                                firstState ??= lastState;
                            }
                            if (!control.IsEffectivelyVisible || control.Bounds.Width <= 0 || control.Bounds.Height <= 0)
                                continue;
                            var text = VisibleText(control);
                            if (control is TerminalControl terminal && (kind is "terminal-screen" or "owned-terminal"))
                            {
                                text = kind == "owned-terminal" ? OwnedTerminalId(terminal) : RenderedTerminalText(terminal);
                                if (kind == "terminal-screen" && terminal.Session is { } session && !terminalChanges.ContainsKey(session))
                                {
                                    void AfterRender() => Dispatcher.UIThread.Post(() =>
                                        window.RequestAnimationFrame(_ => window.RequestAnimationFrame(_ => Observe(null, EventArgs.Empty))));
                                    terminalChanges.Add(session, AfterRender);
                                    session.Changed += AfterRender;
                                }
                                if (!terminalFrameScheduled)
                                {
                                    terminalFrameScheduled = true;
                                    window.RequestAnimationFrame(_ => window.RequestAnimationFrame(_ => Observe(null, EventArgs.Empty)));
                                }
                            }
                            var probe = Probe(control);
                            if (kind == "probe" && control.Name == selector && probe is { IsRunning: false, Result: { } result })
                                text = $"{result.Outcome}|{result.Stage}|{result.HttpStatus}|{result.Machine}|{result.Home}|{probe.Detail}";
                            var matches = kind switch
                            {
                                "id" => control.Name == selector,
                                "text" => text == selector && control is Button,
                                "label" => control is UiTextBlock && text == selector,
                                "contains" => text.Contains(selector, StringComparison.Ordinal) && (control is UiTextBlock or SelectableTextBlock),
                                "tip" => Equals(ToolTip.GetTip(control), selector),
                                "placeholder" => control is TextBox box && box.PlaceholderText == selector,
                                "window" => control == window && window.Title == selector,
                                "frame" => control == window && window.Title == selector,
                                "answer" => IsFinalAssistantParagraph(control, selector),
                                "turn-error" => (control is UiTextBlock or SelectableTextBlock)
                                    && control.DataContext is MessageViewModel { Kind: MessageKind.System, IsStreaming: false } message
                                    && text == (message.ShowPreview ? message.Preview : message.Text)
                                    && text.Contains(selector, StringComparison.Ordinal)
                                    && (control.GetVisualAncestors().OfType<ChatView>().FirstOrDefault()?.DataContext as MainViewModel)?.Messages.LastOrDefault() is { } last
                                    && ReferenceEquals(message, last),
                                "owned-terminal" => control is TerminalControl && text.Length > 0,
                                "terminal-screen" => control is TerminalControl && text.Contains(selector, StringComparison.Ordinal),
                                "probe" => control.Name == selector && probe is { IsRunning: false, Result: not null }
                                    && !string.IsNullOrWhiteSpace(probe.Detail) && HasRenderedProbeResult(control, probe),
                                "size" => control == window && $"{control.Bounds.Width}x{control.Bounds.Height}" == selector,
                                "remote-state" => control.Name == "RemoteStatus" && control.DataContext is MachinesViewModel
                                    { RemoteOutboundBusy: false, RemoteOutboundState: { } state }
                                    && (state.Settings.Url ?? "") == selector,
                                _ => false,
                            };
                            if (!matches || !text.Contains(contains, StringComparison.Ordinal) || enabled && !control.IsEffectivelyEnabled)
                                continue;
                            if (!frameSettled)
                            {
                                framePending = true;
                                window.RequestAnimationFrame(_ => window.RequestAnimationFrame(_ =>
                                {
                                    framePending = false;
                                    frameSettled = true;
                                    Observe(null, EventArgs.Empty);
                                }));
                                return;
                            }
                            var topLeft = control.PointToScreen(default);
                            var bottomRight = control.PointToScreen(new Point(control.Bounds.Width, control.Bounds.Height));
                            var bounds = new PixelRect(topLeft, bottomRight);
                            if (reveal)
                            {
                                var scroll = control.GetVisualAncestors().OfType<ScrollViewer>()
                                    .FirstOrDefault(view => view.Extent.Height > view.Viewport.Height);
                                var bar = scroll?.GetVisualDescendants().OfType<ScrollBar>().FirstOrDefault(value =>
                                    value.Orientation == Orientation.Vertical && value.IsEffectivelyVisible);
                                var thumb = bar?.GetVisualDescendants().OfType<Thumb>().FirstOrDefault();
                                if (scroll is not null && bar is not null && thumb is not null && bar.Maximum > bar.Minimum)
                                {
                                    var origin = scroll.PointToScreen(default);
                                    var scale = window.RenderScaling;
                                    var desired = Math.Clamp(scroll.Offset.Y + (bounds.Center.Y - origin.Y) / scale
                                        - scroll.Viewport.Height / 2, 0, scroll.Extent.Height - scroll.Viewport.Height);
                                    var source = thumb.PointToScreen(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2));
                                    var distance = (desired - scroll.Offset.Y) / (bar.Maximum - bar.Minimum)
                                        * (bar.Bounds.Height - thumb.Bounds.Height) * scale;
                                    ready.TrySetResult(string.Join('\t', "", NetworkGuiNode.Encode(
                                        ((int)Math.Round(source.Y + distance)).ToString(CultureInfo.InvariantCulture)),
                                        source.X, source.Y, 0, 0, "1", 0));
                                    return;
                                }
                                ready.TrySetResult(string.Join('\t', "", "", bounds.X, bounds.Y, bounds.Width, bounds.Height, "1", 0));
                                return;
                            }
                            var windowBounds = new PixelRect(window.PointToScreen(default),
                                window.PointToScreen(new Point(window.Bounds.Width, window.Bounds.Height)));
                            if (!windowBounds.Contains(bounds.Center))
                                continue;
                            if (control.GetVisualAncestors().Any(ancestor => ancestor.ClipToBounds
                                && !new PixelRect(ancestor.PointToScreen(default),
                                    ancestor.PointToScreen(new Point(ancestor.Bounds.Width, ancestor.Bounds.Height))).Contains(bounds.Center)))
                                continue;
                            var response = string.Join('\t', NetworkGuiNode.Encode(control.Name ?? ""),
                                NetworkGuiNode.Encode(kind == "terminal-screen" ? selector : text), bounds.X, bounds.Y, bounds.Width, bounds.Height,
                                control.IsEffectivelyEnabled ? "1" : "0", window.TryGetPlatformHandle()?.Handle.ToInt64() ?? 0);
                            ready.TrySetResult(response);
                            return;
                        }
                    }
                }
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    opened = Window.WindowOpenedEvent.AddClassHandler<Window>((_, _) => Observe(null, EventArgs.Empty));
                    Observe(null, EventArgs.Empty);
                }, DispatcherPriority.Background);
                try
                {
                    await output.WriteLineAsync(await ready.Task.WaitAsync(deadline.Token));
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                {
                    await output.WriteLineAsync($"ERROR\t{NetworkGuiNode.Encode($"kind={kind}; selector={selector}; first={firstState}; last={lastState}")}");
                }
                finally
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        foreach (var window in watched)
                            window.LayoutUpdated -= Observe;
                        foreach (var control in controls)
                            control.PropertyChanged -= Observe;
                        foreach (var (session, changed) in terminalChanges)
                            session.Changed -= changed;
                        opened?.Dispose();
                    });
                }
            }
        });
    }

    /** Observes the production client's completed disposal, including its outbound lease; never initiates disposal. */
    private sealed class FleetReloadObservation : IDisposable
    {
        private readonly TaskCompletionSource _swapped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _retired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private FleetWorkspace? _workspace;
        private IWorkspaceClient? _previous;

        public void Arm(FleetWorkspace workspace)
        {
            if (_workspace is not null)
                throw new InvalidOperationException("The reload observation is already armed.");
            _workspace = workspace;
            _previous = workspace.Client;
            workspace.Changed += OnChanged;
            _ = ObserveDisposalAsync(_previous);
        }

        private void OnChanged()
        {
            if (_workspace is not null && !ReferenceEquals(_workspace.Client, _previous))
                _swapped.TrySetResult();
        }

        private async Task ObserveDisposalAsync(IWorkspaceClient previous)
        {
            try
            {
                await previous.WhenDisposed.ConfigureAwait(false);
                _retired.TrySetResult();
            }
            catch (Exception error)
            {
                _retired.TrySetException(error);
            }
        }

        public Task WaitAsync(CancellationToken token) => Task.WhenAll(_swapped.Task, _retired.Task).WaitAsync(token);

        public void ValidateReplacement()
        {
            if (_workspace is null || ReferenceEquals(_workspace.Client, _previous))
                throw new InvalidOperationException("The GUI has not replaced its production Fleet client.");
        }

        public void Dispose()
        {
            if (_workspace is not null)
                _workspace.Changed -= OnChanged;
            _workspace = null;
            _previous = null;
        }
    }

    private static bool HasRenderedProbeResult(Control button, NetworkProbeViewModel probe)
    {
        var region = button.GetVisualAncestors().OfType<StackPanel>().FirstOrDefault();
        if (region is null)
            return false;
        var visible = region.GetVisualDescendants().OfType<Control>().Where(control => control.IsEffectivelyVisible).ToArray();
        var labels = visible.OfType<UiTextBlock>().Select(block => block.Text).ToArray();
        return labels.Contains(probe.OutcomeText) && labels.Contains(probe.StageText) && labels.Contains(probe.IdentityText)
            && (probe.HttpStatusText.Length == 0 || labels.Contains(probe.HttpStatusText))
            && visible.OfType<SelectableTextBlock>().Any(block => block.Text == probe.Detail);
    }

    private static NetworkProbeViewModel? Probe(Control control) => control.DataContext switch
    {
        MainViewModel main when control.Name == "LocalProxyProbe" => main.Preferences.LocalProxyProbe,
        MainViewModel main when control.Name == "BorrowedProbe" => main.Preferences.BorrowedOutboundProbe,
        MachinesViewModel machines when control.Name == "SshProbe" => machines.SshProbe,
        MachinesViewModel machines when control.Name == "RemoteProbe" => machines.RemoteProbe,
        _ => null,
    };

    private static string OwnedTerminalId(TerminalControl control)
    {
        if (control.DataContext is not TerminalViewModel terminal
            || !control.GetVisualAncestors().OfType<Control>().Any(ancestor =>
                ancestor.DataContext is SessionTabViewModel { IsClosable: true, IsSelected: true } tab
                && ReferenceEquals(tab.Terminal, terminal)))
            return "";
        return (typeof(TerminalViewModel).GetField("_remote",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(terminal)
            as RemotePtyConnection)?.PtyId ?? "";
    }

    private static string RenderedTerminalText(TerminalControl control)
    {
        var frame = typeof(TerminalControl).GetField("_frame",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(control) as TerminalFrame
            ?? throw new MissingFieldException(nameof(TerminalControl), "_frame");
        var text = new StringBuilder();
        for (var row = 0; row < frame.Rows; row++)
            foreach (var cell in frame.Row(row))
                text.Append(cell.Character);
        return text.ToString();
    }

    private static bool IsFinalAssistantParagraph(Control control, string citation)
    {
        if (control is not UiTextBlock
            || control.GetVisualAncestors().OfType<MarkdownView>().FirstOrDefault() is not { } markdown
            || markdown.DataContext is not MessageViewModel { IsAssistant: true, IsStreaming: false } message
            || !message.Text.Contains(citation, StringComparison.Ordinal) || markdown.Text != message.Text)
            return false;
        var messages = (markdown.GetVisualAncestors().OfType<ChatView>().FirstOrDefault()?.DataContext as MainViewModel)?.Messages;
        if (messages is null || !ReferenceEquals(message, messages.LastOrDefault(value => value.IsAssistant)))
            return false;
        return ReferenceEquals(control, markdown.GetVisualDescendants().OfType<UiTextBlock>()
            .LastOrDefault(block => !string.IsNullOrWhiteSpace(VisibleText(block))));
    }

    private static string VisibleText(Control control) => control switch
    {
        // Never serialize editor contents, passwords, models' API keys or other off-screen state.
        TextBox box => box.PlaceholderText ?? "",
        SelectableTextBlock selectable => selectable.Text ?? "",
        UiTextBlock block => !string.IsNullOrEmpty(block.Text) ? block.Text
            : string.Concat(block.Inlines?.OfType<Avalonia.Controls.Documents.Run>().Select(run => run.Text) ?? []),
        ContentControl { Content: string text } => text,
        Button button => string.Join("", button.GetVisualDescendants().OfType<UiTextBlock>()
            .Where(value => value.IsEffectivelyVisible).Select(value => value.Text)),
        Window window => window.Title ?? "",
        _ => "",
    };
}
#endif

#if !NETWORK_GUI_OBSERVER
internal enum NetworkGuiKey { Home, Enter, Down, Alt, F4 }

internal sealed class NetworkGuiProcess : IAsyncDisposable
{
    private const int DisplayWidth = 1600;
    private const int DisplayHeight = 1100;
    private readonly Process _display;
    private readonly int _displayNumber;
    private readonly int _displayProcessId;
    private readonly Process _windowManager;
    private readonly Process _gui;
    private readonly Process _robot;
    private readonly NamedPipeServerStream _pipe;
    private readonly StreamReader _observations;
    private readonly StreamWriter _requests;
    private readonly Task<string>[] _drains;
    public string Evidence { get; }

    private NetworkGuiProcess(Process display, int displayNumber, Process windowManager, Process gui, Process robot, NamedPipeServerStream pipe, string evidence)
    {
        _display = display;
        _displayNumber = displayNumber;
        _displayProcessId = display.Id;
        _windowManager = windowManager;
        _gui = gui;
        _robot = robot;
        _pipe = pipe;
        Evidence = evidence;
        _observations = new(pipe, Encoding.UTF8, leaveOpen: true);
        _requests = new(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        _drains = [gui.StandardOutput.ReadToEndAsync(), gui.StandardError.ReadToEndAsync(),
            display.StandardError.ReadToEndAsync(), robot.StandardError.ReadToEndAsync(),
            windowManager.StandardOutput.ReadToEndAsync(), windowManager.StandardError.ReadToEndAsync()];
    }

    public static async Task<NetworkGuiProcess> StartAsync(string executable, string home, string evidence, CancellationToken token)
    {
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
        startup.CancelAfter(TimeSpan.FromSeconds(5));
        token = startup.Token;
        Assert.True(OperatingSystem.IsLinux(), "The real GUI gate requires an isolated Linux X11 namespace.");
        var mounts = File.ReadLines("/proc/self/mountinfo").Select(line => line.Split(' ')).Where(fields => fields.Length > 4).ToArray();
        var worktree = mounts.LastOrDefault(fields => fields[4] == "/w");
        var temporary = mounts.LastOrDefault(fields => fields[4] == "/tmp");
        Assert.True(worktree is not null && temporary is not null,
            "The real GUI requires explicit worktree and temporary mounts in its isolated namespace.");
        static string DecodeMountPath(string value) => value.Replace("\\040", " ", StringComparison.Ordinal)
            .Replace("\\011", "\t", StringComparison.Ordinal).Replace("\\012", "\n", StringComparison.Ordinal)
            .Replace("\\134", "\\", StringComparison.Ordinal);
        var relative = Path.GetRelativePath(DecodeMountPath(worktree[3]), DecodeMountPath(temporary[3]));
        Assert.True(worktree[2] == temporary[2] && !Path.IsPathRooted(relative) && relative is not "." and not ".."
            && !relative.StartsWith("../", StringComparison.Ordinal),
            "The temporary mount must be physically backed by a directory inside the current owned worktree mount.");
        Directory.CreateDirectory(evidence);
        var classes = Path.Combine(evidence, "java");
        Directory.CreateDirectory(classes);
        using (var compiler = TestProcessScope.Start(Info("javac", home, null,
            "-d", classes, Path.Combine(AppContext.BaseDirectory, "NetworkGuiDriver.java")))! )
        {
            try
            {
                var errors = compiler.StandardError.ReadToEndAsync(token);
                var output = compiler.StandardOutput.ReadToEndAsync(token);
                await compiler.WaitForExitAsync(token);
                Assert.True(compiler.ExitCode == 0, "The Java Robot driver must compile.");
                await errors;
                await output;
            }
            finally
            {
                if (!compiler.HasExited)
                    compiler.Kill(entireProcessTree: true);
                await compiler.WaitForExitAsync();
            }
        }
        var display = TestProcessScope.Start(Info("Xvfb", home, null, "-displayfd", "1", "-screen", "0", $"{DisplayWidth}x{DisplayHeight}x24",
            "-nolisten", "tcp", "-dpi", "96", "-terminate")) ?? throw new IOException("Xvfb could not start.");
        Process? gui = null;
        Process? robot = null;
        Process? windowManager = null;
        NamedPipeServerStream? pipe = null;
        var displayNumber = -1;
        try
        {
            var number = await display.StandardOutput.ReadLineAsync(token);
            Assert.True(int.TryParse(number, out displayNumber), "Xvfb must signal its allocated display through -displayfd.");
            var selectedDisplay = $":{number}";
            const string windowManagerReady = "DSH_NETWORK_GUI_WM_READY";
            windowManager = TestProcessScope.Start(Info("openbox", home, selectedDisplay, "--sm-disable",
                "--startup", $"/bin/sh -c 'echo {windowManagerReady}'"))
                ?? throw new IOException("The owned window manager could not start.");
            string? announcement;
            do
            {
                announcement = await windowManager.StandardOutput.ReadLineAsync(token);
            } while (announcement is not null && announcement != windowManagerReady);
            Assert.Equal(windowManagerReady, announcement);
            Assert.False(windowManager.HasExited, "The owned window manager must remain alive after X11 readiness.");
            var pipeName = $"ngu-{Guid.NewGuid():N}";
            pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var launch = Info(executable, home, selectedDisplay, "gui");
            launch.Environment["DOTNET_STARTUP_HOOKS"] = Path.Combine(AppContext.BaseDirectory, "network-gui-observer.dll");
            launch.Environment["DSH_NETWORK_GUI_OBSERVER"] = pipeName;
            gui = TestProcessScope.Start(launch) ?? throw new IOException("The real dsharp GUI could not start.");
            robot = TestProcessScope.Start(Info("java", home, selectedDisplay, "-cp", classes, "NetworkGuiDriver", selectedDisplay, evidence))
                ?? throw new IOException("The isolated Java Robot could not start.");
            Assert.Equal("READY", await robot.StandardOutput.ReadLineAsync(token));
            await pipe.WaitForConnectionAsync(token);
            var result = new NetworkGuiProcess(display, displayNumber, windowManager, gui, robot, pipe, evidence);
            await result.FindAsync("window", "DeepSeek Harness", token);
            return result;
        }
        catch
        {
            pipe?.Dispose();
            var displayProcessId = display.Id;
            foreach (var process in new[] { robot, gui, windowManager, display }.OfType<Process>())
                await StopAsync(process);
            if (displayNumber >= 0)
                DeleteOwnedDisplayFiles(displayNumber, displayProcessId);
            Directory.Delete(classes, recursive: true);
            throw;
        }
    }

    public async Task<NetworkGuiNode> FindAsync(string kind, string selector, CancellationToken token, string contains = "")
    {
        await _requests.WriteLineAsync($"{kind}\t{NetworkGuiNode.Encode(selector)}\t{NetworkGuiNode.Encode(contains)}");
        var line = await _observations.ReadLineAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), token);
        if (line is null || line.StartsWith("ERROR\t", StringComparison.Ordinal))
            await InputAsync(token, "SHOT", "observer-failure.png", "0", "0",
                DisplayWidth.ToString(CultureInfo.InvariantCulture), DisplayHeight.ToString(CultureInfo.InvariantCulture));
        return NetworkGuiNode.Parse(line ?? throw new IOException($"The real GUI observer disconnected while locating {kind}:{selector}; process exited={_gui.HasExited}."));
    }

    public async Task ClickAsync(string kind, string selector, CancellationToken token)
    {
        var reveal = await FindAsync("reveal", $"{kind}|{selector}", token);
        if (reveal.Text.Length > 0)
            await InputAsync(token, "DRAG", reveal.X.ToString(CultureInfo.InvariantCulture),
                reveal.Y.ToString(CultureInfo.InvariantCulture), reveal.Text);
        var target = await FindAsync("enabled", $"{kind}|{selector}", token);
        Assert.True(target.Enabled, "The real GUI target must be enabled.");
        await InputAsync(token, "CLICK", (target.X + target.Width / 2).ToString(CultureInfo.InvariantCulture),
            (target.Y + target.Height / 2).ToString(CultureInfo.InvariantCulture));
    }

    public async Task TextAsync(string kind, string selector, string value, CancellationToken token)
    {
        await ClickAsync(kind, selector, token);
        await InputAsync(token, "TEXT", NetworkGuiNode.Encode(value));
    }

    public async Task PasteTerminalAsync(string value, CancellationToken token)
    {
        await ClickAsync("id", "Terminal", token);
        await InputAsync(token, "PASTE_TERMINAL", NetworkGuiNode.Encode(value));
    }

    public Task KeyAsync(CancellationToken token, params NetworkGuiKey[] keys)
        => InputAsync(token, ["KEY", .. keys.Select(key => key.ToString().ToUpperInvariant())]);

    public async Task ResizeAsync(int width, int height, CancellationToken token)
    {
        var window = await FindAsync("window", "DeepSeek Harness", token);
        await InputAsync(token, "SIZE", window.Window.ToString(CultureInfo.InvariantCulture),
            width.ToString(CultureInfo.InvariantCulture), height.ToString(CultureInfo.InvariantCulture));
        window = await FindAsync("size", $"{width}x{height}", token);
        Assert.Equal(width, window.Width);
        Assert.Equal(height, window.Height);
    }

    public async Task ScreenshotAsync(string name, CancellationToken token)
    {
        var window = await FindAsync("frame", "DeepSeek Harness", token);
        await InputAsync(token, "SHOT", name, window.X.ToString(CultureInfo.InvariantCulture),
            window.Y.ToString(CultureInfo.InvariantCulture), window.Width.ToString(CultureInfo.InvariantCulture),
            window.Height.ToString(CultureInfo.InvariantCulture));
    }

    public async Task ExitAsync(CancellationToken token)
    {
        await KeyAsync(token, NetworkGuiKey.Alt, NetworkGuiKey.F4); // Delivered only to the dedicated Xvfb.
        await _gui.WaitForExitAsync(token);
        Assert.Equal(0, _gui.ExitCode);
    }

    private async Task InputAsync(CancellationToken token, params string[] command)
    {
        await _robot.StandardInput.WriteLineAsync(string.Join('\t', command));
        await _robot.StandardInput.FlushAsync(token);
        Assert.Equal("OK", await _robot.StandardOutput.ReadLineAsync(token));
    }

    private static ProcessStartInfo Info(string executable, string home, string? display, params string[] arguments)
    {
        var info = new ProcessStartInfo(executable, arguments)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = home,
        };
        info.Environment.Remove("DISPLAY");
        info.Environment.Remove("WAYLAND_DISPLAY");
        info.Environment.Remove("DOTNET_STARTUP_HOOKS");
        info.Environment.Remove("DSH_NETWORK_GUI_OBSERVER");
        info.Environment.Remove("SSH_ASKPASS");
        info.Environment.Remove("SSH_ASKPASS_REQUIRE");
        info.Environment.Remove("GIT_ASKPASS");
        foreach (var name in new[] { "XAUTHORITY", "DBUS_SESSION_BUS_ADDRESS", "SSH_AUTH_SOCK", "JAVA_TOOL_OPTIONS", "_JAVA_OPTIONS", "JDK_JAVA_OPTIONS", "AVALONIA_GLOBAL_SCALE_FACTOR" })
            info.Environment.Remove(name);
        if (display is not null)
            info.Environment["DISPLAY"] = display;
        info.Environment["HOME"] = home;
        info.Environment[HarnessHome.HomeEnv] = home;
        info.Environment[HarnessHome.ChildHomeEnv] = home;
        foreach (var name in new[] { "XDG_CONFIG_HOME", "XDG_CACHE_HOME", "XDG_DATA_HOME", "XDG_RUNTIME_DIR" })
        {
            var directory = Path.Combine(home, name.ToLowerInvariant());
            Directory.CreateDirectory(directory);
            info.Environment[name] = directory;
        }
        return info;
    }

    private static async Task StopAsync(Process process)
    {
        if (!process.HasExited)
            process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
        process.Dispose();
    }

    private static void DeleteOwnedDisplayFiles(int number, int pid)
    {
        var socket = $"/tmp/.X11-unix/X{number}";
        var file = $"/tmp/.X{number}-lock";
        // Xvfb -displayfd omits the PID lock; its socket is inside the verified private temporary mount.
        if (!File.Exists(file) || File.ReadAllText(file).Trim() == pid.ToString(CultureInfo.InvariantCulture))
        {
            File.Delete(socket);
            File.Delete(file);
        }
        Assert.False(File.Exists(file) || File.Exists(socket), "The owned Xvfb lock and socket must be absent after it exits.");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _requests.DisposeAsync();
        }
        finally
        {
            _pipe.Dispose();
            foreach (var process in new[] { _gui, _robot, _windowManager, _display })
                await StopAsync(process);
            await Task.WhenAll(_drains);
            _observations.Dispose();
            DeleteOwnedDisplayFiles(_displayNumber, _displayProcessId);
            Directory.Delete(Path.Combine(Evidence, "java"), recursive: true);
        }
    }
}
#endif
}

#if NETWORK_GUI_OBSERVER
/** Test-only observation of the actual executable; never changes controls or invokes application commands. */
public static class StartupHook
{
    public static void Initialize()
    {
        var pipe = Environment.GetEnvironmentVariable("DSH_NETWORK_GUI_OBSERVER");
        if (string.IsNullOrEmpty(pipe))
            return;
        // Technical observation belongs only to this executable, never to its Host/PTY children.
        Environment.SetEnvironmentVariable("DOTNET_STARTUP_HOOKS", null);
        Environment.SetEnvironmentVariable("DSH_NETWORK_GUI_OBSERVER", null);
        // Resolve dependencies beside this exact observer, without enumerating or retaining loaded assemblies.
        var directory = Path.GetDirectoryName(typeof(StartupHook).Assembly.Location)!;
        System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            var path = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(path) ? System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        Dsh.Tests.NetworkGuiObservation.Start(pipe);
    }
}
#endif


