using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;

namespace Dsh.Inspection;

/** Plugin-development guidance and read-only runtime inspection tools. */
public static class ToolCreative
{
    public const string ListToolName = "creative_inspect_list";
    public const string QueryToolName = "creative_inspect_query";

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    private const string ListDescription =
        "List the inspectable runtime directory in four categories: service (plugin-provided services and their "
        + "provider state), event (notification listener table), config (settings.yaml sections), and tool (tool "
        + "schemas visible to this agent; each config item is prefixed [set]/[default]/[unknown] and uses path keys " 
        + "like logging.keep_days or plugins.\"@deepseek-ai/dsh-toon\".input). Read-only; the event category is " 
        + "not wired yet. Call "
        + "creative_inspect_query with a category and a name from the result for details.";

    private const string QueryDescription =
        "Read one inspectable item selected by category and name, both taken from creative_inspect_list. Read-only: it "
        + "never modifies runtime state and cannot invoke services or tools. For config, name is a path key: it " 
        + "returns the settings.yaml value, or the schema default when unset.";

    private const string SectionText = """
        Use creative_inspect_list and creative_inspect_query for read-only inspection of services, settings, and scope-visible tools; event inspection is unavailable.
        Apply the same development workflow to custom user plugins and preinstalled Dsh.* plugins.
        Build and validate in fresh subprocesses with separate output and intermediate directories, for example dotnet build <project> --artifacts-path <isolated-build-root>, never over binaries held by an existing daemon or GUI.
        Launch the isolated built executable directly with a fresh DSH_HOME, clear inherited DSH_HARNESS_HOME, DSH_PTY_CHILD and DSH_PTY_SESSION_ID, and keep storage.root, storage.run and any .storage-root marker from redirecting into existing state.
        Do not attach a development instance to an existing daemon, GUI, endpoint or session.
        Before choosing process handling, actively call ask_user_question unless the user has already authorized that choice for this task.
        Offer an isolated new instance, a restart of an explicitly identified owned instance, and automatic isolated runs or automatic restarts of that specified owned instance for the rest of this task.
        Reuse the selected authorization without repeated workflow questions only within its stated scope; it does not bypass tool approvals, restrictions or cancellation.
        After an answer, perform the authorized work through available execution tools such as bash or pwsh and owned terminal_open/terminal_send sessions, not through inspection tools.
        For an authorized owned-terminal restart, use terminal_close, wait for its process tree to exit, then open a replacement and launch the isolated executable.
        Never broadly kill processes, restart another instance without explicit authority, or treat a dismissed question as authorization.
        If question or execution tools are unavailable, report the missing capability and request the decision in chat rather than claiming a process was started or restarted.
        """;

    internal static IDisposable Register(Context ctx)
    {
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        var prompt = ctx.Get<SystemPrompt>(SystemPrompt.ServiceName)!;
        return new DisposableBundle(
            tools.Register(ListDefinition(ctx)),
            tools.Register(QueryDefinition(ctx)),
            prompt.Section(PromptSection.Literal("tool:creative", SectionText)));
    }

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
                  "description": "Exact category returned by creative_inspect_list."
                },
                "name": { "type": "string", "description": "Exact item name returned by creative_inspect_list." }
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
