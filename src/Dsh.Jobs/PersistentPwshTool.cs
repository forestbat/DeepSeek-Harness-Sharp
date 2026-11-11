using System.Text;
using Dsh.Runtime;
using Dsh.Core;
using Dsh.Tools;
using Dsh.Subprocess;

namespace Dsh.Jobs;

[global::Dsh.Plugins.DshPluginSettings]
public sealed record PersistentPwshConfig
{
    public const string DefaultDescription =
        "Run PowerShell 7 (`pwsh`) commands in a persistent session. "
        + "State, including the current location, variables, functions, imported modules and `$env:` changes, persists across calls for this agent. "
        + "All output streams are merged into one text result. "
        + "A failed command ends with `[exit code: N]`: N is the exit code of the last native command when it failed, otherwise 1 when the last statement reported an error or a terminating error was thrown. "
        + "`exit` ends the session and the next call starts a fresh one. "
        + "Use `pwsh` for a one-off command, `bash_persistent` for persistent POSIX shell state, and `powershell` (Windows only) when Windows PowerShell 5.1 is required.";

    public long TimeoutMs { get; init; } = 300_000;
    public int MaxOutputChars { get; init; } = 16_000;
    public string Description { get; init; } = DefaultDescription;
}

public static class PersistentPwshTool
{
    public const string ToolName = "pwsh_persistent";

    private const string Executable = "pwsh";

    /** Appended to the command so the status line sees `$?` of the command's last statement. */
    private const string StatusCapture = "$global:__dsh_ok = $?";

    public static IDisposable Register(Context ctx, PersistentPwshConfig? config = null)
    {
        var resolved = config ?? new PersistentPwshConfig();
        var subprocess = ctx.Get<SubprocessService>(SubprocessService.ServiceName)!;
        return PersistentShellTool.Register(
            ctx,
            new PersistentShellDialect(
                ToolName,
                Executable,
                "tool-pwsh-persistent",
                "The PowerShell command to run. Relative path is preferred in the command.",
                "`Select-String`",
                signal => [subprocess.ResolveExecutable(Executable, null, signal).GetAwaiter().GetResult(), "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "-"],
                WrapCommand)
            {
                IsAvailable = PowerShellCoreProbe.IsAvailable,
            },
            new PersistentShellSettings(resolved.TimeoutMs, resolved.MaxOutputChars, resolved.Description));
    }

    /**
     * One stdin line per command, because `pwsh -Command -` runs each complete line as it arrives.
     * The command travels base64-encoded and is dot-sourced, so its variables, functions and location stay in the session scope.
     * `Out-String -Stream` turns error records into text; otherwise the host would write them to stderr, outside the captured stream.
     */
    private static string WrapCommand(string command, PersistentShellMarkers marker)
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{command}\n{StatusCapture}"));
        return ShellEnvironment.PowerShellPreamble
            + $$"""$global:LASTEXITCODE = 0; $global:__dsh_ok = $true; Write-Output '{{marker.Start}}'; try { . ([scriptblock]::Create([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{payload}}')))) *>&1 | Out-String -Stream } catch { $global:__dsh_ok = $false; $_ | Out-String -Stream }; Write-Output ('{{marker.End}}' + $(if ($global:__dsh_ok) { 0 } elseif ($LASTEXITCODE) { $LASTEXITCODE } else { 1 }))""";
    }
}
