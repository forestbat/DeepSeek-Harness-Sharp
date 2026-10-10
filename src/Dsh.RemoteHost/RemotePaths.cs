namespace Dsh.RemoteHost;

/** 远端文件系统的路径风格: 由**远端**平台决定, 客户端据此解析相对路径/取目录名(不能借用本地 OS 的 Path)。 */
public enum RemotePathStyle
{
    Posix,
    Windows,
}

/**
 * 远端路径的纯字符串处理(客户端侧): 远端可能是 Linux/macOS(POSIX)或 Windows, 与客户端本地 OS 无关。
 * 绝对路径或 `~` 原样交给远端后端展开; 相对路径按会话 cwd 用远端风格的分隔符拼接。
 */
public static class RemotePaths
{
    public static RemotePathStyle StyleFor(string? platform)
        => string.Equals(platform, "windows", StringComparison.OrdinalIgnoreCase) ? RemotePathStyle.Windows : RemotePathStyle.Posix;

    /** 解析: 绝对路径原样; 相对路径按 cwd 用远端分隔符拼接; cwd 为空则原样。 */
    public static string Resolve(string? cwd, string? path, RemotePathStyle style)
    {
        var token = (path ?? "").Trim().Trim('"', '\'');
        if (token.Length == 0 || IsAbsolute(token, style))
            return token;
        var basePath = (cwd ?? "").TrimEnd('/', '\\');
        return basePath.Length == 0 ? token : $"{basePath}{Separator(style)}{token}";
    }

    public static bool IsAbsolute(string path, RemotePathStyle style)
    {
        if (path.Length == 0)
            return false;
        if (path[0] is '/' or '\\' or '~')
            return true;
        // 盘符 (C:) 在两个风格下都视为绝对。
        return path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':';
    }

    public static string DirectoryName(string path, RemotePathStyle style)
    {
        var index = LastSeparator(path, style);
        return index < 0 ? "" : index == 0 ? path[..1] : path[..index];
    }

    public static string FileName(string path, RemotePathStyle style)
    {
        var index = LastSeparator(path, style);
        return index >= 0 ? path[(index + 1)..] : path;
    }

    public static string Extension(string path, RemotePathStyle style)
    {
        var name = FileName(path, style);
        var index = name.LastIndexOf('.');
        return index >= 0 ? name[index..] : "";
    }

    private static char Separator(RemotePathStyle style) => style == RemotePathStyle.Windows ? '\\' : '/';

    private static int LastSeparator(string path, RemotePathStyle style)
        => style == RemotePathStyle.Windows ? path.LastIndexOfAny(['/', '\\']) : path.LastIndexOf('/');
}
