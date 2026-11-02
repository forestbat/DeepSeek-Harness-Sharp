using Dsh.Runtime;

namespace Dsh.Tools;

/** Windows PowerShell 5.1 (`powershell.exe`), registered on Windows only. */
public static class WindowsPowerShellTool
{
    public const string ToolName = "powershell";

    private const string Executable = "powershell.exe";

    private const string Description =
        "Execute a Windows PowerShell 5.1 command (`powershell.exe -Command`) and return its stdout/stderr. Windows only. "
        + "Use it only when a command needs 5.1: modules or snap-ins that never moved to PowerShell 7, COM and other Windows-only automation, or checking how a script behaves under 5.1; otherwise prefer `pwsh` (PowerShell 7). "
        + "Each call runs in a fresh process: no state (cwd, variables, functions) persists between calls — pass `workdir` instead of using `cd`. "
        + "Pitfalls: 5.1 lacks PowerShell 7 syntax such as `&&`/`||` pipeline chains, the ternary `? :`, `??` and `ForEach-Object -Parallel`; "
        + "`Out-File` and `>` write UTF-16LE and `Set-Content` writes the ANSI code page by default, and `-Encoding utf8` adds a BOM; "
        + "`curl` and `wget` are aliases of `Invoke-WebRequest`, so call `curl.exe` for the real curl; running a `.ps1` file can be blocked by the execution policy. "
        + "Non-zero exits are reported as `[exit code: N]`; a force-killed command settles as `[exit code: 1]` without a signal marker — treat it as an interruption, not a command failure. "
        + "Long output is truncated to its tail; the full output is saved to a file whose path is reported when available. "
        + "Background execution is not available; long-running commands must finish within the timeout.";

    /** 退出码规则已由 `tool:pwsh` 段覆盖, 这里不再注册第二份相同文案。 */
    private const string SectionText = "";

    public static IDisposable Register(Context ctx, BashToolConfig? config = null)
        => BashTool.RegisterShell(ctx, new ShellCommandDialect(
                ToolName,
                Description,
                SectionText,
                "The Windows PowerShell 5.1 command to execute.",
                PwshTool.DescriptionExample,
                (command, _, envs) => PwshTool.CommandLaunch(Executable, command, envs))
            {
                WindowsOnly = true,
            },
            config ?? new BashToolConfig());
}
