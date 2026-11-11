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
    public void SignedOut_AllowsSignIn_WithoutAnyEnableSwitch()
    {
        var service = new FakeAccountService();
        var panel = new AccountPanelViewModel(service);

        Assert.True(panel.CanSignIn);
        Assert.True(panel.IsSignedOut);
        Assert.Equal("登录到 DeepSeek", panel.TitleText);
    }

    /** 标题栏按钮始终在, 已登录时换成账号名: 它不会因为登录成功而消失。 */
    [Fact]
    public async Task TitleButton_StaysAfterSignIn_AndShowsTheAccountName()
    {
        var service = new FakeAccountService
        {
            SignInHandler = _ => Task.FromResult<AccountProfile?>(new AccountProfile("u-1", "小明", null, "13812345678")),
        };
        var panel = new AccountPanelViewModel(service);

        await panel.TitleCommand.ExecuteAsync(null);

        Assert.True(panel.IsSignedIn);
        Assert.Equal("小明", panel.TitleText);
    }

    /** 未登录点标题栏按钮 = 直接登录, 不需要先去设置页。 */
    [Fact]
    public async Task TitleButton_SignedOut_StartsTheBrowserSignIn()
    {
        var signedIn = new TaskCompletionSource();
        var service = new FakeAccountService
        {
            SignInHandler = _ =>
            {
                signedIn.TrySetResult();
                return Task.FromResult<AccountProfile?>(new AccountProfile("u-1", "小明", null, "13812345678"));
            },
        };
        var panel = new AccountPanelViewModel(service);

        await panel.TitleCommand.ExecuteAsync(null);

        Assert.True(signedIn.Task.IsCompleted);
        Assert.True(panel.IsSignedIn);
    }

    /** 已登录点标题栏按钮 = 打开平台个人页(用量/余额), 不退登录, 也不进设置页。 */
    [Fact]
    public async Task TitleButton_SignedIn_OpensThePlatformPage()
    {
        var service = new FakeAccountService
        {
            SignInHandler = _ => Task.FromResult<AccountProfile?>(new AccountProfile("u-1", "小明", null, "13812345678")),
        };
        var panel = new AccountPanelViewModel(service);
        await panel.SignInCommand.ExecuteAsync(null);
        Assert.True(panel.IsSignedIn);

        await panel.TitleCommand.ExecuteAsync(null);

        Assert.Equal([AccountPlatformPage.Usage], service.OpenedPages);
        Assert.True(panel.IsSignedIn);
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
