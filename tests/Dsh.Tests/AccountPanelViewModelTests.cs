using Dsh.Account;
using Dsh.Gui.ViewModels;
using Dsh.Core;

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
    public void SignedOut_AllowsSignIn_WithoutAnyEnableSwitch()
    {
        var service = new FakeAccountService();
        var panel = new AccountPanelViewModel(service);

        Assert.True(panel.CanSignIn);
        Assert.True(panel.CanOfferSignIn);
        Assert.True(panel.IsSignedOut);
    }

    [Fact]
    public async Task SignedIn_HidesTheTitleBarSignInHint()
    {
        var service = new FakeAccountService
        {
            SignInHandler = _ => Task.FromResult<AccountProfile?>(new AccountProfile("u-1", "小明", null, "13812345678")),
        };
        var panel = new AccountPanelViewModel(service);

        await panel.SignInCommand.ExecuteAsync(null);

        Assert.True(panel.IsSignedIn);
        Assert.False(panel.CanOfferSignIn);
    }

    /** 「更多账号信息」「查询用量」「充值」都跳官方页面: 客户端不自造用量与充值界面。 */
    [Fact]
    public void OfficialPageCommands_OpenUsageAndTopUp()
    {
        var service = new FakeAccountService();
        var panel = new AccountPanelViewModel(service);

        panel.OpenUsageCommand.Execute(null);
        panel.OpenTopUpCommand.Execute(null);

        Assert.Equal([AccountPlatformPage.Usage, AccountPlatformPage.TopUp], service.OpenedPages);
        Assert.Equal("", panel.Status);
    }

    [Fact]
    public void BrowserFailure_ShowsAStatusInsteadOfSilence()
    {
        var service = new FakeAccountService { OpenBrowser = false };
        var panel = new AccountPanelViewModel(service);

        panel.OpenUsageCommand.Execute(null);

        Assert.Contains("platform.deepseek.com", panel.Status);
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
        Assert.Equal("¥12.34", panel.BalanceText);
        Assert.True(panel.HasBalance);
        Assert.False(panel.HasBonus);
    }

    /** 赠金单独一行: 与充值余额不是同一笔钱, 不能混在一个数字里。 */
    [Fact]
    public async Task SignIn_WithBonusWallet_ShowsBonusSeparately()
    {
        var service = new FakeAccountService
        {
            SignInHandler = _ => Task.FromResult<AccountProfile?>(new AccountProfile("u-1", "小明", null, "13812345678")),
            BalanceHandler = _ => Task.FromResult<AccountBalance?>(
                new AccountBalance([new AccountWallet("CNY", "0.42")], [new AccountWallet("CNY", "5.00")])),
        };
        var panel = new AccountPanelViewModel(service);

        await panel.SignInCommand.ExecuteAsync(null);

        Assert.Equal("¥0.42", panel.BalanceText);
        Assert.Equal("赠金 ¥5.00", panel.BonusText);
        Assert.True(panel.HasBonus);
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
