using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Dsh.Boot;
using Dsh.Runtime;
using Dsh.Core;
using Dsh.Host;
using Dsh.GitInspect;
using Dsh.Interaction;
using Dsh.Transport;
using Dsh.Llm;
using Dsh.RemoteHost;

namespace Dsh.Tests;

[Collection(GuiSerialCollection.CollectionName)]
public sealed class RemoteHostCapabilityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Timeout = 5000)]
    public async Task SessionsToolsFilesAndMessageFlowOverTheWire()
    {
        await using var backend = await FakeBackend.CreateAsync(Ct);
        await using var fixture = await HostFixture.CreateAsync(backend, TestContext.Current.CancellationToken);
        var client = fixture.Client;

        Assert.Empty(await client.ListSessionsAsync(Ct));
        var created = await client.CreateSessionAsync("/work", cancellationToken: Ct);
        Assert.Equal("/work", created.Cwd);
        Assert.Single(await client.ListSessionsAsync(Ct));
        Assert.Equal(["read", "write"], await client.ListToolsAsync(created.Id, Ct));

        var work = Path.Combine(backend.Root, "work");
        Directory.CreateDirectory(Path.Combine(work, "sub"));
        await client.Files.WriteFileAsync(Path.Combine(work, "a.txt"), "hello"u8.ToArray(), Ct);
        Assert.Equal("hello"u8.ToArray(), await client.Files.ReadFileAsync(Path.Combine(work, "a.txt"), Ct));

        var listing = await client.Files.ListDirectoryAsync(work, Ct);
        Assert.Equal(work, listing.Path);
        Assert.Equal(backend.Root, listing.Parent);
        Assert.Contains(listing.Entries, entry => entry is { Name: "sub", IsDirectory: true });

        var received = new TaskCompletionSource<RemoteSessionEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var pump = Task.Run(async () =>
        {
            await foreach (var item in client.SubscribeAsync(created.Id, 0, subscription.Token))
                received.TrySetResult(item);
        }, Ct);
        // host 应答 subscribe 之前后端已挂好订阅: 等后端看到订阅即可发消息, 不靠固定延时。
        await backend.Subscribed.Task.WaitAsync(Ct);
        await client.SendMessageAsync(created.Id, "hi", [], Ct);
        var item = await received.Task.WaitAsync(Ct);
        var message = item.Event!;
        Assert.Equal(0L, message.Seq);
        Assert.Equal("hi", Assert.IsType<TextBlock>(Assert.Single(Assert.IsType<UserMessagePayload>(message.Data).Message.Content)).Text);
        await subscription.CancelAsync();
        try
        {
            await pump;
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact(Timeout = 5000)]
    public async Task ApprovalRequestRoundTripsBack()
    {
        await using var backend = await FakeBackend.CreateAsync(Ct);
        await using var fixture = await HostFixture.CreateAsync(backend, TestContext.Current.CancellationToken);
        var received = new TaskCompletionSource<RemoteApprovalRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var pump = Task.Run(async () =>
        {
            await foreach (var change in fixture.Client.PendingChangesAsync(subscription.Token))
            {
                foreach (var request in change.Pending.Approvals)
                    received.TrySetResult(request);
            }
        }, Ct);

        var decision = backend.Pending.AskApprovalAsync(new RemoteApprovalRequest("req-1", "session-1", "bash", null, "allow?"), cancellationToken: Ct);
        var approval = await received.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.True(await fixture.Client.RespondApprovalAsync(approval.RequestId, true, "ok", Ct));
        Assert.True((await decision.WaitAsync(TimeSpan.FromSeconds(5), Ct))?.Allow);
        await subscription.CancelAsync();
        try
        {
            await pump;
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact(Timeout = 5000)]
    public async Task PtyEchoesInputOverTheWire()
    {
        await using var backend = await FakeBackend.CreateAsync(Ct);
        await using var fixture = await HostFixture.CreateAsync(backend, TestContext.Current.CancellationToken);
        var ptyId = await fixture.Client.Pty.StartPtyAsync(new RemotePtyStartRequest("/bin/sh", []), Ct);

        var pipe = new System.IO.Pipelines.Pipe();
        var input = pipe.Reader.AsStream();
        var output = new MemoryStream();
        using var attachCts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var attach = fixture.Client.Pty.AttachPtyAsync(ptyId, input, output, attachCts.Token);

        await pipe.Writer.WriteAsync("ping"u8.ToArray(), Ct);
        await pipe.Writer.FlushAsync(Ct);
        await WaitForAsync(() => output.Length >= 4, Ct);
        Assert.Equal("ping", Encoding.UTF8.GetString(output.ToArray()));

        await attachCts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await attach);
    }

    [Fact(Timeout = 5000)]
    public async Task QuestionRequestRoundTripsBack()
    {
        await using var backend = await FakeBackend.CreateAsync(Ct);
        await using var fixture = await HostFixture.CreateAsync(backend, TestContext.Current.CancellationToken);
        var received = new TaskCompletionSource<RemoteQuestionRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var pump = Task.Run(async () =>
        {
            await foreach (var change in fixture.Client.PendingChangesAsync(subscription.Token))
            {
                foreach (var request in change.Pending.Questions)
                    received.TrySetResult(request);
            }
        }, Ct);

        var answer = backend.Pending.AskQuestionAsync(new RemoteQuestionRequest("q-1", "session-1",
            [new RemoteQuestionItem("q1", "pick?", null, null, [new RemoteQuestionOption("A", null)], false, null)]), cancellationToken: TestContext.Current.CancellationToken);
        var question = await received.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.True(await fixture.Client.RespondQuestionAsync(question.RequestId, [new RemoteQuestionAnswerItem("q1", ["A"], null)], Ct));
        Assert.Equal(["A"], (await answer.WaitAsync(TimeSpan.FromSeconds(5), Ct))!.Answers[0].Selected);
        await subscription.CancelAsync();
        try
        {
            await pump;
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact(Timeout = 30000)]
    public async Task GenericApprovalCancellationKeepsOldHostPendingUntilOrdinaryReject()
    {
        await using var backend = await FakeBackend.CreateAsync(TestContext.Current.CancellationToken);
        await using var origin = await HostFixture.CreateAsync(backend, TestContext.Current.CancellationToken);
        await using var fixture = await MissingDecisionCapabilityRelay.CreateAsync(origin.Client, TestContext.Current.CancellationToken);
        Assert.False(fixture.Client.Supports(DecisionProtocol.Capability));
        var waiting = backend.Pending.AskApprovalAsync(new RemoteApprovalRequest("legacy-approval", "session-1", "bash", null, "Approve?"), cancellationToken: Ct);
        var parameters = JsonSerializer.SerializeToElement(
            new RemoteApprovalResponse("legacy-approval", false, null, Cancelled: true),
            HostProtocolJsonContext.Default.RemoteApprovalResponse);
        await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Client.RequestAsync(HostProtocol.MethodApprovalRespond, parameters, Ct));
        Assert.False(waiting.IsCompleted);
        Assert.Contains((await fixture.Client.ListPendingAsync(Ct)).Approvals, request => request.RequestId == "legacy-approval");
        Assert.True(await fixture.Client.RespondApprovalAsync("legacy-approval", false, cancellationToken: Ct));
        var result = Assert.IsType<RemoteApprovalResponse>(await waiting.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        Assert.False(result.Allow);
        Assert.False(result.Cancelled);
    }

    [Fact(Timeout = 30000)]
    public async Task GenericQuestionDismissalKeepsOldHostPendingUntilOrdinaryAnswer()
    {
        await using var backend = await FakeBackend.CreateAsync(TestContext.Current.CancellationToken);
        await using var origin = await HostFixture.CreateAsync(backend, TestContext.Current.CancellationToken);
        await using var fixture = await MissingDecisionCapabilityRelay.CreateAsync(origin.Client, TestContext.Current.CancellationToken);
        Assert.False(fixture.Client.Supports(DecisionProtocol.Capability));
        var waiting = backend.Pending.AskQuestionAsync(new RemoteQuestionRequest("legacy-question", "session-1",
            [new RemoteQuestionItem("choice", "Choose.", null, null, [new RemoteQuestionOption("A", null), new RemoteQuestionOption("B", null)], false, null)]), cancellationToken: TestContext.Current.CancellationToken);
        var parameters = JsonSerializer.SerializeToElement(
            new RemoteQuestionResponse("legacy-question", [], "dismissed", "user_cancelled"),
            HostProtocolJsonContext.Default.RemoteQuestionResponse);
        await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Client.RequestAsync(HostProtocol.MethodQuestionRespond, parameters, Ct));
        Assert.False(waiting.IsCompleted);
        Assert.Contains((await fixture.Client.ListPendingAsync(Ct)).Questions, request => request.RequestId == "legacy-question");
        Assert.True(await fixture.Client.RespondQuestionAsync("legacy-question", [new RemoteQuestionAnswerItem("choice", ["B"], null)], Ct));
        var result = Assert.IsType<RemoteQuestionResponse>(await waiting.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        Assert.Equal("answered", result.Status);
        Assert.Equal(["B"], Assert.Single(result.Answers).Selected);
    }

    [Theory(Timeout = 5000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealHarnessCoreWorksWithOrWithoutOptionalDomains(bool includeOptional)
    {
        await using var environment = await GuiTestEnvironment.CreateAsync();
        await using var backend = await HarnessRemoteHostBackend.CreateAsync(environment.App, HostPtyDaemonForTests.Unused, TestContext.Current.CancellationToken);
        IRemoteHostBackend composed = includeOptional ? backend : new CoreOnlyBackend(backend);
        await using var fixture = await HostFixture.CreateAsync(composed, Ct);
        var client = fixture.Client;
        var info = await client.InfoAsync(Ct);
        Assert.Equal(client.Hello.Capabilities, info.Capabilities);
        Assert.Contains(HostProtocol.PendingBroadcastCapability, info.Capabilities!);
        Assert.Contains(HostProtocol.SessionRegistryCapability, info.Capabilities!);

        var created = await client.CreateSessionAsync(info.HomeRoot, cancellationToken: Ct);
        Assert.Equal(created.Id, (await client.ResumeSessionAsync(created.Id, cancellationToken: Ct)).Id);
        Assert.Contains(await client.ListSessionsAsync(Ct), session => session.Id == created.Id);
        Assert.NotNull(await client.GetSessionSnapshotAsync(created.Id, Ct));
        Assert.Empty((await client.ListPendingAsync(Ct)).Approvals);

        var file = Path.Combine(info.HomeRoot, "domain-contract.txt");
        if (includeOptional)
        {
            Assert.Contains(FilesProtocol.Capability, info.Capabilities!);
            Assert.Contains(NetworkProtocol.Capability, info.Capabilities!);
            await client.Files.WriteFileAsync(file, "domain"u8.ToArray(), Ct);
            Assert.Equal("domain"u8.ToArray(), await client.Files.ReadFileAsync(file, Ct));
            await client.SessionEditing.RenameSessionAsync(created.Id, "domain title", Ct);
            Assert.Contains(await client.ListSessionsAsync(Ct), session => session.Id == created.Id && session.Title == "domain title");
        }
        else
        {
            var denied = await Assert.ThrowsAsync<JsonRpcException>(() => client.Files.WriteFileAsync(file, "denied"u8.ToArray(), Ct));
            Assert.Equal(JsonRpcPeer.MethodNotFound, denied.Code);
            Assert.False(File.Exists(file));
            var editDenied = await Assert.ThrowsAsync<JsonRpcException>(() => client.SessionEditing.RenameSessionAsync(created.Id, "denied", Ct));
            Assert.Equal(JsonRpcPeer.MethodNotFound, editDenied.Code);
            Func<Task>[] otherDomains =
            [
                () => client.Agent.AgentCatalogAsync(Ct),
                () => client.Pty.StartPtyAsync(new RemotePtyStartRequest("unused", []), Ct),
                () => client.Git.GitStatusAsync(info.HomeRoot, Ct),
                () => client.Notifications.ListNotificationsAsync(cancellationToken: Ct),
                () => client.Plugins.ListPluginsAsync(Ct),
                () => client.Network.GetOutboundAsync(Ct),
                () => client.Network.SetOutboundAsync(new RemoteOutboundProxySetRequest("unsupported", new RemoteOutboundProxySettings()), Ct),
                () => client.Network.ProbeOutboundAsync(new RemoteOutboundProxyProbeRequest(), Ct),
            ];
            foreach (var operation in otherDomains)
            {
                var unsupported = await Assert.ThrowsAsync<JsonRpcException>(operation);
                Assert.Equal(JsonRpcPeer.MethodNotFound, unsupported.Code);
            }
            Assert.Equal(created.Id, (await client.ResumeSessionAsync(created.Id, cancellationToken: Ct)).Id);
        }
    }

    [Fact(Timeout = 5000)]
    public async Task NetworkReload_ReadsOnlyTheHostsFile_RetiresItsOldPool_AndRejectsSnapshots()
    {
        await using var environment = await GuiTestEnvironment.CreateAsync();
        await using var backend = await HarnessRemoteHostBackend.CreateAsync(environment.App, HostPtyDaemonForTests.Unused, TestContext.Current.CancellationToken);
        await using var fixture = await HostFixture.CreateAsync(backend, Ct);
        await using var first = new TestForwardProxy(keepAlive: true);
        await using var second = new TestForwardProxy(keepAlive: true);
        var home = environment.App.Home;
        var proxy = OutboundProxy.FromContext(environment.App.Ctx);
        HarnessSettings.SaveOutboundProxy(home, new OutboundProxySettings { Url = $"http://127.0.0.1:{first.Port}" }, proxy);
        using var consumer = new HttpClient(proxy.CreateHandler());
        Assert.Equal(TestForwardProxy.ProxiedBody, await consumer.GetStringAsync("http://example.com/before-host-reload", Ct));
        using var unrelated = new OutboundProxy();
        HarnessSettings.SaveOutboundProxy(home, new OutboundProxySettings { Url = $"http://127.0.0.1:{second.Port}" }, unrelated);
        var saved = await File.ReadAllTextAsync(home.SettingsFile, Ct);
        Assert.True(fixture.Client.Supports(NetworkProtocol.ReloadCapability));

        await fixture.Client.Network.ReloadAsync(Ct);

        await FleetConnectionTests.WaitForAsync(() => first.ActiveConnections == 0);
        Assert.Equal(TestForwardProxy.ProxiedBody, await consumer.GetStringAsync("http://example.com/after-host-reload", Ct));
        Assert.Equal(saved, await File.ReadAllTextAsync(home.SettingsFile, Ct));
        Assert.Equal(["GET http://example.com/before-host-reload HTTP/1.1"], first.Requests);
        Assert.Equal(["GET http://example.com/after-host-reload HTTP/1.1"], second.Requests);
        using var snapshot = JsonDocument.Parse("""{"network":{"outbound_proxy":{"url":"http://unexpected.invalid:3128"}}}""");
        var rejected = await Assert.ThrowsAsync<JsonRpcException>(() => fixture.Client.RequestAsync(NetworkProtocol.MethodReload, snapshot.RootElement, Ct));
        Assert.Equal(-32602, rejected.Code);
        Assert.Equal(saved, await File.ReadAllTextAsync(home.SettingsFile, Ct));
        Assert.Equal(TestForwardProxy.ProxiedBody, await consumer.GetStringAsync("http://example.com/snapshot-did-not-apply", Ct));
        Assert.Single(first.Requests);
    }

    [Fact(Timeout = 5000)]
    public async Task OutboundOnlyHost_RejectsReload_WhileRealGetSetAndProbeRemainUsable()
    {
        await using var environment = await GuiTestEnvironment.CreateAsync();
        await using var backend = await HarnessRemoteHostBackend.CreateAsync(environment.App, HostPtyDaemonForTests.Unused, TestContext.Current.CancellationToken);
        await using var forwarding = new TestForwardProxy();
        var outbound = new OutboundOnlyNetwork(backend.Network!);
        await using var fixture = await HostFixture.CreateAsync(new CoreOnlyBackend(backend, outbound), Ct);
        var client = fixture.Client;
        Assert.True(client.Supports(NetworkProtocol.Capability));
        Assert.False(client.Supports(NetworkProtocol.ReloadCapability));
        var state = await client.Network.GetOutboundAsync(Ct);
        await client.Network.SetOutboundAsync(new(state.Revision, new($"http://127.0.0.1:{forwarding.Port}")), Ct);
        var saved = await File.ReadAllTextAsync(environment.App.Home.SettingsFile, Ct);
        Assert.True((await client.Network.ProbeOutboundAsync(new(ProbeUrl: "http://example.com/old-host-probe"), Ct)).Reachable);
        await Assert.ThrowsAsync<NotSupportedException>(() => client.Network.ReloadAsync(Ct));
        var unsupported = await Assert.ThrowsAsync<JsonRpcException>(() => client.RequestAsync(NetworkProtocol.MethodReload, null, Ct));
        Assert.Equal(JsonRpcPeer.MethodNotFound, unsupported.Code);
        Assert.Equal(saved, await File.ReadAllTextAsync(environment.App.Home.SettingsFile, Ct));
        Assert.Equal(["GET http://example.com/old-host-probe HTTP/1.1"], forwarding.Requests);
    }

    private sealed class OutboundOnlyNetwork(IRemoteNetworkBackend backend) : IRemoteNetworkBackend
    {
        public Task<RemoteOutboundProxyState> GetOutboundAsync(CancellationToken ct) => backend.GetOutboundAsync(ct);
        public Task<RemoteOutboundProxyState> SetOutboundAsync(RemoteOutboundProxySetRequest request, CancellationToken ct) => backend.SetOutboundAsync(request, ct);
        public Task<RemoteOutboundProxyProbeResult> ProbeOutboundAsync(RemoteOutboundProxyProbeRequest request, CancellationToken ct) => backend.ProbeOutboundAsync(request, ct);
    }

    private sealed class CoreOnlyBackend(IRemoteHostBackend backend, IRemoteNetworkBackend? network = null) : IRemoteHostBackend
    {
        public HostPendingDecisions Pending => backend.Pending;
        public HostSessionRegistry Sessions => backend.Sessions;
        public IRemoteSessionBackend Session => backend.Session;
        public Task<HostInfo> InfoAsync(CancellationToken cancellationToken) => backend.InfoAsync(cancellationToken);
        public IRemoteAgentBackend? Agent => null;
        public IRemotePtyBackend? Pty => null;
        public HostFileSystem? Files => null;
        public HostGitService? Git => null;
        public HostNotificationCenter? Notifications => null;
        public IRemoteSessionEditingBackend? SessionEditing => null;
        public IRemotePluginsBackend? Plugins => null;
        public IRemoteNetworkBackend? Network => network;
    }

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 500 && !condition(); attempt++)
            await Task.Delay(10, cancellationToken);
        Assert.True(condition(), "condition not met in time");
    }

    /** 记录/回放式的远端后端替身; 不依赖 harness。待决策队列、会话登记表、文件系统(限于 Root)与通知历史用真实实现。 */
    internal sealed class FakeBackend : IRemoteHostBackend, IRemoteSessionBackend, IRemotePtyBackend, IAsyncDisposable
    {
        private readonly string _directory;
        private readonly Dictionary<string, RemoteSessionSummary> _sessions = [];
        private readonly Dictionary<string, Channel<RemoteSessionEvent>> _events = [];
        private readonly Dictionary<string, long> _sequences = [];
        private readonly Dictionary<string, Channel<RemotePtyOutput>> _pty = [];

        private FakeBackend(string directory, HostPendingDecisions pending, HostSessionRegistry sessions, HostNotificationCenter notifications)
        {
            _directory = directory;
            Pending = pending;
            Sessions = sessions;
            Notifications = notifications;
            Files = new HostFileSystem(new HostPathPolicy([directory], () => []));
        Git = new HostGitService(Files.Policy, new GitService());
        }

        public static async Task<FakeBackend> CreateAsync(CancellationToken cancellationToken)
        {
            var directory = TempTree.CreateDirectory("rh-cap");
            var pending = new HostPendingDecisions();
            var sessions = await HostSessionRegistry.OpenAsync(Path.Combine(directory, "sessions.json"), pending, cancellationToken);
            var notifications = await HostNotificationCenter.OpenAsync(Path.Combine(directory, HostNotificationCenter.FileName), sessions, pending, cancellationToken);
            return new FakeBackend(directory, pending, sessions, notifications);
        }

        /** The only directory the fake host's file system may touch. */
        public string Root => _directory;

        public HostPendingDecisions Pending { get; }

        public HostSessionRegistry Sessions { get; }

        public HostFileSystem Files { get; }

        public IRemoteSessionBackend Session => this;
        public IRemotePtyBackend Pty => this;
        public IRemoteAgentBackend? Agent => null;
        public HostGitService Git { get; }
        public IRemoteSessionEditingBackend? SessionEditing => null;
        public IRemotePluginsBackend? Plugins => null;
        public IRemoteNetworkBackend? Network => null;

        public HostNotificationCenter Notifications { get; }

        /** 后端完成订阅(挂好事件通道)时置位。 */
        public TaskCompletionSource Subscribed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<HostInfo> InfoAsync(CancellationToken cancellationToken)
            => Task.FromResult(new HostInfo(HostProtocol.Version, "fake", "test", "/home"));

        public Task<IReadOnlyList<RemoteSessionSummary>> ListSessionsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<RemoteSessionSummary>>([.. _sessions.Values]);

        public Task<RemoteSessionSummary> CreateSessionAsync(string cwd, CancellationToken cancellationToken)
        {
            var session = new RemoteSessionSummary($"session-{_sessions.Count + 1}", cwd, "Idle", 0, null, null, 0);
            _sessions[session.Id] = session;
            // Events are kept from creation on, like a real session log that a subscription from seq 0 replays.
            _events[session.Id] = Channel.CreateUnbounded<RemoteSessionEvent>();
            return Task.FromResult(session);
        }

        public Task<RemoteSessionSummary> ResumeSessionAsync(string sessionId, CancellationToken cancellationToken)
        {
            return Task.FromResult(_sessions.TryGetValue(sessionId, out var session) ? session : throw new KeyNotFoundException(sessionId));
        }

        public Task<RemoteSessionSnapshot> GetSessionSnapshotAsync(string sessionId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SendMessageAsync(string sessionId, string text, IReadOnlyList<RemoteImageBlock> images, CancellationToken cancellationToken)
        {
            var seq = _sequences.GetValueOrDefault(sessionId);
            _sequences[sessionId] = seq + 1;
            var item = new SessionEvent
            {
                Type = SessionEventTypes.UserMessage,
                Seq = seq,
                Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Data = new UserMessagePayload(MessageFactory.CreateUserText(text)),
                SurfaceOp = new SurfaceOp.Append(),
            };
            _events[sessionId].Writer.TryWrite(new RemoteSessionEvent(sessionId, item));
            return Task.CompletedTask;
        }

        public Task InterruptAsync(string sessionId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<string>> ListToolsAsync(string sessionId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<string>>(["read", "write"]);

        public Task<string> StartPtyAsync(RemotePtyStartRequest request, CancellationToken cancellationToken)
        {
            var id = $"pty-{Guid.NewGuid():N}";
            _pty[id] = Channel.CreateUnbounded<RemotePtyOutput>();
            return Task.FromResult(id);
        }

        public Task WritePtyAsync(string ptyId, byte[] data, CancellationToken cancellationToken)
        {
            if (_pty.TryGetValue(ptyId, out var channel))
                channel.Writer.TryWrite(new RemotePtyOutput(ptyId, Convert.ToBase64String(data), false));
            return Task.CompletedTask;
        }

        public Task ResizePtyAsync(string ptyId, int columns, int rows, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopPtyAsync(string ptyId, CancellationToken cancellationToken)
        {
            if (_pty.Remove(ptyId, out var channel))
                channel.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public Task<RemoteHostStream<RemoteSessionEvent>> SubscribeAsync(string sessionId, long fromSeq, CancellationToken cancellationToken)
        {
            var channel = _events.TryGetValue(sessionId, out var existing)
                ? existing
                : _events[sessionId] = Channel.CreateUnbounded<RemoteSessionEvent>();
            Subscribed.TrySetResult();
            return Task.FromResult(new RemoteHostStream<RemoteSessionEvent>(channel.Reader.ReadAllAsync(cancellationToken)));
        }

        public Task<RemoteHostStream<RemotePtyOutput>> AttachPtyAsync(string ptyId, CancellationToken cancellationToken)
            => _pty.TryGetValue(ptyId, out var channel)
                ? Task.FromResult(new RemoteHostStream<RemotePtyOutput>(channel.Reader.ReadAllAsync(cancellationToken)))
                : throw new InvalidOperationException($"PTY session not found: {ptyId}");

        /** Every live host connection holds one pending-decision subscription, so this counts open host connections. */
        public int OpenConnections => Pending.SubscriberCount;

        public async ValueTask DisposeAsync()
        {
            await Notifications.DisposeAsync();
            Pending.Dispose();
            await Sessions.DisposeAsync();
            TempTree.Delete(_directory);
        }
    }

    private sealed class MissingDecisionCapabilityRelay(TcpClient socket, TcpClient serverSocket, JsonRpcPeer peer, RemoteHostClient client) : IAsyncDisposable
    {
        public RemoteHostClient Client { get; } = client;

        public static async Task<MissingDecisionCapabilityRelay> CreateAsync(RemoteHostClient upstream, CancellationToken cancellationToken)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var accept = listener.AcceptTcpClientAsync(cancellationToken);
            var socket = new TcpClient();
            await socket.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
            var serverSocket = await accept;
            listener.Stop();
            var hello = upstream.Hello with { Capabilities = upstream.Hello.Capabilities?.Where(capability => capability != DecisionProtocol.Capability).ToArray() };
            var peer = new JsonRpcPeer(serverSocket.GetStream());
            // Only handshake metadata is downgraded; the real Host server handles every decision.
            peer.RequestHandler = async (request, token) => request.Method == HostProtocol.MethodHello
                ? JsonSerializer.SerializeToElement(hello, HostProtocolJsonContext.Default.HostHelloResponse)
                : await upstream.RequestAsync(request.Method!, request.Params, token);
            peer.Start();
            var client = await RemoteHostClient.ConnectAsync(socket.GetStream(), cancellationToken: cancellationToken);
            return new MissingDecisionCapabilityRelay(socket, serverSocket, peer, client);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await peer.DisposeAsync();
            socket.Dispose();
            serverSocket.Dispose();
        }
    }

    private sealed class HostFixture : IAsyncDisposable
    {
        private readonly TcpClient _serverClient;
        private readonly TcpClient _client;
        private readonly Task _serve;

        private HostFixture(TcpClient serverClient, TcpClient client, Task serve, RemoteHostClient hostClient)
        {
            _serverClient = serverClient;
            _client = client;
            _serve = serve;
            Client = hostClient;
        }

        public RemoteHostClient Client { get; }

        public static async Task<HostFixture> CreateAsync(IRemoteHostBackend backend, CancellationToken cancellationToken)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var acceptTask = listener.AcceptTcpClientAsync(cancellationToken);
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
            var serverClient = await acceptTask;
            listener.Stop();

            var server = new RemoteHostServer(new RemoteHostServerOptions(null), backend);
            var serve = Task.Run(() => server.ServeAsync(serverClient.GetStream(), cancellationToken), cancellationToken);
            var hostClient = await RemoteHostClient.ConnectAsync(client.GetStream(), null, cancellationToken);
            return new HostFixture(serverClient, client, serve, hostClient);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            _serverClient.Dispose();
            _client.Dispose();
            try
            {
                await _serve.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception error) when (error is OperationCanceledException or TimeoutException)
            {
            }
        }
    }
}






