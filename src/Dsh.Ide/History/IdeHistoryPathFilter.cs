namespace Dsh.Ide.History;

/**
 * IDE 历史的路径过滤: 各 provider 记录的路径写法不同(JetBrains 用正斜杠 `C:/Users/...`、VSCode 存 `file:///c:/...` 形式的 resource),
 * 而调用方(TUI/GUI)传的是本机工作目录(`C:\Users\...`), 直接子串比较会失配 → 一律先归一化再比(Windows 下不区分大小写)。
 */
public static class IdeHistoryPathFilter
{
    public static bool Matches(string? path, string? filter)
        => string.IsNullOrWhiteSpace(filter)
            || (path is not null && Normalize(path).Contains(Normalize(filter), StringComparison.OrdinalIgnoreCase));

    /** 归一化: 反斜杠→正斜杠、去掉 `file://` 前缀与 `/c:/` 这类 URI 盘符前导斜杠、去掉末尾斜杠。 */
    public static string Normalize(string value)
    {
        var normalized = value.Replace('\\', '/').Trim();
        if (normalized.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            normalized = normalized["file://".Length..];
        if (normalized.Length > 3 && normalized[0] == '/' && normalized[2] == ':' && normalized[3] == '/')
            normalized = normalized[1..];
        return normalized.TrimEnd('/');
    }
}
