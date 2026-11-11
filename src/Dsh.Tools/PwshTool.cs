using Dsh.Runtime;
using Dsh.Core;
using Dsh.Subprocess;

namespace Dsh.Tools;

public static class PwshTool
{
    public const string ToolName = "pwsh";

    internal const string SectionText =
        "Non-zero exits are reported as `[exit code: N]` markers; investigate failures before moving on. "
        + "On Windows a killed process settles as `[exit code: 1]` without a signal marker; treat a bare exit 1 after an interruption as a termination, not a command failure.";

    internal const string DescriptionExample = "\"Get-Process\" → \"List running processes\"";

    private const string Description =
        "Execute a PowerShell 7 command (`pwsh -Command`) and return its stdout/stderr. "
        + "Each call runs in a fresh pwsh process: no state (cwd, variables, functions) persists between calls — "
        + "pass `workdir` instead of using `cd`. Paths use the host's native form (`C:\\...` on Windows); read environment "
        + "variables with `$env:NAME`. Non-zero exits are reported as `[exit code: N]`. "
        + "pwsh is cross-platform PowerShell 7; on Windows prefer it over `powershell` (Windows PowerShell 5.1) unless a command needs 5.1-only modules or COM. "
        + "On Linux/macOS native commands and paths are case-sensitive, and `ls`/`cat`/`curl` are the real executables rather than aliases. "
        + "Use `bash` for POSIX shell syntax and `wsl` for a Linux distribution on Windows. "
        + "Current harness environment facts are exposed through managed `$env:DSH_*` variables; inspect them when needed. "
        + "Commands may run under a file sandbox; a blocked file operation is reported as `[sandbox: file access denied under <mode> mode]` — a policy denial, not a bug in the command; do not retry another way. "
        + "Long output is truncated to its tail; the full output is saved to a file whose path is reported when available. "
        + "On Windows a force-killed command settles as `[exit code: 1]` without a signal marker — treat it as an interruption, not a command failure. "
        + "Background execution is not available; long-running commands must finish within the timeout.";

    public static IDisposable Register(Context ctx, BashToolConfig? config = null)
        => BashTool.RegisterShell(ctx, new ShellCommandDialect(
                ToolName,
                Description,
                SectionText,
                "The PowerShell command to execute.",
                DescriptionExample,
                (command, _, envs) => CommandLaunch("pwsh", command, envs))
            {
                // pwsh 在 Windows 上总提供(pwsh 7 或需用户自行安装); 其他平台必须有 PowerShell Core 才提供, 5.1 由 powershell 工具负责。
                IsAvailable = PowerShellCoreProbe.IsAvailable,
            },
            config ?? new BashToolConfig());

    /** A non-interactive `-Command` run of the given PowerShell executable with the shared preamble. */
    internal static ShellLaunch CommandLaunch(string executable, string command, IReadOnlyDictionary<string, string?>? envs)
        => new(
            [executable, "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", ShellEnvironment.PowerShellPreamble + command],
            BashTool.MergeEnv(envs));
}
