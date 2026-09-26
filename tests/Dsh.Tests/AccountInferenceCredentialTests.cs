using Dsh.Llm;
using Dsh.Runtime;

namespace Dsh.Tests;

/** 未配 API key 时按目标 origin 取账号令牌的接缝。 */
public class AccountInferenceCredentialTests
{
    private const string InferenceOrigin = "https://api.deepseek.com";

    [Fact]
    public void AccountToken_IsUsedWhenTheOriginMatches()
    {
        var ctx = new Context();
        ctx.Provide(IInferenceCredentials.ServiceName, new FakeCredentials(InferenceOrigin, "account-token"));

        var (token, covered) = InferenceCredentials.Resolve(ctx, $"{InferenceOrigin}/v1");

        Assert.Equal("account-token", token);
        Assert.True(covered);
    }

    [Fact]
    public void AccountToken_IsNotUsedForAnotherOrigin()
    {
        var ctx = new Context();
        ctx.Provide(IInferenceCredentials.ServiceName, new FakeCredentials(InferenceOrigin, "account-token"));

        var (token, covered) = InferenceCredentials.Resolve(ctx, "https://proxy.test/v1");

        Assert.Null(token);
        Assert.False(covered);
    }

    [Fact]
    public void CoveredOrigin_WithoutToken_ReportsNotSignedIn()
    {
        var ctx = new Context();
        ctx.Provide(IInferenceCredentials.ServiceName, new FakeCredentials(InferenceOrigin, null));

        var (token, covered) = InferenceCredentials.Resolve(ctx, InferenceOrigin);

        Assert.Null(token);
        Assert.True(covered);
    }

    [Fact]
    public void WithoutAccountService_KeepsPlainCredentialFailure()
    {
        var (token, covered) = InferenceCredentials.Resolve(new Context(), InferenceOrigin);

        Assert.Null(token);
        Assert.False(covered);
    }

    [Fact]
    public void InvalidBaseUrl_IsNotCovered()
    {
        var ctx = new Context();
        ctx.Provide(IInferenceCredentials.ServiceName, new FakeCredentials(InferenceOrigin, "account-token"));

        var (token, covered) = InferenceCredentials.Resolve(ctx, "not a url");

        Assert.Null(token);
        Assert.False(covered);
    }

    private sealed class FakeCredentials(string origin, string? token) : IInferenceCredentials
    {
        public bool Covers(Uri destination)
            => string.Equals(Origin(destination), origin, StringComparison.Ordinal);

        public string? TryGetToken(Uri destination) => Covers(destination) ? token : null;

        private static string Origin(Uri destination)
            => destination.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
    }
}
