using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Dsh.Core;
using Dsh.Llm;
using Dsh.RemoteHost;
using Dsh.Transport;

namespace Dsh.Tests;

[Collection(SerialProcessCollection.Name)]
public sealed class RemoteHostReaderInitializationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory(Timeout = 5000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopDuringInitializationReleasesRealReaderAndPreservesAnotherView(bool pty)
    {
        DelayedBackend? delayed = null;
        await using var host = await HarnessHostFixture.CreateAsync("ri", TestContext.Current.CancellationToken,
            backend => delayed = new DelayedBackend(backend, pty));
        var client = await host.ConnectAsync("owner", Ct);
        string resource;
        if (pty)
        {
            var shell = RemoteHostMultiClientTests.Shell;
            resource = await client.Pty.StartPtyAsync(new RemotePtyStartRequest(shell.FileName, shell.Arguments), Ct);
        }
        else
        {
            resource = (await client.CreateSessionAsync(host.Environment.App.Home.Root, cancellationToken: Ct)).Id;
            RemoteHostMultiClientTests.Agent(host, resource).Session.Append(new UserMessagePayload(MessageFactory.CreateUserText("history")), new SurfaceOp.Append());
        }

        await using var transport = await RemoteHostConnector.ConnectAsync(host.Root, Ct);
        await using var peer = new JsonRpcPeer(transport);
        var received = Channel.CreateUnbounded<JsonRpcMessage>();
        peer.NotificationReceived += message => received.Writer.TryWrite(message);
        peer.Start();
        var gate = delayed!;
        var pending = peer.RequestAsync(pty ? PtyProtocol.MethodPtyAttach : HostProtocol.MethodSessionSubscribe,
            Parameters(pty, resource, "cancelled-view"), Ct);
        try
        {
            await gate.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            // The second view reads the same real resource while the first is still initializing.
            await peer.RequestAsync(pty ? PtyProtocol.MethodPtyAttach : HostProtocol.MethodSessionSubscribe,
                Parameters(pty, resource, "surviving-view"), Ct);
            await peer.RequestAsync(pty ? PtyProtocol.MethodPtyDetach : HostProtocol.MethodSessionUnsubscribe,
                JsonSerializer.SerializeToElement(new RemoteSubscriptionRef("cancelled-view"), HostProtocolJsonContext.Default.RemoteSubscriptionRef), Ct);
            gate.Release.TrySetResult();
            try
            {
                await pending;
            }
            catch (JsonRpcException)
            {
                // Cancellation may reject the outstanding subscribe, but initialization errors must never be ACKed early.
            }
            await gate.ReaderReleased.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

            const string marker = "reader-survives-42";
            if (pty)
            {
                var command = OperatingSystem.IsWindows()
                    ? "set /a readerResult=40+2 & echo reader-survives-!readerResult!\r\n"
                    : "printf 'reader-survives-%s\\n' \"$((40+2))\"\n";
                await peer.RequestAsync(PtyProtocol.MethodPtyWrite,
                    JsonSerializer.SerializeToElement(new RemotePtyInput(resource, Convert.ToBase64String(Encoding.UTF8.GetBytes(command))), PtyProtocolJsonContext.Default.RemotePtyInput), Ct);
            }
            else
            {
                RemoteHostMultiClientTests.Agent(host, resource).Session.Append(new UserMessagePayload(MessageFactory.CreateUserText(marker)), new SurfaceOp.Append());
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            var output = new StringBuilder();
            await foreach (var message in received.Reader.ReadAllAsync(deadline.Token))
            {
                if (pty && message.Method == PtyProtocol.NotificationPtyOutput && message.Params is { } bytes)
                {
                    var frame = JsonSerializer.Deserialize(bytes, PtyProtocolJsonContext.Default.RemotePtyFrame)!;
                    Assert.NotEqual("cancelled-view", frame.SubscriptionId);
                    if (frame.SubscriptionId == "surviving-view")
                        output.Append(Encoding.UTF8.GetString(Convert.FromBase64String(frame.Output.Base64)));
                }
                else if (!pty && message.Method == HostProtocol.NotificationEvent && message.Params is { } events)
                {
                    var frame = JsonSerializer.Deserialize(events, HostProtocolJsonContext.Default.RemoteEventFrame)!;
                    Assert.NotEqual("cancelled-view", frame.SubscriptionId);
                    if (frame.SubscriptionId == "surviving-view" && frame.Event.Event is { } sessionEvent)
                        output.Append(DshJson.Serialize(sessionEvent));
                }
                if (output.ToString().Contains(marker, StringComparison.Ordinal))
                    return;
            }
            Assert.Fail("surviving view ended before receiving the real session/terminal output");
        }
        finally
        {
            gate.Release.TrySetResult();
        }
    }

    [Theory(Timeout = 5000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownResourceFailsTheInitializingRpc(bool pty)
    {
        await using var host = await HarnessHostFixture.CreateAsync("rm", TestContext.Current.CancellationToken);
        await using var transport = await RemoteHostConnector.ConnectAsync(host.Root, Ct);
        await using var peer = new JsonRpcPeer(transport);
        peer.Start();
        await Assert.ThrowsAsync<JsonRpcException>(() => peer.RequestAsync(
            pty ? PtyProtocol.MethodPtyAttach : HostProtocol.MethodSessionSubscribe,
            Parameters(pty, "missing-resource", "missing-view"), Ct));
    }

    [Theory(Timeout = 5000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PipelinedStopDoesNotReviveReadersAndAllowsReaderIdReuse(bool pty)
    {
        DelayedBackend? tracked = null;
        await using var host = await HarnessHostFixture.CreateAsync("ro", TestContext.Current.CancellationToken,
            backend => tracked = new DelayedBackend(backend, pty) { DelayInitialization = false });
        var client = await host.ConnectAsync("owner", Ct);
        string resource;
        if (pty)
        {
            var shell = RemoteHostMultiClientTests.Shell;
            resource = await client.Pty.StartPtyAsync(new RemotePtyStartRequest(shell.FileName, shell.Arguments), Ct);
        }
        else
            resource = (await client.CreateSessionAsync(host.Environment.App.Home.Root, cancellationToken: Ct)).Id;
        await using var transport = await RemoteHostConnector.ConnectAsync(host.Root, Ct);
        await using var peer = new JsonRpcPeer(transport);
        var received = Channel.CreateUnbounded<JsonRpcMessage>();
        peer.NotificationReceived += message => received.Writer.TryWrite(message);
        peer.Start();
        var subscribe = pty ? PtyProtocol.MethodPtyAttach : HostProtocol.MethodSessionSubscribe;
        var stop = pty ? PtyProtocol.MethodPtyDetach : HostProtocol.MethodSessionUnsubscribe;
        List<Task<JsonElement?>> starts = [];
        List<Task<JsonElement?>> stops = [];
        for (var index = 0; index < 64; index++)
        {
            var reader = $"cancelled-{index}";
            starts.Add(peer.RequestAsync(subscribe, Parameters(pty, resource, reader), Ct));
            stops.Add(peer.RequestAsync(stop,
                JsonSerializer.SerializeToElement(new RemoteSubscriptionRef(reader), HostProtocolJsonContext.Default.RemoteSubscriptionRef), Ct));
        }
        await Task.WhenAll(stops).WaitAsync(TimeSpan.FromSeconds(5), Ct);
        foreach (var start in starts)
        {
            try
            {
                await start.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            }
            catch (JsonRpcException)
            {
                // A stop received before acquisition finishes rejects that subscribe, not the connection.
            }
        }
        await tracked!.ReadersReleasedAsync().WaitAsync(TimeSpan.FromSeconds(5), Ct);
        // A completed or unknown stop must not poison a later owner of the same reader ID.
        await peer.RequestAsync(stop,
            JsonSerializer.SerializeToElement(new RemoteSubscriptionRef("surviving-view"), HostProtocolJsonContext.Default.RemoteSubscriptionRef), Ct);
        await peer.RequestAsync(subscribe, Parameters(pty, resource, "surviving-view"), Ct);
        await peer.RequestAsync(subscribe, Parameters(pty, resource, "cancelled-0"), Ct);
        const string marker = "ordered-reader-42";
        if (pty)
        {
            var command = OperatingSystem.IsWindows()
                ? "set /a orderedResult=40+2 & echo ordered-reader-!orderedResult!\r\n"
                : "printf 'ordered-reader-%s\\n' \"$((40+2))\"\n";
            await peer.RequestAsync(PtyProtocol.MethodPtyWrite,
                JsonSerializer.SerializeToElement(new RemotePtyInput(resource, Convert.ToBase64String(Encoding.UTF8.GetBytes(command))), PtyProtocolJsonContext.Default.RemotePtyInput), Ct);
        }
        else
            RemoteHostMultiClientTests.Agent(host, resource).Session.Append(new UserMessagePayload(MessageFactory.CreateUserText(marker)), new SurfaceOp.Append());

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        Dictionary<string, StringBuilder> output = new(StringComparer.Ordinal);
        HashSet<string> completed = [];
        await foreach (var message in received.Reader.ReadAllAsync(deadline.Token))
        {
            string reader;
            string text;
            if (pty && message.Method == PtyProtocol.NotificationPtyOutput && message.Params is { } bytes)
            {
                var frame = JsonSerializer.Deserialize(bytes, PtyProtocolJsonContext.Default.RemotePtyFrame)!;
                reader = frame.SubscriptionId;
                text = Encoding.UTF8.GetString(Convert.FromBase64String(frame.Output.Base64));
            }
            else if (!pty && message.Method == HostProtocol.NotificationEvent && message.Params is { } events)
            {
                var frame = JsonSerializer.Deserialize(events, HostProtocolJsonContext.Default.RemoteEventFrame)!;
                reader = frame.SubscriptionId;
                text = frame.Event.Event is { } sessionEvent ? DshJson.Serialize(sessionEvent) : "";
            }
            else
                continue;
            if (!output.TryGetValue(reader, out var content))
                output[reader] = content = new StringBuilder();
            content.Append(text);
            if (!content.ToString().Contains(marker, StringComparison.Ordinal))
                continue;
            Assert.True(reader is "surviving-view" or "cancelled-0", $"Stopped reader {reader} received new resource output.");
            completed.Add(reader);
            if (completed.Count == 2)
                return;
        }
        Assert.Fail("Both current owners must receive the real resource output.");
    }

    [Theory(Timeout = 5000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidReaderRequestFailsOnlyItsRpc(bool pty)
    {
        await using var host = await HarnessHostFixture.CreateAsync("rv", TestContext.Current.CancellationToken);
        await using var transport = await RemoteHostConnector.ConnectAsync(host.Root, Ct);
        await using var peer = new JsonRpcPeer(transport);
        peer.Start();
        var error = await Assert.ThrowsAsync<JsonRpcException>(() => peer.RequestAsync(
            pty ? PtyProtocol.MethodPtyAttach : HostProtocol.MethodSessionSubscribe, cancellationToken: Ct));
        Assert.Equal(-32602, error.Code);
        Assert.NotNull(await peer.RequestAsync(HostProtocol.MethodPing, cancellationToken: Ct));
    }

    private static JsonElement Parameters(bool pty, string resource, string reader)
        => pty
            ? JsonSerializer.SerializeToElement(new RemotePtyAttachRequest(resource, reader), PtyProtocolJsonContext.Default.RemotePtyAttachRequest)
            : JsonSerializer.SerializeToElement(new RemoteSubscribeRequest(resource, 0, reader), HostProtocolJsonContext.Default.RemoteSubscribeRequest);

    /** Observes disposal or delays delivery of real readers without replacing session or PTY behavior. */
    private sealed class DelayedBackend(IRemoteHostBackend backend, bool delayPty) : IRemoteHostBackend, IRemoteSessionBackend, IRemotePtyBackend
    {
        private int _delayed;
        private readonly List<Task> _readerReleases = [];
        public bool DelayInitialization { get; init; } = true;
        public Task ReadersReleasedAsync()
        {
            lock (_readerReleases)
                return Task.WhenAll(_readerReleases);
        }

        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReaderReleased { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public HostPendingDecisions Pending => backend.Pending;
        public HostSessionRegistry Sessions => backend.Sessions;
        public IRemoteSessionBackend Session => this;
        public IRemotePtyBackend Pty => this;
        public IRemoteAgentBackend? Agent => backend.Agent;
        public HostFileSystem? Files => backend.Files;
        public HostGitService? Git => backend.Git;
        public HostNotificationCenter? Notifications => backend.Notifications;
        public IRemoteSessionEditingBackend? SessionEditing => backend.SessionEditing;
        public IRemotePluginsBackend? Plugins => backend.Plugins;
        public IRemoteNetworkBackend? Network => backend.Network;
        public Task<HostInfo> InfoAsync(CancellationToken ct) => backend.InfoAsync(ct);
        public Task<IReadOnlyList<RemoteSessionSummary>> ListSessionsAsync(CancellationToken ct) => backend.Session.ListSessionsAsync(ct);
        public Task<RemoteSessionSummary> CreateSessionAsync(string cwd, CancellationToken ct) => backend.Session.CreateSessionAsync(cwd, ct);
        public Task<RemoteSessionSummary> ResumeSessionAsync(string id, CancellationToken ct) => backend.Session.ResumeSessionAsync(id, ct);
        public Task<RemoteSessionSnapshot> GetSessionSnapshotAsync(string id, CancellationToken ct) => backend.Session.GetSessionSnapshotAsync(id, ct);
        public Task SendMessageAsync(string id, string text, IReadOnlyList<RemoteImageBlock> images, CancellationToken ct) => backend.Session.SendMessageAsync(id, text, images, ct);
        public Task InterruptAsync(string id, CancellationToken ct) => backend.Session.InterruptAsync(id, ct);
        public Task<IReadOnlyList<string>> ListToolsAsync(string id, CancellationToken ct) => backend.Session.ListToolsAsync(id, ct);
        public Task<string> StartPtyAsync(RemotePtyStartRequest request, CancellationToken ct) => backend.Pty!.StartPtyAsync(request, ct);
        public Task WritePtyAsync(string id, byte[] data, CancellationToken ct) => backend.Pty!.WritePtyAsync(id, data, ct);
        public Task ResizePtyAsync(string id, int columns, int rows, CancellationToken ct) => backend.Pty!.ResizePtyAsync(id, columns, rows, ct);
        public Task StopPtyAsync(string id, CancellationToken ct) => backend.Pty!.StopPtyAsync(id, ct);
        public async Task<RemoteHostStream<RemoteSessionEvent>> SubscribeAsync(string id, long from, CancellationToken ct)
            => await DelayAsync(await backend.Session.SubscribeAsync(id, from, ct), false, ct);
        public async Task<RemoteHostStream<RemotePtyOutput>> AttachPtyAsync(string id, CancellationToken ct)
            => await DelayAsync(await backend.Pty!.AttachPtyAsync(id, ct), true, ct);

        private async Task<RemoteHostStream<T>> DelayAsync<T>(RemoteHostStream<T> stream, bool pty, CancellationToken ct)
        {
            if (!DelayInitialization)
            {
                var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_readerReleases)
                    _readerReleases.Add(released.Task);
                return new RemoteHostStream<T>(stream, async () =>
                {
                    await stream.DisposeAsync();
                    released.TrySetResult();
                });
            }
            if (pty != delayPty || Interlocked.Exchange(ref _delayed, 1) != 0)
                return stream;
            var reader = stream.GetAsyncEnumerator(ct);
            Assert.True(await reader.MoveNextAsync());
            var observed = new ObservedStream<T>(reader, stream, ReaderReleased);
            Ready.TrySetResult();
            await Release.Task; // Intentionally finishes after stop, like an asynchronous acquisition already in flight.
            return new RemoteHostStream<T>(observed, observed.DisposeAsync);
        }
    }

    private sealed class ObservedStream<T>(IAsyncEnumerator<T> reader, IAsyncDisposable owner, TaskCompletionSource released) : IAsyncEnumerable<T>, IAsyncDisposable
    {
        private int _disposed;
        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            => ReadAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
        private async IAsyncEnumerable<T> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            try
            {
                yield return reader.Current;
                while (await reader.MoveNextAsync())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return reader.Current;
                }
            }
            finally
            {
                await DisposeAsync();
            }
        }
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            try
            {
                await reader.DisposeAsync();
            }
            finally
            {
                await owner.DisposeAsync();
            }
            released.TrySetResult();
        }
    }
}






