using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Core;
using Dsh.Llm;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ContentBlock = Dsh.Llm.ContentBlock;

namespace Dsh.Mcp;

/** 把 MCP server 的工具适配成 agent 原生工具(ToolDefinition),经 ToolRuntime 注册后走统一的 schema/审批/dispatch 管道。 */
public static class McpToolBridge
{
    public const string ErrorCode = "MCP_TOOL_ERROR";

    public static ToolDefinition Wrap(string serverName, McpClientTool tool)
        => Wrap(serverName, tool.Name, tool.Description, tool.JsonSchema,
            (arguments, signal) => tool.CallAsync(arguments, null, null, signal));

    public static ToolDefinition Wrap(
        string serverName,
        string toolName,
        string? description,
        JsonElement inputSchema,
        Func<IReadOnlyDictionary<string, object?>?, CancellationToken, ValueTask<CallToolResult>> call)
        => new()
        {
            Name = $"mcp_{Sanitize(serverName)}_{Sanitize(toolName)}",
            Description = string.IsNullOrWhiteSpace(description) ? toolName : description,
            Parameters = SchemaOrDefault(inputSchema),
            Output = new ToolOutputDefinition(OutputSchema(), Render),
            Execute = (args, exec) => ExecuteAsync(call, args, exec),
        };

    internal static string Sanitize(string name)
        => string.Concat(name.Select(character => char.IsLetterOrDigit(character) || character == '_' ? character : '_'));

    private static async Task<object?> ExecuteAsync(
        Func<IReadOnlyDictionary<string, object?>?, CancellationToken, ValueTask<CallToolResult>> call,
        JsonElement args,
        ToolRunContext exec)
    {
        var arguments = args.ValueKind == JsonValueKind.Object
            ? args.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value)
            : null;
        var result = await call(arguments, exec.Signal);
        if (result.IsError == true)
            throw new HarnessException(ErrorText(result), ErrorCode);
        return JsonSerializer.SerializeToElement(result, McpJsonUtilities.DefaultOptions);
    }

    private static string ErrorText(CallToolResult result)
    {
        var text = string.Join('\n', result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        return text.Length > 0 ? $"mcp tool failed: {text}" : "mcp tool failed";
    }

    private static IReadOnlyList<ContentBlock> Render(JsonElement args, JsonElement value)
    {
        if (!value.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return [new TextBlock(value.GetRawText())];
        var texts = content.EnumerateArray()
            .Where(block => block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            .Select(block => block.GetProperty("text").GetString()!)
            .ToList();
        return texts.Count > 0
            ? [new TextBlock(string.Join('\n', texts))]
            : [new TextBlock(value.GetRawText())];
    }

    private static JsonObject SchemaOrDefault(JsonElement schema)
        => schema.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(schema.GetRawText())!.AsObject()
            : OutputSchema();

    private static JsonObject OutputSchema() => new() { ["type"] = "object" };
}
