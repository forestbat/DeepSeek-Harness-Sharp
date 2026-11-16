using System.Text.Json.Nodes;
using Dsh.Ma2a.Services;
using Dsh.Pty;

namespace Dsh.Gui.Terminal;

/** Shares the existing VT screen and input encoder while MA2A owns the remote operation. */
public sealed class Ma2aTerminalConnection(Ma2aServiceSession session, bool resize, bool writable,
    Action<JsonNode?> metadata, Action<Exception> failed, CancellationToken lifetime) : ITerminalConnection, IAsyncDisposable
{
    private readonly TaskCompletionSource _admitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ReadOnlyMemory<byte> _remaining;
    private bool _ended;

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime);
        while (_remaining.IsEmpty && !_ended)
        {
            try { await ReadFrameAsync(linked.Token); }
            catch (Exception error) { _admitted.TrySetException(error); failed(error); throw; }
        }
        var length = Math.Min(buffer.Length, _remaining.Length);
        _remaining[..length].CopyTo(buffer);
        _remaining = _remaining[length..];
        return length;
    }

    private async Task ReadFrameAsync(CancellationToken cancellationToken)
    {
        var frame = await session.ReadAsync(cancellationToken);
        switch (frame.Kind)
        {
            case Ma2aFrameKind.Accept: _admitted.TrySetResult(); metadata(frame.DecodeCbor()); break;
            case Ma2aFrameKind.Data: _remaining = frame.Data; break;
            case Ma2aFrameKind.Control: metadata(frame.DecodeCbor()); break;
            case Ma2aFrameKind.Outcome:
                _ended = true;
                _admitted.TrySetCanceled();
                metadata(frame.DecodeCbor());
                break;
        }
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (!writable) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime);
        await _admitted.Task.WaitAsync(linked.Token);
        await session.SendDataAsync(0, data, linked.Token);
    }

    public async ValueTask ResizeAsync(TerminalSize size, CancellationToken cancellationToken)
    {
        if (!resize) return;
        await _admitted.Task.WaitAsync(cancellationToken);
        await session.ControlAsync(Ma2aServiceRequests.Resize(checked((ushort)size.Columns), checked((ushort)size.Rows)), cancellationToken);
    }

    public async Task EndInputAsync(CancellationToken cancellationToken)
    {
        if (!writable) return;
        await _admitted.Task.WaitAsync(cancellationToken);
        await session.EndAsync(cancellationToken: cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _admitted.TrySetCanceled();
        await session.DisposeAsync();
    }
}
