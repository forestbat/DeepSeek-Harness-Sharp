namespace Dsh.Account;

/** 账号标识脱敏: 手机号保留前 3 后 4, 邮箱保留首字符与域名。 */
public static class AccountMasking
{
    public static string MaskContact(string? contact)
    {
        if (string.IsNullOrEmpty(contact))
            return "";
        var at = contact.IndexOf('@');
        if (at > 0)
        {
            var name = contact[..at];
            var domain = contact[at..];
            return name.Length <= 1 ? $"*{domain}" : $"{name[0]}***{domain}";
        }
        var digits = new string([.. contact.Where(char.IsAsciiDigit)]);
        if (digits.Length >= 7)
            return $"{digits[..3]}****{digits[^4..]}";
        return digits.Length <= 2 ? new string('*', digits.Length) : $"{digits[..1]}***{digits[^1..]}";
    }
}
