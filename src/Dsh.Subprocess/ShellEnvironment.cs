using Dsh.Core;

namespace Dsh.Subprocess;

/** 非交互 shell 的环境覆盖:关闭颜色与分页器,保证子进程输出是纯文本。 */
public static class ShellEnvironment
{
    public static IReadOnlyDictionary<string, string?> NonInteractiveOverrides { get; } = new Dictionary<string, string?>
    {
        ["NO_COLOR"] = "1",
        ["TERM"] = "dumb",
        ["PAGER"] = "cat",
        ["GIT_PAGER"] = "cat",
    };

    /**
     * Prefix for every PowerShell command line: pins UTF-8 output and silences progress records.
     * Windows PowerShell 5.1 writes the OEM code page by default, so non-ASCII output is garbled without it.
     * Redirected 5.1 processes serialize progress records to stderr as CLIXML noise.
     */
    public const string PowerShellPreamble =
        "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); $OutputEncoding = [System.Text.UTF8Encoding]::new($false); $ProgressPreference = 'SilentlyContinue'; ";
}
