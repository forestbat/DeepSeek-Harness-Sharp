using Dsh.Boot;
using Dsh.Core;
using Dsh.Runtime;

namespace Dsh.AgentInstructions;

public static class AgentInstructionsText
{
    private const string InstructionsFileName = "AGENTS.md";

    private static readonly IReadOnlySet<string> GlobalAgentDirs = new HashSet<string>(StringComparer.Ordinal)
    {
        ".config", ".kilocode", ".kilo", ".opencode",
    };

    public static IReadOnlyList<string> Discover(HarnessHome home, string? cwd)
        => DiscoverFrom(home, cwd);

    public static IReadOnlyList<string> DiscoverFrom(HarnessHome home, string? start)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var homeRoot = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var candidatePath = string.IsNullOrEmpty(start) ? Directory.GetCurrentDirectory() : start;
        var dir = Directory.Exists(candidatePath)
            ? Path.GetFullPath(candidatePath)
            : Path.GetDirectoryName(Path.GetFullPath(candidatePath));
        if (dir is null)
            return found;
        while (true)
        {
            var candidate = Path.Combine(dir, InstructionsFileName);
            if (File.Exists(candidate) && seen.Add(candidate))
                found.Add(candidate);
            if (string.Equals(dir, homeRoot, StringComparison.Ordinal))
                break;
            var parent = Directory.GetParent(dir);
            if (parent is null || GlobalAgentDirs.Contains(parent.Name))
                break;
            dir = parent.FullName;
        }
        found.Reverse();
        var harnessGlobal = Path.Combine(home.Root, InstructionsFileName);
        if (File.Exists(harnessGlobal) && seen.Add(harnessGlobal))
            found.Add(harnessGlobal);
        return found;
    }

    /** Return newly loaded optional instructions without re-reading the static prompt's files. */
    public static async Task<string> RenderNewAsync(HarnessHome home, string? start, ISet<string> loaded,
        ToolRunContext? run, CancellationToken signal = default)
    {
        var parts = new List<string>();
        foreach (var path in DiscoverFrom(home, start))
        {
            signal.ThrowIfCancellationRequested();
            lock (loaded)
                if (loaded.Contains(path)) continue;
            string content;
            try
            {
                content = (run is null ? await File.ReadAllTextAsync(path, signal).ConfigureAwait(false)
                    : await SafetyFileAccess.ReadTextAsync(path, run, signal).ConfigureAwait(false)).Trim();
            }
            catch (UnauthorizedAccessException)
            {
                signal.ThrowIfCancellationRequested();
                parts.Add("Optional directory instructions were not loaded because access was denied.");
                continue;
            }
            catch (Exception error) when (error is IOException || PathAbsence.IsAbsent(error))
            {
                signal.ThrowIfCancellationRequested();
                parts.Add("Optional directory instructions could not be loaded; no instructions from that file were added.");
                continue;
            }
            lock (loaded)
                if (!loaded.Add(path)) continue;
            if (content.Length > 0)
                parts.Add($"## {path}\n\n{content}");
        }
        return string.Join("\n\n", parts);
    }

    public static string Render(HarnessHome home, string? cwd)
    {
        var parts = new List<string>();
        foreach (var path in Discover(home, cwd))
        {
            var content = File.ReadAllText(path).Trim();
            if (content.Length > 0)
                parts.Add($"## {path}\n\n{content}");
        }
        var rules = HarnessSettings.Load(home).Rules;
        if (rules.Count > 0)
            parts.Add($"## Rules\n\n{string.Join("\n", rules.Select(rule => $"- {rule}"))}");
        return parts.Count == 0 ? "" : $"# Agent Instructions\n\n{string.Join("\n\n", parts)}";
    }
}
