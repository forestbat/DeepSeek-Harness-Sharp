using System.Text;
using System.Text.RegularExpressions;

namespace Dsh.Llm;

/**
 * 文本文件引用里的图片: 提示词引用了一个文本文件(不限于 md/txt)时, 该文件内容中出现的本地图片路径,
 * 其图片字节也应作为附件交给模型, 而不是只把路径文本发过去。
 * 边界(已定): 内容扫描(不依赖固定语法); 相对路径先按该文本文件所在目录、再按工作区解析;
 * 只扫一层且仅本地图(不递归展开其引用的其它文本, 不下载 http(s) 图片)。
 */
public static class TextFileImageReferences
{
    /** 单条消息最多附带多少张, 防止一个长文档拖入过多图片。 */
    public const int MaxImages = 32;

    /** 文本文件单文件读取上限; 超过视为过大而跳过。 */
    public const long MaxTextBytes = 2 * 1024 * 1024;

    private static readonly Regex ReferencePattern = new(
        """@([^\s@"']+)""", RegexOptions.Compiled);

    private static readonly Regex ImageTokenPattern = new(
        """(?<!\S)(?:[A-Za-z]:[\\/])?[^\s@"'<>|]*?\.(?:png|jpe?g|gif|webp|avif)(?![\w])""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /** 内容里出现的图片路径 token(未解析, 去重); 供远端等自行解析路径的实现复用同一套识别规则。 */
    public static IReadOnlyList<string> ImageTokens(string content)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in ImageTokenPattern.Matches(content))
        {
            var token = match.Value.Trim('"', '\'');
            if (token.Length > 0 && seen.Add(token))
                results.Add(token);
        }
        return results;
    }

    /** 提示词里 `@path` 引用的路径 token(未解析); 调用方自行判断是否文本文件并解析。 */
    public static IReadOnlyList<string> ReferenceTokens(string text)
    {
        var results = new List<string>();
        foreach (Match match in ReferencePattern.Matches(text))
        {
            var token = match.Groups[1].Value.Trim('"', '\'');
            if (token.Length > 0)
                results.Add(token);
        }
        return results;
    }

    /** 扫描 text 里引用的本地文本文件, 返回其中图片路径解析出的图片文件绝对路径(去重、限量)。 */
    public static IReadOnlyList<string> Scan(string text, string cwd)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in ReferencedTextFiles(text, cwd))
        {
            var directory = Path.GetDirectoryName(file) ?? cwd;
            string content;
            try
            {
                if (new FileInfo(file).Length > MaxTextBytes)
                    continue;
                content = File.ReadAllText(file, Encoding.UTF8);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                continue;
            }

            if (content.Contains('\0'))
                continue;
            foreach (Match match in ImageTokenPattern.Matches(content))
            {
                if (results.Count >= MaxImages)
                    return results;
                if (ResolveExisting(match.Value.Trim('"', '\''), directory, cwd) is { } image && seen.Add(image))
                    results.Add(image);
            }
        }

        return results;
    }

    private static IEnumerable<string> ReferencedTextFiles(string text, string cwd)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in ReferencePattern.Matches(text))
        {
            if (ResolveExisting(match.Groups[1].Value.Trim('"', '\''), cwd, cwd) is not { } file)
                continue;
            // 图片本身走原有的直接图片路径逻辑, 这里只要文本文件。
            if (ImageAttachments.MediaTypeForExtension(Path.GetExtension(file)) is not null)
                continue;
            if (seen.Add(file))
                yield return file;
        }
    }

    /** 依次按 primary 目录、再按 cwd 解析相对路径, 返回存在的文件绝对路径。 */
    private static string? ResolveExisting(string token, string primary, string cwd)
    {
        var local = ToLocalPath(token);
        if (local is null)
            return null;
        if (Path.IsPathRooted(local))
            return File.Exists(local) ? local : null;
        var underPrimary = Path.GetFullPath(Path.Combine(primary, local));
        if (File.Exists(underPrimary))
            return underPrimary;
        var underCwd = Path.GetFullPath(Path.Combine(cwd, local));
        return File.Exists(underCwd) ? underCwd : null;
    }

    private static string? ToLocalPath(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;
        if (token.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return new Uri(token).LocalPath;
            }
            catch (UriFormatException)
            {
                return null;
            }
        }

        return token;
    }
}
