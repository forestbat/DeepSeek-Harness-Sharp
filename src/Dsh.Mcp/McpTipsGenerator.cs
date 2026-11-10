using System.Text;
using Dsh.Boot;
using ModelContextProtocol.Client;

namespace Dsh.Mcp;

internal static class McpTipsGenerator
{
    public const string DefaultFileName = "DSH_TOOLS_TIPS.md";

    public static async Task<string> GenerateAndSaveAsync(
        HarnessHome? home,
        IReadOnlyList<McpServerHandle> handles,
        IReadOnlyList<McpConflictResolution> resolutions,
        CancellationToken signal = default)
    {
        var content = Generate(handles, resolutions);
        if (home is not null)
        {
            var path = Path.Combine(home.Root, DefaultFileName);
            await File.WriteAllTextAsync(path, content, Encoding.UTF8, signal);
        }
        return content;
    }

    public static string Generate(
        IReadOnlyList<McpServerHandle> handles,
        IReadOnlyList<McpConflictResolution> resolutions)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# DSH MCP 工具调和指引 (DSH_TOOLS_TIPS.md)");
        sb.AppendLine();
        AppendToolFamilies(sb, handles);
        AppendServerPrompts(sb, handles);
        AppendDisambiguationRules(sb, handles, resolutions);
        AppendApprovedResolutions(sb, resolutions);
        return sb.ToString();
    }

    public static string GenerateRouterPrompt(McpServerHandle handle)
    {
        var server = handle.Config.Name;
        var tools = handle.Tools;
        var count = tools.Count;
        var samples = tools.Take(3).Select(t => $"[{t.Name}]").ToList();
        var sampleStr = samples.Count > 0 ? string.Join("", samples) : "无";
        var estimatedTokens = EstimateTokens(tools);
        var sb = new StringBuilder();
        sb.AppendLine($"该工具为 {server} 业务的路由工具，调取该工具，即可获取所有相关工具。示例工具为 {sampleStr}，共含有 {count} 个工具，预计产生 {estimatedTokens} tokens 上下文调用。");
        sb.AppendLine("禁止使用 shell（bash/pwsh 等）代替路由工具或普通 MCP 工具，除非工具调用失败。");
        return sb.ToString().TrimEnd();
    }

    public static int EstimateTokens(IReadOnlyList<McpClientTool> tools)
    {
        var chars = 0;
        foreach (var tool in tools)
        {
            chars += tool.Name.Length;
            chars += tool.Description?.Length ?? 0;
            chars += 60;
        }
        return Math.Max(40, chars / 4 + tools.Count * 20);
    }

    private static void AppendToolFamilies(StringBuilder sb, IReadOnlyList<McpServerHandle> handles)
    {
        sb.AppendLine("## 1. 已装载工具族与分工概览");
        if (handles.Count == 0)
        {
            sb.AppendLine("当前没有启用的 MCP 服务。");
            sb.AppendLine();
            return;
        }
        foreach (var handle in handles)
        {
            var toolList = handle.Tools.Count > 0
                ? string.Join(", ", handle.Tools.Select(t => t.Name))
                : "无公开工具";
            sb.AppendLine($"- 服务 [{handle.Config.Name}] ({handle.Config.Transport}) 提供 {handle.Tools.Count} 个工具：{toolList}。");
            sb.AppendLine($"  {GenerateRouterPrompt(handle)}");
        }
        sb.AppendLine();
    }

    private static void AppendServerPrompts(StringBuilder sb, IReadOnlyList<McpServerHandle> handles)
    {
        sb.AppendLine("## 2. Server 自带提示词与指引");
        var hasPrompts = false;
        foreach (var handle in handles)
        {
            foreach (var prompt in handle.Prompts)
            {
                hasPrompts = true;
                var desc = !string.IsNullOrWhiteSpace(prompt.Description) ? prompt.Description : "无额外说明";
                sb.AppendLine($"- [{handle.Config.Name}/{prompt.Name}]：{desc}。");
            }
        }
        if (!hasPrompts)
            sb.AppendLine("当前已连接的 MCP 服务未声明内置提示词。");
        sb.AppendLine();
    }

    private static void AppendDisambiguationRules(
        StringBuilder sb,
        IReadOnlyList<McpServerHandle> handles,
        IReadOnlyList<McpConflictResolution> resolutions)
    {
        sb.AppendLine("## 3. 工具分工与冲突消歧规则");
        sb.AppendLine("- 当存在多个功能相近的工具时，优先查阅本节规则后再决定调用目标。");
        var hasIde = handles.Any(h => McpSemanticAnalyzer.IsIdeServer(h.Config.Name));
        if (hasIde)
            sb.AppendLine("- 当涉及代码语法分析、语义查找或重构操作时，优先使用 IDE 扩展 MCP 工具代替内置普通工具。");
        var redundancies = resolutions
            .Where(r => r.Kind == McpConflictKind.SemanticRedundancy)
            .ToList();
        foreach (var item in redundancies)
            sb.AppendLine($"- 针对服务 [{string.Join(" 与 ", item.ServerNames)}] 的重叠工具：建议以 [{item.Target}] 为主入口。");
        sb.AppendLine();
    }

    private static void AppendApprovedResolutions(StringBuilder sb, IReadOnlyList<McpConflictResolution> resolutions)
    {
        sb.AppendLine("## 4. 已批准生效的消解决策");
        var approved = resolutions.Where(r => r.Status == McpResolutionStatus.Approved).ToList();
        if (approved.Count == 0)
        {
            sb.AppendLine("暂无经用户批准生效的消解决策，所有工具保持默认调度。");
            sb.AppendLine();
            return;
        }
        foreach (var item in approved)
            sb.AppendLine($"- 决策 [{item.Id}]：针对服务 [{string.Join(", ", item.ServerNames)}] 执行 [{item.Action}]，目标为 [{item.Target}]。");
        sb.AppendLine();
    }
}
