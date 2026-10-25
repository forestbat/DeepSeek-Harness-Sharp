using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Channels;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Fleet;
using Dsh.Gui.Services;
using Dsh.Gui.ViewModels;
using Dsh.Gui.Views;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.RemoteHost;
using Dsh.Runtime;
using Dsh.Web;

namespace Dsh.Tests;

/**
 * Local outbound proxy coverage uses the actual composition root, web_fetch, commands and settings persistence.
 * Controlled loopback responses verify transport diagnostics, not real external business success.
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
        List<string> stages = [];

        var result = await OutboundProxyProbe.RunAsync(
            new OutboundProxySettings { Url = $"http://127.0.0.1:{proxy.Port}", ProbeUrl = FetchTarget },
            TestContext.Current.CancellationToken, stageChanged: stages.Add);

        Assert.True(result.Reachable, result.Message);
        Assert.Equal(OutboundProxyProbe.HttpTargetStage, result.Stage);
        Assert.Equal(200, result.HttpStatus);
        Assert.Equal([OutboundProxyProbe.ConfigurationStage, OutboundProxyProbe.ProxyConnectStage, OutboundProxyProbe.HttpTargetStage], stages);
        Assert.Equal([$"GET {FetchTarget} HTTP/1.1"], proxy.Requests);
    }

    [Fact]
    public async Task Probe_ReportsUnreachableProxyLayer_WithoutPassword()
    {
        var port = FreePort();
        List<string> stages = [];

        var result = await OutboundProxyProbe.RunAsync(
            new OutboundProxySettings { Url = $"socks5://alice:{EscapedPassword}@127.0.0.1:{port}", ProbeUrl = FetchTarget },
            TestContext.Current.CancellationToken, stageChanged: stages.Add);

        Assert.False(result.Reachable);
        Assert.Equal(OutboundProxyProbe.ProxyConnectStage, result.Stage);
        Assert.Null(result.HttpStatus);
        Assert.Equal([OutboundProxyProbe.ConfigurationStage, OutboundProxyProbe.ProxyConnectStage], stages);
        Assert.Contains($"socks5://***@127.0.0.1:{port}", result.Message);
        Assert.DoesNotContain(EscapedPassword, result.Message);
        Assert.DoesNotContain(Password, result.Message);
    }

    [Theory]
    [InlineData("http://example.test/")]
    [InlineData("https://example.test/")]
    public async Task Probe_ReportsProxyAuthenticationFailure(string probeUrl)
    {
        await using var proxy = new TestForwardProxy(requiredCredential: $"alice:{Password}");
        List<string> stages = [];

        var result = await OutboundProxyProbe.RunAsync(
            new OutboundProxySettings { Url = $"http://alice:wrong@127.0.0.1:{proxy.Port}", ProbeUrl = probeUrl },
            TestContext.Current.CancellationToken, stageChanged: stages.Add);

        Assert.False(result.Reachable);
        Assert.Equal(OutboundProxyProbe.ProxyAuthStage, result.Stage);
        Assert.Equal(407, result.HttpStatus);
        Assert.Equal([OutboundProxyProbe.ConfigurationStage, OutboundProxyProbe.ProxyConnectStage, OutboundProxyProbe.HttpTargetStage, OutboundProxyProbe.ProxyAuthStage], stages);
        Assert.DoesNotContain("alice", result.Message);
        Assert.DoesNotContain("wrong", result.Message);
    }

    [Fact]
    public async Task Probe_RejectsUnsupportedScheme_WithRedactedReason()
    {
        List<string> stages = [];
        var result = await OutboundProxyProbe.RunAsync(
            new OutboundProxySettings { Url = $"ftp://alice:{EscapedPassword}@127.0.0.1:21" },
            TestContext.Current.CancellationToken, stageChanged: stages.Add);

        Assert.False(result.Reachable);
        Assert.Equal(OutboundProxyProbe.ConfigurationStage, result.Stage);
        Assert.Null(result.HttpStatus);
        Assert.Equal([OutboundProxyProbe.ConfigurationStage], stages);
        Assert.Contains("ftp://***@127.0.0.1:21", result.Message);
        Assert.DoesNotContain(EscapedPassword, result.Message);
        Assert.DoesNotContain(Password, result.Message);
        Assert.DoesNotContain("alice", result.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, false)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.NotFound, true)]
    public async Task Probe_DirectTargetStatus_IsReachability_NotBusinessSuccess(HttpStatusCode statusCode, bool configuredProxy)
    {
        await using var target = new TestForwardProxy(statusCode: statusCode);
        await using var unusedProxy = new TestForwardProxy();
        List<string> stages = [];

        var result = await OutboundProxyProbe.RunAsync(
            new OutboundProxySettings
            {
                Url = configuredProxy ? $"http://127.0.0.1:{unusedProxy.Port}" : null,
                ProbeUrl = $"http://127.0.0.1:{target.Port}/target",
            },
            TestContext.Current.CancellationToken, stageChanged: stages.Add);

        Assert.True(result.Reachable, result.Message);
        Assert.Equal(OutboundProxyProbe.HttpTargetStage, result.Stage);
        Assert.Equal((int)statusCode, result.HttpStatus);
        Assert.Equal([OutboundProxyProbe.ConfigurationStage, OutboundProxyProbe.HttpTargetStage], stages);
        Assert.Equal(["GET /target HTTP/1.1"], target.Requests);
        Assert.Equal(0, unusedProxy.Connections);
    }

    [Theory]
    [InlineData("ftp://example.test/")]
    [InlineData("not-a-url")]
    public async Task Probe_InvalidTarget_StopsAtConfiguration(string probeUrl)
    {
        await using var proxy = new TestForwardProxy();
        List<string> stages = [];

        var result = await OutboundProxyProbe.RunAsync(
            new OutboundProxySettings { Url = $"http://127.0.0.1:{proxy.Port}", ProbeUrl = probeUrl },
            TestContext.Current.CancellationToken, stageChanged: stages.Add);

        Assert.False(result.Reachable);
        Assert.Equal(OutboundProxyProbe.ConfigurationStage, result.Stage);
        Assert.Null(result.HttpStatus);
        Assert.Equal([OutboundProxyProbe.ConfigurationStage], stages);
        Assert.Equal(0, proxy.Connections);
    }

    [Theory]
    [InlineData(OutboundProxyProbe.ConfigurationStage)]
    [InlineData(OutboundProxyProbe.ProxyConnectStage)]
    [InlineData(OutboundProxyProbe.HttpTargetStage)]
    [InlineData(OutboundProxyProbe.ProxyAuthStage)]
    public async Task Probe_UserCancellationAtPhaseBoundary_StillThrows(string cancelStage)
    {
        await using var proxy = new TestForwardProxy(requiredCredential: $"alice:{Password}");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        List<string> stages = [];

        var pending = OutboundProxyProbe.RunAsync(
            new OutboundProxySettings { Url = $"http://127.0.0.1:{proxy.Port}", ProbeUrl = FetchTarget },
            cancellation.Token, stageChanged: stage =>
            {
                stages.Add(stage);
                if (stage == cancelStage)
                    cancellation.Cancel();
            });

        Assert.Equal(OutboundProxyProbe.ConfigurationStage, stages[0]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(cancelStage, stages[^1]);
        Assert.Empty(proxy.Requests);
    }

    [Fact]
    public async Task Probe_ConnectTunnelRejection_IsNotATargetHttpResponse()
    {
        await using var proxy = new TestForwardProxy();
        List<string> stages = [];

        var result = await OutboundProxyProbe.RunAsync(
            new OutboundProxySettings
            {
                Url = $"http://127.0.0.1:{proxy.Port}",
                ProbeUrl = $"https://alice:{EscapedPassword}@example.test/",
            },
            TestContext.Current.CancellationToken, stageChanged: stages.Add);

        Assert.False(result.Reachable);
        Assert.Equal(OutboundProxyProbe.HttpTargetStage, result.Stage);
        Assert.Equal(403, result.HttpStatus);
        Assert.Equal([OutboundProxyProbe.ConfigurationStage, OutboundProxyProbe.ProxyConnectStage, OutboundProxyProbe.HttpTargetStage], stages);
        Assert.Equal(["CONNECT example.test:443 HTTP/1.1"], proxy.Requests);
        Assert.DoesNotContain(Password, result.Message);
        Assert.DoesNotContain(EscapedPassword, result.Message);
        Assert.DoesNotContain("alice", result.Message);
    }

    [Fact]
    public async Task Probe_TargetCredentials_AreNotDisclosedOnReachableResponse()
    {
        await using var target = new TestForwardProxy();
        var probeUrl = $"http://alice:{EscapedPassword}@127.0.0.1:{target.Port}/target";

        var result = await OutboundProxyProbe.RunAsync(
            new OutboundProxySettings { ProbeUrl = probeUrl },
            TestContext.Current.CancellationToken);

        Assert.True(result.Reachable, result.Message);
        Assert.Equal(OutboundProxyProbe.HttpTargetStage, result.Stage);
        Assert.Equal(200, result.HttpStatus);
        Assert.Equal(["GET /target HTTP/1.1"], target.Requests);
        Assert.DoesNotContain(probeUrl, result.Message);
        Assert.DoesNotContain(Password, result.Message);
        Assert.DoesNotContain(EscapedPassword, result.Message);
        Assert.DoesNotContain("alice", result.Message);
    }

    [Fact]
    public async Task Probe_UserCancellationDuringHttpRequest_StillThrows()
    {
        await using var proxy = new TestForwardProxy(pauseResponses: true);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        List<string> stages = [];
        var pending = OutboundProxyProbe.RunAsync(
            new OutboundProxySettings { Url = $"http://127.0.0.1:{proxy.Port}", ProbeUrl = FetchTarget },
            cancellation.Token, stageChanged: stages.Add);
        Assert.Equal(OutboundProxyProbe.ConfigurationStage, stages[0]);
        Assert.Equal($"GET {FetchTarget} HTTP/1.1", await proxy.WaitForRequestAsync(TestContext.Current.CancellationToken));

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal([OutboundProxyProbe.ConfigurationStage, OutboundProxyProbe.ProxyConnectStage, OutboundProxyProbe.HttpTargetStage], stages);
        Assert.Single(proxy.Requests);
    }

    [Fact]
    public void Probe_OldWireSchema_PreservesMissingStructuredFields()
    {
        var result = JsonSerializer.Deserialize(
            """{"reachable":true,"message":"legacy transport response"}""",
            NetworkProtocolJsonContext.Default.RemoteOutboundProxyProbeResult);

        Assert.True(result.Reachable);
        Assert.Null(result.Stage);
        Assert.Null(result.HttpStatus);
        Assert.Null(result.HostHome);
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
        Assert.Equal([$"GET {FetchTarget} HTTP/1.1"], proxy.Requests);
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
    [InlineData("Authorization", "Bearer")]
    [InlineData("Proxy-Authorization", "Basic")]
    [InlineData("authorization", "Bearer")]
    public void Redact_HidesAuthenticationHeaderValues(string header, string scheme)
    {
        var credential = Convert.ToBase64String(Encoding.UTF8.GetBytes($"probe-user:{Password}"));

        var result = OutboundProxy.Redact($"{header}: {scheme} {credential}\r\nnext: safe");

        Assert.DoesNotContain(credential, result);
        Assert.Contains("next: safe", result);
    }

    [Fact]
    public void Redact_HidesKnownRawAndEncodedSecrets_AndUrlCredentials()
    {
        const string username = "probe-user";
        var proxyUrl = $"http://{username}:{EscapedPassword}@example.test:8080";
        var standaloneSecret = "probe-token:/+";
        var text = $"url={proxyUrl}; encoded-url={Uri.EscapeDataString(proxyUrl)}; user={username}; password={Password}; encoded-password={EscapedPassword}; token={standaloneSecret}; encoded-token={Uri.EscapeDataString(standaloneSecret)}; safe detail";

        var result = OutboundProxy.Redact(text, proxyUrl, standaloneSecret);

        Assert.DoesNotContain(proxyUrl, result);
        Assert.DoesNotContain(Uri.EscapeDataString(proxyUrl), result);
        Assert.DoesNotContain(username, result);
        Assert.DoesNotContain(Password, result);
        Assert.DoesNotContain(EscapedPassword, result);
        Assert.DoesNotContain(standaloneSecret, result);
        Assert.DoesNotContain(Uri.EscapeDataString(standaloneSecret), result);
        Assert.Contains("safe detail", result);
    }

    [Theory]
    [InlineData("Authorization", "Bearer")]
    [InlineData("proxy-authorization", "Basic")]
    public void Redact_HidesQuotedAndEscapedAuthenticationHeaders(string header, string scheme)
    {
        // These randomized credentials are synthetic test data, not real credentials.
        var credential = $"synthetic-{Guid.NewGuid():N}:user@value\\\"";
        var json = $"{{\"{header}\":\"{scheme} {JsonEncodedText.Encode(credential)}\",\"safe\":\"retained\"}}";
        foreach (var encoder in new[] { JavaScriptEncoder.Default, JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
        {
            var diagnostic = json;
            for (var depth = 0; depth < 3; depth++)
            {
                var result = OutboundProxy.Redact(diagnostic);
                Assert.DoesNotContain(credential[..credential.IndexOf(':')], result);
                Assert.Contains("retained", result);
                diagnostic = JsonEncodedText.Encode(diagnostic, encoder).ToString();
            }
        }
    }

    [Theory]
    [InlineData("Authorization")]
    [InlineData("Proxy-Authorization")]
    public void Redact_ClearsAssignmentHeadersBeforeKnownSecretsOrUserInfo(string header)
    {
        // These randomized credentials are synthetic test data, not real credentials.
        var credential = $"synthetic-{Guid.NewGuid():N}";
        var result = OutboundProxy.Redact($"{header}=Basic {header}:{credential}@example.test\r\nsafe: retained", header);

        Assert.DoesNotContain(credential, result);
        Assert.Contains("safe: retained", result);
    }

    [Fact]
    public void Redact_HidesJsonEncodedKnownSecrets_InNestedRpcDiagnostics()
    {
        // These randomized credentials are synthetic test data, not real credentials.
        var secret = $"synthetic-{Guid.NewGuid():N}:\"\\\n中";
        var representations = new HashSet<string>(StringComparer.Ordinal) { secret, Uri.EscapeDataString(secret) };
        foreach (var encoder in new[] { JavaScriptEncoder.Default, JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
        {
            var encoded = JsonEncodedText.Encode(secret, encoder).ToString();
            representations.Add(encoded);
            representations.Add(JsonEncodedText.Encode(encoded, JavaScriptEncoder.Default).ToString());
            representations.Add(JsonEncodedText.Encode(encoded, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString());
        }
        var diagnostic = $"RPC error message: {string.Join(" | ", representations)}; safe detail";

        var result = OutboundProxy.Redact(diagnostic, secret);

        foreach (var representation in representations)
            Assert.DoesNotContain(representation, result);
        Assert.Contains("safe detail", result);
    }

    [Fact]
    public void Redact_FullyMasksKnownUrlShapedPasswordsAndProxyCommands()
    {
        // These randomized credentials are synthetic test data, not real credentials.
        var password = $"https://password-{Guid.NewGuid():N}.example.test/value";
        var proxyCommand = $"ssh -i 'synthetic-{Guid.NewGuid():N}' proxy-command.example.test -W %h:%p";
        var diagnostic = $"password={password}; proxy={proxyCommand}; quoted={proxyCommand.Replace("'", "'\\''", StringComparison.Ordinal)}; safe detail";

        var result = OutboundProxy.Redact(diagnostic, password, proxyCommand);

        Assert.DoesNotContain(password, result);
        Assert.DoesNotContain("proxy-command.example.test", result);
        Assert.Contains("safe detail", result);
    }

    [Fact]
    public void RedactUrlCredentials_RetainsEndpointAndHidesRawAndEncodedCredentials()
    {
        // These randomized credentials are synthetic test data, not real credentials.
        var username = $"synthetic-user-{Guid.NewGuid():N}:suffix";
        var password = $"synthetic-password-{Guid.NewGuid():N}:\"\\\n中";
        var url = $"ftp://{Uri.EscapeDataString(username)}:{Uri.EscapeDataString(password)}@proxy.example.test:21";
        var diagnostic = $"{url}; encoded-url={Uri.EscapeDataString(url)}; user={username}; password={password}; json-password={JsonEncodedText.Encode(password)}; safe detail";

        var result = OutboundProxy.RedactUrlCredentials(diagnostic, url);

        Assert.Contains("ftp://***@proxy.example.test:21", result);
        Assert.DoesNotContain(username, result);
        Assert.DoesNotContain(password, result);
        Assert.DoesNotContain(Uri.EscapeDataString(username), result);
        Assert.DoesNotContain(Uri.EscapeDataString(password), result);
        Assert.DoesNotContain(JsonEncodedText.Encode(password).ToString(), result);
        Assert.Contains("safe detail", result);
    }

    [Theory]
    [InlineData("https://proxy.example.test/?token=")]
    [InlineData("https://proxy.example.test/#")]
    [InlineData("malformed proxy ")]
    public void RedactUrlCredentials_MasksSensitiveOrMalformedUrlContexts(string prefix)
    {
        // These randomized credentials are synthetic test data, not real credentials.
        var url = $"{prefix}synthetic-{Guid.NewGuid():N}";

        var result = OutboundProxy.RedactUrlCredentials($"{url}; encoded={Uri.EscapeDataString(url)}; safe detail", url);

        Assert.DoesNotContain(url, result);
        Assert.DoesNotContain(Uri.EscapeDataString(url), result);
        Assert.Contains("safe detail", result);
    }

    [Fact(Timeout = 5000)]
    public async Task Doctor_RealSshMisconfiguredInstallation_RedactsRepairCommands()
    {
        FleetSshEndToEndTests.RequireEnvironment();
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("This real SSH regression requires the POSIX SSH fixture.");
            return;
        }
        var cancellation = TestContext.Current.CancellationToken;
        await using var ssh = await TestSshServer.StartAsync(cancellation);
        // These randomized credentials are synthetic test data, not real credentials.
        var password = $"synthetic-{Guid.NewGuid():N}";
        var home = Path.Combine(ssh.Home, "doctor-home");
        var run = Path.Combine(home, "run");
        Directory.CreateDirectory(run);
        File.SetUnixFileMode(run, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        var bin = Path.Combine(ssh.Home, ".dsharp", System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier);
        Directory.CreateDirectory(bin);
        // A real non-Host executable represents an invalid configured installation without emulating SSH or RPC replies.
        File.CreateSymbolicLink(Path.Combine(bin, "dsharp"), OperatingSystem.IsMacOS() ? "/usr/bin/false" : "/bin/false");
        var direct = ssh.Machine($"doctor-{password}", home);
        var trusted = await FleetDoctor.CheckSshAsync(direct, new SshTransport(direct), cancellation);
        Assert.True(trusted.Outcome == DoctorOutcome.Ok, trusted.Detail);
        var proxyCommand = $"ssh -F /dev/null -p {direct.Port} -i {RemoteShell.Quote(direct.KeyPath!)} -o BatchMode=yes -o StrictHostKeyChecking=yes -o UserKnownHostsFile={RemoteShell.Quote(Path.Combine(ssh.Root, "known_hosts"))} -W %h:%p {RemoteShell.Quote($"{direct.User}@{direct.Host}")} # {password}";
        var machine = direct with { Password = password, Proxy = proxyCommand };

        var checks = await FleetDoctor.CheckAsync(machine, FleetConnectionTests.TestOptions, cancellation);

        Assert.Equal(DoctorOutcome.Ok, Assert.Single(checks, check => check.Name == "ssh").Outcome);
        Assert.Equal(DoctorOutcome.Failed, Assert.Single(checks, check => check.Name == "permissions").Outcome);
        Assert.Equal(DoctorOutcome.Failed, Assert.Single(checks, check => check.Name == "connection").Outcome);
        foreach (var check in checks)
        {
            Assert.DoesNotContain(password, check.Detail);
            if (check.Fix is not { } fix)
                continue;
            Assert.DoesNotContain(password, fix);
        }
        Assert.Contains("chmod", Assert.Single(checks, check => check.Name == "permissions").Fix);
        Assert.Contains("host serve", Assert.Single(checks, check => check.Name == "connection").Fix);
    }

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


/** An actual loopback TCP proxy with controlled responses or optional real forwarding, not external business smoke. */
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
    private readonly HttpStatusCode _statusCode;
    private readonly bool _observePeerCloseWhilePaused;
    private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();
    private readonly ConcurrentBag<Task> _serving = [];
    private readonly Channel<string> _received = Channel.CreateUnbounded<string>();
    private readonly Channel<int> _closed = Channel.CreateUnbounded<int>();
    private readonly SemaphoreSlim _responses = new(0);
    private readonly Task _accepting;
    private int _connections;
    private int _authenticationChallenges;

    public const string ProxiedBody = "via-test-proxy";

    public TestForwardProxy(bool socks = false, string? requiredCredential = null, bool forward = false, bool keepAlive = false, bool pauseResponses = false, bool pauseBody = false, HttpStatusCode statusCode = HttpStatusCode.OK, bool observePeerCloseWhilePaused = false)
    {
        if (observePeerCloseWhilePaused && (socks || forward || keepAlive))
            throw new ArgumentException("Peer-close observation is limited to controlled nonpersistent HTTP responses.", nameof(observePeerCloseWhilePaused));
        _socks = socks;
        _requiredCredential = requiredCredential;
        _forward = forward;
        _keepAlive = keepAlive;
        _pauseResponses = pauseResponses;
        _pauseBody = pauseBody;
        _statusCode = statusCode;
        _observePeerCloseWhilePaused = observePeerCloseWhilePaused;
        _listener.Start();
        _accepting = AcceptAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public int Connections => Volatile.Read(ref _connections);

    public int AuthenticationChallenges => Volatile.Read(ref _authenticationChallenges);

    public int ActiveConnections => _clients.Count;

    public ValueTask<string> WaitForRequestAsync(CancellationToken cancellationToken)
        => _received.Reader.ReadAsync(cancellationToken);

    public ValueTask<int> WaitForClosedConnectionAsync(CancellationToken cancellationToken)
        => _closed.Reader.ReadAsync(cancellationToken);

    public void ReleaseResponse() => _responses.Release();

    public void RequireCredential(string credential) => Volatile.Write(ref _requiredCredential, credential);

    public ConcurrentQueue<string> Requests { get; } = new();

    public ConcurrentQueue<string> HttpAttempts { get; } = new();

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
            HttpAttempts.Enqueue(lines[0]);
            var requiredCredential = Volatile.Read(ref _requiredCredential);
            if (requiredCredential is not null && BasicCredential(lines) != requiredCredential)
            {
                Interlocked.Increment(ref _authenticationChallenges);
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
            {
                if (_observePeerCloseWhilePaused && lines[0].StartsWith("GET ", StringComparison.Ordinal))
                {
                    if (!await WaitForResponseOrPeerCloseAsync(stream))
                        return;
                }
                else
                    await _responses.WaitAsync(_stop.Token);
            }
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

    /** The opt-in controlled HTTP barrier observes EOF without retaining a cancelled client's socket. */
    private async Task<bool> WaitForResponseOrPeerCloseAsync(NetworkStream stream)
    {
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        var response = _responses.WaitAsync(waiting.Token);
        var peerClosed = stream.ReadAsync(new byte[1], waiting.Token).AsTask();
        var responseReady = false;
        try
        {
            if (await Task.WhenAny(response, peerClosed) == peerClosed)
            {
                if (await peerClosed != 0)
                    throw new IOException("Unexpected request data while the controlled HTTP response is paused.");
            }
            else
            {
                await response;
                responseReady = true;
            }
        }
        finally
        {
            await waiting.CancelAsync();
            try
            {
                await Task.WhenAll(response, peerClosed);
            }
            catch (OperationCanceledException) when (waiting.IsCancellationRequested)
            {
            }
            finally
            {
                if (!peerClosed.IsCanceled || _stop.IsCancellationRequested)
                    responseReady = false;
                // Return a concurrently acquired permit when EOF or failure prevents a response.
                if (!responseReady && response.IsCompletedSuccessfully)
                    _responses.Release();
            }
        }
        if (peerClosed.IsCompletedSuccessfully && peerClosed.Result != 0)
            throw new IOException("Unexpected request data while the controlled HTTP response is paused.");
        return responseReady;
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
        var head = $"HTTP/1.1 {(int)_statusCode} {_statusCode}\r\nContent-Type: text/plain\r\nContent-Length: {ProxiedBody.Length}\r\nConnection: {(_keepAlive ? "keep-alive" : "close")}\r\n\r\n";
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
