using System.Net;
using System.Text;
using Dsh.Account;

namespace Dsh.Tests;

public sealed class AccountProtocolTests
{
    [Fact]
    public async Task AuthInit_ParsesResult()
    {
        var handler = new AccountHttpHandler(_ => AccountHttpHandler.Json(
            AccountHttpHandler.Envelope("""{"authorize_url":"https://platform.test/dsh/authorize?x=1","authorize_id":"auth-1","expires_in":300}""")));
        var client = Client(handler);

        var result = await client.AuthInitAsync(new AuthInitRequest("challenge", "state", "http://127.0.0.1:1/oauth/callback", "zh_CN", "desktop"),
            TestContext.Current.CancellationToken);

        Assert.Equal("auth-1", result.AuthorizeId);
        Assert.Equal(300, result.ExpiresIn);
    }

    [Fact]
    public async Task NonZeroTopLevelCode_IsProtocolFailure()
    {
        var handler = new AccountHttpHandler(_ => AccountHttpHandler.Json("""{"code":1,"data":{"biz_code":0,"biz_data":{}}}"""));
        await Assert.ThrowsAsync<AccountException>(() => Client(handler).AuthInitAsync(Request(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NonZeroBizCode_IsProtocolFailure()
    {
        var handler = new AccountHttpHandler(_ => AccountHttpHandler.Json("""{"code":0,"data":{"biz_code":7,"biz_data":{}}}"""));
        await Assert.ThrowsAsync<AccountException>(() => Client(handler).AuthInitAsync(Request(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TopLevel40003_WithToken_IsExpired()
    {
        var handler = new AccountHttpHandler(_ => AccountHttpHandler.Json("""{"code":40003,"msg":"Authorization Failed"}"""));
        var error = await Assert.ThrowsAsync<AccountException>(() => Client(handler).GetCurrentUserAsync("token", TestContext.Current.CancellationToken));
        Assert.Equal(AccountFailure.Expired, error.Failure);
    }

    [Fact]
    public async Task Http401_WithToken_IsExpired()
    {
        var handler = new AccountHttpHandler(_ => AccountHttpHandler.Json("{}", HttpStatusCode.Unauthorized));
        var error = await Assert.ThrowsAsync<AccountException>(() => Client(handler).GetCurrentUserAsync("token", TestContext.Current.CancellationToken));
        Assert.Equal(AccountFailure.Expired, error.Failure);
    }

    [Fact]
    public async Task Redirect_IsProtocolFailure()
    {
        var handler = new AccountHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://elsewhere.test") } });
        var error = await Assert.ThrowsAsync<AccountException>(() => Client(handler).AuthInitAsync(Request(), TestContext.Current.CancellationToken));
        Assert.Equal(AccountFailure.Protocol, error.Failure);
    }

    [Fact]
    public async Task OversizeBody_IsProtocolFailure()
    {
        var handler = new AccountHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new string('a', 70_000), Encoding.UTF8, "application/json"),
        });
        var error = await Assert.ThrowsAsync<AccountException>(() => Client(handler).GetCurrentUserAsync("token", TestContext.Current.CancellationToken));
        Assert.Equal(AccountFailure.Protocol, error.Failure);
    }

    [Fact]
    public async Task ClientHeaders_AreTheFiveIdentityHeaders()
    {
        var handler = new AccountHttpHandler(_ => AccountHttpHandler.Json(AccountHttpHandler.Envelope("{}")));
        _ = await Client(handler).GetCurrentUserAsync("token", TestContext.Current.CancellationToken);

        var headers = handler.Requests[0].Headers;
        Assert.Equal("", headers["x-client-bundle-id"]);
        Assert.Equal("web", headers["x-client-platform"]);
        Assert.Equal("1.2.3", headers["x-client-version"]);
        Assert.Equal("zh_CN", headers["x-client-locale"]);
        Assert.True(long.TryParse(headers["x-client-timezone-offset"], out _));
        Assert.False(headers.ContainsKey("authorization"));
        Assert.Equal("token", headers["x-dsh-auth-token"]);
    }

    [Fact]
    public async Task Logout_UsesUsersLogoutPath()
    {
        var handler = new AccountHttpHandler(_ => AccountHttpHandler.Json(AccountHttpHandler.Envelope("{}")));
        _ = await Client(handler).LogoutAsync("token", TestContext.Current.CancellationToken);
        Assert.Equal("/auth-api/v0/users/logout", handler.Requests[0].Path);
        Assert.Equal("POST", handler.Requests[0].Method);
    }

    private static AuthInitRequest Request()
        => new("challenge", "state", "http://127.0.0.1:1/oauth/callback", "zh_CN", "desktop");

    private static PlatformClient Client(AccountHttpHandler handler)
        => new(handler, "https://platform.test", "web", "1.2.3", "zh_CN", 5_000, disposeHandler: false);
}
