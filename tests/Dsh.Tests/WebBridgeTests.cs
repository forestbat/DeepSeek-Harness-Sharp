using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.RemoteHost;
using Dsh.Transport;
using Dsh.WebBridge;

namespace Dsh.Tests;

/** The browser bridge end to end: a real in-process host daemon on its loopback endpoint, the bridge on a free loopback port, and real HTTP and WebSocket clients. */
[Collection(SerialProcessCollection.Name)]
public sealed class WebBridgeTests
{
    private const string ForeignOrigin = "http://evil.example";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Explicit = true)]
    public async Task WebSocket_UsesSharedFleetForRealSessionEvents()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = await HarnessHostFixture.CreateAsync("wbe", cancellationToken);
        await using var bridge = WebBridgeServer.Start(new WebBridgeOptions(host.Root, 0));
        var access = new BridgeAccess(bridge.Port, LocalEndpointToken.Read(WebBridgeEndpoint.PortFile(host.Root))!);
        await access.WaitForMachinesAsync(statuses => statuses.Values.All(state => state == "Connected"), TimeSpan.FromSeconds(5), cancellationToken);
        using var socket = await access.ConnectWebSocketAsync(await access.LoginAsync(cancellationToken), access.Origin, cancellationToken);
        var info = await WebSocketRpc.RequestAsync(socket, 1, HostProtocol.MethodInfo, null);
        Assert.Equal(HostProtocol.Version, info.GetProperty("ProtocolVersion").GetInt32());
        var request = new RemoteSessionCreateRequest(host.Root);
        var parameters = System.Text.Json.JsonSerializer.Serialize(request, HostProtocolJsonContext.Default.RemoteSessionCreateRequest);
        var created = await WebSocketRpc.RequestAsync(socket, 2, HostProtocol.MethodSessionCreate, parameters);
        var sessionId = created.GetProperty("Id").GetString();
        await WebSocketRpc.RequestAsync(socket, 3, HostProtocol.MethodSessionSubscribe, $$"""{"SessionId":"{{sessionId}}","ViewId":"web-events","FromSeq":0}""");
        await WebSocketRpc.SendAsync(socket, 4, HostProtocol.MethodSessionMessage, $$"""{"SessionId":"{{sessionId}}","Text":"web-user-message"}""");
        var pushed = await WebSocketRpc.ReceiveUntilAsync(socket, message => message.TryGetProperty("method", out var method)
            && method.GetString() == HostProtocol.NotificationEvent
            && message.GetProperty("params").GetProperty("EventJson").GetString()!.Contains("web-user-message", StringComparison.Ordinal));
        Assert.Equal(sessionId, pushed.GetProperty("params").GetProperty("SessionId").GetString());
    }

    [Theory(Timeout = 60000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrowserQuestionResponses_PreserveChoiceContract_AndAuthoritativeResolutionWins(bool dismissed)
    {
        await using var host = await HarnessHostFixture.CreateAsync("wbq", TestContext.Current.CancellationToken);
        await using var bridge = WebBridgeServer.Start(new WebBridgeOptions(host.Root, 0));
        var access = new BridgeAccess(bridge.Port, LocalEndpointToken.Read(WebBridgeEndpoint.PortFile(host.Root))!);
        await access.WaitForMachinesAsync(states => states.Values.All(state => state == "Connected"), TimeSpan.FromSeconds(10), Ct);
        var cookies = await access.LoginAsync(Ct);
        using var first = await access.ConnectWebSocketAsync(cookies, access.Origin, Ct);
        using var second = await access.ConnectWebSocketAsync(cookies, access.Origin, Ct);
        var service = host.Environment.App.Ctx.Get<UserQuestionService>(UserQuestionService.ServiceName)!;
        var waiting = service.Ask(new AskUserQuestionRequest(
        [
            new("targets", "Choose targets", Detail: "Shared choices", Options:
            [
                new("Tests", Value: "tests", Default: true),
                new("Disabled", Value: "disabled", Disabled: true),
                new("Docs", Value: "docs"),
            ], MultiSelect: true, AllowCustom: false),
            new("notes", "Add notes"),
        ], host.Environment.Agent), Ct);
        var firstView = await PendingViewAsync(first, "Questions", present: true);
        var secondView = await PendingViewAsync(second, "Questions", present: true);
        var request = firstView.GetProperty("Pending").GetProperty("Questions")[0];
        var requestId = request.GetProperty("RequestId").GetString()!;
        Assert.Equal(requestId, secondView.GetProperty("Pending").GetProperty("Questions")[0].GetProperty("RequestId").GetString());
        var question = request.GetProperty("Questions")[0];
        Assert.False(question.GetProperty("AllowCustom").GetBoolean());
        Assert.True(question.GetProperty("MultiSelect").GetBoolean());
        Assert.Equal("tests", question.GetProperty("Options")[0].GetProperty("Value").GetString());
        Assert.True(question.GetProperty("Options")[0].GetProperty("Default").GetBoolean());
        Assert.True(question.GetProperty("Options")[1].GetProperty("Disabled").GetBoolean());
        var response = new RemoteQuestionResponse(requestId,
            dismissed ? [] : [new("targets", ["tests", "docs"], null), new("notes", [], "Browser notes")],
            dismissed ? "dismissed" : "answered", dismissed ? "user_cancelled" : null);
        var json = JsonSerializer.Serialize(response, HostProtocolJsonContext.Default.RemoteQuestionResponse);
        Assert.True((await WebSocketRpc.RequestAsync(first, 1, HostProtocol.MethodQuestionRespond, json)).GetBoolean());
        var resolved = await PendingViewAsync(second, "Questions", present: false);
        Assert.Empty(resolved.GetProperty("Pending").GetProperty("Questions").EnumerateArray());
        Assert.False((await WebSocketRpc.RequestAsync(second, 1, HostProtocol.MethodQuestionRespond, json)).GetBoolean());
        var result = await waiting.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        Assert.Equal(dismissed, result.IsDismissed);
        if (dismissed)
        {
            Assert.Empty(result.Answers);
            Assert.Equal("user_cancelled", result.Reason);
        }
        else
        {
            Assert.Equal(["tests", "docs"], result.Answers[0].Selected);
            Assert.Equal("Browser notes", result.Answers[1].Custom);
        }
    }

    [Theory(Timeout = 60000)]
    [InlineData(true, false, false, ApprovalOutcome.AllowedOnce)]
    [InlineData(true, true, false, ApprovalOutcome.AllowedForSession)]
    [InlineData(false, false, false, ApprovalOutcome.Rejected)]
    [InlineData(false, false, true, ApprovalOutcome.Cancelled)]
    public async Task BrowserApprovalResponses_CarryAllFourOutcomes_AndRemoveOtherClientPending(
        bool allow, bool rememberForSession, bool cancelled, ApprovalOutcome expected)
    {
        await using var host = await HarnessHostFixture.CreateAsync("wba", TestContext.Current.CancellationToken);
        await using var bridge = WebBridgeServer.Start(new WebBridgeOptions(host.Root, 0));
        var access = new BridgeAccess(bridge.Port, LocalEndpointToken.Read(WebBridgeEndpoint.PortFile(host.Root))!);
        await access.WaitForMachinesAsync(states => states.Values.All(state => state == "Connected"), TimeSpan.FromSeconds(10), Ct);
        var cookies = await access.LoginAsync(Ct);
        using var first = await access.ConnectWebSocketAsync(cookies, access.Origin, Ct);
        using var second = await access.ConnectWebSocketAsync(cookies, access.Origin, Ct);
        var waiting = RemoteHostMultiClientTests.RaiseApprovalAsync(host, host.Environment.Agent, "bash");
        var firstView = await PendingViewAsync(first, "Approvals", present: true);
        await PendingViewAsync(second, "Approvals", present: true);
        var request = firstView.GetProperty("Pending").GetProperty("Approvals")[0];
        var requestId = request.GetProperty("RequestId").GetString()!;
        Assert.Equal("bash", request.GetProperty("Tool").GetString());
        Assert.Equal("{}", request.GetProperty("Arguments").GetString());
        var response = new RemoteApprovalResponse(requestId, allow, null, rememberForSession, cancelled);
        var json = JsonSerializer.Serialize(response, HostProtocolJsonContext.Default.RemoteApprovalResponse);
        Assert.True((await WebSocketRpc.RequestAsync(first, 1, HostProtocol.MethodApprovalRespond, json)).GetBoolean());
        await PendingViewAsync(second, "Approvals", present: false);
        Assert.False((await WebSocketRpc.RequestAsync(second, 1, HostProtocol.MethodApprovalRespond, json)).GetBoolean());
        Assert.Equal(expected, await waiting.WaitAsync(TimeSpan.FromSeconds(20), Ct));
    }

    private static async Task<JsonElement> PendingViewAsync(ClientWebSocket socket, string kind, bool present)
    {
        var message = await WebSocketRpc.ReceiveUntilAsync(socket, message =>
            message.TryGetProperty("method", out var method) && method.GetString() == BridgeProtocol.NotificationView
            && message.GetProperty("params").GetProperty("State").GetString() == "Connected"
            && (message.GetProperty("params").GetProperty("Pending").GetProperty(kind).GetArrayLength() > 0) == present);
        return message.GetProperty("params");
    }

    [Fact(Explicit = true)]
    public async Task DetachingOneBusyTerminalView_KeepsOtherViewsAndBrowserRequestsAlive()
    {
        await using var host = await HarnessHostFixture.CreateAsync("wbv", TestContext.Current.CancellationToken);
        await using var bridge = WebBridgeServer.Start(new WebBridgeOptions(host.Root, 0));
        var access = new BridgeAccess(bridge.Port, LocalEndpointToken.Read(WebBridgeEndpoint.PortFile(host.Root))!);
        await access.WaitForMachinesAsync(statuses => statuses.Values.All(state => state == "Connected"), TimeSpan.FromSeconds(5), Ct);
        var cookies = await access.LoginAsync(Ct);
        using var browser = await access.ConnectWebSocketAsync(cookies, access.Origin, Ct);
        using var otherBrowser = await access.ConnectWebSocketAsync(cookies, access.Origin, Ct);
        var creator = await host.ConnectAsync("terminal-owner", Ct);
        var ptyId = await creator.Pty.StartPtyAsync(new RemotePtyStartRequest(TestShell.FileName, TestShell.Arguments, Cwd: host.Environment.App.Home.Root), Ct);
        await WebSocketRpc.RequestAsync(browser, 1, PtyProtocol.MethodPtyAttach, $$"""{"PtyId":"{{ptyId}}","ViewId":"kept-local","Columns":120,"Rows":40}""");
        await WebSocketRpc.RequestAsync(otherBrowser, 1, PtyProtocol.MethodPtyAttach, $$"""{"PtyId":"{{ptyId}}","ViewId":"kept-remote","Columns":120,"Rows":40}""");
        await WebSocketRpc.RequestAsync(browser, 2, PtyProtocol.MethodPtyAttach, $$"""{"PtyId":"{{ptyId}}","ViewId":"closing-view","Columns":120,"Rows":40}""");
        const int floodLineCount = 10_000;
        var echo = TestShell.EchoAnswer("view-output");
        var flood = OperatingSystem.IsWindows()
            ? $"for /l %i in (1,1,{floodLineCount}) do @({echo})"
            : $$"""i=0; while [ "$i" -lt {{floodLineCount}} ]; do {{echo}}; i=$((i+1)); done""";
        var input = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{flood}\r"));
        await WebSocketRpc.SendAsync(browser, 3, PtyProtocol.MethodPtyWrite, $$"""{"PtyId":"{{ptyId}}","Base64":"{{input}}"}""");
        await WebBridgeHubTests.ReadTerminalUntilAsync(browser, ptyId, "view-output-42", "closing-view");
        await WebSocketRpc.RequestAsync(browser, 4, PtyProtocol.MethodPtyDetach, $$"""{"PtyId":"{{ptyId}}","ViewId":"closing-view"}""");
        Assert.Equal(HostProtocol.Version, (await WebSocketRpc.RequestAsync(browser, 5, HostProtocol.MethodInfo, null)).GetProperty("ProtocolVersion").GetInt32());
        Assert.Equal(HostProtocol.Version, (await WebSocketRpc.RequestAsync(otherBrowser, 2, HostProtocol.MethodInfo, null)).GetProperty("ProtocolVersion").GetInt32());
        var command = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{TestShell.EchoAnswer("surviving-view")}\r"));
        await WebSocketRpc.SendAsync(browser, 6, PtyProtocol.MethodPtyWrite, $$"""{"PtyId":"{{ptyId}}","Base64":"{{command}}"}""");
        await WebBridgeHubTests.ReadTerminalUntilAsync(browser, ptyId, "surviving-view-42", "kept-local");
        await WebBridgeHubTests.ReadTerminalUntilAsync(otherBrowser, ptyId, "surviving-view-42", "kept-remote");
        Assert.Equal(WebSocketState.Open, browser.State);
        Assert.Equal(WebSocketState.Open, otherBrowser.State);
    }

    [Theory(Timeout = 5000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosingBrowser_AbortsOnlyItsUnfinishedUploads_IncludingUnreadBeginReply(bool closeBeforeReadingBeginReply)
    {
        await using var host = await HarnessHostFixture.CreateAsync("wbu", TestContext.Current.CancellationToken);
        await using var bridge = WebBridgeServer.Start(new WebBridgeOptions(host.Root, 0));
        var access = new BridgeAccess(bridge.Port, LocalEndpointToken.Read(WebBridgeEndpoint.PortFile(host.Root))!);
        await access.WaitForMachinesAsync(statuses => statuses.Values.All(state => state == "Connected"), TimeSpan.FromSeconds(5), Ct);
        var cookies = await access.LoginAsync(Ct);
        using var closing = await access.ConnectWebSocketAsync(cookies, access.Origin, Ct);
        using var retained = await access.ConnectWebSocketAsync(cookies, access.Origin, Ct);
        var abandonedDirectory = Path.Combine(host.Environment.App.Home.Root, "abandoned-browser");
        var retainedDirectory = Path.Combine(host.Environment.App.Home.Root, "retained-browser");
        Directory.CreateDirectory(abandonedDirectory);
        Directory.CreateDirectory(retainedDirectory);
        var abandonedPath = Path.Combine(abandonedDirectory, "unfinished.bin");
        var retainedPath = Path.Combine(retainedDirectory, "kept.bin");
        var retainedBegin = JsonSerializer.Serialize(new RemoteUploadBegin(retainedPath), FilesProtocolJsonContext.Default.RemoteUploadBegin);
        var retainedUpload = (await WebSocketRpc.RequestAsync(retained, 1, FilesProtocol.MethodFsUploadBegin, retainedBegin)).GetProperty("UploadId").GetString()!;
        var prefix = Encoding.UTF8.GetBytes("second-");
        var chunk = JsonSerializer.Serialize(new RemoteUploadChunk(retainedUpload, 0, Convert.ToBase64String(prefix)), FilesProtocolJsonContext.Default.RemoteUploadChunk);
        Assert.Equal(prefix.Length, (await WebSocketRpc.RequestAsync(retained, 2, FilesProtocol.MethodFsUploadChunk, chunk)).GetInt64());
        var abandonedBegin = JsonSerializer.Serialize(new RemoteUploadBegin(abandonedPath), FilesProtocolJsonContext.Default.RemoteUploadBegin);
        await WebSocketRpc.SendAsync(closing, 1, FilesProtocol.MethodFsUploadBegin, abandonedBegin);
        if (!closeBeforeReadingBeginReply)
        {
            var reply = await WebSocketRpc.ReceiveUntilAsync(closing, message => message.TryGetProperty("id", out var id) && id.GetInt32() == 1);
            var uploadId = reply.GetProperty("result").GetProperty("UploadId").GetString()!;
            Assert.Single(Directory.GetFiles(abandonedDirectory));
            // Separate browser connections used to provide this ownership boundary at the Host.
            var foreignAbort = JsonSerializer.Serialize(new RemoteUploadRef(uploadId), FilesProtocolJsonContext.Default.RemoteUploadRef);
            await WebSocketRpc.SendAsync(retained, 3, FilesProtocol.MethodFsUploadAbort, foreignAbort);
            var refused = await WebSocketRpc.ReceiveUntilAsync(retained, message => message.TryGetProperty("id", out var id) && id.GetInt32() == 3);
            Assert.Equal(-32602, refused.GetProperty("error").GetProperty("code").GetInt32());
            Assert.Single(Directory.GetFiles(abandonedDirectory));
        }
        if (closeBeforeReadingBeginReply)
        {
            // Observe real Host allocation without consuming the begin response, then lose the browser transport.
            // The relay must recover its late UploadId before finishing cleanup.
            await WebSocketRpc.WaitForAsync(() => Directory.GetFiles(abandonedDirectory).Length == 1);
            closing.Abort();
        }
        else
            await closing.CloseAsync(WebSocketCloseStatus.NormalClosure, "", Ct);
        await WebSocketRpc.WaitForAsync(() => Directory.GetFiles(abandonedDirectory).Length == 0);
        Assert.False(File.Exists(abandonedPath));
        Assert.Single(Directory.GetFiles(retainedDirectory));
        Assert.Equal(HostProtocol.Version, (await WebSocketRpc.RequestAsync(retained, 4, HostProtocol.MethodInfo, null)).GetProperty("ProtocolVersion").GetInt32());
        var suffix = Encoding.UTF8.GetBytes("continued");
        var continued = JsonSerializer.Serialize(new RemoteUploadChunk(retainedUpload, prefix.Length, Convert.ToBase64String(suffix)), FilesProtocolJsonContext.Default.RemoteUploadChunk);
        Assert.Equal(prefix.Length + suffix.Length, (await WebSocketRpc.RequestAsync(retained, 5, FilesProtocol.MethodFsUploadChunk, continued)).GetInt64());
        var commit = JsonSerializer.Serialize(new RemoteUploadCommit(retainedUpload, prefix.Length + suffix.Length), FilesProtocolJsonContext.Default.RemoteUploadCommit);
        await WebSocketRpc.RequestAsync(retained, 6, FilesProtocol.MethodFsUploadCommit, commit);
        Assert.Equal("second-continued", await File.ReadAllTextAsync(retainedPath, Ct));
        Assert.Equal(["kept.bin"], Directory.GetFiles(retainedDirectory).Select(Path.GetFileName));
        Assert.Empty(Directory.GetFiles(abandonedDirectory));
        Assert.Equal(WebSocketState.Open, retained.State);
    }

    [Fact(Explicit = true)]
    public async Task WebSocket_WithoutSessionCookie_IsRefused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await BridgeFixture.StartAsync(cancellationToken);
        using var socket = BridgeAccess.CreateWebSocket(new CookieContainer(), fixture.Access.Origin);

        await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(fixture.Access.WebSocketUri, cancellationToken));
        Assert.Equal(HttpStatusCode.Unauthorized, socket.HttpStatusCode);
        Assert.Equal(0, fixture.RelayedConnections);
    }

    [Fact(Explicit = true)]
    public async Task WebSocket_FromForeignOrigin_IsRefusedEvenWithSession()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await BridgeFixture.StartAsync(cancellationToken);
        using var socket = BridgeAccess.CreateWebSocket(await fixture.Access.LoginAsync(cancellationToken), ForeignOrigin);

        await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(fixture.Access.WebSocketUri, cancellationToken));
        Assert.Equal(HttpStatusCode.Forbidden, socket.HttpStatusCode);
    }

    [Fact(Explicit = true)]
    public async Task Login_ExchangesTokenForStrictHttpOnlySessionCookie()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await BridgeFixture.StartAsync(cancellationToken);
        var cookies = new CookieContainer();
        using var client = BridgeAccess.CreateHttpClient(cookies);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(fixture.Access.Url(WebBridgeServer.SessionPath), cancellationToken)).StatusCode);

        using var wrongToken = await fixture.Access.PostLoginAsync(client, "0123", fixture.Access.Origin, cancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongToken.StatusCode);
        using var crossSite = await fixture.Access.PostLoginAsync(client, fixture.Token, ForeignOrigin, cancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, crossSite.StatusCode);
        Assert.Empty(cookies.GetAllCookies());

        using var accepted = await fixture.Access.PostLoginAsync(client, fixture.Token, fixture.Access.Origin, cancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        var setCookie = Assert.Single(accepted.Headers.GetValues("Set-Cookie"));
        Assert.Contains("HttpOnly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SameSite=Strict", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fixture.Token, setCookie, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync(fixture.Access.Url(WebBridgeServer.SessionPath), cancellationToken)).StatusCode);
    }

    [Fact(Explicit = true)]
    public async Task HostHeader_MustNameLoopback_ButMayCarryAForwardedPort()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await BridgeFixture.StartAsync(cancellationToken);
        using var client = BridgeAccess.CreateHttpClient(new CookieContainer());

        Assert.Equal(HttpStatusCode.Forbidden, await GetWithHostAsync(client, fixture, $"evil.example:{fixture.Bridge.Port}"));
        Assert.Equal(HttpStatusCode.Forbidden, await GetWithHostAsync(client, fixture, $"127.0.0.1.evil.example:{fixture.Bridge.Port}"));
        Assert.Equal(HttpStatusCode.OK, await GetWithHostAsync(client, fixture, "localhost:9999"));
    }

    [Fact(Explicit = true)]
    public async Task FrontEnd_IsServedFromEmbeddedAssetsWithSecurityHeaders()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await BridgeFixture.StartAsync(cancellationToken);
        using var client = BridgeAccess.CreateHttpClient(new CookieContainer());

        using var page = await client.GetAsync(fixture.Access.Url("/"), cancellationToken);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
        Assert.Contains("frame-ancestors 'none'", Assert.Single(page.Headers.GetValues("Content-Security-Policy")), StringComparison.Ordinal);

        using var script = await client.GetAsync(fixture.Access.Url("/rpc.js"), cancellationToken);
        Assert.Equal("text/javascript", script.Content.Headers.ContentType?.MediaType);

        using var protocol = await client.GetAsync(fixture.Access.Url("/protocol.js"), cancellationToken);
        Assert.Equal(HttpStatusCode.OK, protocol.StatusCode);
        Assert.Equal("text/javascript", protocol.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", Assert.Single(protocol.Headers.GetValues("X-Content-Type-Options")));
        Assert.Contains("script-src 'self'", Assert.Single(protocol.Headers.GetValues("Content-Security-Policy")), StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Token, await protocol.Content.ReadAsStringAsync(cancellationToken), StringComparison.Ordinal);
        Assert.False(protocol.Headers.Contains("Access-Control-Allow-Origin"));
        using var forbiddenProtocol = new HttpRequestMessage(HttpMethod.Get, fixture.Access.Url("/protocol.js"));
        forbiddenProtocol.Headers.Host = "evil.example";
        using var rejectedProtocol = await client.SendAsync(forbiddenProtocol, cancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, rejectedProtocol.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(fixture.Access.Url("/missing.js"), cancellationToken)).StatusCode);
    }

    [Fact(Explicit = true)]
    public async Task BusyPort_DoesNotReplaceRunningBridgeCredentials()
    {
        await using var fixture = await BridgeFixture.StartAsync(TestContext.Current.CancellationToken);
        var token = fixture.Token;
        Assert.Throws<IOException>(() => WebBridgeServer.Start(new WebBridgeOptions(fixture.Root, fixture.Bridge.Port)));
        Assert.Equal(token, LocalEndpointToken.Read(WebBridgeEndpoint.PortFile(fixture.Root)));
        var cookies = await fixture.Access.LoginAsync(Ct);
        using var client = BridgeAccess.CreateHttpClient(cookies);
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync(fixture.Access.Url(WebBridgeServer.SessionPath), Ct)).StatusCode);
    }

    [Fact(Explicit = true)]
    public async Task RestartBridge_RejectsCookieFromPreviousProcessLifetime()
    {
        await using var fixture = await BridgeFixture.StartAsync(TestContext.Current.CancellationToken);
        var cookies = await fixture.Access.LoginAsync(Ct);
        await fixture.Bridge.DisposeAsync();
        await using var replacement = WebBridgeServer.Start(new WebBridgeOptions(fixture.Root, 0));
        var access = new BridgeAccess(replacement.Port, LocalEndpointToken.Read(WebBridgeEndpoint.PortFile(fixture.Root))!);
        using var client = BridgeAccess.CreateHttpClient(cookies);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(access.Url(WebBridgeServer.SessionPath), Ct)).StatusCode);
        using var login = await access.PostLoginAsync(client, access.Token, access.Origin, Ct);
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync(access.Url(WebBridgeServer.SessionPath), Ct)).StatusCode);
    }

    [Fact(Explicit = true)]
    public async Task BrowserSockets_ShareFleetConnection_AndClosingOneKeepsTheOtherUsable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await BridgeFixture.StartAsync(cancellationToken);
        var cookies = await fixture.Access.LoginAsync(cancellationToken);
        using var first = await fixture.Access.ConnectWebSocketAsync(cookies, fixture.Access.Origin, cancellationToken);
        using var second = await fixture.Access.ConnectWebSocketAsync(cookies, fixture.Access.Origin, cancellationToken);
        await WebSocketRpc.RequestAsync(first, 1, HostProtocol.MethodInfo, null);
        await WebSocketRpc.RequestAsync(second, 1, HostProtocol.MethodInfo, null);
        Assert.Equal(0, fixture.RelayedConnections);
        await first.CloseAsync(WebSocketCloseStatus.NormalClosure, "", cancellationToken);
        Assert.Equal("fake", (await WebSocketRpc.RequestAsync(second, 2, HostProtocol.MethodInfo, null)).GetProperty("HostVersion").GetString());
        Assert.Equal(0, fixture.RelayedConnections);
    }

    [Fact(Explicit = true)]
    public async Task HostShutdown_PublishesAuthoritativeOfflineView_WithoutClosingBrowserTransport()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await BridgeFixture.StartAsync(cancellationToken);
        using var socket = await fixture.Access.ConnectWebSocketAsync(await fixture.Access.LoginAsync(cancellationToken), fixture.Access.Origin, cancellationToken);
        await WebSocketRpc.RequestAsync(socket, 1, HostProtocol.MethodInfo, null);
        await fixture.StopDaemonAsync();
        var view = await WebSocketRpc.ReceiveUntilAsync(socket, message => message.TryGetProperty("method", out var method)
            && method.GetString() == "bridge.view" && message.GetProperty("params").GetProperty("State").GetString() != "Connected");
        Assert.NotEqual("Connected", view.GetProperty("params").GetProperty("State").GetString());
        Assert.Equal(WebSocketState.Open, socket.State);
    }

    [Fact(Explicit = true)]
    public async Task WebSocket_WithoutHostDaemon_PublishesOfflineView()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await BridgeFixture.StartAsync(cancellationToken, withDaemon: false);
        using var socket = await fixture.Access.ConnectWebSocketAsync(await fixture.Access.LoginAsync(cancellationToken), fixture.Access.Origin, cancellationToken);
        var view = await WebSocketRpc.ReceiveUntilAsync(socket, message => message.TryGetProperty("method", out var method)
            && method.GetString() == "bridge.view" && message.GetProperty("params").GetProperty("State").GetString() == "Offline");
        Assert.False(string.IsNullOrEmpty(view.GetProperty("params").GetProperty("Detail").GetString()));
        Assert.Equal(WebSocketState.Open, socket.State);
    }

    [Fact(Explicit = true)]
    public async Task PrintToken_PrintsRunningBridgeTokenAndLoginUrl()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await BridgeFixture.StartAsync(cancellationToken);
        var output = new StringWriter();

        var exit = await WebBridgeCommand.RunAsync(fixture.Root, [WebBridgeCommand.PrintTokenOption], output, new StringWriter(), NoHost, cancellationToken);

        string[] expected = [fixture.Token, $"{WebBridgeEndpoint.LocalUrl(fixture.Bridge.Port)}#token={fixture.Token}"];
        Assert.Equal(0, exit);
        Assert.Equal(expected, output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact(Explicit = true)]
    public async Task Command_StartsBridgeAndPrintsAccessHints_ThenRemovesTokenOnStop()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = TempTree.CreateSocketDirectory("wc");
        try
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var output = new StringWriter();
            var hostEnsured = false;
            var command = WebBridgeCommand.RunAsync(root, [WebBridgeCommand.PortOption, "0"], output, new StringWriter(), () =>
            {
                hostEnsured = true;
                return Task.CompletedTask;
            }, stop.Token);
            await WebSocketRpc.WaitForAsync(() => File.Exists(WebBridgeEndpoint.TokenFile(root)) && output.ToString().Contains("ssh -N -L", StringComparison.Ordinal));

            Assert.True(hostEnsured);
            Assert.Contains(WebBridgeEndpoint.TokenFile(root), output.ToString(), StringComparison.Ordinal);
            await stop.CancelAsync();
            Assert.Equal(0, await command);
            Assert.False(File.Exists(WebBridgeEndpoint.TokenFile(root)));
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Fact(Explicit = true)]
    public async Task Command_RejectsUnknownArgumentsAndMissingBridge()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = TempTree.CreateSocketDirectory("wa");
        try
        {
            var error = new StringWriter();
            Assert.Equal(2, await WebBridgeCommand.RunAsync(root, ["--port", "notaport"], new StringWriter(), error, NoHost, cancellationToken));
            Assert.Equal(1, await WebBridgeCommand.RunAsync(root, [WebBridgeCommand.PrintTokenOption], new StringWriter(), error, NoHost, cancellationToken));
            Assert.Contains("no web bridge is running", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    /** Independent logins share the host's pending state; closing the originator does not cancel its approval. */
    [Fact(Explicit = true)]
    public async Task Approval_IsSharedAcrossLogins_AndSurvivesClosingTheOriginator()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await HarnessHostFixture.CreateAsync("wba", ct);
        await using var bridge = WebBridgeServer.Start(new WebBridgeOptions(host.Root, 0));
        var access = new BridgeAccess(bridge.Port, LocalEndpointToken.Read(WebBridgeEndpoint.PortFile(host.Root))!);
        await access.WaitForMachinesAsync(states => states.Values.All(state => state == "Connected"), TimeSpan.FromSeconds(5), Ct);
        using var first = await access.ConnectWebSocketAsync(await access.LoginAsync(Ct), access.Origin, Ct);
        using var second = await access.ConnectWebSocketAsync(await access.LoginAsync(Ct), access.Origin, Ct);
        var request = new RemoteSessionCreateRequest(host.Root);
        var created = await WebSocketRpc.RequestAsync(first, 1, HostProtocol.MethodSessionCreate,
            JsonSerializer.Serialize(request, HostProtocolJsonContext.Default.RemoteSessionCreateRequest));
        var sessionId = created.GetProperty("Id").GetString()!;
        var approval = RemoteHostMultiClientTests.RaiseApprovalAsync(host, RemoteHostMultiClientTests.Agent(host, sessionId), "bash");
        var firstView = await WebSocketRpc.ReceiveUntilAsync(first, HasApproval);
        var secondView = await WebSocketRpc.ReceiveUntilAsync(second, HasApproval);
        Assert.Equal(firstView.GetProperty("params").GetProperty("Pending").GetRawText(),
            secondView.GetProperty("params").GetProperty("Pending").GetRawText());
        var pending = await WebSocketRpc.RequestAsync(first, 2, HostProtocol.MethodPendingList, null);
        var approvalId = Assert.Single(pending.GetProperty("Approvals").EnumerateArray()).GetProperty("RequestId").GetString();
        var otherPending = await WebSocketRpc.RequestAsync(second, 1, HostProtocol.MethodPendingList, null);
        Assert.Equal(approvalId, Assert.Single(otherPending.GetProperty("Approvals").EnumerateArray()).GetProperty("RequestId").GetString());
        await first.CloseAsync(WebSocketCloseStatus.NormalClosure, "", Ct);

        var answer = $$"""{"RequestId":"{{approvalId}}","Allow":true}""";
        Assert.True((await WebSocketRpc.RequestAsync(second, 2, HostProtocol.MethodApprovalRespond, answer)).GetBoolean());
        Assert.Equal(Dsh.Core.ApprovalOutcome.AllowedOnce, await approval.WaitAsync(ct));
        Assert.False((await WebSocketRpc.RequestAsync(second, 3, HostProtocol.MethodApprovalRespond, answer)).GetBoolean());
        Assert.Empty((await WebSocketRpc.RequestAsync(second, 4, HostProtocol.MethodPendingList, null)).GetProperty("Approvals").EnumerateArray());
        Assert.Contains((await WebSocketRpc.RequestAsync(second, 5, HostProtocol.MethodSessionsList, null)).EnumerateArray(),
            session => session.GetProperty("SessionId").GetString() == sessionId);

        static bool HasApproval(JsonElement message) => message.TryGetProperty("method", out var method)
            && method.GetString() == BridgeProtocol.NotificationView
            && message.GetProperty("params").GetProperty("Pending").GetProperty("Approvals").GetArrayLength() > 0;
    }

    /** The bridge preserves the real terminal across a lost host link and resizes before replaying its snapshot. */
    [Fact(Explicit = true)]
    public async Task Terminal_SegmentedInput_AndOfflineResize_SurviveHostReconnect()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await HarnessHostFixture.CreateAsync("wbr", ct);
        await using var bridge = WebBridgeServer.Start(new WebBridgeOptions(host.Root, 0));
        var access = new BridgeAccess(bridge.Port, LocalEndpointToken.Read(WebBridgeEndpoint.PortFile(host.Root))!);
        await access.WaitForMachinesAsync(states => states.Values.All(state => state == "Connected"), TimeSpan.FromSeconds(5), Ct);
        using var socket = await access.ConnectWebSocketAsync(await access.LoginAsync(Ct), access.Origin, Ct);
        var request = new RemotePtyStartRequest(TestShell.FileName, TestShell.Arguments, host.Environment.App.Home.Root, Columns: 80, Rows: 24);
        var started = await WebSocketRpc.RequestAsync(socket, 1, PtyProtocol.MethodPtyStart,
            JsonSerializer.Serialize(request, PtyProtocolJsonContext.Default.RemotePtyStartRequest));
        var ptyId = started.GetProperty("PtyId").GetString()!;
        await WebSocketRpc.SendAsync(socket, 2, PtyProtocol.MethodPtyAttach,
            $$"""{"PtyId":"{{ptyId}}","ViewId":"phone","Columns":60,"Rows":20}""");
        var initial = await SnapshotAsync();
        Assert.Equal(19, Screen(initial, 60, 20).ScrollBottom);
        Assert.Equal((60, 20), await SizeAsync());

        // Like a phone keyboard, text and Enter are separate acknowledged writes.
        // This asserts the transport/PTY boundary, not terminalPanel's JavaScript queue.
        await WebSocketRpc.RequestAsync(socket, 3, PtyProtocol.MethodPtyWrite,
            $$"""{"PtyId":"{{ptyId}}","Base64":"{{Convert.ToBase64String(Encoding.UTF8.GetBytes(TestShell.EchoAnswer("ordered")))}}"}""");
        await WebSocketRpc.SendAsync(socket, 4, PtyProtocol.MethodPtyWrite,
            $$"""{"PtyId":"{{ptyId}}","Base64":"{{Convert.ToBase64String("\r"u8)}}"}""");
        await WebBridgeHubTests.ReadTerminalUntilAsync(socket, ptyId, "ordered-42", "phone");

        await host.StopHostAsync();
        await WebSocketRpc.ReceiveUntilAsync(socket, message => message.TryGetProperty("method", out var method)
            && method.GetString() == BridgeProtocol.NotificationView && message.GetProperty("params").GetProperty("State").GetString() != "Connected");
        await WebSocketRpc.RequestAsync(socket, 5, PtyProtocol.MethodPtyResize,
            $$"""{"PtyId":"{{ptyId}}","ViewId":"phone","Columns":110,"Rows":35}""");
        await host.StartHostAsync(Ct);
        var resumed = Screen(await SnapshotAsync(), 110, 35);
        Assert.Equal((110, 35), await SizeAsync());
        Assert.Equal(34, resumed.ScrollBottom);
        Assert.Contains("ordered-42", string.Join('\n', Enumerable.Range(0, resumed.Height).Select(row =>
            new string(resumed.Row(row).ToArray().Select(cell => cell.Character).ToArray()))), StringComparison.Ordinal);
        Assert.Equal(WebSocketState.Open, socket.State);
        await WebSocketRpc.RequestAsync(socket, 6, PtyProtocol.MethodPtyStop, $$"""{"PtyId":"{{ptyId}}"}""");
        var stopped = Assert.Single((await WebSocketRpc.RequestAsync(socket, 7, HostProtocol.MethodSessionsList, null)).EnumerateArray(),
            session => session.GetProperty("SessionId").GetString() == ptyId);
        Assert.Equal(HostSessionStatuses.Exited, stopped.GetProperty("Status").GetString());

        async Task<JsonElement> SnapshotAsync()
            => (await WebSocketRpc.ReceiveUntilAsync(socket, message => message.TryGetProperty("method", out var method)
                && method.GetString() == PtyProtocol.NotificationPtyOutput
                && message.GetProperty("params").GetProperty("ViewId").GetString() == "phone"
                && message.GetProperty("params").GetProperty("Snapshot").GetBoolean())).GetProperty("params");

        async Task<(int Columns, int Rows)> SizeAsync()
        {
            var pty = Assert.Single(await PtyDaemonClient.ListAsync(host.PtyEndpoint.SocketPath, host.PtyEndpoint.PortFile, Ct), session => session.Id == ptyId);
            return (pty.Columns, pty.Rows);
        }

        static VtScreen Screen(JsonElement snapshot, int columns, int rows)
        {
            var screen = new VtScreen(columns, rows);
            screen.Feed(Convert.FromBase64String(snapshot.GetProperty("Base64").GetString()!));
            return screen;
        }
    }

    private static Task NoHost() => Task.CompletedTask;

    private static async Task<HttpStatusCode> GetWithHostAsync(HttpClient client, BridgeFixture fixture, string host)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, fixture.Access.Url("/"));
        request.Headers.Host = host;
        using var response = await client.SendAsync(request, Ct);
        return response.StatusCode;
    }

    /** A host daemon (RemoteHostServer on its owner-only loopback endpoint, backed by the capability tests' fake backend) plus a bridge pointing at it. */
    private sealed class BridgeFixture : IAsyncDisposable
    {
        private readonly CancellationTokenSource _daemonStop;
        private readonly Task _daemon;

        private BridgeFixture(string root, RemoteHostCapabilityTests.FakeBackend backend, CancellationTokenSource daemonStop, Task daemon, WebBridgeServer bridge)
        {
            Root = root;
            Backend = backend;
            _daemonStop = daemonStop;
            _daemon = daemon;
            Bridge = bridge;
            // The test reads the token where a user would: the owner-only file next to the bridge's port file.
            Token = LocalEndpointToken.Read(WebBridgeEndpoint.PortFile(root)) ?? throw new InvalidOperationException("bridge issued no token");
        }

        public string Root { get; }

        public RemoteHostCapabilityTests.FakeBackend Backend { get; }

        public WebBridgeServer Bridge { get; }

        public string Token { get; }

        /** Extra host connections beyond the single shared Fleet connection: browser transports must not open any. */
        public int RelayedConnections => Backend.OpenConnections - 1;

        public BridgeAccess Access => new(Bridge.Port, Token);

        public static async Task<BridgeFixture> StartAsync(CancellationToken cancellationToken, bool withDaemon = true)
        {
            // Socket paths are capped at 108 characters, so the daemon root is a shallow directory inside the repository.
            var root = TempTree.CreateSocketDirectory("wb");
            var backend = await RemoteHostCapabilityTests.FakeBackend.CreateAsync(cancellationToken);
            var daemonStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var server = new RemoteHostServer(new RemoteHostServerOptions(), backend);
            var daemon = withDaemon
                ? Task.Run(() => RemoteHostListener.ServeLoopbackAsync(server, root, daemonStop.Token), cancellationToken)
                : Task.CompletedTask;
            if (withDaemon)
            {
                // A completed handshake proves the daemon serves; its own host connection must be gone before tests count connections.
                await using (var probe = await RemoteHostConnector.ConnectAsync(root, cancellationToken))
                await using (await RemoteHostClient.ConnectAsync(probe, null, cancellationToken))
                    Assert.Equal(1, backend.OpenConnections);
                await WebSocketRpc.WaitForAsync(() => backend.OpenConnections == 0);
            }

            var bridge = WebBridgeServer.Start(new WebBridgeOptions(root, 0));
            if (withDaemon)
            {
                await WebSocketRpc.WaitForAsync(() => backend.OpenConnections == 1);
                var access = new BridgeAccess(bridge.Port, LocalEndpointToken.Read(WebBridgeEndpoint.PortFile(root))!);
                await access.WaitForMachinesAsync(statuses => statuses.Values.All(state => state == "Connected"), TimeSpan.FromSeconds(5), cancellationToken);
            }
            return new BridgeFixture(root, backend, daemonStop, daemon, bridge);
        }

        /** Stops the host daemon, which closes every host connection it serves. */
        public async Task StopDaemonAsync()
        {
            await _daemonStop.CancelAsync();
            try
            {
                await _daemon;
            }
            catch (OperationCanceledException)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Bridge.DisposeAsync();
            await StopDaemonAsync();
            _daemonStop.Dispose();
            await Backend.DisposeAsync();
            TempTree.Delete(Root);
        }
    }
}



