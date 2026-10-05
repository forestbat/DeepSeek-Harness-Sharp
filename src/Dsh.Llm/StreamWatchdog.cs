namespace Dsh.Llm;

/**
 * 给流式响应加 idle 与 lifetime 两个上界:
 * idle 每个 chunk 重置(含思考 delta), lifetime 只累计"阻塞在上游等待"的时间、不重置, 专抓滴流式永不完成的流。
 * 超时取消底层请求并产出一个可重试的 Timeout finish; 调用方取消时直接结束, 由调用链按取消处理。
 */
public sealed class StreamWatchdog : IDisposable
{
    private readonly LlmStreamLimits _limits;
    private readonly CancellationToken _user;
    private readonly CancellationTokenSource _linked;
    private bool _disposed;

    public StreamWatchdog(LlmStreamLimits limits, CancellationToken user)
    {
        _limits = limits;
        _user = user;
        _linked = CancellationTokenSource.CreateLinkedTokenSource(user);
    }

    /** 传给适配器的令牌: 超时或用户取消都会经它中止底层请求。 */
    public CancellationToken Token => _linked.Token;

    public async IAsyncEnumerable<StreamChunk> Guard(IAsyncEnumerable<StreamChunk> source)
    {
        var enumerator = source.GetAsyncEnumerator(Token);
        Task? outstanding = null;
        try
        {
            using var lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(Token);
            if (_limits.MaxLifetimeMs > 0)
                lifetimeCts.CancelAfter(_limits.MaxLifetimeMs);
            var lifetime = Task.Delay(Timeout.InfiniteTimeSpan, lifetimeCts.Token);
            while (true)
            {
                using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(Token);
                if (_limits.IdleTimeoutMs > 0)
                    idleCts.CancelAfter(_limits.IdleTimeoutMs);
                var idle = Task.Delay(Timeout.InfiniteTimeSpan, idleCts.Token);
                var move = enumerator.MoveNextAsync().AsTask();
                var winner = await Task.WhenAny(move, idle, lifetime);
                if (winner == move)
                {
                    bool has;
                    try
                    {
                        has = await move;
                    }
                    catch (OperationCanceledException) when (Token.IsCancellationRequested)
                    {
                        yield break;
                    }
                    if (!has)
                        yield break;
                    yield return enumerator.Current;
                    continue;
                }
                outstanding = move;
                Observe(move);
                _linked.Cancel();
                if (_user.IsCancellationRequested)
                    yield break;
                var detail = winner == lifetime
                    ? $"stream exceeded the {_limits.MaxLifetimeMs}ms lifetime cap"
                    : $"stream produced no data for {_limits.IdleTimeoutMs}ms";
                yield return new StreamChunk.Finish(new FinishReason.Error(new LlmFailure(detail, LlmFailureCodes.Timeout)));
                yield break;
            }
        }
        finally
        {
            await ReleaseAsync(enumerator, outstanding);
        }
    }

    /** 编译器的异步迭代器在有未决 MoveNextAsync 时释放会抛 NotSupportedException, 并且不跑上游 finally。
     *  超时路径上的 move 随令牌取消落定, 落定后再释放; 上游无视取消时交给后台任务, 不阻塞收尾。 */
    /** 编译器的异步迭代器在有未决 MoveNextAsync 时释放会抛 NotSupportedException, 并且不跑上游 finally。
     *  超时路径上的 move 随令牌取消落定, 落定后再释放; 上游无视取消时把释放交给后台任务后立即返回, 绝不阻塞收尾。 */
    private static ValueTask ReleaseAsync(IAsyncEnumerator<StreamChunk> enumerator, Task? outstanding)
    {
        if (outstanding is null || outstanding.IsCompleted)
            return enumerator.DisposeAsync();
        _ = ReleaseWhenSettledAsync(enumerator, outstanding);
        return ValueTask.CompletedTask;
    }

    private static async Task ReleaseWhenSettledAsync(IAsyncEnumerator<StreamChunk> enumerator, Task outstanding)
    {
        try
        {
            await outstanding;
        }
        catch (Exception)
        {
            // 上游的取消/错误已在超时路径上处理, 这里只等它可以被释放
        }
        try
        {
            await enumerator.DisposeAsync();
        }
        catch (Exception)
        {
            // 释放发生在收尾路径, 已没有可报告的对象
        }
    }

    private static void Observe(Task task)
        => _ = task.ContinueWith(
            static completed => { _ = completed.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _linked.Cancel();
        _linked.Dispose();
    }
}
