using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;

namespace Dsh.Inspection;

/** cordis_inspect_list / cordis_inspect_query:创造模式的运行时只读检查工具(保留上游工具名以对齐生态)。 */
public static class ToolCordis
{
    public const string ListToolName = "cordis_inspect_list";
    public const string QueryToolName = "cordis_inspect_query";

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    private const string ListDescription =
        "List the inspectable runtime directory in four categories: service (plugin-provided services and their "
        + "provider state), event (notification listener table), config (settings.yaml sections), and tool (tool "
        + "schemas visible to this agent). Read-only; the event category is reported unavailable in this build. Call "
        + "cordis_inspect_query with a category and an item name from the result for details.";

    private const string QueryDescription =
        "Read one inspectable item selected by category and name, both taken from cordis_inspect_list. Read-only: it "
        + "never modifies runtime state and cannot invoke services or tools; event queries fail in this build.";

    internal static IReadOnlyList<ToolDefinition> Definitions(Context ctx)
        => [ListDefinition(ctx), QueryDefinition(ctx)];

    private static ToolDefinition ListDefinition(Context ctx) => new()
    {
        Name = ListToolName,
        Description = ListDescription,
        Parameters = JsonNode.Parse("""
            {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "category": {
                  "type": "string",
                  "enum": ["service", "event", "config", "tool"],
                  "description": "Optional category to list; omit to list all four."
                }
              }
            }
            """)!.AsObject(),
        Output = new ToolOutputDefinition(
            JsonNode.Parse("""
                {
                  "type": "object",
                  "additionalProperties": false,
                  "required": ["categories"],
                  "properties": {
                    "categories": {
                      "type": "array",
                      "items": {
                        "type": "object",
                        "additionalProperties": false,
                        "required": ["category", "available", "items"],
                        "properties": {
                          "category": { "type": "string" },
                          "available": { "type": "boolean" },
                          "reason": { "type": "string" },
                          "items": {
                            "type": "array",
                            "items": {
                              "type": "object",
                              "additionalProperties": false,
                              "required": ["name", "detail"],
                              "properties": {
                                "name": { "type": "string" },
                                "detail": { "type": "string" }
                              }
                            }
                          }
                        }
                      }
                    }
                  }
                }
                """)!.AsObject(),
            RenderJson),
        Execute = (args, exec) => ExecuteList(ctx, args, exec),
    };

    private static ToolDefinition QueryDefinition(Context ctx) => new()
    {
        Name = QueryToolName,
        Description = QueryDescription,
        Parameters = JsonNode.Parse("""
            {
              "type": "object",
              "additionalProperties": false,
              "required": ["category", "name"],
              "properties": {
                "category": {
                  "type": "string",
                  "enum": ["service", "event", "config", "tool"],
                  "description": "Exact category returned by cordis_inspect_list."
                },
                "name": { "type": "string", "description": "Exact item name returned by cordis_inspect_list." }
              }
            }
            """)!.AsObject(),
        Output = new ToolOutputDefinition(
            JsonNode.Parse("""
                {
                  "type": "object",
                  "additionalProperties": false,
                  "required": ["category", "name", "data"],
                  "properties": {
                    "category": { "type": "string" },
                    "name": { "type": "string" },
                    "data": { "description": "Category-specific JSON detail." }
                  }
                }
                """)!.AsObject(),
            RenderJson),
        Execute = (args, exec) => ExecuteQuery(ctx, args, exec),
    };

    private static Task<object?> ExecuteList(Context ctx, JsonElement args, ToolRunContext exec)
    {
        var category = args.TryGetProperty("category", out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
        if (category is not null && !InspectCatalog.Categories.Contains(category, StringComparer.Ordinal))
            throw new InvalidOperationException(
                $"unknown inspect category \"{category}\"; expected one of {string.Join(", ", InspectCatalog.Categories)}");
        return Task.FromResult<object?>(InspectCatalog.List(ctx, exec.Agent?.ScopeKey, category));
    }

    private static Task<object?> ExecuteQuery(Context ctx, JsonElement args, ToolRunContext exec)
    {
        var category = RequireString(args, "category");
        var name = RequireString(args, "name");
        return Task.FromResult<object?>(InspectCatalog.Query(ctx, exec.Agent?.ScopeKey, category, name));
    }

    private static string RequireString(JsonElement args, string field)
    {
        var value = args.TryGetProperty(field, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{QueryToolName} requires a non-empty \"{field}\"");
        return value;
    }

    private static IReadOnlyList<ContentBlock> RenderJson(JsonElement _, JsonElement value)
        => [new TextBlock(JsonSerializer.Serialize(JsonNode.Parse(value.GetRawText()), Pretty))];
}
