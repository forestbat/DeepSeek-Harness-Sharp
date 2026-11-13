using Dsh.Llm;
using Dsh.Runtime;

namespace Dsh.Tests;

/** 未配 API key 时按目标 origin 取账号令牌的接缝, 以及两种凭据的请求头形态。 */
public class AccountInferenceCredentialTests
{
    private const string InferenceOrigin = "https://api.deepseek.com";

    [Fact]
    public void AccountToken_IsUsedWhenTheOriginMatches()
    {
        var ctx = new Context();
        ctx.Provide(IInferenceCredentials.ServiceName, new FakeCredentials(InferenceOrigin, "account-token"));

        var (credential, covered) = InferenceCredentials.Resolve(ctx, $"{InferenceOrigin}/v1");

        Assert.Equal("account-token", credential!.Token);
        Assert.Equal(LlmCredentialScheme.DshAuthToken, credential.Scheme);
        Assert.True(covered);
    }

    [Fact]
    public void AccountToken_IsNotUsedForAnotherOrigin()
    {
        var ctx = new Context();
        ctx.Provide(IInferenceCredentials.ServiceName, new FakeCredentials(InferenceOrigin, "account-token"));

        var (credential, covered) = InferenceCredentials.Resolve(ctx, "https://proxy.test/v1");

        Assert.Null(credential);
        Assert.False(covered);
    }

    [Fact]
    public void CoveredOrigin_WithoutToken_ReportsNotSignedIn()
    {
        var ctx = new Context();
        ctx.Provide(IInferenceCredentials.ServiceName, new FakeCredentials(InferenceOrigin, null));

        var (credential, covered) = InferenceCredentials.Resolve(ctx, InferenceOrigin);

        Assert.Null(credential);
        Assert.True(covered);
    }

    [Fact]
    public void WithoutAccountService_KeepsPlainCredentialFailure()
    {
        var (credential, covered) = InferenceCredentials.Resolve(new Context(), InferenceOrigin);

        Assert.Null(credential);
        Assert.False(covered);
    }

    [Fact]
    public void InvalidBaseUrl_IsNotCovered()
    {
        var ctx = new Context();
        ctx.Provide(IInferenceCredentials.ServiceName, new FakeCredentials(InferenceOrigin, "account-token"));

        var (credential, covered) = InferenceCredentials.Resolve(ctx, "not a url");

        Assert.Null(credential);
        Assert.False(covered);
    }

    /** 账号令牌只能走 x-dsh-auth-token: 换成 Bearer 会被端点当作 API Key 判 401。 */
    [Fact]
    public void AccountCredential_UsesTheDshAuthTokenHeader()
    {
        var credential = new LlmCredential("account-token", LlmCredentialScheme.DshAuthToken);

        Assert.Equal(("x-dsh-auth-token", "account-token"), credential.Header());
    }

    [Fact]
    public void ApiKeyCredential_UsesBearer()
    {
        var credential = new LlmCredential("sk-test", LlmCredentialScheme.Bearer);

        Assert.Equal(("Authorization", "Bearer sk-test"), credential.Header());
    }

    /** 端点拒绝账号令牌时交给账号服务清登录态; API Key 被拒则不动账号。 */
    [Fact]
    public void Reject_ClearsOnlyTheAccountCredential()
    {
        var ctx = new Context();
        var credentials = new FakeCredentials(InferenceOrigin, "account-token");
        ctx.Provide(IInferenceCredentials.ServiceName, credentials);

        InferenceCredentials.Reject(ctx, new LlmCredential("sk-test", LlmCredentialScheme.Bearer));
        Assert.Null(credentials.Rejected);

        InferenceCredentials.Reject(ctx, new LlmCredential("account-token", LlmCredentialScheme.DshAuthToken));
        Assert.Equal("account-token", credentials.Rejected);
    }

    private sealed class FakeCredentials(string origin, string? token) : IInferenceCredentials
    {
        public string? Rejected { get; private set; }

        public Uri? InferenceOrigin => new(origin);

        public bool Covers(Uri destination)
            => string.Equals(Origin(destination), origin, StringComparison.Ordinal);

        public string? TryGetToken(Uri destination) => Covers(destination) ? token : null;

        public void RejectToken(string rejected) => Rejected = rejected;

        private static string Origin(Uri destination)
            => destination.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
    }
}