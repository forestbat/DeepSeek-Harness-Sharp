using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Agent;

namespace Dsh.Subagent;

/** list_subagent_models: the LLM routes a subagent can run on, read live from the registered (keyed) providers. */
public static class SubagentModelsTool
{
    public const string ToolName = "list_subagent_models";

    private const string ProviderField = "provider";
    private const string ModelField = "model";
    private const string NameField = "name";
    private const string EffortsField = "reasoningEfforts";
    private const string DefaultEffortField = "defaultReasoningEffort";

    private const string SectionText =
        "list_subagent_models lists the LLM routes a subagent can run on: the models of every provider that has an API key configured. "
        + "Call it before delegating to a model other than the default, for example when the user asks for a stronger, cheaper or different model for delegated work. "
        + "Pass one listed `provider` and `model` together to the subagent tool, optionally with one of that route's reasoning efforts as `reasoning_effort`; omit all three to keep the default route.";

    private const string Description =
        "List the LLM routes available to subagents without changing the current agent. "
        + "Each row is one provider and model (with its reasoning efforts when advertised) that can be passed unchanged as the subagent tool's `provider`, `model` and `reasoning_effort`. "
        + "Only providers with a configured API key are listed. Pass `provider` to list a single provider's models.";

    public static IDisposable Apply(Context ctx)
    {
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        var systemPrompt = ctx.Get<SystemPrompt>(SystemPrompt.ServiceName)!;
        var section = systemPrompt.Section(PromptSection.Literal($"tool:{ToolName}", SectionText));
        var registration = tools.Register(Definition(ctx));
        return new Registration(registration, section);
    }

    private static ToolDefinition Definition(Context ctx) => new()
    {
        Name = ToolName,
        Description = Description,
        Parameters = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = new JsonObject
            {
                [ProviderField] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Registered LLM provider id. Omit to list the routes of every provider.",
                },
            },
        },
        Output = new ToolOutputDefinition(OutputSchema, (_, value) => [new TextBlock(Render(value))]),
        IsConcurrencySafe = _ => true,
        Execute = (args, _) => Task.FromResult<object?>(ListRoutes(ctx, args)),
    };

    private static readonly JsonObject OutputSchema = new()
    {
        ["type"] = "array",
        ["items"] = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray(ProviderField, ModelField, NameField),
            ["properties"] = new JsonObject
            {
                [ProviderField] = new JsonObject { ["type"] = "string" },
                [ModelField] = new JsonObject { ["type"] = "string" },
                [NameField] = new JsonObject { ["type"] = "string" },
                [EffortsField] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                [DefaultEffortField] = new JsonObject { ["type"] = "string" },
            },
        },
    };

    private static JsonArray ListRoutes(Context ctx, JsonElement args)
    {
        var llm = ctx.Get<LlmRuntime>(LlmRuntime.ServiceName, false)
            ?? throw new InvalidOperationException("cannot list subagent LLM routes because the `llm` service is unavailable");
        var providers = llm.ListProviders().Select(provider => provider.Id).Order(StringComparer.Ordinal).ToList();
        var filter = args.TryGetProperty(ProviderField, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
        if (filter is not null && !providers.Contains(filter))
        {
            var available = providers.Count == 0 ? "(none)" : string.Join(", ", providers);
            throw new InvalidOperationException(
                $"LLM provider \"{filter}\" is not registered; providers with a configured API key: {available}");
        }
        var rows = new JsonArray();
        foreach (var provider in providers.Where(provider => filter is null || provider == filter))
        {
            foreach (var model in llm.ListModels(provider))
                rows.Add(Row(model, llm.ResolveModelInfo(provider, model.Id).Reasoning));
        }
        return rows;
    }

    private static JsonObject Row(LlmModelInfo model, LlmModelReasoningInfo? reasoning)
    {
        var row = new JsonObject
        {
            [ProviderField] = model.Provider,
            [ModelField] = model.Id,
            [NameField] = model.Name,
        };
        if (reasoning is not { Efforts.Count: > 0 })
            return row;
        row[EffortsField] = new JsonArray([.. reasoning.Efforts.Select(effort => JsonValue.Create(effort.Id.Value))]);
        if (reasoning.DefaultEffort is { } defaultEffort)
            row[DefaultEffortField] = defaultEffort.Value;
        return row;
    }

    private static string Render(JsonElement value)
    {
        if (value.GetArrayLength() == 0)
            return "(no LLM routes: no provider with a configured API key advertises models)";
        var lines = value.EnumerateArray().Select(row =>
        {
            var line = $"provider: {row.GetProperty(ProviderField).GetString()}, model: {row.GetProperty(ModelField).GetString()} — {row.GetProperty(NameField).GetString()}";
            if (!row.TryGetProperty(EffortsField, out var efforts))
                return line;
            var defaultEffort = row.TryGetProperty(DefaultEffortField, out var fallback) ? $" (default {fallback.GetString()})" : "";
            return $"{line}; reasoning_effort: {string.Join(", ", efforts.EnumerateArray().Select(effort => effort.GetString()))}{defaultEffort}";
        });
        return string.Join("\n", lines);
    }

    private sealed class Registration(IDisposable tool, IDisposable section) : IDisposable
    {
        public void Dispose()
        {
            tool.Dispose();
            section.Dispose();
        }
    }
}
