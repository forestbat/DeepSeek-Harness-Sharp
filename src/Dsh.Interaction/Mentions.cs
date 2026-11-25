using System.Text;
using System.Text.RegularExpressions;
using Dsh.Core;

namespace Dsh.Interaction;

public sealed record MentionSessionInfo(string Id, string? Title, string? Summary, long CreatedAt = 0)
{
    public static MentionSessionInfo FromSnapshot(SessionPersistenceSnapshot snapshot)
        => new(snapshot.Header.Id.Value, snapshot.Header.Title, null, snapshot.Header.CreatedAt);
}

/**
 * `@` 引用的解析: 会话 id/标题优先, 否则按 cwd 匹配文件系统条目。
 * 展开时只把**会话**引用换成元信息块; 文件/目录引用保留用户输入的 `@路径`。
 */
public static partial class MentionResolver
{
    [GeneratedRegex(@"@([^\s@]+)", RegexOptions.Compiled)]
    private static partial Regex MentionPattern();

    public static Task<IReadOnlyList<string>> ResolveCandidatesAsync(
        string token,
        string cwd,
        IReadOnlyList<MentionSessionInfo> sessions,
        CancellationToken cancellationToken = default)
        => Task.Run(() => ResolveCandidates(token, cwd, sessions), cancellationToken);

    public static IReadOnlyList<string> ResolveCandidates(string token, string cwd, IReadOnlyList<MentionSessionInfo> sessions)
    {
        var files = ResolveFileSystemCandidates(token, cwd);
        if (string.IsNullOrEmpty(token))
            return files;
        var sessionMatches = sessions
            .Where(session => session.Id.StartsWith(token, StringComparison.OrdinalIgnoreCase)
                || (session.Title?.StartsWith(token, StringComparison.OrdinalIgnoreCase) ?? false))
            .Select(session => session.Id);
        return [.. sessionMatches, .. files];
    }

    /** 仅当光标落在「@token」区间内或紧邻末尾时才算活跃引用; token 之后已输入提示词时视为普通文本。 */
    public static bool TryGetActiveToken(string text, int caret, out int start, out int end)
    {
        var at = text.LastIndexOf('@');
        if (at < 0)
        {
            start = 0;
            end = 0;
            return false;
        }
        var tokenEnd = at + 1;
        while (tokenEnd < text.Length && !char.IsWhiteSpace(text[tokenEnd]) && text[tokenEnd] != '@')
            tokenEnd++;
        start = at;
        end = tokenEnd;
        return caret > at && caret <= tokenEnd;
    }

    /** 只替换引用 token 区间并补尾随空格, token 之后已输入的提示词原样保留; 返回新文本与光标位置。 */
    public static (string Text, int Caret) ReplaceActiveToken(string text, int start, int end, string candidate)
        => ($"{text[..start]}@{candidate} {text[end..]}", start + candidate.Length + 2);

    /**
     * 只展开会话引用(`@会话id/标题` → 一段元信息); 文件与目录引用原样留在文本里。
     *
     * 旧做法把被引用文件的正文(或目录清单)抄进用户消息: 每次引用都是几十 KB 的不可回收文本,
     * 压缩要为它重写摘要, 保留尾部也被它占满; 而模型要内容时用 read/grep 自己取, 又快又不带无关行。
     */
    public static string ExpandMentions(string text, IReadOnlyList<MentionSessionInfo> sessions)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        return MentionPattern().Replace(text, match =>
        {
            var session = FindSession(match.Groups[1].Value, sessions);
            return session is null ? match.Value : FormatSession(session);
        });
    }

    private static MentionSessionInfo? FindSession(string token, IReadOnlyList<MentionSessionInfo> sessions)
        => sessions.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, token, StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate.Title, token, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<string> ResolveFileSystemCandidates(string token, string cwd)
    {
        try
        {
            var (directory, prefix) = ResolveListing(token, cwd);
            if (directory is null || !Directory.Exists(directory))
                return [];
            var directories = Directory.EnumerateDirectories(directory)
                .Select(Path.GetFileName)
                .Where(name => name is not null && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase);
            var files = Directory.EnumerateFiles(directory)
                .Select(Path.GetFileName)
                .Where(name => name is not null && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase);
            return [.. directories.Concat(files)
                .Select(name => Slash(Path.GetRelativePath(cwd, Path.Combine(directory, name!))))];
        }
        catch (Exception)
        {
            return [];
        }
    }

    /** 空 token 或结尾分隔符 → 列 cwd / 该目录的子项; 否则在 token 的父目录里按最后一段前缀过滤。 */
    private static (string? Directory, string Prefix) ResolveListing(string token, string cwd)
    {
        if (string.IsNullOrEmpty(token))
            return (cwd, "");
        var normalized = token.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        if (normalized.EndsWith(Path.DirectorySeparatorChar))
            return (Path.GetFullPath(Path.Combine(cwd, normalized)), "");
        var fullPath = Path.GetFullPath(Path.Combine(cwd, token));
        return (Path.GetDirectoryName(fullPath), Path.GetFileName(fullPath));
    }

    private static string FormatSession(MentionSessionInfo session)
    {
        var builder = new StringBuilder();
        builder.AppendLine();
        builder.AppendLine($"[session {session.Id}]");
        if (!string.IsNullOrWhiteSpace(session.Title))
            builder.AppendLine($"Title: {session.Title}");
        if (!string.IsNullOrWhiteSpace(session.Summary))
            builder.AppendLine($"Summary: {session.Summary}");
        return builder.ToString().TrimEnd();
    }

    private static string Slash(string path)
        => Path.DirectorySeparatorChar == '/'
            ? path
            : path.Replace(Path.DirectorySeparatorChar, '/');
}
