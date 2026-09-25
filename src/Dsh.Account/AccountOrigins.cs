using System.Text.RegularExpressions;

namespace Dsh.Account;

/** 平台地址与浏览器跳转校验: 只允许配置的 platformOrigin, 禁止凭证/片段与任意路径。 */
public static class AccountOrigins
{
    public const string AuthorizePath = "/dsh/authorize";

    public const string AuthorizedPath = "/dsh/authorized";

    public const string CallbackPath = "/oauth/callback";

    private static readonly string[] LoopbackHosts = ["localhost", "127.0.0.1", "[::1]"];

    private static readonly Regex LoopbackPort = new(
        """^http://(?:localhost|127\.0\.0\.1|\[::1\]):([0-9]+)/?$""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string PlatformOrigin(string value, bool allowLoopbackHttp)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url))
            throw new AccountException(AccountFailure.Protocol);
        var loopback = LoopbackHosts.Contains(url.Host, StringComparer.OrdinalIgnoreCase);
        var schemeOk = url.Scheme == Uri.UriSchemeHttps
            || (allowLoopbackHttp && loopback && url.Scheme == Uri.UriSchemeHttp);
        if (url.UserInfo.Length > 0 || url.AbsolutePath != "/" || url.Query.Length > 0 || url.Fragment.Length > 0 || !schemeOk)
            throw new AccountException(AccountFailure.Protocol);
        return url.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
    }

    public static string InferenceOrigin(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url))
            throw new AccountException(AccountFailure.Protocol);
        if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
            throw new AccountException(AccountFailure.Protocol);
        if (url.UserInfo.Length > 0 || url.AbsolutePath != "/" || url.Query.Length > 0 || url.Fragment.Length > 0)
            throw new AccountException(AccountFailure.Protocol);
        return url.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
    }

    /** 平台返回的浏览器地址必须是同源固定路径, 且不得携带凭证或片段。 */
    public static string BrowserUrl(string value, string origin, string path)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url))
            throw new AccountException(AccountFailure.Protocol);
        var sameOrigin = string.Equals(url.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped), origin, StringComparison.Ordinal);
        if (!sameOrigin || url.AbsolutePath != path || url.UserInfo.Length > 0 || url.Fragment.Length > 0)
            throw new AccountException(AccountFailure.Protocol);
        return url.AbsoluteUri;
    }

    /** 回环回调 origin 必须是显式端口的 http://localhost|127.0.0.1|::1。 */
    public static string LoginOrigin(string value)
    {
        var match = LoopbackPort.Match(value);
        if (!match.Success || !Uri.TryCreate(value, UriKind.Absolute, out var url))
            throw new AccountException(AccountFailure.Protocol);
        var port = int.Parse(match.Groups[1].Value);
        if (port == 0 || url.Port != port)
            throw new AccountException(AccountFailure.Protocol);
        return $"{url.Scheme}://{url.Host}:{port}";
    }
}
