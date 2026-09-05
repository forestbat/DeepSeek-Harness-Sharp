using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cordis;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Subagent;

public sealed record SubagentToolConfig
{
    public required string Provider { get; init; }
    public string ToolName { get; init; } = SubagentTool.DefaultToolName;
    public bool EnableRunInBackground { get; init; } = true;
    public string BackgroundMode { get; init; } = "one-shot";
    public AgentOptions? AgentOptions { get; init; }
    public string? Persona { get; init; }
    public ToolRestriction? ToolFilter { get; init; }
    public int? MaxDepth { get; init; } = 3;
}

public static class SubagentTool
{
    public const string DefaultToolName = "subagent";

    private const string SpawnDescription =
        "Delegate a self-contained task to a subagent (a separate agent that works in its own context) "
        + "to offload focused, independent work — research, a scoped implementation, an analysis — "
        + "so it does not consume this conversation's context. The subagent returns its result, not its "
        + "intermediate steps. Give it a complete, standalone prompt: it does not see this conversation.";

    private const string ForkDescription =
        "Delegate a task to a forked subagent (a separate agent that inherits this conversation's context, "
        + "so you can delegate without restating context). The subagent returns its result, not its "
        + "intermediate steps. Forked context is a copy, not a link: nothing the subagent does changes this "
        + "conversation.";

    private const string BackgroundSuffix =
        " Set `run_in_background: true` to get a job id back immediately instead of waiting for the result.";

    private const string RunInBackgroundDescription =
        "Set to `true` to run this subagent in the background. `true` returns a background job id immediately; "
        + "`false` waits and returns the subagent's final reply.";

    private const string BackgroundJobsUnavailable =
        "background jobs unavailable: load @deepseek-ai/dsh-jobs and @deepseek-ai/dsh-tool-jobs";

    public static IDisposable Apply(Context ctx, SubagentToolConfig config)
    {
        if (config.MaxDepth is < 0)
            throw new ArgumentException("subagent maxDepth must be a non-negative safe integer");
        if (config.ToolFilter is { Allow: null, Deny: null })
        {
            throw new InvalidOperationException(
                "tool-subagent: `toolFilter` is configured but names neither `allow` nor `deny` — "
                + "remove the key or fill the filter");
        }
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        var subagents = ctx.Get<SubagentRuntime>(SubagentRuntime.ServiceName)!;
        var continuable = config.BackgroundMode == "continuable";
        var mount = new MountState();
        void Mount(ISubagentProvider provider)
        {
            AssertProviderConfiguration(config, provider, continuable);
            mount.Set(provider, tools.Register(BuildDefinition(ctx, config, provider)));
        }
        var removeAdded = ctx.On(SubagentRuntime.ProviderAddedEvent, (_, args) =>
        {
            if (args[0] is ISubagentProvider provider && provider.Name == config.Provider && !ReferenceEquals(mount.Provider, provider))
                Mount(provider);
            return new ValueTask<object?>();
        });
        var removeRemoved = ctx.On(SubagentRuntime.ProviderRemovedEvent, (_, args) =>
        {
            if (args[0] is string name && name == config.Provider)
                mount.Clear();
            return new ValueTask<object?>();
        });
        var initial = subagents.GetProvider(config.Provider);
        if (initial is not null)
        {
            Mount(initial);
        }
        else
        {
            ctx.Logger.Info(
                $"tool-subagent: provider \"{config.Provider}\" is not registered yet; the tool stays unmounted until it is");
        }
        return new DisposeBundle([mount, new FuncDispose(removeAdded), new FuncDispose(removeRemoved)]);
    }

    private static void AssertProviderConfiguration(SubagentToolConfig config, ISubagentProvider provider, bool continuable)
    {
        if (config.MaxDepth is not null && !provider.Capabilities.DepthLimit)
        {
            throw new InvalidOperationException(
                $"tool-subagent: provider \"{config.Provider}\" cannot enforce maxDepth (no depthLimit capability) — "
                + "set maxDepth: 'provider-managed' to leave the recursion budget to the provider");
        }
        if (config.AgentOptions is not null && !provider.Capabilities.AgentOptions)
        {
            throw new InvalidOperationException(
                $"tool-subagent: provider \"{config.Provider}\" does not support child agentOptions");
        }
        if (continuable)
        {
            throw new InvalidOperationException(
                $"tool-subagent: provider \"{config.Provider}\" does not support `backgroundMode: continuable`");
        }
    }

