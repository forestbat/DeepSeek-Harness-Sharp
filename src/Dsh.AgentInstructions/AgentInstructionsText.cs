using Dsh.Boot;

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

    /** 只返回新的 AGENTS 文件; 已加载路径由调用方维护, 避免与静态段重复。 */
    public static string RenderNew(HarnessHome home, string? start, ISet<string> loaded)
    {
        var parts = new List<string>();
        foreach (var path in DiscoverFrom(home, start))
        {
            if (!loaded.Add(path))
                continue;
            try
            {
                var content = File.ReadAllText(path).Trim();
                if (content.Length > 0)
                    parts.Add($"## {path}\n\n{content}");
            }
            catch (IOException)
            {
            }
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
