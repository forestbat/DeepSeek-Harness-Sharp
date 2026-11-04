using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dsh.Interaction;

/** 工具调用与结果的展示文本: 把转义过的 JSON 还原成可读字符, shell 工具优先展示命令原文。 */
public static class ToolPresentation
{
    /** 只做转义还原: 把 \u0022 / \u6D4B 这类 Unicode 转义还原成原字符。 */
    private static readonly JsonSerializerOptions UnescapedJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions IndentedJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    private const int IndentLimit = 2000;

    public static string FormatArguments(string? rawArguments)
    {
        if (string.IsNullOrWhiteSpace(rawArguments))
            return "";
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(rawArguments);
        }
        catch (JsonException)
        {
            return rawArguments.Trim();
        }
        if (node is not JsonObject arguments)
            return Render(node);
        if (arguments["command"] is JsonValue command && command.TryGetValue<string>(out var text))
            return FormatShell(arguments, text);
        return Render(arguments);
    }

    public static string FormatResult(string text)
    {
        var stripped = UserFacingText.StripSystemReminders(text);
        if (stripped.Length == 0)
            return "(no output)";
        // 只有看起来像 JSON 且确实含转义的结果才重新序列化, 其余原样显示, 避免改动正文。
        if (stripped[0] is not ('{' or '[')
            || (stripped.IndexOf("\\u", StringComparison.Ordinal) < 0
                && stripped.IndexOf("\\\"", StringComparison.Ordinal) < 0))
            return stripped;
        try
        {
            var node = JsonNode.Parse(stripped);
            return node is null ? stripped : node.ToJsonString(UnescapedJson);
        }
        catch (JsonException)
        {
            return stripped;
        }
    }

    /** 小对象缩进展示便于阅读, 大对象保持单行, 避免一条工具调用撑满屏幕。 */
    private static string Render(JsonNode? node)
    {
        if (node is null)
            return "";
        var compact = node.ToJsonString(UnescapedJson);
        return compact.Length <= IndentLimit ? node.ToJsonString(IndentedJson) : compact;
    }

    private static string FormatShell(JsonObject arguments, string command)
    {
        var lines = new List<string> { command.TrimEnd() };
        foreach (var (name, value) in arguments)
        {
            if (name == "command")
                continue;
            lines.Add($"{name}: {Scalar(value)}");
        }
        return string.Join('\n', lines);
    }

    private static string Scalar(JsonNode? value) => value switch
    {
        null => "",
        JsonValue scalar when scalar.TryGetValue<string>(out var text) => text,
        JsonValue scalar => scalar.ToJsonString(),
        _ => value.ToJsonString(UnescapedJson),
    };
}