    private static ToolDefinition BuildDefinition(Context ctx, SubagentToolConfig config, ISubagentProvider provider)
    {
        var description = (provider.InheritsParentContext ? ForkDescription : SpawnDescription)
            + (config.EnableRunInBackground ? BackgroundSuffix : "");
        var properties = new JsonObject
        {
            ["description"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "A short (3–5 word) description of the task to delegate.",
            },
            ["prompt"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "The prompt for the subagent. The subagent does not have any of your conversation history, "
                    + "so include all necessary information and context in the prompt.",
            },
        };
        if (config.EnableRunInBackground)
        {
            properties["run_in_background"] = new JsonObject
            {
                ["type"] = "boolean",
                ["description"] = RunInBackgroundDescription,
            };
        }
        return new ToolDefinition
        {
            Name = config.ToolName,
            Description = description,
            Parameters = new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["required"] = new JsonArray("description", "prompt"),
                ["properties"] = properties,
            },
            Output = new ToolOutputDefinition(OutputSchema, (_, value) => [new TextBlock(RenderResult(value))]),
            IsConcurrencySafe = _ => true,
            Execute = (args, runContext) => Execute(ctx, config, provider, args, runContext),
        };
    }

    private static readonly JsonObject OutputSchema = new()
    {
        ["oneOf"] = new JsonArray
        (
            new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["required"] = new JsonArray("kind", "jobId"),
                ["properties"] = new JsonObject
                {
                    ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("background") },
                    ["jobId"] = new JsonObject { ["type"] = "string" },
                },
            },
            new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["required"] = new JsonArray("kind", "subagentId"),
                ["properties"] = new JsonObject
                {
                    ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("continuable") },
                    ["subagentId"] = new JsonObject { ["type"] = "string" },
                },
            },
            new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["required"] = new JsonArray("kind", "runId", "output"),
                ["properties"] = new JsonObject
                {
                    ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("foreground") },
                    ["runId"] = new JsonObject { ["type"] = "string" },
                    ["output"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject() },
                },
            }
        ),
    };

    private static async Task<object?> Execute(
        Context ctx, SubagentToolConfig config, ISubagentProvider provider, JsonElement args, ToolRunContext exec)
    {
        var parent = exec.Agent
            ?? throw new InvalidOperationException("subagent tool requires a calling agent (exec.agent was undefined)");
        var parentOptions = ChildCompositionSupport.ParentAgentOptionsForDelegation(parent);
        if (HasConfiguredLlmSelection(config.AgentOptions))
            await PreflightChildLlmRoute(ctx, parentOptions, config.AgentOptions, exec.Signal);
        var subagents = ctx.Get<SubagentRuntime>(SubagentRuntime.ServiceName)!;
        if (!ReferenceEquals(subagents.GetProvider(config.Provider), provider))
        {
            throw new InvalidOperationException(
                $"subagent provider \"{config.Provider}\" changed during delegation; retry the call");
        }
        exec.Signal.ThrowIfCancellationRequested();
        var request = new SubagentStartRequest
        {
            Label = args.GetProperty("description").GetString(),
            Prompt = [new TextBlock(args.GetProperty("prompt").GetString() ?? "")],
            Parent = parent,
            Signal = exec.Signal,
            AgentOptions = config.AgentOptions,
            Persona = config.Persona,
            ToolFilter = config.ToolFilter,
            MaxDepth = config.MaxDepth,
        };
        if (ResolveRunInBackground(args, config))
            throw new InvalidOperationException(BackgroundJobsUnavailable);
        return await SettleForegroundRun(await subagents.StartAsync(config.Provider, request));
    }

    private static bool ResolveRunInBackground(JsonElement args, SubagentToolConfig config)
    {
        var requested = args.TryGetProperty("run_in_background", out var flag)
            && flag.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? flag.GetBoolean()
                : (bool?)null;
        if (!config.EnableRunInBackground && requested == true)
        {
            throw new InvalidOperationException(
                "run_in_background is disabled for this tool instance (enableRunInBackground: false)");
        }
        return requested ?? false;
    }

    private static bool HasConfiguredLlmSelection(AgentOptions? options)
        => options?.Provider is not null || options?.Model is not null || options?.ReasoningEffort is not null;

    private static async Task PreflightChildLlmRoute(
        Context ctx, AgentOptions parentOptions, AgentOptions? requested, CancellationToken signal)
    {
        var provider = requested?.Provider ?? parentOptions.Provider;
        var model = requested?.Model ?? parentOptions.Model;
        if (provider is null || model is null)
            throw new InvalidOperationException("cannot select child LLM values without an effective provider and model");
        var routeChanged = provider != parentOptions.Provider || model != parentOptions.Model;
        var reasoningEffort = requested?.ReasoningEffort ?? (routeChanged ? null : parentOptions.ReasoningEffort);
        var llm = ctx.Get<LlmRuntime>(LlmRuntime.ServiceName)
            ?? throw new InvalidOperationException("tool-subagent requires the llm service to preflight the child route");
        await llm.PrepareCall(new LlmCallConfig(provider, model, reasoningEffort), signal);
    }

    private static async Task<object?> SettleForegroundRun(ISubagentRun run)
    {
        JsonObject? value = null;
        Exception? executionError = null;
        try
        {
            var result = await run.Result;
            if (StopReasonError(result.StopReason) is { } failure)
                throw new InvalidOperationException(WithDiagnosticAndPartialText(failure, result));
            value = new JsonObject
            {
                ["kind"] = "foreground",
                ["runId"] = run.Id.Value,
                ["output"] = SerializeOutput(result.Output),
            };
        }
        catch (Exception error)
        {
            executionError = error;
        }
        Exception? disposalError = null;
        try
        {
            await run.DisposeAsync();
        }
        catch (Exception error)
        {
            disposalError = error;
        }
        if (executionError is not null && disposalError is not null)
        {
            throw new AggregateErrorException(
                $"subagent run failed: {executionError.Message}; dispose failed: {disposalError.Message}",
                [executionError, disposalError]);
        }
        if (executionError is not null)
            ExceptionDispatchInfo.Throw(executionError);
        if (disposalError is not null)
            ExceptionDispatchInfo.Throw(disposalError);
        return value!;
    }

    private static string? StopReasonError(SubagentStopReason stopReason) => stopReason switch
    {
        SubagentStopReason.Completed => null,
        SubagentStopReason.Aborted => "subagent run was cancelled",
        SubagentStopReason.Error => "subagent run failed",
        SubagentStopReason.MaxTokens => "subagent run hit its token limit before finishing",
        SubagentStopReason.Refusal => "subagent declined the task",
        _ => $"subagent run ended abnormally ({SubagentStopReasonWire.Of(stopReason)})",
    };

    private static string WithDiagnosticAndPartialText(string headline, SubagentResult result)
    {
        var message = headline;
        if (result.Diagnostic is not null)
            message += $"\n\nsubagent diagnostic: {result.Diagnostic}";
        var partial = OutputValueText(result.Output);
        if (partial.Length > 0)
            message += $"\n\npartial output before the failure: {partial}";
        return message;
    }

    private static JsonArray SerializeOutput(IReadOnlyList<ContentBlock> output)
    {
        var array = new JsonArray();
        foreach (var block in output)
            array.Add(JsonSerializer.SerializeToNode(block, DshJson.Options));
        return array;
    }

    private static string RenderResult(JsonElement value)
    {
        var kind = value.GetProperty("kind").GetString();
        if (kind == "background")
            return $"Subagent queued as background job {value.GetProperty("jobId").GetString()}";
        if (kind == "continuable")
            return $"Subagent running in the background as continuable subagent {value.GetProperty("subagentId").GetString()}";
        return OutputValueText(value.GetProperty("output"));
    }

    private static string OutputValueText(JsonElement output)
    {
        if (output.ValueKind != JsonValueKind.Array)
            return "";
        return string.Concat(output.EnumerateArray()
            .Where(block => block.TryGetProperty("type", out var type) && type.GetString() == "text")
            .Select(block => block.TryGetProperty("text", out var text) ? text.GetString() ?? "" : ""));
    }

    private static string OutputValueText(IReadOnlyList<ContentBlock> output)
        => string.Concat(output.OfType<TextBlock>().Select(block => block.Text));

    public static SubagentToolConfig ParseConfig(object? config)
    {
        var dict = config as IReadOnlyDictionary<string, object?>
            ?? throw new ArgumentException(
                "tool-subagent: `provider` is required — register a subagent provider, then set `provider` to its name");
        var provider = dict.GetValueOrDefault("provider") as string;
        if (string.IsNullOrEmpty(provider))
        {
            throw new ArgumentException(
                "tool-subagent: `provider` is required — register a subagent provider, then set `provider` to its name");
        }
        if (dict.GetValueOrDefault("modelSelectionSettings") is true)
        {
            throw new NotSupportedException(
                "tool-subagent: modelSelectionSettings requires the settings service and scoped model-selection installs, "
                + "which are not ported");
        }
        return new SubagentToolConfig
        {
            Provider = provider,
            ToolName = dict.GetValueOrDefault("toolName") as string ?? DefaultToolName,
            EnableRunInBackground = dict.GetValueOrDefault("enableRunInBackground") as bool? ?? true,
            BackgroundMode = dict.GetValueOrDefault("backgroundMode") as string ?? "one-shot",
            AgentOptions = ParseAgentOptions(dict.GetValueOrDefault("agentOptions")),
            Persona = dict.GetValueOrDefault("persona") as string,
            ToolFilter = ParseToolFilter(dict.GetValueOrDefault("toolFilter")),
            MaxDepth = ParseMaxDepth(dict.GetValueOrDefault("maxDepth")),
        };
    }

    public static IDisposable Apply(Context ctx, object? config) => Apply(ctx, ParseConfig(config));

    private static AgentOptions? ParseAgentOptions(object? value)
    {
        if (value is not IReadOnlyDictionary<string, object?> dict)
            return null;
        return new AgentOptions(
            dict.GetValueOrDefault("provider") as string,
            dict.GetValueOrDefault("model") as string,
            dict.GetValueOrDefault("reasoningEffort") is string effort ? ReasoningEffortId.Create(effort) : null,
            ToInt(dict.GetValueOrDefault("maxTokens")));
    }

    private static ToolRestriction? ParseToolFilter(object? value)
    {
        if (value is not IReadOnlyDictionary<string, object?> dict)
            return null;
        return new ToolRestriction(ToStringList(dict.GetValueOrDefault("allow")), ToStringList(dict.GetValueOrDefault("deny")));
    }

    private static int? ParseMaxDepth(object? value) => value switch
    {
        null => 3,
        "provider-managed" => null,
        _ => ToInt(value) ?? throw new ArgumentException("subagent maxDepth must be a non-negative safe integer"),
    };

    private static int? ToInt(object? value) => value switch
    {
        null => null,
        int i => i,
        long l => checked((int)l),
        double d when d == Math.Floor(d) => checked((int)d),
        _ => throw new ArgumentException($"expected an integer, got {value}"),
    };

    private static IReadOnlyList<string>? ToStringList(object? value)
    {
        if (value is null)
            return null;
        if (value is System.Collections.IEnumerable items)
            return items.Cast<object?>().Select(item => item?.ToString() ?? "").ToList();
        throw new ArgumentException($"expected a string array, got {value}");
    }

    private sealed class MountState : IDisposable
    {
        private IDisposable? _registration;

        public ISubagentProvider? Provider { get; private set; }

        public void Set(ISubagentProvider provider, IDisposable registration)
        {
            Clear();
            Provider = provider;
            _registration = registration;
        }

        public void Clear()
        {
            _registration?.Dispose();
            _registration = null;
            Provider = null;
        }

        public void Dispose() => Clear();
    }

    private sealed class FuncDispose(Func<bool> dispose) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            dispose();
        }
    }

    private sealed class DisposeBundle(IReadOnlyList<IDisposable> disposables) : IDisposable
    {
        public void Dispose()
        {
            foreach (var disposable in disposables)
                disposable.Dispose();
        }
    }
}
