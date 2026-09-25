using Dsh.Account;

namespace Dsh.Tests;

public sealed class LogoutRetrierTests
{
    [Fact]
    public async Task RetriesUntilRemoteRevocationSucceeds()
    {
        var attempts = 0;
        await LogoutRetrier.RunAsync(_ =>
        {
            attempts++;
            return attempts < 3 ? Task.FromException(new AccountException(AccountFailure.Network)) : Task.CompletedTask;
        }, new LogoutRetryPolicy(5, 1, 1_000), TestContext.Current.CancellationToken);

        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task StopsAfterMaxRetries()
    {
        var attempts = 0;
        await LogoutRetrier.RunAsync(_ =>
        {
            attempts++;
            return Task.FromException(new AccountException(AccountFailure.Network));
        }, new LogoutRetryPolicy(5, 1, 1_000), TestContext.Current.CancellationToken);

        Assert.Equal(6, attempts);
    }

    [Fact]
    public async Task CancelledLifetime_DoesNotAttempt()
    {
        var attempts = 0;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await LogoutRetrier.RunAsync(_ =>
        {
            attempts++;
            return Task.CompletedTask;
        }, new LogoutRetryPolicy(5, 1, 1_000), cancelled.Token);

        Assert.Equal(0, attempts);
    }
}
