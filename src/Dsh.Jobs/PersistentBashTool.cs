using Dsh.Runtime;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Subprocess;

namespace Dsh.Jobs;

[global::Dsh.Plugins.DshPluginSettings]
public sealed record PersistentBashConfig
{
    public const string DefaultDescription = "Run commands in a persistent bash shell. State, including the current directory and exported environment variables, persists across calls for this agent. On Windows this bash is Git-Bash (MSYS2), not WSL; use `pwsh_persistent` for a persistent PowerShell session.";

    public string? BashPath { get; init; }
    public long TimeoutMs { get; init; } = 300_000;
    public int MaxOutputChars { get; init; } = 16_000;
    public string Description { get; init; } = DefaultDescription;
}

public static class BashDiscovery
{
    public static IReadOnlyList<string> BashCandidates(Func<string, string?> env, string? gitExe)
    {
        var candidates = new List<string>();
        if (gitExe is not null && (gitExe.Contains('/') || gitExe.Contains('\\')))
        {
            var dir = Path.GetDirectoryName(gitExe)!;
            var root = Path.GetDirectoryName(dir)!;
            candidates.Add(Path.Combine(root, "bin", "bash.exe"));
            candidates.Add(Path.Combine(dir, "bash.exe"));
            var grandRoot = Path.GetDirectoryName(root);
            if (grandRoot is not null)
                candidates.Add(Path.Combine(grandRoot, "bin", "bash.exe"));
        }
        if (env("ProgramFiles") is { } programFiles)
            candidates.Add(Path.Combine(programFiles, "Git", "bin", "bash.exe"));
        if (env("ProgramFiles(x86)") is { } programFilesX86)
            candidates.Add(Path.Combine(programFilesX86, "Git", "bin", "bash.exe"));
        if (env("LOCALAPPDATA") is { } localAppData)
            candidates.Add(Path.Combine(localAppData, "Programs", "Git", "bin", "bash.exe"));
        if (env("USERPROFILE") is { } userProfile)
            candidates.Add(Path.Combine(userProfile, "scoop", "apps", "git", "current", "bin", "bash.exe"));
        return candidates.Distinct().ToList();
    }
}

public sealed class BashResolver(SubprocessService subprocess, string? explicitBashPath)
{
    private string? _inferred;

    public string Resolve(CancellationToken signal)
    {
        if (!string.IsNullOrEmpty(explicitBashPath))
            return subprocess.ResolveExecutable(explicitBashPath, null, signal).GetAwaiter().GetResult();
        if (_inferred is not null)
            return subprocess.ResolveExecutable(_inferred, null, signal).GetAwaiter().GetResult();
        string? gitExe = null;
        try
        {
            gitExe = subprocess.ResolveExecutable("git", null, signal).GetAwaiter().GetResult();
        }
        catch (FileNotFoundException)
        {
        }
        foreach (var candidate in BashDiscovery.BashCandidates(Environment.GetEnvironmentVariable, gitExe))
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                _inferred = subprocess.ResolveExecutable(candidate, null, signal).GetAwaiter().GetResult();
                return _inferred;
            }
            catch (FileNotFoundException)
            {
            }
        }
        try
        {
            return subprocess.ResolveExecutable("bash", null, signal).GetAwaiter().GetResult();
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"bash executable not found — install Git for Windows, expose a bash on PATH, or set the tool-bash-persistent `bashPath` config ({error.Message})");
        }
    }
}

public static class PersistentBashTool
{
    public const string ToolName = "bash_persistent";

    public static IDisposable Register(Context ctx, PersistentBashConfig? config = null)
    {
        var resolved = config ?? new PersistentBashConfig();
        var resolver = new BashResolver(ctx.Get<SubprocessService>(SubprocessService.ServiceName)!, resolved.BashPath);
        return PersistentShellTool.Register(
            ctx,
            new PersistentShellDialect(
                ToolName,
                "bash",
                "tool-bash-persistent",
                "The bash command to run. Relative path is preferred in the command.",
                "`grep -n`",
                signal => [resolver.Resolve(signal)],
                WrapCommand)
            {
                // Windows 上 bash 与 pwsh 并存时模型总选 bash: 只把 bash 从可见工具里收起, 内部调用仍然可用。
                IsAvailable = static () => !OperatingSystem.IsWindows(),
            },
            new PersistentShellSettings(resolved.TimeoutMs, resolved.MaxOutputChars, resolved.Description));
    }

    private static string QuoteForBash(string value)
    {
        var escaped = value
            .Replace(@"\", @"\\")
            .Replace("'", "\\'")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n");
        return $"$'{escaped}'";
    }

    private static string WrapCommand(string command, PersistentShellMarkers marker)
        => $"printf '%s\\n' {QuoteForBash(marker.Start)}; eval -- {QuoteForBash(command)} 2>&1; __dsh_persistent_bash_status=$?; printf '%s%s\\n' {QuoteForBash(marker.End)} \"$__dsh_persistent_bash_status\"";
}
