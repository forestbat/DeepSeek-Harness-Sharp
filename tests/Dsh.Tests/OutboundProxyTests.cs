using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Gui.Services;
using Dsh.Gui.ViewModels;
using Dsh.Gui.Views;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Web;

namespace Dsh.Tests;

/**
 * 本机出口代理(settings.yaml 的 network.outbound_proxy)端到端验证: 经真实组合根装配, 走真实 web_fetch 工具、/proxy 命令、自检与设置写回。
 * TestForwardProxy 是测试专用的本地代理替身: 只记录收到的请求并回固定响应, 不向外转发; 公网 IP 目标避免单元测试依赖 DNS。
 */
public sealed class OutboundProxyTests
{
    private const string FetchTarget = "http://1.1.1.1/page";
    private const string Password = "s:cr@t";
    private const string EscapedPassword = "s%3Acr%40t";

    [Fact]
    public async Task WebFetch_GoesThroughConfiguredHttpProxy()
    {
        await using var proxy = new TestForwardProxy();
        using var harness = await TestHarness.ComposeAsync($"url: http://127.0.0.1:{proxy.Port}");

        var result = await harness.FetchAsync(FetchTarget);

        Assert.False(result.IsError, Text(result));
        Assert.Contains(TestForwardProxy.ProxiedBody, Text(result));
        Assert.Equal([$"GET {FetchTarget} HTTP/1.1"], proxy.Requests);
    }

