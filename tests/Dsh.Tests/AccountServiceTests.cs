using System.Net;
using System.Text.Json;
using Dsh.Account;
using Dsh.Boot;
using Dsh.Llm;
using Dsh.Runtime;

namespace Dsh.Tests;

public sealed class AccountServiceTests
{
    private const string Origin = "https://platform.test";

    private static readonly HttpClient CallbackClient = new();

    [Fact]
    public async Task SignIn_RunsPkceFlow_StoresGrantAndReadsDetails()
    {
        using var home = new AccountTempHome();
        var opened = new List<string>();
        string? challenge = null;
        var handler = new AccountHttpHandler(request =>
        {
            switch (request.Path)
            {
                case "/auth-api/v0/dsh/auth_init":
                    var init = JsonDocument.Parse(request.Body!).RootElement;
                    challenge = init.GetProperty("code_challenge").GetString();
                    StartCallback(init.GetProperty("redirect_uri").GetString()!, "code-123", init.GetProperty("state").GetString()!);
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
        using var service = new AccountService(new Context(), Config(enabled: true), home.Home, handler, url => { opened.Add(url); return true; });

        var profile = await service.SignInAsync(TestContext.Current.CancellationToken);
        var balance = await service.GetBalanceAsync(TestContext.Current.CancellationToken);

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
        using var service = new AccountService(new Context(), Config(enabled: true), home.Home, handler, _ => true);

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
        using var service = new AccountService(new Context(), Config(enabled: true), home.Home, handler, _ => true);

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
        using var service = new AccountService(new Context(), Config(enabled: true), home.Home, handler, _ => true);
        service.SessionExpired += () => expired = true;

        var profile = await service.GetProfileAsync(TestContext.Current.CancellationToken);

        Assert.Null(profile);
        Assert.True(expired);
        Assert.False(File.Exists(store.GrantPath));
        Assert.Equal(AccountPhase.SignedOut, service.Snapshot.Phase);
    }

    [Fact]
    public async Task SignIn_WhenDisabled_ThrowsDisabled()
    {
        using var home = new AccountTempHome();
        using var service = new AccountService(new Context(), Config(enabled: false), home.Home,
            new AccountHttpHandler(_ => AccountHttpHandler.Json("{}")), _ => true);

        var error = await Assert.ThrowsAsync<AccountException>(() => service.SignInAsync(TestContext.Current.CancellationToken));
        Assert.Equal(AccountFailure.Disabled, error.Failure);
    }

    [Fact]
    public void SetEnabled_PersistsToPluginParameters()
    {
        using var home = new AccountTempHome();
        using var service = new AccountService(new Context(), Config(enabled: false), home.Home,
            new AccountHttpHandler(_ => AccountHttpHandler.Json("{}")), _ => true);

        service.SetEnabled(true);

        Assert.True(service.Enabled);
        var parameters = HarnessSettings.Load(home.Home).Plugins["@deepseek-ai/dsh-account"].Parameters;
        Assert.Equal(true, parameters["accountEnabled"]);
    }

    [Fact]
    public void InferenceToken_IsReleasedOnlyForTheConfiguredInferenceOrigin()
    {
        using var home = new AccountTempHome();
        new AccountStore(home.Home).WriteGrant(new AccountGrant(1, "grant-token", Origin));
        var ctx = new Context();
        using var service = new AccountService(ctx, Config(enabled: true), home.Home,
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
        using var service = new AccountService(new Context(), Config(enabled: true), home.Home,
            new AccountHttpHandler(_ => AccountHttpHandler.Json("{}")), _ => true);

        Assert.Null(service.TryGetToken(new Uri(AccountConfig.DefaultInferenceOrigin)));
    }

    [Fact]
    public void InferenceToken_IsNotReleasedWhenDisabled()
    {
        using var home = new AccountTempHome();
        new AccountStore(home.Home).WriteGrant(new AccountGrant(1, "grant-token", Origin));
        using var service = new AccountService(new Context(), Config(enabled: false), home.Home,
            new AccountHttpHandler(_ => AccountHttpHandler.Json("{}")), _ => true);

        Assert.False(service.Covers(new Uri(AccountConfig.DefaultInferenceOrigin)));
        Assert.Null(service.TryGetToken(new Uri(AccountConfig.DefaultInferenceOrigin)));
    }

    [Fact]
    public void InferenceToken_RequiresMatchingIssuer()
    {
        using var home = new AccountTempHome();
        new AccountStore(home.Home).WriteGrant(new AccountGrant(1, "grant-token", "https://other.test"));
        using var service = new AccountService(new Context(), Config(enabled: true), home.Home,
            new AccountHttpHandler(_ => AccountHttpHandler.Json("{}")), _ => true);

        Assert.True(service.Covers(new Uri(AccountConfig.DefaultInferenceOrigin)));
        Assert.Null(service.TryGetToken(new Uri(AccountConfig.DefaultInferenceOrigin)));
    }

    private static void StartCallback(string redirectUri, string code, string state)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                _ = await CallbackClient.GetAsync($"{redirectUri}?code={code}&state={state}");
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
            {
            }
        });
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 400 && !condition(); attempt++)
            await Task.Delay(5, TestContext.Current.CancellationToken);
        Assert.True(condition(), "condition not reached in time");
    }

    private static AccountConfig Config(bool enabled) => new()
    {
        PlatformOrigin = Origin,
        Enabled = enabled,
        AttemptTimeoutMs = 10_000,
        RequestTimeoutMs = 5_000,
        LogoutRetryDelayMs = 1,
    };
}
