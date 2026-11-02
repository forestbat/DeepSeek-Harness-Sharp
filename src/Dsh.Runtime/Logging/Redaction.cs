using System.Text.RegularExpressions;

namespace Dsh.Runtime.Logging;

public static partial class Redaction
{
    private const string CredentialName = """(?:api[_-]?key|access[_-]?token|refresh[_-]?token|session[_-]?token|client[_-]?secret|private[_-]?key|password|passwd|passphrase|secret|token|credentials?|authorization)""";

    [GeneratedRegex($$"""(?im)^(?<indent>[ \t]*)(?<field>["']?{{CredentialName}}["']?[ \t]*:[ \t]*)[|>][-+1-9]*[^\r\n]*(?:\r?\n(?:\k<indent>[ \t]+[^\r\n]*|[ \t]*(?=\r?$)))*""", RegexOptions.CultureInvariant)]
    private static partial Regex CredentialBlockPattern();

    [GeneratedRegex($$"""(?i)(?<field>(?<![\w-])["']?{{CredentialName}}["']?[ \t]*[:=][ \t]*)(?:"(?:\\[\s\S]|[^"\\])*"|'(?:''|[^'])*'|[^\r\n,}]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex CredentialFieldPattern();

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
        var redacted = text;
        if (text.AsSpan().IndexOfAny(':', '=') >= 0)
        {
            redacted = CredentialBlockPattern().Replace(redacted, "${indent}${field}\"***\"");
            redacted = CredentialFieldPattern().Replace(redacted, "${field}\"***\"");
        }
        redacted = ApiKeyPattern().Replace(redacted, "sk-***");
        redacted = GitHubTokenPattern().Replace(redacted, "$1_***");
        return BearerPattern().Replace(redacted, "Bearer ***");
    }
}
