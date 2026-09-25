using Dsh.Account;

namespace Dsh.Tests;

public sealed class AccountOriginsTests
{
    [Fact]
    public void PlatformOrigin_AcceptsHttps()
        => Assert.Equal("https://platform.deepseek.com", AccountOrigins.PlatformOrigin("https://platform.deepseek.com", false));

    [Fact]
    public void PlatformOrigin_RejectsCredentialsPathQueryFragmentAndHttp()
    {
        Assert.Throws<AccountException>(() => AccountOrigins.PlatformOrigin("https://u:p@platform.deepseek.com", false));
        Assert.Throws<AccountException>(() => AccountOrigins.PlatformOrigin("https://platform.deepseek.com/x", false));
        Assert.Throws<AccountException>(() => AccountOrigins.PlatformOrigin("https://platform.deepseek.com?a=1", false));
        Assert.Throws<AccountException>(() => AccountOrigins.PlatformOrigin("https://platform.deepseek.com#f", false));
        Assert.Throws<AccountException>(() => AccountOrigins.PlatformOrigin("http://platform.deepseek.com", false));
    }

    [Fact]
    public void PlatformOrigin_AllowsLoopbackHttpOnlyWhenOptedIn()
    {
        Assert.Throws<AccountException>(() => AccountOrigins.PlatformOrigin("http://127.0.0.1:9000", false));
        Assert.Equal("http://127.0.0.1:9000", AccountOrigins.PlatformOrigin("http://127.0.0.1:9000", true));
        Assert.Throws<AccountException>(() => AccountOrigins.PlatformOrigin("http://example.com", true));
    }

    [Fact]
    public void BrowserUrl_RejectsForeignOriginWrongPathAndCredentials()
    {
        const string origin = "https://platform.deepseek.com";
        Assert.Equal(origin + "/dsh/authorize?state=s",
            AccountOrigins.BrowserUrl(origin + "/dsh/authorize?state=s", origin, AccountOrigins.AuthorizePath));
        Assert.Throws<AccountException>(() => AccountOrigins.BrowserUrl("https://evil.test/dsh/authorize", origin, AccountOrigins.AuthorizePath));
        Assert.Throws<AccountException>(() => AccountOrigins.BrowserUrl(origin + "/dsh/other", origin, AccountOrigins.AuthorizePath));
        Assert.Throws<AccountException>(() => AccountOrigins.BrowserUrl(origin + "/dsh/authorize#f", origin, AccountOrigins.AuthorizePath));
        Assert.Throws<AccountException>(() => AccountOrigins.BrowserUrl("https://u:p@platform.deepseek.com/dsh/authorize", origin, AccountOrigins.AuthorizePath));
    }

    [Fact]
    public void LoginOrigin_RequiresExplicitLoopbackPort()
    {
        Assert.Equal("http://127.0.0.1:43123", AccountOrigins.LoginOrigin("http://127.0.0.1:43123"));
        Assert.Throws<AccountException>(() => AccountOrigins.LoginOrigin("http://127.0.0.1"));
        Assert.Throws<AccountException>(() => AccountOrigins.LoginOrigin("http://example.com:80"));
        Assert.Throws<AccountException>(() => AccountOrigins.LoginOrigin("http://127.0.0.1:80/x"));
    }

    [Fact]
    public void InferenceOrigin_AcceptsOriginAndRejectsPaths()
    {
        Assert.Equal("https://api.deepseek.com", AccountOrigins.InferenceOrigin("https://api.deepseek.com"));
        Assert.Throws<AccountException>(() => AccountOrigins.InferenceOrigin("https://api.deepseek.com/v1"));
    }
}
