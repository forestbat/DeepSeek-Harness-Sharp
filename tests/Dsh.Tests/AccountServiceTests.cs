using System.Net;
using System.Text.Json;
using Dsh.Account;
using Dsh.Boot;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Core;

namespace Dsh.Tests;

public sealed class AccountServiceTests
{
    private const string Origin = "https://platform.test";

    private static readonly HttpClient CallbackClient = new();

    [Fact]
    public async Task SignIn_RunsPkceFlow_StoresGrantAndReadsDetails()
    {
        using var home = new AccountTempHome();
        var cancellationToken = TestContext.Current.CancellationToken;
        string? callbackUrl = null;
        Task<HttpResponseMessage>? callbackTask = null;
        var opened = new List<string>();
        string? challenge = null;
        var handler = new AccountHttpHandler(request =>
        {
            switch (request.Path)
            {
                case "/auth-api/v0/dsh/auth_init":
                    var init = JsonDocument.Parse(request.Body!).RootElement;
                    challenge = init.GetProperty("code_challenge").GetString();
                    callbackUrl = $"{init.GetProperty("redirect_uri").GetString()}?code=code-123&state={init.GetProperty("state").GetString()}";
                    return AccountHttpHandler.Json(AccountHttpHandler.Envelope(
                        "{\"authorize_url\":\"" + Origin + "/dsh/authorize?x=1\",\"authorize_id\":\"auth-1\",\"expires_in\":300}"));
                case "/auth-api/v0/dsh/auth_exchange":
                    var exchange = JsonDocument.Parse(request.Body!).RootElement;
                    Assert.Equal(challenge, AccountPkce.Challenge(exchange.GetProperty("code_verifier").GetString()!));
                    Assert.Equal(AccountDeviceInfo.Model(), exchange.GetProperty("device_model").GetString());
                    return AccountHttpHandler.Json(AccountHttpHandler.Envelope(
                        "{\"token\":\"grant-token\",\"authorized_url\":\"" + Origin + "/dsh/authorized\",\"user\":{\"id\":\"u-1\",\"mobile\":\"13812345678\",\"id_profile\":{\"name\":\"小明\"}}}"));
                case "/auth-api/v0/users/current":
                    Assert.Equal("grant-token", request.Headers["x-dsh-auth-token"]);
                    return AccountHttpHandler.Json(AccountHttpHandler.Envelope(
                        """{"id":"u-1","mobile":"13812345678","id_profile":{"name":"小明"}}"""));
                case "/api/v0/users/get_user_summary":
                    return AccountHttpHandler.Json(AccountHttpHandler.Envelope(
                        """{"normal_wallets":[{"currency":"CNY","balance":"12.3400"}],"bonus_wallets":[{"currency":"CNY","balance":"5.00"}]}"""));
                default:
                    return AccountHttpHandler.Json(AccountHttpHandler.Envelope("{}"));
            }
        });
        using var service = new AccountService(new Context(), Config(), home.Home, handler, url =>
        {
            opened.Add(url);
            if (callbackTask is null && callbackUrl is not null)
                callbackTask = CallbackClient.GetAsync(callbackUrl, cancellationToken);
            return true;
        });

        var profile = await service.SignInAsync(cancellationToken);
        Assert.NotNull(callbackTask);
        using var callbackResponse = await callbackTask;
        Assert.Equal(HttpStatusCode.OK, callbackResponse.StatusCode);
        var balance = await service.GetBalanceAsync(cancellationToken);

        Assert.NotNull(profile);
        Assert.Equal("小明", profile.Name);
        Assert.Equal("138****5678", profile.MaskedContact);
        Assert.Equal("12.3400", Assert.Single(balance!.NormalWallets).Balance);
        Assert.Equal(AccountPhase.SignedIn, service.Snapshot.Phase);
        Assert.Contains(opened, url => url.Contains("/dsh/authorize", StringComparison.Ordinal));
        Assert.Contains(opened, url => url.Contains("login_source=desktop", StringComparison.Ordinal));
        Assert.Equal("grant-token", new AccountStore(home.Home).ReadGrant(Origin)!.Token);
        Assert.True(File.Exists(new AccountStore(home.Home).DevicePath));
    }

    [Fact]
    public async Task CancelAsync_StopsWaitingAndSendsAuthCancel()
    {
        using var home = new AccountTempHome();
        var handler = new AccountHttpHandler(request => request.Path switch
        {
            "/auth-api/v0/dsh/auth_init" => AccountHttpHandler.Json(AccountHttpHandler.Envelope(
                $$"""{"authorize_url":"{{Origin}}/dsh/authorize","authorize_id":"auth-9","expires_in":300}""")),
            _ => AccountHttpHandler.Json(AccountHttpHandler.Envelope("{}")),
        });
        using var service = new AccountService(new Context(), Config(), home.Home, handler, _ => true);

        var task = service.SignInAsync(TestContext.Current.CancellationToken);
        await WaitForAsync(() => service.Snapshot.Phase == AccountPhase.WaitingBrowser);
        await service.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(AccountPhase.Cancelled, service.Snapshot.Phase);
        Assert.Contains(handler.Requests, request => request.Path == "/auth-api/v0/dsh/auth_cancel");
    }

