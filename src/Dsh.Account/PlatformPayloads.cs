using System.Text.Json;

namespace Dsh.Account;

/** 平台业务负载解析: 只读取需要的字段, 金额保持字符串原样。 */
public static class PlatformPayloads
{
    public static AccountProfile ParseProfile(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new AccountException(AccountFailure.Protocol);
        string? name = null;
        string? picture = null;
        if (value.TryGetProperty("id_profile", out var profile) && profile.ValueKind == JsonValueKind.Object)
        {
            name = OptionalString(profile, "name");
            picture = OptionalString(profile, "picture");
        }
        var contact = OptionalString(value, "mobile")
            ?? OptionalString(value, "mobile_number")
            ?? OptionalString(value, "email");
        return new AccountProfile(OptionalString(value, "id"), name, picture, contact);
    }

    public static AccountBalance ParseBalance(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new AccountException(AccountFailure.Protocol);
        return new AccountBalance(ParseWallets(value, "normal_wallets"), ParseWallets(value, "bonus_wallets"));
    }

    public static string? OptionalString(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
            && property.GetString() is { Length: > 0 } text
                ? text
                : null;

    public static long? OptionalInt(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt64(out var number)
                ? number
                : null;

    private static IReadOnlyList<AccountWallet> ParseWallets(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var wallets) || wallets.ValueKind != JsonValueKind.Array)
            return [];
        var result = new List<AccountWallet>();
        foreach (var wallet in wallets.EnumerateArray())
        {
            var currency = OptionalString(wallet, "currency");
            var balance = OptionalString(wallet, "balance");
            if (currency is not null && balance is not null)
                result.Add(new AccountWallet(currency, balance));
        }
        return result;
    }
}