    [Fact]
    public async Task WebFetch_Https_OpensConnectTunnelThroughProxy()
    {
        await using var proxy = new TestForwardProxy();
        using var harness = await TestHarness.ComposeAsync($"url: 127.0.0.1:{proxy.Port}");

        var result = await harness.FetchAsync("https://1.1.1.1/");

        Assert.True(result.IsError);
        Assert.Contains(proxy.Requests, line => line.StartsWith("CONNECT 1.1.1.1:443 ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WebFetch_HttpProxyCredentials_AreSentAndRedacted()
    {
        await using var proxy = new TestForwardProxy(requiredCredential: $"alice:{Password}");
        using var harness = await TestHarness.ComposeAsync($"url: http://alice:{EscapedPassword}@127.0.0.1:{proxy.Port}");

        var result = await harness.FetchAsync(FetchTarget);

        Assert.False(result.IsError, Text(result));
        Assert.Equal([$"alice:{Password}"], proxy.Credentials);
        Assert.Equal($"http://***@127.0.0.1:{proxy.Port}", OutboundProxy.FromContext(harness.App.Ctx).Describe());
    }

    [Fact]
    public async Task WebFetch_Socks5WithCredentials_SendsTargetThroughProxy()
    {
        await using var proxy = new TestForwardProxy(socks: true);
        using var harness = await TestHarness.ComposeAsync($"url: socks5://alice:{EscapedPassword}@127.0.0.1:{proxy.Port}");

        var result = await harness.FetchAsync(FetchTarget);

        Assert.False(result.IsError, Text(result));
        Assert.Contains(TestForwardProxy.ProxiedBody, Text(result));
        Assert.Equal([$"alice:{Password}"], proxy.Credentials);
        Assert.Equal(["SOCKS 1.1.1.1:80", "GET /page HTTP/1.1"], proxy.Requests);
    }

    /** 未配置(跟随系统代理)或目标在 bypass 里时, 请求不得经过配置之外的这个代理。 */
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WebFetch_WithoutProxyOrBypassed_DoesNotTouchTestProxy(bool bypassed)
    {
        await using var proxy = new TestForwardProxy();
        var network = bypassed ? $"url: http://127.0.0.1:{proxy.Port}\n    bypass: [1.1.1.1]" : null;
        using var harness = await TestHarness.ComposeAsync(network);

        Assert.NotEqual(proxy.Port, OutboundProxy.FromContext(harness.App.Ctx).ProxyFor(new Uri(FetchTarget))?.Port);

        Assert.Equal(0, proxy.Connections);
        Assert.NotEqual(proxy.Port, OutboundProxy.FromContext(harness.App.Ctx).ProxyFor(new Uri(FetchTarget))?.Port);
    }

    [Fact]
    public async Task Probe_ReportsReachableProxy()
    {
        await using var proxy = new TestForwardProxy();

        var result = await OutboundProxyProbe.RunAsync(
            new OutboundProxySettings { Url = $"http://127.0.0.1:{proxy.Port}", ProbeUrl = FetchTarget },
            TestContext.Current.CancellationToken);

        Assert.True(result.Reachable, result.Message);
        Assert.StartsWith("本机出口代理可用", result.Message);
    }

    [Fact]
    public async Task Probe_ReportsUnreachableProxyLayer_WithoutPassword()
    {
        var port = FreePort();

        var result = await OutboundProxyProbe.RunAsync(
            new OutboundProxySettings { Url = $"socks5://alice:{EscapedPassword}@127.0.0.1:{port}", ProbeUrl = FetchTarget },
            TestContext.Current.CancellationToken);

        Assert.False(result.Reachable);
        Assert.StartsWith("本机出口代理不通: 无法连接代理", result.Message);
        Assert.Contains($"socks5://***@127.0.0.1:{port}", result.Message);
        Assert.DoesNotContain(EscapedPassword, result.Message);
        Assert.DoesNotContain(Password, result.Message);
    }

    [Fact]
    public async Task Probe_ReportsProxyAuthenticationFailure()
    {
        await using var proxy = new TestForwardProxy(requiredCredential: $"alice:{Password}");

        var result = await OutboundProxyProbe.RunAsync(
            new OutboundProxySettings { Url = $"http://alice:wrong@127.0.0.1:{proxy.Port}", ProbeUrl = "https://example.test/" },
            TestContext.Current.CancellationToken);

        Assert.False(result.Reachable);
        Assert.Equal($"本机出口代理不通: 代理 http://***@127.0.0.1:{proxy.Port} 要求认证或用户名密码错误(HTTP 407)", result.Message);
    }

    [Fact]
    public async Task Probe_RejectsUnsupportedScheme_WithRedactedReason()
    {
        var result = await OutboundProxyProbe.RunAsync(
            new OutboundProxySettings { Url = $"ftp://alice:{EscapedPassword}@127.0.0.1:21" },
            TestContext.Current.CancellationToken);

        Assert.False(result.Reachable);
        Assert.Contains("配置无效", result.Message);
        Assert.Contains("ftp://***@127.0.0.1:21", result.Message);
        Assert.DoesNotContain(EscapedPassword, result.Message);
    }

    [Fact]
    public async Task ProxyCommand_TestsAndReloadsSettings()
    {
        await using var proxy = new TestForwardProxy();
        using var harness = await TestHarness.ComposeAsync(null);
        var settings = HarnessSettings.Load(harness.App.Home);
        settings.Network = new NetworkSettings
        {
            OutboundProxy = new OutboundProxySettings { Url = $"http://127.0.0.1:{proxy.Port}", ProbeUrl = FetchTarget },
        };
        settings.Save(harness.App.Home);

        var test = await harness.CommandAsync("/proxy test");
        var reload = await harness.CommandAsync("/proxy reload");

        Assert.IsType<CommandResult.Success>(test);
        Assert.Contains("本机出口代理可用", ((CommandResult.Success)test).Text);
        Assert.IsType<CommandResult.Success>(reload);
        Assert.Equal(new Uri($"http://127.0.0.1:{proxy.Port}"), OutboundProxy.FromContext(harness.App.Ctx).Address);
    }

    [Fact]
    public async Task ProxyCommand_SavesSettings_AndReroutesAnExistingHttpClient()
    {
        await using var first = new TestForwardProxy();
        await using var second = new TestForwardProxy();
        using var harness = await TestHarness.ComposeAsync(null);
        var proxy = OutboundProxy.FromContext(harness.App.Ctx);
        using var client = new HttpClient(proxy.CreateHandler());

        Assert.IsType<CommandResult.Success>(await harness.CommandAsync($"/proxy set http://127.0.0.1:{first.Port}"));
        Assert.Equal(TestForwardProxy.ProxiedBody, await client.GetStringAsync(FetchTarget, TestContext.Current.CancellationToken));
        Assert.IsType<CommandResult.Success>(await harness.CommandAsync($"/proxy set http://127.0.0.1:{second.Port}"));
        Assert.Equal(TestForwardProxy.ProxiedBody, await client.GetStringAsync(FetchTarget, TestContext.Current.CancellationToken));
        var saved = await File.ReadAllTextAsync(Path.Combine(harness.App.Home.Root, "settings.yaml"), TestContext.Current.CancellationToken);
        Assert.Contains(TestHarness.KeptComment, saved);
        Assert.Equal($"http://127.0.0.1:{second.Port}", HarnessSettings.Load(harness.App.Home).Network?.OutboundProxy?.Url);
        Assert.IsType<CommandResult.Error>(await harness.CommandAsync("/proxy set gopher://127.0.0.1:70"));
        Assert.Equal(saved, await File.ReadAllTextAsync(Path.Combine(harness.App.Home.Root, "settings.yaml"), TestContext.Current.CancellationToken));
        Assert.Single(first.Requests);
        Assert.Single(second.Requests);
        Assert.IsType<CommandResult.Success>(await harness.CommandAsync("/proxy clear"));
        Assert.Null(HarnessSettings.Load(harness.App.Home).Network?.OutboundProxy?.Url);
        Assert.Null(proxy.Address);
    }

    [Fact]
    public async Task SettingsFacade_WritesNetworkSection_KeepsComments_AndAppliesLive()
    {
        using var harness = await TestHarness.ComposeAsync(null);
        var facade = new SettingsFacade(harness.App.Ctx, harness.App.Home);
        var path = facade.SettingsPath;

        facade.UpdateOutboundProxy(new OutboundProxySettings { Url = "http://bob:pw@127.0.0.1:7890", Bypass = ["*.corp.example", "*"] });
        var saved = HarnessSettings.Load(harness.App.Home).Network?.OutboundProxy;
        var written = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        Assert.Throws<ArgumentException>(() => facade.UpdateOutboundProxy(new OutboundProxySettings { Url = "gopher://127.0.0.1:70" }));
        var afterInvalid = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        facade.UpdateOutboundProxy(new OutboundProxySettings());
        var cleared = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);

        Assert.Contains(TestHarness.KeptComment, written);
        Assert.Equal("http://bob:pw@127.0.0.1:7890", saved?.Url);
        Assert.Equal(["*.corp.example", "*"], saved?.Bypass);
        Assert.Equal(written, afterInvalid);
        Assert.DoesNotContain("network:", cleared);
        Assert.Contains(TestHarness.KeptComment, cleared);
        Assert.Null(OutboundProxy.FromContext(harness.App.Ctx).Address);
    }

    [Fact]
    public async Task PublicationFailure_PreservesTheLiveClientAndTheCompleteConfiguration()
    {
        var cancellation = TestContext.Current.CancellationToken;
        await using var first = new TestForwardProxy();
        await using var second = new TestForwardProxy();
        using var harness = await TestHarness.ComposeAsync($"url: http://127.0.0.1:{first.Port}");
        var proxy = OutboundProxy.FromContext(harness.App.Ctx);
        using var client = new HttpClient(proxy.CreateHandler());
        var path = harness.App.Home.SettingsFile;
        var original = await File.ReadAllTextAsync(path, cancellation);
        Assert.Equal(TestForwardProxy.ProxiedBody, await client.GetStringAsync(FetchTarget, cancellation));
        var preserved = Path.Combine(harness.App.Home.Root, "preserved-settings.yaml");
        File.Move(path, preserved);
        try
        {
            Directory.CreateDirectory(path);
            var error = Record.Exception(() =>
                new SettingsFacade(harness.App.Ctx, harness.App.Home).UpdateOutboundProxy(new OutboundProxySettings
                {
                    Url = $"http://127.0.0.1:{second.Port}",
                }));
            Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
            Assert.True(Directory.Exists(path));
            Assert.Equal(original, await File.ReadAllTextAsync(preserved, cancellation));
            Assert.Equal(TestForwardProxy.ProxiedBody, await client.GetStringAsync(FetchTarget, cancellation));
            Assert.Equal(2, first.Requests.Count);
            Assert.Empty(second.Requests);
        }
        finally
        {
            Directory.Delete(path);
            File.Move(preserved, path);
        }
    }

    [Theory]
    [InlineData("http://user:pass@host:8080", "http://***@host:8080")]
    [InlineData("user:pass@host:8080", "***@host:8080")]
    [InlineData("socks5://host:1080/path@x", "socks5://host:1080/path@x")]
    public void Redact_HidesUserInfoOnly(string url, string expected)
        => Assert.Equal(expected, OutboundProxy.Redact(url));

    [Theory]
    [InlineData("http://localhost/", true)]
    [InlineData("http://127.0.0.1/", true)]
    [InlineData("http://intranet/", true)]
    [InlineData("http://192.168.1.10/", true)]
    [InlineData("http://[fe80::1]/", true)]
    [InlineData("http://api.corp.example/", true)]
    [InlineData("http://corp.example/", true)]
    [InlineData("http://example.com/", false)]
    [InlineData("http://8.8.8.8/", false)]
    public void ConfiguredProxy_BypassesLocalAndListedHosts(string url, bool direct)
    {
        var proxy = new OutboundProxy();
        proxy.Configure("http://127.0.0.1:7890", ["*.corp.example"]);

        Assert.Equal(direct, proxy.ProxyFor(new Uri(url)) is null);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("socks5")]
    public async Task WebFetch_ProxyReceivesPinnedPublicIp_AndOriginalHost(string scheme)
    {
        await using var server = new TestForwardProxy(socks: scheme == "socks5");
        var proxy = new OutboundProxy();
        proxy.Configure($"{scheme}://127.0.0.1:{server.Port}", null);
        var provider = new HttpFetchProvider(resolveAddresses: (_, _) =>
            Task.FromResult<IReadOnlyList<PublicAddress>>([new("1.1.1.1", 4)]), proxy: proxy);

        var result = await provider.Fetch(new WebFetchRequest("http://unresolvable.test/page"), TestContext.Current.CancellationToken);

        Assert.Equal(TestForwardProxy.ProxiedBody, result.Body.Content);
        Assert.Contains("Host: unresolvable.test", server.Headers);
        Assert.Equal(scheme == "socks5"
            ? ["SOCKS 1.1.1.1:80", "GET /page HTTP/1.1"]
            : ["GET http://1.1.1.1/page HTTP/1.1"], server.Requests);
    }

    [Fact]
    public async Task WebFetch_ConnectReceivesPinnedIp_NotOriginalHostname()
    {
        await using var server = new TestForwardProxy();
        var proxy = new OutboundProxy();
        proxy.Configure($"http://127.0.0.1:{server.Port}", null);
        var provider = new HttpFetchProvider(resolveAddresses: (_, _) =>
            Task.FromResult<IReadOnlyList<PublicAddress>>([new("1.1.1.1", 4)]), proxy: proxy);

        await Assert.ThrowsAsync<WebError>(() => provider.Fetch(new WebFetchRequest("https://unresolvable.test/"), TestContext.Current.CancellationToken));

        Assert.Equal(["CONNECT 1.1.1.1:443 HTTP/1.1"], server.Requests);
    }

    [Theory]
    [InlineData("127.0.0.1", 4)]
    [InlineData("10.1.2.3", 4)]
    [InlineData("::1", 6)]
    [InlineData("::ffff:192.168.1.2", 6)]
    public async Task WebFetch_ProxyRejectsPrivateAndMixedDns_BeforeContact(string address, int family)
    {
        await using var server = new TestForwardProxy();
        var proxy = new OutboundProxy();
        proxy.Configure($"http://127.0.0.1:{server.Port}", null);
        var provider = new HttpFetchProvider(resolveAddresses: (host, token) =>
            WebFetchHttpNetwork.ResolvePublicAddresses(host, token, (_, _) =>
                Task.FromResult<IReadOnlyList<PublicAddress>>([new("1.1.1.1", 4), new(address, family)])), proxy: proxy);

        var error = await Assert.ThrowsAsync<WebError>(() => provider.Fetch(new WebFetchRequest("http://private.test/"), TestContext.Current.CancellationToken));

        Assert.Equal(WebErrorCodes.BlockedUrl, error.Code);
        Assert.Equal(0, server.Connections);
    }

    private static string Text(ToolExecutionResult result) => string.Concat(result.Content.OfType<TextBlock>().Select(block => block.Text));

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /** 组合真实 harness(默认插件全开), settings.yaml 里只放 network.outbound_proxy 与一行需保留的注释。 */
    private sealed class TestHarness(HarnessApp app, string directory) : IDisposable
    {
        public const string KeptComment = "# keep this comment";

        public HarnessApp App { get; } = app;

        public static async Task<TestHarness> ComposeAsync(string? outboundProxyYaml)
        {
            var directory = TempTree.CreateDirectory("outbound-proxy");
            var home = new HarnessHome(Path.Combine(directory, "home"));
            Directory.CreateDirectory(home.Root);
            var network = outboundProxyYaml is null ? "" : $"network:\n  outbound_proxy:\n    {outboundProxyYaml}\n";
            await File.WriteAllTextAsync(
                Path.Combine(home.Root, "settings.yaml"),
                $"{KeptComment}\nglobal_default_model: null\nproviders: {{}}\nlogging:\n  file: false\n{network}");
            return new TestHarness(await ConfigBoot.Compose(new HarnessOptions(home, Cwd: directory)), directory);
        }

        public async Task<ToolExecutionResult> FetchAsync(string url)
        {
            var tools = App.Ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
            return await tools.Execute(new ToolExecutionInput
            {
                CallId = ToolCallId.Create($"call-{Guid.NewGuid():N}"),
                Name = "web_fetch",
                Arguments = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["url"] = url }),
                Signal = TestContext.Current.CancellationToken,
            });
        }

        public async Task<CommandResult> CommandAsync(string line)
        {
            var agents = App.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
            var handle = await agents.Create(new CreateAgentOptions(Cwd: directory));
            var commands = App.Ctx.Get<CommandsService>(CommandsService.ServiceName)!;
            var execution = await commands.Execute(handle.Agent, line, TestContext.Current.CancellationToken);
            return execution!.Result;
        }

        public void Dispose()
        {
            App.Dispose();
            TempTree.Delete(directory);
        }
    }
}

