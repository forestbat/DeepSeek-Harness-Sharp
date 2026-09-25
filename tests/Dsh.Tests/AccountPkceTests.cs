using Dsh.Account;

namespace Dsh.Tests;

public sealed class AccountPkceTests
{
    [Fact]
    public void Challenge_MatchesRfc7636KnownVector()
    {
        const string verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", AccountPkce.Challenge(verifier));
    }

    [Fact]
    public void NewVerifier_IsBase64UrlOf32Bytes()
    {
        var verifier = AccountPkce.NewVerifier();
        Assert.Equal(43, verifier.Length);
        Assert.DoesNotContain('=', verifier);
        Assert.NotEqual(verifier, AccountPkce.NewVerifier());
    }

    [Fact]
    public void NewState_IsBase64UrlOf32Bytes()
    {
        var state = AccountPkce.NewState();
        Assert.Equal(43, state.Length);
        Assert.DoesNotContain('=', state);
        Assert.NotEqual(state, AccountPkce.NewState());
    }

    [Fact]
    public void FixedTimeEquals_AcceptsEqualAndRejectsMismatchOrLengthChange()
    {
        Assert.True(AccountPkce.FixedTimeEquals("abc", "abc"));
        Assert.False(AccountPkce.FixedTimeEquals("abc", "abd"));
        Assert.False(AccountPkce.FixedTimeEquals("abc", "abcd"));
    }
}
