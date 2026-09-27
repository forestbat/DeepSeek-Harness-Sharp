using System.Xml.Linq;

namespace Dsh.Tests;

/** 模块引用白名单:防止插件重新耦合到宿主/原生装载层或互相引用。 */
public sealed class PluginReferenceRulesTests
{
    private static readonly HashSet<string> HostInfraModules = ["Dsh.Plugins.Host", "Dsh.Plugins.Native"];

    private static readonly Dictionary<string, HashSet<string>> HostInfraAllowedConsumers = new()
    {
        ["Dsh.Plugins.Host"] = ["Dsh.Boot", "Dsh.Plugins.Native.Host"],
        ["Dsh.Plugins.Native"] = ["Dsh.Plugins.Host", "Dsh.Plugins.Native.Host"],
    };

    private static readonly HashSet<string> PluginAllowedTargets =
        ["Dsh.Runtime", "Dsh.Plugins.Abstractions", "Dsh.Core", "Dsh.Llm", "Dsh.Interaction", "Dsh.Boot", "Dsh.Ptc", "Dsh.Presets", "Dsh.Pty"];

    private static readonly HashSet<string> FeaturePlugins =
    [
        "Dsh.Interaction", "Dsh.Tools", "Dsh.Jobs", "Dsh.PtyTerminal", "Dsh.Persistence", "Dsh.Compaction",
        "Dsh.Subagent", "Dsh.Goal", "Dsh.Interaction.AskUser", "Dsh.Skills", "Dsh.PlanMode",
        "Dsh.SessionQuery", "Dsh.Mcp", "Dsh.Memory", "Dsh.AgentInstructions", "Dsh.Checkpoints",
        "Dsh.IdeHistory", "Dsh.Web", "Dsh.Workflow", "Dsh.Telemetry", "Dsh.E2b", "Dsh.Presets",
        "Dsh.Account",
    ];

    private static readonly Dictionary<string, HashSet<string>> CoreLayerAllowed = new()
    {
        ["Dsh.Runtime"] = [],
        ["Dsh.Pty"] = [],
        ["Dsh.Plugins.Native"] = [],
        ["Dsh.Plugins.Abstractions"] = ["Dsh.Runtime"],
        ["Dsh.Llm"] = ["Dsh.Runtime"],
        ["Dsh.Core"] = ["Dsh.Runtime", "Dsh.Plugins.Abstractions", "Dsh.Llm"],
    };

    [Fact]
    public void HostInfra_ReferencedOnlyByPluginLoaders()
    {
        foreach (var (project, references) in ProjectReferences())
        {
            var illegal = references
                .Where(HostInfraModules.Contains)
                .Where(infra => !HostInfraAllowedConsumers.TryGetValue(infra, out var allowed) || !allowed.Contains(project))
                .ToList();
            Assert.True(illegal.Count == 0, $"{project} must not reference: {string.Join(", ", illegal)}");
        }
    }

    [Fact]
    public void FeaturePlugins_ReferenceOnlySharedLayers()
    {
        foreach (var (project, references) in ProjectReferences())
        {
            if (!FeaturePlugins.Contains(project))
                continue;
            var illegal = references.Where(target => !PluginAllowedTargets.Contains(target)).ToList();
            Assert.True(illegal.Count == 0, $"{project} must not reference: {string.Join(", ", illegal)}");
        }
    }

    [Fact]
    public void CoreLayer_DoesNotReferenceUpwards()
    {
        foreach (var (project, allowed) in CoreLayerAllowed)
        {
            var illegal = ProjectReferences()[project].Where(target => !allowed.Contains(target)).ToList();
            Assert.True(illegal.Count == 0, $"{project} must only reference [{string.Join(", ", allowed)}], found: {string.Join(", ", illegal)}");
        }
    }

    private static Dictionary<string, HashSet<string>> ProjectReferences()
    {
        var srcDir = Path.Combine(RepoRoot(), "src");
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var csproj in Directory.EnumerateFiles(srcDir, "*.csproj", SearchOption.AllDirectories))
        {
            var project = Path.GetFileNameWithoutExtension(csproj);
            var references = XDocument.Load(csproj)
                .Descendants("ProjectReference")
                .Select(element => element.Attribute("Include")?.Value)
                .OfType<string>()
                .Select(include => Path.GetFileNameWithoutExtension(include.Replace('\\', '/')))
                .ToHashSet(StringComparer.Ordinal);
            result[project] = references;
        }
        return result;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DeepSeek-Harness-Sharp.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found from test assembly location");
    }
}