/** 设置页「网络」分页: 草稿自检走同一自检服务, 保存写回 settings.yaml 并立即作用于本进程。 */
[Collection(GuiSerialCollection.CollectionName)]
public sealed class OutboundProxyGuiTests
{
    [Fact]
    public async Task NetworkSection_ChecksDraftAndSavesLive() => await HeadlessGui.RunAsync(async () =>
    {
        await using var proxy = new TestForwardProxy();
        using var environment = GuiTestEnvironment.CreateBlocking();
        var window = new MainWindow(environment.App, await environment.OpenChatAsync());
        var preferences = window.ViewModel!.Preferences;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            window.ViewModel.ShowSettingsCommand.Execute(null);
            var network = preferences.Sections.First(section => section.Section == SettingsSection.Network);
            network.SelectCommand!.Execute(network);
            preferences.OutboundProxyUrl = $"http://127.0.0.1:{proxy.Port}";
            preferences.OutboundProxyProbeUrl = "http://example.test/";
            await preferences.CheckProxyCommand.ExecuteAsync(null);
            preferences.SaveNetworkCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            Assert.True(preferences.IsNetworkSection);
            Assert.StartsWith("本机出口代理可用", preferences.ProxyCheckResult);
            Assert.Equal(new Uri($"http://127.0.0.1:{proxy.Port}"), OutboundProxy.FromContext(environment.App.Ctx).Address);
            Assert.Equal($"http://127.0.0.1:{proxy.Port}", HarnessSettings.Load(environment.App.Home).Network?.OutboundProxy?.Url);
            var page = window.GetVisualDescendants().OfType<SettingsPageView>().Single();
            Assert.Contains(page.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>(), block => block.Text == "本机出口代理");
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });
}

