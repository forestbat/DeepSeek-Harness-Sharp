using System.Text.RegularExpressions;

namespace Dsh.Runtime.Logging;

public static partial class Redaction
{
    [GeneratedRegex(@"\bsk-[A-Za-z0-9_\-]{4,}", RegexOptions.None)]
    private static partial Regex ApiKeyPattern();

    [GeneratedRegex(@"\bBearer\s+[A-Za-z0-9_\-\.=]{8,}", RegexOptions.None)]
    private static partial Regex BearerPattern();

    /** GitHub personal, OAuth, user-to-server, server-to-server and refresh tokens. */
    [GeneratedRegex(@"\b(gh[pousr]|github_pat)_[A-Za-z0-9_]{16,}", RegexOptions.None)]
    private static partial Regex GitHubTokenPattern();

    public static string Apply(string text)
    {
        if (text.Length == 0)
            return text;
        var redacted = ApiKeyPattern().Replace(text, "sk-***");
        redacted = GitHubTokenPattern().Replace(redacted, "$1_***");
        return BearerPattern().Replace(redacted, "Bearer ***");
    }
}
