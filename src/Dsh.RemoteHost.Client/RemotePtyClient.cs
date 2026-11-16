using StreamJsonRpc;
using System.Collections.Concurrent;
using System.Threading.Channels;
using Dsh.RemoteHost.Rpc;
using Dsh.Transport;

namespace Dsh.RemoteHost;

/** Client implementation of the optional Pty domain. */
public sealed class RemotePtyClient : IWorkspaceTerminals
{
    private readonly RemoteHostClient _host;
    private readonly IPtyRpc _rpc;

    internal RemotePtyClient(RemoteHostClient host, IPtyRpc rpc)
    {
        _host = host;
        _rpc = rpc;
    }

    private readonly ConcurrentDictionary<string, Channel<RemotePtyOutput>> _ptyStreams = new(StringComparer.Ordinal);

    public async Task<string> StartPtyAsync(RemotePtyStartRequest request, CancellationToken cancellationToken = default)
    {
        var reference = await _rpc.StartAsync(request, cancellationToken).ConfigureAwait(false);
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
            await _rpc.AttachAsync(new RemotePtyAttachRequest(ptyId, subscriptionId), cancellationToken).ConfigureAwait(false);
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
            await StopStreamAsync(subscriptionId).ConfigureAwait(false);
            await output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            await pumpInput.ConfigureAwait(false);
        }
    }

    public async Task StopPtyAsync(string ptyId, CancellationToken cancellationToken = default)
        => await _rpc.StopAsync(new RemotePtyRef(ptyId), cancellationToken).ConfigureAwait(false);

    private async Task PumpPtyInputAsync(string ptyId, Stream input, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        try
        {
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                await _rpc.WriteAsync(new RemotePtyInput(ptyId, Convert.ToBase64String(buffer.AsSpan(0, read))), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException)
        {
        }
    }

    private async Task StopStreamAsync(string subscriptionId)
    {
        if (_host.IsClosed)
            return;
        try
        {
            await _rpc.DetachAsync(new RemoteSubscriptionRef(subscriptionId), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or ConnectionLostException)
        {
        }
    }

    public async Task ResizePtyAsync(string ptyId, int columns, int rows, CancellationToken cancellationToken = default)
        => await _rpc.ResizeAsync(new RemotePtyResize(ptyId, columns, rows), cancellationToken).ConfigureAwait(false);

    internal Task OnOutputAsync(RemotePtyFrame frame)
    {
        if (_ptyStreams.TryGetValue(frame.SubscriptionId, out var stream))
            stream.Writer.TryWrite(frame.Output);
        return Task.CompletedTask;
    }

    internal void OnClosed(Exception failure)
    {
        foreach (var stream in _ptyStreams.Values)
            stream.Writer.TryComplete(failure);
    }
}
