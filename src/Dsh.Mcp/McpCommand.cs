using Dsh.Runtime;
using Dsh.Boot;
using Dsh.Interaction;

namespace Dsh.Mcp;

public static class McpCommand
{
    public static IDisposable Register(Context ctx, McpService service, HarnessHome home)
    {
        var commands = ctx.Get<CommandsService>(CommandsService.ServiceName)!;
        return commands.Register(new CommandDefinition
        {
            Name = "mcp",
            Description = "Show MCP server status; /mcp reload, /mcp resolutions, /mcp resolve, /mcp tips",
            Handler = async invocation =>
            {
                var input = invocation.RawInput.Trim();
                if (string.Equals(input, "reload", StringComparison.OrdinalIgnoreCase))
                {
                    var servers = McpServerConfig.FromSettings(HarnessSettings.Load(home));
                    await service.ReloadAsync(servers, invocation.Signal);
                    return new CommandResult.Success(Format(service.Status(), service.GovernanceReport));
                }
                if (string.Equals(input, "resolutions", StringComparison.OrdinalIgnoreCase))
                    return new CommandResult.Success(service.Store.FormatDiff());
                if (input.StartsWith("resolve", StringComparison.OrdinalIgnoreCase))
                    return await HandleResolveAsync(service, input, invocation.Signal);
                if (string.Equals(input, "tips", StringComparison.OrdinalIgnoreCase))
                    return await HandleTipsAsync(service);
                return new CommandResult.Success(Format(service.Status(), service.GovernanceReport));
            },
        });
    }

    private static async Task<CommandResult> HandleResolveAsync(
        McpService service,
        string input,
        CancellationToken signal)
    {
        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
            return new CommandResult.Error("用法: /mcp resolve <resolution-id> approve|reject");
        var id = parts[1];
        var action = parts[2];
        if (string.Equals(action, "approve", StringComparison.OrdinalIgnoreCase))
        {
            var ok = await service.ApproveResolutionAsync(id, signal);
            return ok
                ? new CommandResult.Success($"已批准并采纳建议 [{id}]，调和指引与工具列表已更新。")
                : new CommandResult.Error($"未找到待处理的建议 [{id}]。");
        }
        if (string.Equals(action, "reject", StringComparison.OrdinalIgnoreCase))
        {
            var ok = await service.RejectResolutionAsync(id, signal);
            return ok
                ? new CommandResult.Success($"已驳回建议 [{id}]。")
                : new CommandResult.Error($"未找到待处理的建议 [{id}]。");
        }
        return new CommandResult.Error("无效动作，只支持 approve 或 reject。");
    }

    private static async Task<CommandResult> HandleTipsAsync(McpService service)
    {
        var report = service.GovernanceReport;
        if (report is null)
            return new CommandResult.Success("尚未生成 DSH_TOOLS_TIPS.md。");
        var content = File.Exists(report.TipsPath)
            ? await File.ReadAllTextAsync(report.TipsPath)
            : "提示词文件尚未落盘。";
        return new CommandResult.Success($"[调和提示词文件: {report.TipsPath}]\n\n{content}");
    }

    internal static string Format(IReadOnlyList<McpServerStatus> statuses, McpGovernanceReport? report = null)
    {
        if (statuses.Count == 0)
            return "no mcp servers enabled";
        var lines = statuses.Select(status => status.Connected
            ? $"{status.Name}: {status.Transport} connected ({status.ToolCount} tools registered{(status.Resources ? ", resources" : "")})"
            : $"{status.Name}: {status.Transport} error: {status.Error}").ToList();
        if (report is not null)
        {
            var pending = report.Resolutions.Count(r => r.Status == McpResolutionStatus.Pending);
            var approved = report.Resolutions.Count(r => r.Status == McpResolutionStatus.Approved);
            lines.Add($"MCP 治理概况: 检测到 {report.Conflicts.Count} 项冲突/建议 (待审批: {pending}, 已批准生效: {approved})");
            lines.Add($"调和提示词: {report.TipsPath}");
        }
        return string.Join('\n', lines);
    }
}
