using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Runtime;
using Dsh.Runtime.Events;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Sdk;
using Dsh.Agent;

namespace Dsh.Subagent;

[global::Dsh.Plugins.DshPluginSettings]
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
    public const string DefaultToolName = ISubagentService.DefaultToolName;

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

    private const string ModelSelectionSuffix =
        " Child LLM selection is optional: omit `provider`, `model` and `reasoning_effort` to use the configured child defaults "
        + "and inherit the rest from this conversation. To run the child on another route, call `list_subagent_models` and pass "
        + "one listed `provider` and `model` together; changing the route without `reasoning_effort` uses that model's default effort.";

    private const string ProviderParameter = "provider";
    private const string ModelParameter = "model";
    private const string ReasoningEffortParameter = "reasoning_effort";

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
        var removeAdded = ctx.On<SubagentProviderAddedNotification>(notification =>
        {
            if (notification.Provider.Name == config.Provider && !ReferenceEquals(mount.Provider, notification.Provider))
                Mount(notification.Provider);
        });
        var removeRemoved = ctx.On<SubagentProviderRemovedNotification>(notification =>
        {
            if (notification.Name == config.Provider)
                mount.Clear();
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
            + (config.EnableRunInBackground ? BackgroundSuffix : "")
            + (provider.Capabilities.AgentOptions ? ModelSelectionSuffix : "");
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
        if (provider.Capabilities.AgentOptions)
        {
            properties[ProviderParameter] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "LLM provider id for the child, as listed by `list_subagent_models`. "
                    + "Supply together with `model`; omit both to keep the default route.",
            };
            properties[ModelParameter] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Model id for the child, as listed by `list_subagent_models` for that provider. Supply together with `provider`.",
            };
            properties[ReasoningEffortParameter] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Reasoning effort for the child's route, one of the efforts `list_subagent_models` lists for it. "
                    + "Omit to inherit a compatible effort or use a newly selected model's default.",
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

    /**
     * 工具输出契约: 后台分支的 runId 是必需的, jobId 只在有 job 控制器时才有
     * (BackgroundRunDispatch.JobId 可为 null), 所以不能把 jobId 写成 required。
     * 改动这里必须同步改 StartBackgroundRun / SettleForegroundRun 的返回值,
     * 回归用例见 SubagentTests.OutputContract_*。
     */
    private static readonly JsonObject OutputSchema = new()
    {
        ["oneOf"] = new JsonArray
        (
            new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["required"] = new JsonArray("kind", "runId"),
                ["properties"] = new JsonObject
                {
                    ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("background") },
                    ["runId"] = new JsonObject { ["type"] = "string" },
                    ["jobId"] = new JsonObject { ["type"] = "string" },
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
        var agentOptions = RequestedAgentOptions(parentOptions, config.AgentOptions, args);
        if (!ReferenceEquals(agentOptions, config.AgentOptions) && !provider.Capabilities.AgentOptions)
            throw new InvalidOperationException($"subagent provider \"{config.Provider}\" does not support child model selection");
        var configuredRoute = config.AgentOptions is { Provider: not null } configured
            && agentOptions?.Provider == configured.Provider && agentOptions.Model == configured.Model;
        if (HasConfiguredLlmSelection(agentOptions))
            await PreflightChildLlmRoute(ctx, parentOptions, agentOptions, configuredRoute, exec.Signal);
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
            ParentCallId = exec.CallId,
            Signal = exec.Signal,
            AgentOptions = agentOptions,
            Persona = config.Persona,
            ToolFilter = config.ToolFilter,
            MaxDepth = config.MaxDepth,
        };
        if (!ResolveRunInBackground(args, config))
            return await SettleForegroundRun(await subagents.StartAsync(config.Provider, request));
        return await StartBackgroundRun(ctx, parent, subagents, config.Provider, request with { Background = true });
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

    /**
     * Overlay the model-facing route fields on the configured child defaults.
     * Provider and model form one route and must come together.
     * Changing the route without an effort drops the configured effort so the selected model's default applies.
     */
    private static AgentOptions? RequestedAgentOptions(AgentOptions parentOptions, AgentOptions? configured, JsonElement args)
    {
        var provider = OptionalRouteField(args, ProviderParameter);
        var model = OptionalRouteField(args, ModelParameter);
        var effort = OptionalRouteField(args, ReasoningEffortParameter);
        if (provider is null && model is null && effort is null)
            return configured;
        if ((provider is null) != (model is null))
            throw new InvalidOperationException("child LLM `provider` and `model` must be supplied together");
        var baseline = configured ?? new AgentOptions();
        var routeChanged = provider is not null
            && (provider != (baseline.Provider ?? parentOptions.Provider) || model != (baseline.Model ?? parentOptions.Model));
        return baseline with
        {
            Provider = provider ?? baseline.Provider,
            Model = model ?? baseline.Model,
            ReasoningEffort = effort is not null
                ? ReasoningEffortId.Create(effort)
                : routeChanged ? null : baseline.ReasoningEffort,
        };
    }

    private static string? OptionalRouteField(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return null;
        var text = value.GetString();
        return string.IsNullOrEmpty(text)
            ? throw new InvalidOperationException($"child LLM `{name}` must be non-empty")
            : text;
    }

    private static bool HasConfiguredLlmSelection(AgentOptions? options)
        => options?.Provider is not null || options?.Model is not null || options?.ReasoningEffort is not null;

    private static async Task PreflightChildLlmRoute(
        Context ctx, AgentOptions parentOptions, AgentOptions? requested, bool configuredRoute, CancellationToken signal)
    {
        var provider = requested?.Provider ?? parentOptions.Provider;
        var model = requested?.Model ?? parentOptions.Model;
        if (provider is null || model is null)
            throw new InvalidOperationException("cannot select child LLM values without an effective provider and model");
        var routeChanged = provider != parentOptions.Provider || model != parentOptions.Model;
        var reasoningEffort = requested?.ReasoningEffort ?? (routeChanged ? null : parentOptions.ReasoningEffort);
        var llm = ctx.Get<LlmRuntime>(LlmRuntime.ServiceName)
            ?? throw new InvalidOperationException("tool-subagent requires the llm service to preflight the child route");
        try
        {
            await llm.PrepareCall(new LlmCallConfig(provider, model, reasoningEffort), signal);
        }
        catch (LlmException error) when (configuredRoute)
        {
            throw new InvalidOperationException(
                $"{error.Message}; the child route {provider}/{model} comes from the tool's `agentOptions` or settings.yaml `subagent.default_model` "
                + "— fix that setting, or pass a `provider` and `model` listed by `list_subagent_models`",
                error);
        }
    }

    /**
     * 后台运行: 立即返回, 由完成回调把结果投回父会话并唤醒它。
     *
     * 这样用户在主会话按停只结束父自己的回合, 子代理继续跑完; 跑完的通知会把已经停下的父会话叫醒接着走。
     */
    private static async Task<object?> StartBackgroundRun(
        Context ctx,
        IAgent parent,
        SubagentRuntime subagents,
        string provider,
        SubagentStartRequest request)
    {
        var run = await subagents.StartAsync(provider, request);
        var dispatch = BackgroundRunDispatch.Start(ctx, parent, subagents, run, request.Label ?? "subagent task");
        var value = new JsonObject
        {
            ["kind"] = "background",
            ["runId"] = run.Id.Value,
        };
        if (dispatch.JobId is { } jobId)
            value["jobId"] = jobId;
        return value;
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
        if (result.Interruption is { } interruption)
            message += InterruptionHint(interruption);
        if (result.Diagnostic is not null)
            message += $"\n\nsubagent diagnostic: {result.Diagnostic}";
        var partial = OutputValueText(result.Output);
        if (partial.Length > 0)
            message += $"\n\npartial output before the failure: {partial}";
        return message;
    }

    private static string InterruptionHint(SubagentInterruptionSnapshot interruption) => interruption switch
    {
        { Phase: SubagentInterruptionPhase.ToolExecution, PendingToolCall: { } pending } =>
            $"\n\ninterrupted during tool execution (tool: {pending.ToolName}, outcome unknown)"
            + " — the tool's side effects are unknown; rerun the task instead of resuming",
        { Phase: SubagentInterruptionPhase.LlmStream, PartialOutput: { Length: > 0 } partial } =>
            $"\n\ninterrupted mid-stream after {interruption.CompletedTurns} completed turn(s);"
            + $" partial text of the unfinished turn: {partial}",
        { CompletedTurns: var turns } =>
            $"\n\ninterrupted at a turn boundary after {turns} completed turn(s); the subagent state is intact",
    };

    private static JsonArray SerializeOutput(IReadOnlyList<ContentBlock> output)
    {
        var array = new JsonArray();
        foreach (var block in output)
            array.Add(DshJson.ToNode(block));
        return array;
    }

    private static string RenderResult(JsonElement value)
    {
        var kind = value.GetProperty("kind").GetString();
        if (kind == "background")
        {
            var runId = value.GetProperty("runId").GetString();
            return value.TryGetProperty("jobId", out var jobId) && jobId.GetString() is { Length: > 0 } id
                ? $"Subagent queued as background job {id} (run {runId}); its result will be posted back to this session when it finishes"
                : $"Subagent queued in the background (run {runId}); its result will be posted back to this session when it finishes";
        }
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
        var dict = config as IReadOnlyDictionary<string, object?> ?? new Dictionary<string, object?>();
        var provider = dict.GetValueOrDefault("provider") as string;
        if (string.IsNullOrEmpty(provider))
            provider = SpawnInProcessProvider.DefaultProviderName;
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

    public static IDisposable Apply(Context ctx, object? config) => Apply(ctx, WithSettingsDefaultModel(ctx, ParseConfig(config)));

    /** Plugin `agentOptions` naming no route fall back to settings.yaml `subagent.default_model`. */
    private static SubagentToolConfig WithSettingsDefaultModel(Context ctx, SubagentToolConfig config)
    {
        if (config.AgentOptions is { Provider: not null } or { Model: not null })
            return config;
        if (ctx.GetProp("harnessOptions") is not HarnessOptions options)
            return config;
        var setting = HarnessSettings.Load(options.Home).Subagent?.DefaultModel;
        if (HarnessSettings.ParseModelRoute(setting) is not { } route)
        {
            if (!string.IsNullOrWhiteSpace(setting))
                ctx.Logger.Warn("%s", $"tool-subagent: ignoring settings.yaml `subagent.default_model: {setting}`; expected provider/model");
            return config;
        }
        return config with
        {
            AgentOptions = (config.AgentOptions ?? new AgentOptions()) with { Provider = route.Provider, Model = route.Model },
        };
    }

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
        double d when double.IsInteger(d) => checked((int)d),
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
