using System.Security.Cryptography;
using System.Text;
using ModelContextProtocol.Client;

namespace Dsh.Mcp;

internal static class McpSemanticAnalyzer
{
    private static readonly HashSet<string> HostToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "read_file", "write_file", "edit_file", "list_directory",
        "grep_search", "find_by_name", "run_command", "run_code",
    };

    private static readonly string[][] DomainKeywords =
    [
        ["roslyn", "codelens", "diagnostics", "ast", "syntax", "compiler", "symbol", "refactor"],
        ["search", "query", "grep", "find_tool"],
        ["filesystem", "file_read", "file_write", "directory_list"],
        ["terminal", "command_run", "shell_exec", "bash_run"],
    ];

    private static readonly string[] IdeKeywords =
    [
        "rider", "idea", "pycharm", "webstorm", "clion", "goland", "rustrover", "rubymine", "phpstorm", "datagrip", "jetbrains",
        "vscode", "vscodium", "visualstudio", "cursor", "windsurf", "zed", "eclipse", "ide",
    ];

    public static bool IsIdeServer(string serverName)
    {
        foreach (var keyword in IdeKeywords)
        {
            if (serverName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public static IReadOnlyList<McpConflict> Analyze(IReadOnlyList<McpServerHandle> handles)
    {
        var conflicts = new List<McpConflict>();
        var serverTools = CollectServerTools(handles);
        CheckDefectiveDefinitions(serverTools, conflicts);
        CheckNameCollisions(serverTools, conflicts);
        CheckSemanticRedundancy(serverTools, conflicts);
        CheckHostToolShadowing(serverTools, conflicts);
        return conflicts.ToList();
    }

    private static List<(string Server, McpClientTool Tool)> CollectServerTools(IReadOnlyList<McpServerHandle> handles)
    {
        var result = new List<(string Server, McpClientTool Tool)>();
        foreach (var handle in handles)
        {
            foreach (var tool in handle.Tools)
                result.Add((handle.Config.Name, tool));
        }
        return result;
    }

    private static void CheckDefectiveDefinitions(
        List<(string Server, McpClientTool Tool)> serverTools,
        List<McpConflict> conflicts)
    {
        foreach (var (server, tool) in serverTools)
        {
            var desc = tool.Description?.Trim();
            if (string.IsNullOrEmpty(desc) || string.Equals(desc, tool.Name, StringComparison.OrdinalIgnoreCase))
            {
                var id = GenerateId("defective", server, tool.Name);
                conflicts.Add(new McpConflict(
                    id,
                    McpConflictKind.DefectiveDefinition,
                    [server],
                    [tool.Name],
                    $"服务 '{server}' 的工具 '{tool.Name}' 缺少有效描述，会导致模型误用或决策困惑。",
                    McpResolutionAction.DisableTool,
                    tool.Name));
            }
        }
    }

    private static void CheckNameCollisions(
        List<(string Server, McpClientTool Tool)> serverTools,
        List<McpConflict> conflicts)
    {
        var byToolName = serverTools
            .GroupBy(item => item.Tool.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(item => item.Server).Distinct().Count() > 1);

        foreach (var group in byToolName)
        {
            var servers = group.Select(item => item.Server).Distinct().ToList();
            var id = GenerateId("collision", string.Join("_", servers), group.Key);
            conflicts.Add(new McpConflict(
                id,
                McpConflictKind.NameCollision,
                servers,
                [group.Key],
                $"工具 '{group.Key}' 在服务 [{string.Join(", ", servers)}] 之间重名，存在同名调用冲突。",
                McpResolutionAction.PreferServer,
                servers[0]));
        }
    }

    private static void CheckSemanticRedundancy(
        List<(string Server, McpClientTool Tool)> serverTools,
        List<McpConflict> conflicts)
    {
        for (var i = 0; i < serverTools.Count; i++)
        {
            for (var j = i + 1; j < serverTools.Count; j++)
            {
                var (serverA, toolA) = serverTools[i];
                var (serverB, toolB) = serverTools[j];
                if (string.Equals(serverA, serverB, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.Equals(toolA.Name, toolB.Name, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!BelongToSameDomain(toolA, toolB))
                    continue;
                var id = GenerateId("redundancy", serverA, serverB, toolA.Name, toolB.Name);
                conflicts.Add(new McpConflict(
                    id,
                    McpConflictKind.SemanticRedundancy,
                    [serverA, serverB],
                    [toolA.Name, toolB.Name],
                    $"服务 '{serverA}' 的 '{toolA.Name}' 与服务 '{serverB}' 的 '{toolB.Name}' 职责相近，容易产生语义混淆。",
                    McpResolutionAction.ApplyPromptGuidance,
                    serverA));
            }
        }
    }

    private static void CheckHostToolShadowing(
        List<(string Server, McpClientTool Tool)> serverTools,
        List<McpConflict> conflicts)
    {
        foreach (var (server, tool) in serverTools)
        {
            if (!HostToolNames.Contains(tool.Name) && !IsHostEquivalent(tool))
                continue;
            var isIdeServer = IsIdeServer(server);
            var id = GenerateId("shadow", server, tool.Name);
            var action = isIdeServer ? McpResolutionAction.PreferServer : McpResolutionAction.PrioritizeHostTool;
            var desc = isIdeServer
                ? $"服务 '{server}' 的 '{tool.Name}' 属于 IDE 扩展工具，建议在代码编辑与重构时优先使用该工具替代内置工具。"
                : $"服务 '{server}' 的 '{tool.Name}' 与宿主原生内置工具重叠，默认优先保持宿主原生工具。";
            conflicts.Add(new McpConflict(
                id,
                McpConflictKind.HostToolShadowing,
                [server],
                [tool.Name],
                desc,
                action,
                isIdeServer ? server : tool.Name));
        }
    }

    private static bool BelongToSameDomain(McpClientTool a, McpClientTool b)
    {
        var textA = $"{a.Name} {a.Description}".ToLowerInvariant();
        var textB = $"{b.Name} {b.Description}".ToLowerInvariant();
        foreach (var keywords in DomainKeywords)
        {
            var matchA = keywords.Any(k => textA.Contains(k, StringComparison.Ordinal));
            var matchB = keywords.Any(k => textB.Contains(k, StringComparison.Ordinal));
            if (matchA && matchB)
                return true;
        }
        return false;
    }

    private static bool IsHostEquivalent(McpClientTool tool)
    {
        var text = $"{tool.Name} {tool.Description}".ToLowerInvariant();
        return text.Contains("read file", StringComparison.Ordinal)
            || text.Contains("write file", StringComparison.Ordinal)
            || text.Contains("run command", StringComparison.Ordinal);
    }

    private static string GenerateId(string prefix, params string[] parts)
    {
        var raw = string.Join(":", parts);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        var hex = Convert.ToHexString(hash).ToLowerInvariant()[..10];
        return $"mcp-{prefix}-{hex}";
    }
}
