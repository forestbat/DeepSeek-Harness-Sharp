using Dsh.Account;
using Dsh.Gui.ViewModels;

namespace Dsh.Tests;

public sealed class AccountPanelViewModelTests
{
    [Fact]
    public void UnavailableService_DisablesSignIn()
    {
        var panel = new AccountPanelViewModel(null);

        Assert.False(panel.IsAvailable);
        Assert.False(panel.CanSignIn);
        Assert.False(panel.CanCancel);
        Assert.True(panel.IsSignedOut);
    }

    [Fact]
    public void ToggleEnabled_FlowsToService_AndUnlocksSignIn()
    {
        var service = new FakeAccountService { Enabled = false };
        var panel = new AccountPanelViewModel(service);
        Assert.False(panel.CanSignIn);

        panel.IsEnabled = true;

        Assert.True(service.Enabled);
        Assert.True(panel.CanSignIn);
    }

    [Fact]
    public async Task SignIn_Success_ShowsMaskedProfileAndBalance()
    {
        var service = new FakeAccountService
        {
            SignInHandler = _ => Task.FromResult<AccountProfile?>(new AccountProfile("u-1", "小明", null, "13812345678")),
            BalanceHandler = _ => Task.FromResult<AccountBalance?>(new AccountBalance([new AccountWallet("CNY", "12.34")], [])),
        };
        var panel = new AccountPanelViewModel(service);

        await panel.SignInCommand.ExecuteAsync(null);

        Assert.True(panel.IsSignedIn);
        Assert.Equal("小明", panel.DisplayName);
        Assert.Equal("138****5678", panel.MaskedContact);
        Assert.Equal("小", panel.Initial);
        Assert.Contains("12.34", panel.BalanceText);
    }

    [Fact]
    public async Task SignIn_Failure_ShowsFailureText()
    {
        var service = new FakeAccountService
        {
            SignInHandler = _ => Task.FromException<AccountProfile?>(new AccountException(AccountFailure.Network)),
        };
        var panel = new AccountPanelViewModel(service);

        await panel.SignInCommand.ExecuteAsync(null);

        Assert.True(panel.IsFailed);
        Assert.Equal("网络不可用或平台未响应", panel.Status);
    }

    [Fact]
    public async Task SignIn_Cancelled_ShowsCancelled()
    {
        var started = new TaskCompletionSource();
        var service = new FakeAccountService
        {
            SignInHandler = async cancellationToken =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return null;
            },
        };
        var panel = new AccountPanelViewModel(service);

        var signIn = panel.SignInCommand.ExecuteAsync(null);
        await started.Task;
        Assert.True(panel.IsWaiting);
        Assert.True(panel.CanCancel);

        await panel.CancelCommand.ExecuteAsync(null);
        await signIn;

        Assert.True(panel.IsCancelled);
    }

    [Fact]
    public async Task SignOut_ReturnsToSignedOut()
    {
        var service = new FakeAccountService
        {
            SignInHandler = _ => Task.FromResult<AccountProfile?>(new AccountProfile("u-1", "小明", null, "13812345678")),
        };
        var panel = new AccountPanelViewModel(service);
        await panel.SignInCommand.ExecuteAsync(null);
        Assert.True(panel.IsSignedIn);

        await panel.SignOutCommand.ExecuteAsync(null);

        Assert.True(panel.IsSignedOut);
        Assert.Equal("", panel.DisplayName);
    }
}
