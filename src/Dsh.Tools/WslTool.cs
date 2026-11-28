using System.Text.Json;
using Dsh.Runtime;

namespace Dsh.Tools;

/** A bash command inside a WSL distribution (`wsl.exe [-d distro] --exec bash -lc`), registered on Windows only. */
public static class WslTool
{
    public const string ToolName = "wsl";

    private const string DistroParameter = "distro";
    private const string ForwardedVariablesName = "WSLENV";

    private const string SectionText =
        "Check the [exit code: N] marker on every wsl result; a non-zero code that comes with a wsl.exe message means WSL or the distribution is unavailable, not that the command itself failed.";

    private const string Description =
        "Execute a bash command inside a WSL (Windows Subsystem for Linux) distribution and return its stdout/stderr. Windows only. "
        + "Runs `wsl.exe [-d <distro>] --exec bash -lc <command>`: a fresh login shell per call (`~/.profile` is sourced), so no state persists — pass `workdir` instead of using `cd`. "
        + "Use it for Linux-only tooling; the `bash` tool on Windows is Git-Bash, not WSL. "
        + "`distro` selects the distribution (default: WSL's default distribution); list installed ones with `wsl.exe --list --verbose` from `pwsh`. "
        + "Pitfalls: the Windows working directory appears as `/mnt/c/...` (a directory WSL cannot map, such as a network share, falls back to the Linux home); "
        + "I/O under `/mnt` is slow, so build in the Linux file system (`~`) when possible; "
        + "files edited on Windows may have CRLF line endings that break scripts (`$'\\r': command not found`) — strip them with `sed -i 's/\\r$//'`; "
        + "Linux tools write LF and UTF-8; `wslpath -u 'C:\\path'` and `wslpath -w /path` convert paths, and Windows programs run with their `.exe` suffix (e.g. `cmd.exe /c`). "
        + "`envs` are forwarded unchanged through `WSLENV`; managed `DSH_*` variables are not visible inside WSL. "
        + "When WSL or the distribution is missing, wsl.exe's own message and non-zero exit code are returned; if wsl.exe itself is absent the call fails with an install hint. "
        + "Non-zero exits are reported as `[exit code: N]`. "
        + "Long output is truncated to its tail; the full output is saved to a file whose path is reported when available. "
        + "Background execution is not available; long-running commands must finish within the timeout.";

    public static IDisposable Register(Context ctx, BashToolConfig? config = null)
        => BashTool.RegisterShell(ctx, new ShellCommandDialect(
                ToolName,
                Description,
                SectionText,
                "The bash command to run inside the distribution.",
                "\"apt list --installed\" → \"List installed Linux packages\"",
                Launch)
            {
                WindowsOnly = true,
                ExtraParameters = new Dictionary<string, System.Text.Json.Nodes.JsonObject>
                {
                    [DistroParameter] = ToolSchemas.StringParam("WSL distribution name as listed by `wsl.exe --list`. Defaults to the default distribution."),
                },
            },
            config ?? new BashToolConfig());

    /** `--exec` hands the argv straight to bash with no default-shell pass in between, so quotes and `$` in the command arrive intact. */
    private static ShellLaunch Launch(string command, JsonElement args, IReadOnlyDictionary<string, string?>? envs)
    {
        string[] target = DistroOf(args) is { } distro ? ["-d", distro] : [];
        var env = new Dictionary<string, string?>(BashTool.MergeEnv(envs));
        env[ForwardedVariablesName] = ForwardedNames(env);
        env[WslExecutable.Utf8MessagesName] = WslExecutable.Enabled;
        return new ShellLaunch([WslExecutable.Require(guidanceSuffix: ", or use the bash/pwsh tools instead"), .. target, "--exec", "bash", "-lc", command], env);
    }

    private static string? DistroOf(JsonElement args)
    {
        if (!args.TryGetProperty(DistroParameter, out var element) || element.ValueKind == JsonValueKind.Null)
            return null;
        if (element.ValueKind != JsonValueKind.String || element.GetString() is not { } distro || distro.Trim().Length == 0)
            throw new ArgumentException("invalid distro: expected a non-empty distribution name");
        return distro;
    }

    /** Windows variables reach Linux only when WSLENV names them: keep the user's own entries and add every override. */
    private static string ForwardedNames(IReadOnlyDictionary<string, string?> env)
    {
        string?[] names =
        [
            Environment.GetEnvironmentVariable(ForwardedVariablesName),
            .. env.Where(entry => entry.Value is not null && entry.Key != ForwardedVariablesName).Select(entry => entry.Key),
        ];
        return string.Join(':', names.Where(name => !string.IsNullOrEmpty(name)));
    }
}
