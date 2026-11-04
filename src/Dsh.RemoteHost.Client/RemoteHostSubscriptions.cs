using System.Runtime.CompilerServices;

namespace Dsh.RemoteHost;

/** 连接失效后 Closed 才完成，此时下一次获取租约不会再返回旧连接。 */
public readonly record struct HostLease(RemoteHostClient Client, CancellationToken Lifetime, Task Closed);

/** 会话历史由 Host 提供，客户端每个读者只持有下一条事件序号。 */
public sealed class RemoteHostSubscriptions(Func<CancellationToken, Task<HostLease>> connect, CancellationToken lifetime)
{
    public async IAsyncEnumerable<RemoteSessionEvent> SubscribeAsync(string sessionId, long fromSeq, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var owner = CancellationTokenSource.CreateLinkedTokenSource(lifetime, cancellationToken);
        var next = fromSeq;
        while (true)
        {
            var lease = await connect(owner.Token).ConfigureAwait(false);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(owner.Token, lease.Lifetime);
            await using var reader = lease.Client.SubscribeAsync(sessionId, next, linked.Token).GetAsyncEnumerator(linked.Token);
            while (true)
            {
                bool available;
                try
                {
                    available = await reader.MoveNextAsync().ConfigureAwait(false);
                }
                catch (Exception error) when (!owner.IsCancellationRequested && error is OperationCanceledException or IOException or ObjectDisposedException)
                {
                    break;
                }
                if (!available)
                    break;
                var item = reader.Current;
                next = item.Reset ? 0 : (item.Event?.Seq ?? next - 1) + 1;
                yield return item;
            }
            await lease.Closed.WaitAsync(owner.Token).ConfigureAwait(false);
        }
    }

    public async Task AttachPtyAsync(string ptyId, Stream input, Stream output, CancellationToken cancellationToken,
        Func<IWorkspaceHost, CancellationToken, Task>? prepare = null)
    {
        using var owner = CancellationTokenSource.CreateLinkedTokenSource(lifetime, cancellationToken);
        while (true)
        {
            var lease = await connect(owner.Token).ConfigureAwait(false);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(owner.Token, lease.Lifetime);
            try
            {
                if (prepare is not null)
                    await prepare(lease.Client, linked.Token).ConfigureAwait(false);
                await lease.Client.Pty.AttachPtyAsync(ptyId, input, output, linked.Token).ConfigureAwait(false);
                return;
            }
            catch (Exception error) when (!owner.IsCancellationRequested && error is OperationCanceledException or IOException or ObjectDisposedException)
            {
            }
            await lease.Closed.WaitAsync(owner.Token).ConfigureAwait(false);
        }
    }
}

