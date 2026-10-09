namespace Dsh.Boot;

/**
 * settings.yaml 含 apiKey/authToken/SSH 密码等机密, 落盘一律收紧为「仅属主可读写」。
 * Windows 文件系统无 Unix 权限位, 权限由用户 profile 的 ACL 保证, 故只在类 Unix 上设置。
 */
internal static class SettingsFilePermissions
{
    private const UnixFileMode OwnerReadWrite = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /** 写入内容, 并把文件收紧为仅属主可读写。 */
    public static void Write(string path, string content)
    {
        File.WriteAllText(path, content);
        Restrict(path);
    }

    /** 把已落盘的文件收紧为仅属主可读写(Windows 跳过)。 */
    public static void Restrict(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, OwnerReadWrite);
    }
}