/** An actual TCP proxy with optional authentication, forwarding, persistent connections and response barriers. */
internal sealed class TestForwardProxy : IAsyncDisposable
{
    private const byte SocksVersion = 5;
    private const byte UserPasswordMethod = 2;
    private const byte NoAcceptableMethod = byte.MaxValue;
    private const byte NoAuthMethod = 0;
    private const byte AddressIpv4 = 1;
    private const byte AddressDomain = 3;
    private const int Ipv4Length = 4;
    private const int Ipv6Length = 16;

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly bool _socks;
    private string? _requiredCredential;
    private readonly bool _forward;
    private readonly bool _keepAlive;
    private readonly bool _pauseResponses;
    private readonly bool _pauseBody;
    private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();
    private readonly ConcurrentBag<Task> _serving = [];
    private readonly Channel<string> _received = Channel.CreateUnbounded<string>();
    private readonly Channel<int> _closed = Channel.CreateUnbounded<int>();
    private readonly SemaphoreSlim _responses = new(0);
    private readonly Task _accepting;
    private int _connections;

    public const string ProxiedBody = "via-test-proxy";

    public TestForwardProxy(bool socks = false, string? requiredCredential = null, bool forward = false, bool keepAlive = false, bool pauseResponses = false, bool pauseBody = false)
    {
        _socks = socks;
        _requiredCredential = requiredCredential;
        _forward = forward;
        _keepAlive = keepAlive;
        _pauseResponses = pauseResponses;
        _pauseBody = pauseBody;
        _listener.Start();
        _accepting = AcceptAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public int Connections => Volatile.Read(ref _connections);

    public int ActiveConnections => _clients.Count;

    public ValueTask<string> WaitForRequestAsync(CancellationToken cancellationToken)
        => _received.Reader.ReadAsync(cancellationToken);

    public ValueTask<int> WaitForClosedConnectionAsync(CancellationToken cancellationToken)
        => _closed.Reader.ReadAsync(cancellationToken);

    public void ReleaseResponse() => _responses.Release();

    public void RequireCredential(string credential) => Volatile.Write(ref _requiredCredential, credential);

    public ConcurrentQueue<string> Requests { get; } = new();

    public ConcurrentQueue<string> Credentials { get; } = new();

    public ConcurrentQueue<string> Headers { get; } = new();

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _accepting;
        foreach (var client in _clients.Keys)
            client.Dispose();
        await Task.WhenAll(_serving);
        _received.Writer.TryComplete();
        _closed.Writer.TryComplete();
        _responses.Dispose();
        _stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }
            var connection = Interlocked.Increment(ref _connections);
            _clients.TryAdd(client, 0);
            _serving.Add(ServeAsync(client, connection));
        }
    }

    private async Task ServeAsync(TcpClient client, int connection)
    {
        try
        {
            using (client)
            {
                await using var stream = client.GetStream();
                await (_socks ? ServeSocksAsync(stream) : ServeHttpAsync(stream));
            }
        }
        catch (Exception error) when (error is IOException or SocketException or EndOfStreamException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            _clients.TryRemove(client, out _);
            _closed.Writer.TryWrite(connection);
        }
    }

    private async Task ServeHttpAsync(NetworkStream stream)
    {
        while (await ReadHeadAsync(stream) is { } head)
        {
            var lines = head.Split("\r\n");
            var requiredCredential = Volatile.Read(ref _requiredCredential);
            if (requiredCredential is not null && BasicCredential(lines) != requiredCredential)
            {
                await WriteAsync(stream, "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"test\"\r\nContent-Length: 0\r\n\r\n");
                continue;
            }
            if (requiredCredential is not null)
                Credentials.Enqueue(requiredCredential);
            Requests.Enqueue(lines[0]);
            foreach (var line in lines.Skip(1))
                Headers.Enqueue(line);
            _received.Writer.TryWrite(lines[0]);
            if (_pauseResponses)
                await _responses.WaitAsync(_stop.Token);
            if (_forward)
            {
                var parts = lines[0].Split(' ');
                var connect = parts[0] == "CONNECT";
                var target = new Uri(connect ? $"http://{parts[1]}" : parts[1]);
                using var remote = new TcpClient();
                await remote.ConnectAsync(target.Host, target.Port, _stop.Token);
                await using var upstream = remote.GetStream();
                if (connect)
                    await WriteAsync(stream, "HTTP/1.1 200 Connection Established\r\n\r\n");
                else
                    await WriteAsync(upstream, $"{parts[0]} {target.PathAndQuery} {parts[2]}\r\n{string.Join("\r\n", lines.Skip(1))}\r\n\r\n");
                await RelayAsync(stream, upstream);
                return;
            }
            await (lines[0].StartsWith("CONNECT ", StringComparison.Ordinal)
                ? WriteAsync(stream, "HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")
                : WriteBodyAsync(stream));
            if (!_keepAlive || lines[0].StartsWith("CONNECT ", StringComparison.Ordinal))
                return;
        }
    }

    private async Task ServeSocksAsync(NetworkStream stream)
    {
        var greeting = await ReadExactAsync(stream, 2);
        var methods = await ReadExactAsync(stream, greeting[1]);
        var authenticate = methods.Contains(UserPasswordMethod);
        if (Volatile.Read(ref _requiredCredential) is not null && !authenticate)
        {
            await stream.WriteAsync(new[] { SocksVersion, NoAcceptableMethod }, _stop.Token);
            return;
        }
        await stream.WriteAsync(new[] { SocksVersion, authenticate ? UserPasswordMethod : NoAuthMethod }, _stop.Token);
        if (authenticate && !await ReadSocksCredentialAsync(stream))
            return;
        var request = await ReadExactAsync(stream, 4);
        var host = request[3] switch
        {
            AddressIpv4 => new IPAddress(await ReadExactAsync(stream, Ipv4Length)).ToString(),
            AddressDomain => Encoding.ASCII.GetString(await ReadExactAsync(stream, (await ReadExactAsync(stream, 1))[0])),
            _ => new IPAddress(await ReadExactAsync(stream, Ipv6Length)).ToString(),
        };
        var port = await ReadExactAsync(stream, 2);
        var targetPort = (port[0] << 8) | port[1];
        Requests.Enqueue($"SOCKS {host}:{targetPort}");
        if (_forward)
        {
            using var remote = new TcpClient();
            await remote.ConnectAsync(host, targetPort, _stop.Token);
            await using var upstream = remote.GetStream();
            await stream.WriteAsync(new byte[] { SocksVersion, 0, 0, AddressIpv4, 0, 0, 0, 0, 0, 0 });
            await RelayAsync(stream, upstream);
            return;
        }
        await stream.WriteAsync(new byte[] { SocksVersion, 0, 0, AddressIpv4, 0, 0, 0, 0, 0, 0 });
        if (await ReadHeadAsync(stream) is { } head)
        {
            Requests.Enqueue(head.Split("\r\n")[0]);
            foreach (var line in head.Split("\r\n").Skip(1))
                Headers.Enqueue(line);
        }
        await WriteBodyAsync(stream);
    }

    private async Task RelayAsync(Stream client, Stream upstream)
    {
        using var done = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        var send = client.CopyToAsync(upstream, done.Token);
        var receive = upstream.CopyToAsync(client, done.Token);
        await Task.WhenAny(send, receive);
        await done.CancelAsync();
        try
        {
            await Task.WhenAll(send, receive);
        }
        catch (OperationCanceledException) when (done.IsCancellationRequested)
        {
        }
    }

    private async Task<bool> ReadSocksCredentialAsync(NetworkStream stream)
    {
        var header = await ReadExactAsync(stream, 2);
        var user = Encoding.UTF8.GetString(await ReadExactAsync(stream, header[1]));
        var password = Encoding.UTF8.GetString(await ReadExactAsync(stream, (await ReadExactAsync(stream, 1))[0]));
        var credential = $"{user}:{password}";
        Credentials.Enqueue(credential);
        var required = Volatile.Read(ref _requiredCredential);
        var accepted = required is null || required == credential;
        await stream.WriteAsync(new byte[] { 1, accepted ? (byte)0 : (byte)1 }, _stop.Token);
        return accepted;
    }

    private static string? BasicCredential(string[] lines)
        => lines.FirstOrDefault(line => line.StartsWith("Proxy-Authorization: Basic ", StringComparison.OrdinalIgnoreCase)) is { } header
            ? Encoding.UTF8.GetString(Convert.FromBase64String(header["Proxy-Authorization: Basic ".Length..].Trim()))
            : null;

    private async Task WriteBodyAsync(NetworkStream stream)
    {
        var head = $"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {ProxiedBody.Length}\r\nConnection: {(_keepAlive ? "keep-alive" : "close")}\r\n\r\n";
        if (!_pauseBody)
        {
            await WriteAsync(stream, $"{head}{ProxiedBody}");
            return;
        }
        await WriteAsync(stream, head);
        await _responses.WaitAsync(_stop.Token);
        await WriteAsync(stream, ProxiedBody);
    }

    private static async Task WriteAsync(NetworkStream stream, string text)
        => await stream.WriteAsync(Encoding.ASCII.GetBytes(text));

    private static async Task<string?> ReadHeadAsync(NetworkStream stream)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (!(bytes.Count >= 4 && bytes[^4] == '\r' && bytes[^3] == '\n' && bytes[^2] == '\r' && bytes[^1] == '\n'))
        {
            if (await stream.ReadAsync(one) == 0)
                return null;
            bytes.Add(one[0]);
        }
        return Encoding.ASCII.GetString(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(bytes)[..^4]);
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count)
    {
        var buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer);
        return buffer;
    }
}
