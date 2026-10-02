using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;

namespace Dsh.SessionQuery;

/** session_search:在历史会话(含当前进程活会话)里做全文检索。 */
public sealed record SessionSearchResult(IReadOnlyList<SessionQueryHit> Hits);

public static class ToolSessionQueryTool
{
    public const string ToolName = "session_search";
    public const int DefaultLimit = 20;
    public const int MaxLimit = 100;

    private const string SectionText =
        "Use the session_search tool to find earlier conversation history (user/assistant/tool text) "
        + "by keyword; it returns session ids with matched snippets. Open a hit with /session <id>.";

    public static IDisposable Apply(Context ctx, object? config = null)
    {
        var limit = config is IReadOnlyDictionary<string, object?> dict
            && dict.GetValueOrDefault("limit") is long configured
                ? (int)Math.Clamp(configured, 1, MaxLimit)
                : DefaultLimit;
        return Register(ctx, limit);
    }

    public static IDisposable Register(Context ctx, int limit)
    {
        var service = ctx.Get<SessionQueryService>(SessionQueryService.ServiceName)
            ?? throw new InvalidOperationException("sessionQuery service is not registered");
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        var systemPrompt = ctx.Get<SystemPrompt>(SystemPrompt.ServiceName)!;
        var section = systemPrompt.Section(PromptSection.Literal("tool:session_search", SectionText));
        var registration = tools.Register(new ToolDefinition
        {
            Name = ToolName,
            Description = "Search earlier conversation history for a keyword and return matching sessions with snippets.",
            Parameters = JsonNode.Parse($$"""
                {
                  "type": "object",
                  "additionalProperties": false,
                  "required": ["query"],
                  "properties": {
                    "query": { "type": "string", "description": "Keywords to search for in user, assistant and tool text." },
                    "limit": { "type": "integer", "minimum": 1, "maximum": {{MaxLimit}}, "description": "Maximum hits; defaults to {{limit}}." }
                  }
                }
                """)!.AsObject(),
            Output = new ToolOutputDefinition(
                JsonNode.Parse("""
                    {
                      "type": "object",
                      "additionalProperties": false,
                      "required": ["hits"],
                      "properties": {
                        "hits": {
                          "type": "array",
                          "items": {
                            "type": "object",
                            "additionalProperties": false,
                            "required": ["sessionId", "seq", "type", "snippet"],
                            "properties": {
                              "sessionId": { "type": "string" },
                              "seq": { "type": "integer" },
                              "type": { "type": "string" },
                              "snippet": { "type": "string" }
                            }
                          }
                        }
                      }
                    }
                    """)!.AsObject(),
                (_, value) =>
                [
                    new TextBlock(
                        value.GetProperty("hits").GetArrayLength() == 0
                            ? "No matching history."
                            : string.Join('\n', value.GetProperty("hits").EnumerateArray()
                                .Select(hit => $"{hit.GetProperty("sessionId").GetString()} [{hit.GetProperty("type").GetString()}] {hit.GetProperty("snippet").GetString()}"))),
                ]),
            Execute = (args, _) => Execute(args, service, limit),
        });
        return new Registration(registration, section);
    }

    private static Task<object?> Execute(JsonElement args, SessionQueryService service, int defaultLimit)
    {
        var query = args.TryGetProperty("query", out var queryElement) ? queryElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(query))
            throw new InvalidOperationException("session_search requires a non-empty query");
        var limit = args.TryGetProperty("limit", out var limitElement) && limitElement.TryGetInt32(out var requested)
            ? Math.Clamp(requested, 1, MaxLimit)
            : defaultLimit;
        return Task.FromResult<object?>(new SessionSearchResult(service.Search(query, limit)));
    }

    private sealed class Registration(IDisposable registration, IDisposable section) : IDisposable
    {
        public void Dispose()
        {
            section.Dispose();
            registration.Dispose();
        }
    }
}
