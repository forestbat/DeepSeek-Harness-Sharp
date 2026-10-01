using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Dsh.Tui;

/**
 * 回退字体族解析结果的磁盘缓存: 键 = 策略版本 + 字号 + 平台 + 已装字体清单(排序后取 SHA256)。
 * 命中则跳过逐族渲染探针与 CJK 抽样评分(实测本机这两步约 8s、十余万次小文件读)。
 * 文件是纯文本: 首行键, 其余行是解析出的族名并保持优先级顺序; 读写失败一律当未命中。
 */
internal static class GlyphFallbackCache
{
    private const string Version = "1";

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", "cache", "glyph-fallback.txt");

    public static string Key(double fontSize, IReadOnlyList<string> inventory)
    {
        var payload = string.Join('\n',
        [
            Version,
            fontSize.ToString(CultureInfo.InvariantCulture),
            Environment.OSVersion.Platform.ToString(),
            .. inventory,
        ]);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    public static IReadOnlyList<string>? TryLoad(string path, string key)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var lines = File.ReadAllLines(path);
            if (lines.Length < 2 || !string.Equals(lines[0], key, StringComparison.Ordinal))
                return null;
            return [.. lines.Skip(1).Where(line => line.Length > 0)];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Save(string path, string key, IReadOnlyList<string> names)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(path, [key, .. names]);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }
}