    [Fact]
    public async Task SignOut_DeletesLocalGrant_AndRevokesRemotelyWithRetry()
    {
        using var home = new AccountTempHome();
        var store = new AccountStore(home.Home);
        store.WriteGrant(new AccountGrant(1, "grant-token", Origin));
        var logoutAttempts = 0;
        var handler = new AccountHttpHandler(request =>
        {
            if (request.Path != "/auth-api/v0/users/logout")
                return AccountHttpHandler.Json(AccountHttpHandler.Envelope("{}"));
            logoutAttempts++;
            return logoutAttempts < 2
                ? AccountHttpHandler.Json("{}", HttpStatusCode.InternalServerError)
                : AccountHttpHandler.Json(AccountHttpHandler.Envelope("{}"));
        });
        using var service = new AccountService(new Context(), Config(), home.Home, handler, _ => true);

        await service.SignOutAsync();

        Assert.False(File.Exists(store.GrantPath));
        Assert.Equal(AccountPhase.SignedOut, service.Snapshot.Phase);
        await WaitForAsync(() => logoutAttempts >= 2);
        Assert.Contains(handler.Requests, request =>
            request.Path == "/auth-api/v0/users/logout" && request.Headers["x-dsh-auth-token"] == "grant-token");
    }

    [Fact]
    public async Task GetProfile_Unauthorized_ClearsGrantAndRaisesSessionExpired()
    {
        using var home = new AccountTempHome();
        var store = new AccountStore(home.Home);
        store.WriteGrant(new AccountGrant(1, "grant-token", Origin));
        var expired = false;
        var handler = new AccountHttpHandler(_ => AccountHttpHandler.Json("""{"code":40003}"""));
        using var service = new AccountService(new Context(), Config(), home.Home, handler, _ => true);
        service.SessionExpired += () => expired = true;

        var profile = await service.GetProfileAsync(TestContext.Current.CancellationToken);

        Assert.Null(profile);
        Assert.True(expired);
        Assert.False(File.Exists(store.GrantPath));
        Assert.Equal(AccountPhase.SignedOut, service.Snapshot.Phase);
    }

    /** 「用量」「充值」只有平台官网页面上有, 客户端只负责把这两个地址交给系统浏览器。 */
    [Fact]
    public void OpenPlatformPage_OpensTheOfficialUsageAndTopUpPages()
    {
        using var home = new AccountTempHome();
        var opened = new List<string>();
        using var service = new AccountService(new Context(), Config(), home.Home,
            new AccountHttpHandler(_ => AccountHttpHandler.Json("{}")), url =>
            {
                opened.Add(url);
                return true;
            });

        Assert.True(service.OpenPlatformPage(AccountPlatformPage.Usage));
        Assert.True(service.OpenPlatformPage(AccountPlatformPage.TopUp));

        Assert.Equal([$"{Origin}/usage", $"{Origin}/top_up"], opened);
    }

    /** 浏览器起不来时如实返回 false, 由 UI 提示用户手动访问。 */
    [Fact]
    public void OpenPlatformPage_ReportsFailureWhenTheBrowserCannotStart()
    {
        using var home = new AccountTempHome();
        using var service = new AccountService(new Context(), Config(), home.Home,
            new AccountHttpHandler(_ => AccountHttpHandler.Json("{}")), _ => false);

        Assert.False(service.OpenPlatformPage(AccountPlatformPage.Usage));
    }

    [Fact]
    public void InferenceToken_IsReleasedOnlyForTheConfiguredInferenceOrigin()
    {
        using var home = new AccountTempHome();
        new AccountStore(home.Home).WriteGrant(new AccountGrant(1, "grant-token", Origin));
        var ctx = new Context();
        using var service = new AccountService(ctx, Config(), home.Home,
            new AccountHttpHandler(_ => AccountHttpHandler.Json("{}")), _ => true);

        Assert.True(service.Covers(new Uri(AccountConfig.DefaultInferenceOrigin)));
        Assert.Equal("grant-token", service.TryGetToken(new Uri(AccountConfig.DefaultInferenceOrigin)));
        Assert.Null(service.TryGetToken(new Uri("https://api.deepseek.com.evil.test")));
        Assert.Null(service.TryGetToken(new Uri("https://proxy.test")));
        Assert.Null(service.TryGetToken(new Uri("http://api.deepseek.com")));
        Assert.False(service.Covers(new Uri("https://proxy.test")));
        Assert.Same(service, ctx.Get<IInferenceCredentials>(IInferenceCredentials.ServiceName, false));
    }

    [Fact]
    public void InferenceToken_RequiresSignedInGrant()
    {
        using var home = new AccountTempHome();
        using var service = new AccountService(new Context(), Config(), home.Home,
            new AccountHttpHandler(_ => AccountHttpHandler.Json("{}")), _ => true);

        Assert.Null(service.TryGetToken(new Uri(AccountConfig.DefaultInferenceOrigin)));
    }

    [Fact]
    public void InferenceToken_RequiresMatchingIssuer()
    {
        using var home = new AccountTempHome();
        new AccountStore(home.Home).WriteGrant(new AccountGrant(1, "grant-token", "https://other.test"));
        using var service = new AccountService(new Context(), Config(), home.Home,
            new AccountHttpHandler(_ => AccountHttpHandler.Json("{}")), _ => true);

        Assert.True(service.Covers(new Uri(AccountConfig.DefaultInferenceOrigin)));
        Assert.Null(service.TryGetToken(new Uri(AccountConfig.DefaultInferenceOrigin)));
    }


    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 400 && !condition(); attempt++)
            await Task.Delay(5, TestContext.Current.CancellationToken);
        Assert.True(condition(), "condition not reached in time");
    }

    private static AccountConfig Config() => new()
    {
        PlatformOrigin = Origin,
        AttemptTimeoutMs = 10_000,
        RequestTimeoutMs = 5_000,
        LogoutRetryDelayMs = 1,
    };
}
