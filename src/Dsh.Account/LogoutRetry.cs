namespace Dsh.Account;

/** 退出撤销策略: 首次失败后最多重试 MaxRetries 次, 每次延迟翻倍。 */
public sealed record LogoutRetryPolicy(int MaxRetries, int DelayMs, int RequestTimeoutMs);

/** 独立于本地状态的远程撤销: 失败只消耗一次尝试, 绝不恢复已删除的凭据。 */
public static class LogoutRetrier
{
    public static async Task RunAsync(Func<CancellationToken, Task> revoke, LogoutRetryPolicy policy, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt <= policy.MaxRetries; attempt++)
        {
            if (cancellationToken.IsCancellationRequested)
                return;
            if (attempt > 0)
            {
                try
                {
                    await Task.Delay(policy.DelayMs * (1 << (attempt - 1)), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
            try
            {
                await revoke(cancellationToken);
                return;
            }
            catch (Exception)
            {
                // 远程撤销失败只消耗一次尝试; 本地凭据已删除, 绝不恢复。
                if (cancellationToken.IsCancellationRequested)
                    return;
            }
        }
    }
}
