using System.Text.Json;
using System.Text.Json.Nodes;
using Cordis;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Interaction.AskUser;

public static class AskUserTool
{
    public const string PluginName = "tool-ask-user";

    private const string Description =
        "Ask the user a concise question when you need confirmation, a choice, or missing information before proceeding. "
        + "Send one or more questions, each with a stable id that will be echoed in the answer.";

    public static IDisposable Register(Context ctx)
    {
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)
            ?? throw new InvalidOperationException("tool-ask-user requires the tools service");
        return tools.Register(new ToolDefinition
        {
            Name = "ask_user_question",
            Description = Description,
            Parameters = JsonNode.Parse("""
                {
                  "type": "object",
                  "properties": {
                    "questions": {
                      "type": "array",
                      "description": "Questions to ask the user before continuing.",
                      "items": {
                        "type": "object",
                        "additionalProperties": true,
                        "properties": {
                          "id": { "type": "string", "description": "Stable id for this question; echoed in the answer." },
                          "question": { "type": "string", "description": "The specific question to ask the user." },
                          "header": { "type": "string", "description": "Optional short heading for the question, such as \"Confirm\" or \"Choose Mode\"." },
                          "options": {
                            "type": "array",
                            "description": "Optional choices to show the user. If you recommend one, put it first and append \"(Recommended)\" to that label.",
                            "items": {
                              "type": "object",
                              "additionalProperties": true,
                              "properties": {
                                "label": { "type": "string", "description": "Short user-facing option label." },
                                "description": { "type": "string", "description": "One sentence explaining the tradeoff or impact." }
                              }
                            }
                          },
                          "multi_select": { "type": "boolean", "description": "Whether the user may select more than one option. Defaults to false." }
                        },
                        "required": ["id", "question"]
                      }
                    }
                  },
                  "required": ["questions"]
                }
                """)!.AsObject(),
            Output = new ToolOutputDefinition(
                JsonNode.Parse("""
                    {
                      "type": "object",
                      "additionalProperties": false,
                      "required": ["answers"],
                      "properties": {
                        "answers": {
                          "type": "array",
                          "items": {
                            "type": "object",
                            "additionalProperties": false,
                            "required": ["id", "selected"],
                            "properties": {
                              "id": { "type": "string" },
                              "selected": { "type": "array", "items": { "type": "string" } },
                              "custom": { "type": "string" }
                            }
                          }
                        }
                      }
                    }
                    """)!.AsObject(),
                (_, value) => [new TextBlock(value.GetRawText())]),
            Execute = (args, exec) => Execute(ctx, args, exec),
        });
    }

    private static async Task<object?> Execute(Context ctx, JsonElement args, ToolRunContext exec)
    {
        var questions = ParseQuestions(args);
        var userQuestions = ctx.Get<UserQuestionService>(UserQuestionService.ServiceName)
            ?? throw new InvalidOperationException("tool-ask-user requires the userQuestions service");
        var result = await userQuestions.Ask(new AskUserQuestionRequest(questions, exec.Agent), exec.Signal);
        var answers = new JsonArray();
        foreach (var answer in result.Answers)
        {
            var item = new JsonObject
            {
                ["id"] = answer.Id,
                ["selected"] = new JsonArray(answer.Selected.Select(label => (JsonNode?)JsonValue.Create(label)).ToArray()),
            };
            if (answer.Custom is not null)
                item["custom"] = answer.Custom;
            answers.Add(item);
        }
        var value = new JsonObject { ["answers"] = answers };
        return JsonDocument.Parse(value.ToJsonString()).RootElement;
    }

    private static IReadOnlyList<AskUserQuestionItem> ParseQuestions(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty("questions", out var element)
            || element.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("invalid `questions`: expected an array of questions");
        var questions = new List<AskUserQuestionItem>();
        foreach (var candidate in element.EnumerateArray())
        {
            if (candidate.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("invalid `questions`: expected an array of questions");
            questions.Add(new AskUserQuestionItem(
                StringArg(candidate, "id") ?? throw new ArgumentException("invalid `questions`: question `id` must be a string"),
                StringArg(candidate, "question") ?? throw new ArgumentException("invalid `questions`: question `question` must be a string"),
                Header: StringArg(candidate, "header"),
                Options: ParseOptions(candidate),
                MultiSelect: candidate.TryGetProperty("multi_select", out var multiSelect)
                    && multiSelect.ValueKind is JsonValueKind.True or JsonValueKind.False
                    && multiSelect.GetBoolean()));
        }
        return questions;
    }

    private static IReadOnlyList<AskUserQuestionOption>? ParseOptions(JsonElement question)
    {
        if (!question.TryGetProperty("options", out var element) || element.ValueKind != JsonValueKind.Array)
            return null;
        return element.EnumerateArray()
            .Where(option => option.ValueKind == JsonValueKind.Object)
            .Select(option => new AskUserQuestionOption(
                StringArg(option, "label") ?? throw new ArgumentException("invalid `questions`: option `label` must be a string"),
                StringArg(option, "description")))
            .ToList();
    }

    private static string? StringArg(JsonElement element, string name)
        => element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}
