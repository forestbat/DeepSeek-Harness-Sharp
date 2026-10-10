using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Dsh.RemoteHost;

namespace Dsh.Tests;

public sealed class RemoteHostCapabilityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SessionsToolsFilesAndMessageFlowOverTheWire()
    {
        var backend = new FakeBackend();
        await using var fixture = await HostFixture.CreateAsync(backend, Ct);
        var client = fixture.Client;

        Assert.Empty(await client.ListSessionsAsync(Ct));
        var created = await client.CreateSessionAsync("/work", Ct);
        Assert.Equal("/work", created.Cwd);
        Assert.Single(await client.ListSessionsAsync(Ct));
        Assert.Equal(["read", "write"], await client.ListToolsAsync(created.Id, Ct));

        await client.WriteFileAsync("/work/a.txt", "hello"u8.ToArray(), Ct);
        Assert.Equal("hello"u8.ToArray(), await client.ReadFileAsync("/work/a.txt", Ct));

        var listing = await client.ListDirectoryAsync("/work", Ct);
        Assert.Equal("/work", listing.Path);
        Assert.Equal("/", listing.Parent);
        Assert.Contains(listing.Entries, entry => entry is { Name: "sub", IsDirectory: true });

        var events = new List<RemoteEventInfo>();
        using var subscription = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var pump = Task.Run(async () =>
        {
            await foreach (var item in client.SubscribeAsync(created.Id, 0, subscription.Token))
                events.Add(item);
        }, Ct);
        await Task.Delay(200, Ct);
        await client.SendMessageAsync(created.Id, "hi", [], Ct);
        await WaitForAsync(() => events.Count > 0, Ct);
        Assert.Equal("user:hi", events[0].EventJson);
        await subscription.CancelAsync();
        try
        {
            await pump;
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact]
    public async Task ApprovalRequestRoundTripsBack()
    {
        var backend = new FakeBackend();
        await using var fixture = await HostFixture.CreateAsync(backend, Ct);
        var received = new TaskCompletionSource<RemoteApprovalRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var pump = Task.Run(async () =>
        {
            await foreach (var request in fixture.Client.ApprovalsAsync(subscription.Token))
                received.TrySetResult(request);
        }, Ct);

        backend.RaiseApproval(new RemoteApprovalRequest("req-1", "session-1", "bash", null, "allow?"));
        var approval = await received.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await fixture.Client.RespondApprovalAsync(approval.RequestId, true, "ok", Ct);
        await WaitForAsync(() => backend.ApprovalAnswer("req-1") is not null, Ct);
        Assert.True(backend.ApprovalAnswer("req-1"));
        await subscription.CancelAsync();
        try
        {
            await pump;
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact]
    public async Task PtyEchoesInputOverTheWire()
    {
        var backend = new FakeBackend();
        await using var fixture = await HostFixture.CreateAsync(backend, Ct);
        var ptyId = await fixture.Client.StartPtyAsync("/bin/sh", [], Ct);

        var pipe = new System.IO.Pipelines.Pipe();
        var input = pipe.Reader.AsStream();
        var output = new MemoryStream();
        using var attachCts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var attach = fixture.Client.AttachPtyAsync(ptyId, input, output, attachCts.Token);

        await pipe.Writer.WriteAsync("ping"u8.ToArray(), Ct);
        await pipe.Writer.FlushAsync(Ct);
        await WaitForAsync(() => output.Length >= 4, Ct);
        Assert.Equal("ping", Encoding.UTF8.GetString(output.ToArray()));

        await attachCts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await attach);
    }

    [Fact]
    public async Task QuestionRequestRoundTripsBack()
    {
        var backend = new FakeBackend();
        await using var fixture = await HostFixture.CreateAsync(backend, Ct);
        var received = new TaskCompletionSource<RemoteQuestionRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var pump = Task.Run(async () =>
        {
            await foreach (var request in fixture.Client.QuestionsAsync(subscription.Token))
                received.TrySetResult(request);
        }, Ct);

        backend.RaiseQuestion(new RemoteQuestionRequest("q-1", "session-1",
            [new RemoteQuestionItem("q1", "pick?", null, null, [new RemoteQuestionOption("A", null)], false, null)]));
        var question = await received.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await fixture.Client.RespondQuestionAsync(question.RequestId, [new RemoteQuestionAnswerItem("q1", ["A"], null)], Ct);
        await WaitForAsync(() => backend.QuestionAnswer("q-1") is not null, Ct);
        Assert.Equal("A", backend.QuestionAnswer("q-1"));
        await subscription.CancelAsync();
        try
        {
            await pump;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 500 && !condition(); attempt++)
            await Task.Delay(10, cancellationToken);
        Assert.True(condition(), "condition not met in time");
    }

    /** 记录/回放式的远端后端替身; 不依赖 harness。 */
    private sealed class FakeBackend : IRemoteHostBackend
    {
        private readonly Dictionary<string, RemoteSessionInfo> _sessions = [];
        private readonly Dictionary<string, Channel<RemoteEventInfo>> _events = [];
        private readonly Dictionary<string, byte[]> _files = [];
        private readonly Dictionary<string, RemoteDirectoryListing> _directories = new(StringComparer.Ordinal)
        {
            ["/work"] = new RemoteDirectoryListing(
                "/work",
                "/",
                [new RemoteDirectoryEntry("sub", "/work/sub", true), new RemoteDirectoryEntry("a.txt", "/work/a.txt", false)]),
        };
        private readonly Dictionary<string, Channel<RemotePtyOutput>> _pty = [];
        private readonly Channel<RemoteApprovalRequest> _approvals = Channel.CreateUnbounded<RemoteApprovalRequest>();
        private readonly Channel<RemoteQuestionRequest> _questions = Channel.CreateUnbounded<RemoteQuestionRequest>();
        private readonly Dictionary<string, bool> _approvalAnswers = [];
        private readonly Dictionary<string, string> _questionAnswers = [];

        public Task<HostInfo> InfoAsync(CancellationToken cancellationToken)
            => Task.FromResult(new HostInfo(HostProtocol.Version, "fake", "test", "/home"));

        public Task<IReadOnlyList<RemoteSessionInfo>> ListSessionsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<RemoteSessionInfo>>([.. _sessions.Values]);

        public Task<RemoteSessionInfo> CreateSessionAsync(string cwd, CancellationToken cancellationToken)
        {
            var session = new RemoteSessionInfo($"session-{_sessions.Count + 1}", cwd, "Idle", 0, null, null, 0);
            _sessions[session.Id] = session;
            return Task.FromResult(session);
        }

        public Task<RemoteSessionInfo> ResumeSessionAsync(string sessionId, CancellationToken cancellationToken)
            => Task.FromResult(_sessions.TryGetValue(sessionId, out var session) ? session : throw new KeyNotFoundException(sessionId));

        public Task SendMessageAsync(string sessionId, string text, IReadOnlyList<RemoteImageBlock> images, CancellationToken cancellationToken)
        {
            if (_events.TryGetValue(sessionId, out var channel))
                channel.Writer.TryWrite(new RemoteEventInfo(sessionId, $"user:{text}"));
            return Task.CompletedTask;
        }

        public Task InterruptAsync(string sessionId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<string>> ListToolsAsync(string sessionId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<string>>(["read", "write"]);

        public Task RespondApprovalAsync(string requestId, bool allow, string? reason, CancellationToken cancellationToken)
        {
            _approvalAnswers[requestId] = allow;
            return Task.CompletedTask;
        }

        public Task RespondQuestionAsync(string requestId, IReadOnlyList<RemoteQuestionAnswerItem> answers, CancellationToken cancellationToken)
        {
            _questionAnswers[requestId] = answers.Count > 0 && answers[0].Selected.Count > 0 ? answers[0].Selected[0] : "";
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<RemoteQuestionRequest> QuestionsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var request in _questions.Reader.ReadAllAsync(cancellationToken))
                yield return request;
        }

        public Task<byte[]> ReadFileAsync(string path, CancellationToken cancellationToken)
            => Task.FromResult(_files.TryGetValue(path, out var bytes) ? bytes : throw new FileNotFoundException(path));

        public Task WriteFileAsync(string path, byte[] content, CancellationToken cancellationToken)
        {
            _files[path] = content;
            return Task.CompletedTask;
        }

        public Task<RemoteDirectoryListing> ListDirectoryAsync(string path, CancellationToken cancellationToken)
            => Task.FromResult(_directories.TryGetValue(path, out var listing)
                ? listing
                : throw new DirectoryNotFoundException(path));

        public Task<string> StartPtyAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
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

        public Task StopPtyAsync(string ptyId, CancellationToken cancellationToken)
        {
            if (_pty.Remove(ptyId, out var channel))
                channel.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<RemoteEventInfo> SubscribeAsync(string sessionId, long fromSeq, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var channel = _events.TryGetValue(sessionId, out var existing)
                ? existing
                : _events[sessionId] = Channel.CreateUnbounded<RemoteEventInfo>();
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken))
                yield return item;
        }

        public async IAsyncEnumerable<RemoteApprovalRequest> ApprovalsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var request in _approvals.Reader.ReadAllAsync(cancellationToken))
                yield return request;
        }

        public async IAsyncEnumerable<RemotePtyOutput> PtyOutputAsync(string ptyId, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (!_pty.TryGetValue(ptyId, out var channel))
                yield break;
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken))
                yield return item;
        }

        public void RaiseApproval(RemoteApprovalRequest request) => _approvals.Writer.TryWrite(request);

        public bool? ApprovalAnswer(string requestId) => _approvalAnswers.TryGetValue(requestId, out var answer) ? answer : null;

        public void RaiseQuestion(RemoteQuestionRequest request) => _questions.Writer.TryWrite(request);

        public string? QuestionAnswer(string requestId) => _questionAnswers.GetValueOrDefault(requestId);
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
