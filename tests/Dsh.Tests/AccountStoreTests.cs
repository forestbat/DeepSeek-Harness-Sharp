using System.Text;
using Dsh.Account;

namespace Dsh.Tests;

public sealed class AccountStoreTests
{
    private const string Origin = "https://platform.test";

    [Fact]
    public void Grant_RoundtripsThroughProtector()
    {
        using var home = new AccountTempHome();
        var store = new AccountStore(home.Home);
        store.WriteGrant(new AccountGrant(1, "secret-token", Origin));

        var grant = store.ReadGrant(Origin);
        Assert.NotNull(grant);
        Assert.Equal("secret-token", grant.Token);
        Assert.Equal(Origin, grant.Issuer);
    }

    [Fact]
    public void IssuerMismatch_DiscardsStoredGrant()
    {
        using var home = new AccountTempHome();
        var store = new AccountStore(home.Home);
        store.WriteGrant(new AccountGrant(1, "secret-token", "https://other.test"));

        Assert.Null(store.ReadGrant(Origin));
        Assert.False(File.Exists(store.GrantPath));
    }

    [Fact]
    public void Device_IsGeneratedOnceAndReused()
    {
        using var home = new AccountTempHome();
        var store = new AccountStore(home.Home);
        var first = store.GetOrCreateDeviceId();
        Assert.True(Guid.TryParse(first, out _));
        Assert.Equal(first, store.GetOrCreateDeviceId());
    }

    [Fact]
    public void Windows_StoresDpapiCiphertext()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var home = new AccountTempHome();
        var store = new AccountStore(home.Home);
        store.WriteGrant(new AccountGrant(1, "secret-token", Origin));

        var bytes = File.ReadAllBytes(store.GrantPath);
        Assert.DoesNotContain("\"token\"", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void Unix_StoresPlaintextWithOwnerOnlyMode()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var home = new AccountTempHome();
        var store = new AccountStore(home.Home);
        store.WriteGrant(new AccountGrant(1, "secret-token", Origin));

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.GrantPath));
    }
}
