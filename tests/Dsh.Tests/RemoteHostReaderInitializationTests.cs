using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Dsh.Core;
using Dsh.Ide;
using Dsh.Ide.Bridge;
using Dsh.Llm;
using Dsh.RemoteHost;
using Dsh.RemoteHost.Rpc;
using Dsh.Transport;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;

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
        var tap = new PushTap();
        using var peer = JsonRpcChannel.Connect(transport, HostRpcFormatter.CreateJson(EditorProtocolJsonContext.Default), contracts: [new(RpcTargetMetadata.FromShape<IHostPush>(), tap)], methods: tap.Methods());
        var gate = delayed!;
        var pending = peer.InvokeAsync(pty ? PtyProtocol.MethodPtyAttach : HostProtocol.MethodSessionSubscribe,
            Parameters(pty, resource, "cancelled-view"), Ct);
        try
        {
            await gate.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            // The second view reads the same real resource while the first is still initializing.
            await peer.InvokeAsync(pty ? PtyProtocol.MethodPtyAttach : HostProtocol.MethodSessionSubscribe,
                Parameters(pty, resource, "surviving-view"), Ct);
            await peer.InvokeAsync(pty ? PtyProtocol.MethodPtyDetach : HostProtocol.MethodSessionUnsubscribe,
                JsonSerializer.SerializeToElement(new RemoteSubscriptionRef("cancelled-view"), HostProtocolJsonContext.Default.RemoteSubscriptionRef), Ct);
            gate.Release.TrySetResult();
            try
            {
                await pending;
            }
            catch (Exception error) when (error is RemoteRpcException or OperationCanceledException)
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
                await peer.InvokeAsync(PtyProtocol.MethodPtyWrite,
                    JsonSerializer.SerializeToElement(new RemotePtyInput(resource, Convert.ToBase64String(Encoding.UTF8.GetBytes(command))), PtyProtocolJsonContext.Default.RemotePtyInput), Ct);
            }
            else
            {
                RemoteHostMultiClientTests.Agent(host, resource).Session.Append(new UserMessagePayload(MessageFactory.CreateUserText(marker)), new SurfaceOp.Append());
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            var output = new StringBuilder();
            await foreach (var frame in tap.ReadAsync(pty, deadline.Token))
            {
                Assert.NotEqual("cancelled-view", frame.Reader);
                if (frame.Reader == "surviving-view")
                    output.Append(frame.Text);
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
        using var peer = JsonRpcChannel.Connect(transport, HostRpcFormatter.CreateJson(EditorProtocolJsonContext.Default));
        await Assert.ThrowsAsync<RemoteInvocationException>(() => peer.InvokeAsync(
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
        var tap = new PushTap();
        using var peer = JsonRpcChannel.Connect(transport, HostRpcFormatter.CreateJson(EditorProtocolJsonContext.Default), contracts: [new(RpcTargetMetadata.FromShape<IHostPush>(), tap)], methods: tap.Methods());
        var subscribe = pty ? PtyProtocol.MethodPtyAttach : HostProtocol.MethodSessionSubscribe;
        var stop = pty ? PtyProtocol.MethodPtyDetach : HostProtocol.MethodSessionUnsubscribe;
        List<Task<JsonElement>> starts = [];
        List<Task<JsonElement>> stops = [];
        for (var index = 0; index < 64; index++)
        {
            var reader = $"cancelled-{index}";
            starts.Add(peer.InvokeAsync(subscribe, Parameters(pty, resource, reader), Ct));
            stops.Add(peer.InvokeAsync(stop,
                JsonSerializer.SerializeToElement(new RemoteSubscriptionRef(reader), HostProtocolJsonContext.Default.RemoteSubscriptionRef), Ct));
        }
        await Task.WhenAll(stops).WaitAsync(TimeSpan.FromSeconds(5), Ct);
        foreach (var start in starts)
        {
            try
            {
                await start.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            }
            catch (Exception error) when (error is RemoteRpcException or OperationCanceledException)
            {
                // A stop received before acquisition finishes rejects that subscribe, not the connection.
            }
        }
        await tracked!.ReadersReleasedAsync().WaitAsync(TimeSpan.FromSeconds(5), Ct);
        // A completed or unknown stop must not poison a later owner of the same reader ID.
        await peer.InvokeAsync(stop,
            JsonSerializer.SerializeToElement(new RemoteSubscriptionRef("surviving-view"), HostProtocolJsonContext.Default.RemoteSubscriptionRef), Ct);
        await peer.InvokeAsync(subscribe, Parameters(pty, resource, "surviving-view"), Ct);
        await peer.InvokeAsync(subscribe, Parameters(pty, resource, "cancelled-0"), Ct);
        const string marker = "ordered-reader-42";
        if (pty)
        {
            var command = OperatingSystem.IsWindows()
                ? "set /a orderedResult=40+2 & echo ordered-reader-!orderedResult!\r\n"
                : "printf 'ordered-reader-%s\\n' \"$((40+2))\"\n";
            await peer.InvokeAsync(PtyProtocol.MethodPtyWrite,
                JsonSerializer.SerializeToElement(new RemotePtyInput(resource, Convert.ToBase64String(Encoding.UTF8.GetBytes(command))), PtyProtocolJsonContext.Default.RemotePtyInput), Ct);
        }
        else
            RemoteHostMultiClientTests.Agent(host, resource).Session.Append(new UserMessagePayload(MessageFactory.CreateUserText(marker)), new SurfaceOp.Append());

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        Dictionary<string, StringBuilder> output = new(StringComparer.Ordinal);
        HashSet<string> completed = [];
        await foreach (var frame in tap.ReadAsync(pty, deadline.Token))
        {
            if (!output.TryGetValue(frame.Reader, out var content))
                output[frame.Reader] = content = new StringBuilder();
            content.Append(frame.Text);
            if (!content.ToString().Contains(marker, StringComparison.Ordinal))
                continue;
            Assert.True(frame.Reader is "surviving-view" or "cancelled-0", $"Stopped reader {frame.Reader} received new resource output.");
            completed.Add(frame.Reader);
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
        using var peer = JsonRpcChannel.Connect(transport, HostRpcFormatter.CreateJson(EditorProtocolJsonContext.Default));
        var error = await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => peer.InvokeAsync(
            pty ? PtyProtocol.MethodPtyAttach : HostProtocol.MethodSessionSubscribe, null, Ct));
        Assert.Equal(JsonRpcErrorCode.InvalidParams, (JsonRpcErrorCode)error.ErrorCode);
        Assert.NotEqual(default, await peer.InvokeAsync(HostProtocol.MethodPing, null, Ct));
    }

    private static JsonElement Parameters(bool pty, string resource, string reader)
        => pty
            ? JsonSerializer.SerializeToElement(new RemotePtyAttachRequest(resource, reader), PtyProtocolJsonContext.Default.RemotePtyAttachRequest)
            : JsonSerializer.SerializeToElement(new RemoteSubscribeRequest(resource, 0, reader), HostProtocolJsonContext.Default.RemoteSubscribeRequest);

    /** 一条推送帧归一成"哪个 reader 收到什么文本", 让事件流与 PTY 流共用同一段断言。 */
    private readonly record struct PushedFrame(string Reader, string Text);

    /** 客户端侧的推送目标: 把强类型推送帧倒进通道, 供测试按顺序观察。 */
    private sealed class PushTap : IHostPush
    {
        private readonly Channel<PushedFrame> _frames = Channel.CreateUnbounded<PushedFrame>(new UnboundedChannelOptions { SingleReader = true });

        public Task EventAsync(RemoteEventFrame frame, CancellationToken cancellationToken = default)
        {
            _frames.Writer.TryWrite(new PushedFrame(frame.SubscriptionId,
                frame.Event.Event is { } sessionEvent ? DshJson.Serialize(sessionEvent) : ""));
            return Task.CompletedTask;
        }

        public Task PtyOutputAsync(RemotePtyFrame frame, CancellationToken cancellationToken = default)
        {
            _frames.Writer.TryWrite(new PushedFrame(frame.SubscriptionId,
                Encoding.UTF8.GetString(Convert.FromBase64String(frame.Output.Base64))));
            return Task.CompletedTask;
        }

        public Task PendingChangedAsync(RemotePendingChange change, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SessionsChangedAsync(RemoteSessionChange change, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task NotifyChangedAsync(RemoteNotificationChange change, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /** client.ide.* 两条反向方法按线协议名登记。 */
        public (string Method, Delegate Handler)[] Methods() =>
        [
            (EditorProtocol.MethodClientOpen, (Func<RemoteEditorOpen, CancellationToken, Task<EditorResult>>)ClientOpenAsync),
            (EditorProtocol.MethodClientCancel, (Func<RemoteEditorCancel, CancellationToken, Task<bool>>)ClientCancelAsync),
        ];

        private static Task<EditorResult> ClientOpenAsync(RemoteEditorOpen request, CancellationToken cancellationToken = default)
            => Task.FromResult(new EditorResult { Error = EditorErrorCode.CLIENT_UNAVAILABLE });

        private static Task<bool> ClientCancelAsync(RemoteEditorCancel request, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        /** 两种帧都写在同一条通道里, 顺序就是到达顺序。 */
        public IAsyncEnumerable<PushedFrame> ReadAsync(bool pty, CancellationToken cancellationToken)
            => _frames.Reader.ReadAllAsync(cancellationToken);
    }

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
        public Task<RemoteSessionSnapshot> GetSessionSnapshotAsync(string id, long fromEvent, int maxEvents, CancellationToken ct) => backend.Session.GetSessionSnapshotAsync(id, fromEvent, maxEvents, ct);
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
