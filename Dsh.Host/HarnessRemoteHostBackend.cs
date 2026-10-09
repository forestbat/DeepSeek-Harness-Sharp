using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Pty;
using Dsh.RemoteHost;
using Dsh.Runtime;
using Dsh.Runtime.Events;

namespace Dsh.Host;

/**
 * 远端宿主能力后端: 直连进程内 harness 服务(会话 / 事件 / 工具 / 审批 / 文件 / PTY)。
 * 事件与审批是「远端发起」: 事件经 ctx 通知转成推送流; 审批经 ctx 的 waterfall 桥接成"远端问、本地答"。
 */
public sealed class HarnessRemoteHostBackend : IRemoteHostBackend, IDisposable
{
    private readonly HarnessApp _app;
    private readonly Channel<RemoteApprovalRequest> _approvals = Channel.CreateUnbounded<RemoteApprovalRequest>();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<object?>> _pendingApprovals = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AgentHandle> _handles = new(StringComparer.Ordinal);
    private readonly Func<bool> _approvalBridge;
    private long _approvalSequence;

    public HarnessRemoteHostBackend(HarnessApp app)
    {
        _app = app;
        _approvalBridge = app.Ctx.OnWaterfall<ApprovalRequestNotification>(BridgeApprovalAsync, new EventOptions { Global = true });
    }

    private async ValueTask<object?> BridgeApprovalAsync(ApprovalRequestNotification notification, Func<ValueTask<object?>> next)
    {
        var requestId = Interlocked.Increment(ref _approvalSequence).ToString();
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingApprovals[requestId] = completion;
        var request = notification.Request;
        await _approvals.Writer.WriteAsync(new RemoteApprovalRequest(
            requestId,
            request.Agent.Id.Value,
            $"批准工具 {request.ToolName}？",
            request.ToolName));
        return await completion.Task.ConfigureAwait(false);
    }

    private AgentRegistry Registry => _app.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName);

    public Task<HostInfo> InfoAsync(CancellationToken cancellationToken)
        => Task.FromResult(new HostInfo(HostProtocol.Version, "host", Platform(), _app.Home.Root));

    public Task<IReadOnlyList<RemoteSessionInfo>> ListSessionsAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<RemoteSessionInfo>>([.. Registry.List().Select(Describe)]);

    public async Task<RemoteSessionInfo> CreateSessionAsync(string cwd, CancellationToken cancellationToken)
    {
        var handle = await Registry.Create(new CreateAgentOptions(Cwd: cwd), cancellationToken).ConfigureAwait(false);
        _handles[handle.Agent.Id.Value] = handle;
        return Describe(handle.Agent);
    }

    public async Task<RemoteSessionInfo> ResumeSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        var handle = await Registry.Resume(new ResumeAgentOptions(SessionId.Create(sessionId)), cancellationToken).ConfigureAwait(false);
        _handles[handle.Agent.Id.Value] = handle;
        return Describe(handle.Agent);
    }

    public Task SendMessageAsync(string sessionId, string text, CancellationToken cancellationToken)
    {
        Require(sessionId).Followup(MessageFactory.CreateUserMessage([new TextBlock(text)]));
        return Task.CompletedTask;
    }

    public Task InterruptAsync(string sessionId, CancellationToken cancellationToken)
    {
        Require(sessionId).Cancel(new AgentCancelCause.User());
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListToolsAsync(string sessionId, CancellationToken cancellationToken)
    {
        var runtime = _app.Ctx.Get<ToolRuntime>(ToolRuntime.ServiceName, false);
        if (runtime is null)
            return Task.FromResult<IReadOnlyList<string>>([]);
        var scope = Registry.Get(SessionId.Create(sessionId))?.ScopeKey;
        IReadOnlyList<string> names = [.. runtime.Schemas(scope).Select(schema => schema.Name)];
        return Task.FromResult(names);
    }

    public Task RespondApprovalAsync(string requestId, bool allow, string? reason, CancellationToken cancellationToken)
    {
        if (_pendingApprovals.TryRemove(requestId, out var completion))
            completion.TrySetResult(allow ? ApprovalOutcome.AllowedOnce : ApprovalOutcome.Rejected);
        return Task.CompletedTask;
    }

    public Task<byte[]> ReadFileAsync(string path, CancellationToken cancellationToken)
        => File.ReadAllBytesAsync(path, cancellationToken);

    public Task WriteFileAsync(string path, byte[] content, CancellationToken cancellationToken)
        => File.WriteAllBytesAsync(path, content, cancellationToken);

    public async Task<string> StartPtyAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var session = await PtyHost.Default
            .StartAsync(new PtyStartInfo { FileName = fileName, Arguments = arguments, Home = _app.Home.Root }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return session.Id.Value;
    }

    public async Task WritePtyAsync(string ptyId, byte[] data, CancellationToken cancellationToken)
    {
        if (PtyHost.Default.Get(ptyId) is { } session)
            await session.WriteAsync(data, cancellationToken).ConfigureAwait(false);
    }

    public Task StopPtyAsync(string ptyId, CancellationToken cancellationToken)
        => PtyHost.Default.StopAsync(ptyId);

    public async IAsyncEnumerable<RemoteEventInfo> SubscribeAsync(string sessionId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<RemoteEventInfo>();
        var unsubscribe = _app.Ctx.On<SessionEventNotification>(notification =>
        {
            if (string.Equals(notification.Session.Id.Value, sessionId, StringComparison.Ordinal))
                channel.Writer.TryWrite(new RemoteEventInfo(sessionId, notification.Event.Type, null, notification.Event.Time));
        });
        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return item;
        }
        finally
        {
            channel.Writer.TryComplete();
            unsubscribe();
        }
    }

    public async IAsyncEnumerable<RemoteApprovalRequest> ApprovalsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in _approvals.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return item;
    }

    public async IAsyncEnumerable<RemotePtyOutput> PtyOutputAsync(string ptyId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var session = PtyHost.Default.Get(ptyId);
        if (session is null)
            yield break;
        var buffer = new byte[8192];
        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await session.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                yield return new RemotePtyOutput(ptyId, "", true);
                yield break;
            }

            yield return new RemotePtyOutput(ptyId, Convert.ToBase64String(buffer, 0, read), false);
        }
    }

    private IAgent Require(string sessionId)
        => Registry.Get(SessionId.Create(sessionId)) ?? throw new InvalidOperationException($"unknown session: {sessionId}");

    private static RemoteSessionInfo Describe(IAgent agent)
        => new(agent.Id.Value, agent.Session.Header.Cwd ?? "", agent.Status.ToString(), 0);

    private static string Platform()
        => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

    public void Dispose()
    {
        _approvalBridge();
        foreach (var handle in _handles.Values)
            handle.Dispose.Dispose();
        _handles.Clear();
        _approvals.Writer.TryComplete();
    }
}
