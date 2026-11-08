using System.Text.Json;
using System.Text.RegularExpressions;
using Dsh.Core;

namespace Dsh.Interaction;

/** 审批提示的展示辅助: 从工具参数里挑出命令/路径这类主参数, 并给出粗粒度的影响提示。GUI 弹窗与 TUI 提示行共用。 */
public static class ApprovalHints
{
    private const int MaxArgumentChars = 400;
    private const int MaxImpactReasonChars = 60;

    private static readonly string[] PrimaryKeys =
        ["command", "cmd", "script", "path", "file_path", "filePath", "url", "pattern", "query", "content"];

    private static readonly (string Pattern, string Hint)[] ImpactRules =
    [
        (@"\brm\b|\bdel\b|\brmdir\b|Remove-Item|shutil\.rmtree", "⚠ 可能删除文件，不可撤销"),
        (@"\bmv\b|Move-Item|\bmove\b", "⚠ 可能移动或覆盖文件"),
        (@"git\s+(push|reset|clean)|--force\b", "⚠ 可能改写远端或本地提交历史"),
        (@"\bformat\b|mkfs|diskpart|fdisk", "⚠ 可能格式化磁盘"),
        (@"DROP\s+(TABLE|DATABASE)|TRUNCATE", "⚠ 可能清空数据库对象"),
        (@"shutdown|reboot|Stop-Computer", "⚠ 可能关闭或重启机器"),
        (@">\s*[^\s>]|Out-File|Set-Content", "⚠ 可能覆盖文件内容"),
        (@"curl|Invoke-WebRequest|wget|npm\s+install|pip\s+install", "⚠ 会访问网络"),
    ];

    public static string PrimaryArgument(string toolName, string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return "";
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return Trim(argumentsJson, MaxArgumentChars);
            foreach (var key in PrimaryKeys)
            {
                if (TryRead(document.RootElement, key) is { Length: > 0 } value)
                    return Trim(value, MaxArgumentChars);
            }
            return Trim(argumentsJson, MaxArgumentChars);
        }
        catch (JsonException)
        {
            return Trim(argumentsJson, MaxArgumentChars);
        }
    }

    public static string Impact(string toolName, string? argumentsJson)
    {
        var subject = PrimaryArgument(toolName, argumentsJson);
        if (subject.Length == 0)
            return "";
        foreach (var (pattern, hint) in ImpactRules)
        {
            if (Regex.IsMatch(subject, pattern, RegexOptions.IgnoreCase))
                return hint;
        }
        return "";
    }

    public static string RequestLine(ApprovalRequest request)
        => string.IsNullOrWhiteSpace(request.Reason)
            ? $"智能体请求执行工具 {request.ToolName}"
            : $"智能体请求执行工具 {request.ToolName} · {Trim(request.Reason, MaxImpactReasonChars)}";

    public static string SettingsDiff(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return "";
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("settingsMutation", out var marker) || marker.ValueKind != JsonValueKind.True)
                return "";
            var patch = root.TryGetProperty("diff", out var diff) && diff.ValueKind == JsonValueKind.String
                ? diff.GetString() ?? "" : "";
            return $"{patch}\nbeforeHash={TryRead(root, "beforeHash")}\nafterHash={TryRead(root, "afterHash")}";
        }
        catch (JsonException)
        {
            return "";
        }
    }


    private static string? TryRead(JsonElement element, string key)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase))
                continue;
            return property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                _ => property.Value.GetRawText(),
            };
        }
        return null;
    }

    private static string Trim(string text, int limit)
        => text.Length <= limit ? text : text[..limit] + "…";
}
