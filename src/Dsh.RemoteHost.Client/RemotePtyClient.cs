using System.Text.Json;
using static Dsh.RemoteHost.RemoteHostRpc;
using System.Collections.Concurrent;
using System.Threading.Channels;
using Dsh.Transport;

namespace Dsh.RemoteHost;

/** Client implementation of the optional Pty domain. */
public sealed class RemotePtyClient
{
    private readonly RemoteHostClient _host;

    internal RemotePtyClient(RemoteHostClient host) => _host = host;

    private readonly ConcurrentDictionary<string, Channel<RemotePtyOutput>> _ptyStreams = new(StringComparer.Ordinal);

    public async Task<string> StartPtyAsync(RemotePtyStartRequest request, CancellationToken cancellationToken = default)
    {
        var reference = await _host.SendAsync(PtyProtocol.MethodPtyStart, Serialize(request, PtyProtocolJsonContext.Default.RemotePtyStartRequest), PtyProtocolJsonContext.Default.RemotePtyRef, cancellationToken).ConfigureAwait(false);
        return reference.PtyId;
    }

    /** 每个视图单独订阅同一 PTY，结束时只释放自己的推送。 */
    public async Task AttachPtyAsync(string ptyId, Stream input, Stream output, CancellationToken cancellationToken = default)
    {
        var subscriptionId = Guid.NewGuid().ToString("N");
        var channel = Channel.CreateUnbounded<RemotePtyOutput>(new UnboundedChannelOptions { SingleReader = true });
        _ptyStreams[subscriptionId] = channel;
        using var inputLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task pumpInput = Task.CompletedTask;
        try
        {
            await _host.SendAsync(PtyProtocol.MethodPtyAttach, Serialize(new RemotePtyAttachRequest(ptyId, subscriptionId), PtyProtocolJsonContext.Default.RemotePtyAttachRequest), PtyProtocolJsonContext.Default.Boolean, cancellationToken).ConfigureAwait(false);
            pumpInput = PumpPtyInputAsync(ptyId, input, inputLifetime.Token);
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var bytes = Convert.FromBase64String(item.Base64);
                if (bytes.Length > 0)
                    await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                if (item.Eof)
                    break;
            }
        }
        finally
        {
            _ptyStreams.TryRemove(subscriptionId, out _);
            await inputLifetime.CancelAsync().ConfigureAwait(false);
            await StopStreamAsync(PtyProtocol.MethodPtyDetach, subscriptionId).ConfigureAwait(false);
            await output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            await pumpInput.ConfigureAwait(false);
        }
    }

    public Task StopPtyAsync(string ptyId, CancellationToken cancellationToken = default)
        => _host.SendAsync(PtyProtocol.MethodPtyStop, Serialize(new RemotePtyRef(ptyId), PtyProtocolJsonContext.Default.RemotePtyRef), PtyProtocolJsonContext.Default.Boolean, cancellationToken);

    private async Task PumpPtyInputAsync(string ptyId, Stream input, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        try
        {
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                await _host.SendAsync(PtyProtocol.MethodPtyWrite, Serialize(new RemotePtyInput(ptyId, Convert.ToBase64String(buffer.AsSpan(0, read))), PtyProtocolJsonContext.Default.RemotePtyInput), PtyProtocolJsonContext.Default.Boolean, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException)
        {
        }
    }

    private async Task StopStreamAsync(string method, string subscriptionId)
    {
        if (_host.IsClosed)
            return;
        try
        {
            await _host.SendAsync(method, Serialize(new RemoteSubscriptionRef(subscriptionId), PtyProtocolJsonContext.Default.RemoteSubscriptionRef), PtyProtocolJsonContext.Default.Boolean, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
        }
    }

    public Task ResizePtyAsync(string ptyId, int columns, int rows, CancellationToken cancellationToken = default)
        => _host.SendAsync(PtyProtocol.MethodPtyResize, Serialize(new RemotePtyResize(ptyId, columns, rows), PtyProtocolJsonContext.Default.RemotePtyResize), PtyProtocolJsonContext.Default.Boolean, cancellationToken);

    internal void OnNotification(JsonRpcMessage message)
    {
        if (message.Method != PtyProtocol.NotificationPtyOutput || message.Params is not { } parameters)
            return;
        var frame = JsonSerializer.Deserialize(parameters, PtyProtocolJsonContext.Default.RemotePtyFrame);
        if (_ptyStreams.TryGetValue(frame.SubscriptionId, out var stream))
            stream.Writer.TryWrite(frame.Output);
    }

    internal void OnClosed(Exception failure)
    {
        foreach (var stream in _ptyStreams.Values)
            stream.Writer.TryComplete(failure);
    }
}



