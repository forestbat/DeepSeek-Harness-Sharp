using System.Runtime.InteropServices;

namespace Dsh.Pty;

/** 交互式 shell 的解析与子环境: 终端内接管形态与独立窗口形态共用同一份默认值。 */
public static class PtyShell
{
    /** 默认 shell: Windows 优先 pwsh, 其次 powershell/cmd; Unix 用 $SHELL, 退回 bash/sh。 */
    public static string Resolve()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var candidate in new[] { "pwsh.exe", "powershell.exe", "cmd.exe" })
            {
                if (ExistsOnPath(candidate))
                    return candidate;
            }
            return Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
        }

        var configured = Environment.GetEnvironmentVariable("SHELL");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return configured;
        return File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";
    }

    /** 给 shell 的默认参数: 交互式登录-ish shell, 不做 rc 以外的额外手脚。 */
    public static IReadOnlyList<string> Arguments(string shell)
    {
        var name = Path.GetFileNameWithoutExtension(shell);
        if (name.Equals("pwsh", StringComparison.OrdinalIgnoreCase) || name.Equals("powershell", StringComparison.OrdinalIgnoreCase))
            return ["-NoLogo"];
        if (name.Equals("bash", StringComparison.OrdinalIgnoreCase) || name.Equals("zsh", StringComparison.OrdinalIgnoreCase))
            return ["-i"];
        return [];
    }

    /** shell 子环境: 明确声明 xterm 兼容终端, 关掉分页器与颜色探测噪声。 */
    public static IReadOnlyDictionary<string, string?> ChildEnvironment()
        => new Dictionary<string, string?>
        {
            ["TERM"] = "xterm-256color",
            ["COLORTERM"] = "truecolor",
            ["PAGER"] = "cat",
            ["GIT_PAGER"] = "cat",
            ["DSH_SHELL"] = "1",
        };

    /** 打开失败时的兜底说明(裸 TTY 或受限容器里可能没有可用的 shell)。 */
    public static bool Exists(string shell)
        => OperatingSystem.IsWindows() ? ExistsOnPath(shell) : File.Exists(shell);

    private static bool ExistsOnPath(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return false;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(directory.Trim(), executable)))
                    return true;
            }
            catch (Exception)
            {
                // 非法 PATH 项(空白/通配符)直接跳过
            }
        }
        return false;
    }
}


