using System.Text.Json;
using System.Text.Json.Nodes;
using Cordis;
using Dsh.Llm;

namespace Dsh.Core;

public enum ApprovalOutcome
{
    AllowedOnce,
    Rejected,
    Cancelled,
    Unavailable,
}

public sealed record ApprovalRequest(IAgent Agent, string ToolName, ToolCallId CallId, string? Reason = null);

public interface IApprovalService
{
    Task<ApprovalOutcome> Request(ApprovalRequest request, CancellationToken signal);
}

public sealed record ToolView(
    IReadOnlyDictionary<string, ToolDefinition> Visible,
    IReadOnlySet<string> KnownNames,
    IReadOnlySet<string> RestrictableNames);

public sealed class ToolRuntime : Service
{
    public const string ServiceName = "tools";
    public const string PreExecuteEvent = "tools/pre-execute";
    public const string ExecuteEvent = "tools/execute";
    public const string PostExecuteEvent = "tools/post-execute";
    public const string ResultEvent = "tools/result";
    public const string ChangeEvent = "tools/change";
    public const string RunCodeName = "run_code";

    private sealed class ToolLayer
    {
        public NamedEntries<ToolDefinition> Tools { get; }
        public AnonymousEntries<ToolRestriction> Restrictions { get; } = new();
        public AnonymousEntries<Func<ToolExecution, string?>> Guards { get; } = new();
        public ToolPresentationMode? Mode { get; set; }

        public ToolLayer(ScopeKey? scope)
        {
            Tools = new NamedEntries<ToolDefinition>(name => new InvalidOperationException(scope is null
                ? $"tool \"{name}\" is already registered (for a per-agent variant, register through that agent's agent.ctx instead)"
                : $"tool \"{name}\" is already registered in this scope"));
        }

        public bool Admits(string name)
            => Restrictions.Values.All(filter =>
                (filter.Allow is null || filter.Allow.Contains(name))
                && (filter.Deny is null || !filter.Deny.Contains(name)));
    }

    private readonly ScopedLayers<ToolLayer> _layers;
    private readonly ToolPresentationMode _defaultMode;

    public ToolRuntime(Context ctx, ToolPresentationMode defaultMode = ToolPresentationMode.Native) : base(ctx, ServiceName)
    {
        _defaultMode = defaultMode;
        _layers = new ScopedLayers<ToolLayer>(scope => new ToolLayer(scope), () => ctx.Emit(ChangeEvent));
        var systemPrompt = ctx.Get<SystemPrompt>(SystemPrompt.ServiceName)
            ?? throw new InvalidOperationException("tools requires the systemPrompt service");
        systemPrompt.Tools(context => WireSchemas(context.Scope));
    }

    public IDisposable Register(ToolDefinition definition)
    {
        if (definition.Name == RunCodeName)
        {
            throw new InvalidOperationException(
                $"tool name \"{RunCodeName}\" is reserved for the PTC mode presentation transport and cannot be registered or shadowed");
        }
        if (definition.TimeoutMs is <= 0)
            throw new ArgumentException($"tool \"definition.Name\" timeoutMs must be a positive finite number");
        return _layers.Effect(Ctx, null,
            layer => layer.Tools.Insert(definition.Name, definition),
            layer => layer.Tools.Remove(definition.Name));
    }

    public IDisposable Restrict(ToolRestriction filter)
    {
        var scope = DshScope.ScopeOf(Ctx)
            ?? throw new InvalidOperationException("tools.restrict() requires a scoped context (agent.ctx): a context-global restriction would mask every agent — deny the tool for the intended agent instead");
        if (filter.Allow is null && filter.Deny is null)
            throw new InvalidOperationException("tools.restrict({}) is a no-op: pass allow and/or deny (an empty filter is almost always a materialized-empty-config bug)");
        if (filter.Allow?.Contains(RunCodeName) == true || filter.Deny?.Contains(RunCodeName) == true)
            throw new InvalidOperationException($"tools.restrict() cannot name reserved PTC mode presentation transport \"{RunCodeName}\"; restrict end-capability tools instead");
        var known = View(scope).RestrictableNames;
        var unknown = (filter.Allow ?? []).Concat(filter.Deny ?? []).Where(name => !known.Contains(name)).ToList();
        if (unknown.Count > 0)
        {
            throw new InvalidOperationException(
                $"tools.restrict() names unknown global tool{(unknown.Count > 1 ? "s" : "")} {string.Join(", ", unknown.Select(name => $"\"{name}\""))}; known global tools: {string.Join(", ", known.Order())}");
        }
        return _layers.Effect(Ctx, scope,
            layer => layer.Restrictions.Append(filter),
            layer => layer.Restrictions.Remove(filter));
    }

    public IDisposable Guard(Func<ToolExecution, string?> guard)
        => _layers.Effect(Ctx, null,
            layer => layer.Guards.Append(guard),
            layer => layer.Guards.Remove(guard),
            notify: false);

    public IDisposable PresentAs(ToolPresentationMode mode)
    {
        var scope = DshScope.ScopeOf(Ctx)
            ?? throw new InvalidOperationException("tools.presentAs() requires a scoped context (agent.ctx): a context-global presentation is the mode config field on the tools row");
        return _layers.Effect(Ctx, scope,
            layer =>
            {
                if (layer.Mode is not null)
                {
                    throw new InvalidOperationException(
                        $"tools.presentAs(\"{mode}\") conflicts with \"{layer.Mode}\" already declared for this scope; one composition selects one presentation");
                }
                layer.Mode = mode;
            },
            layer => layer.Mode = null);
    }

    private string? GuardReason(ToolExecution exec)
    {
        var globalReason = _layers.Global.Guards.Values.Select(guard => guard(exec)).FirstOrDefault(reason => reason is not null);
        if (globalReason is not null)
            return globalReason;
        if (exec.Agent is null)
            return null;
        foreach (var layer in _layers.ChainLayers(exec.Agent.ScopeKey))
        {
            var reason = layer.Guards.Values.Select(guard => guard(exec)).FirstOrDefault(candidate => candidate is not null);
            if (reason is not null)
                return reason;
        }
        return null;
    }

    private ToolPresentationMode ModeFor(ScopeKey? scope)
    {
        var layers = _layers.ChainLayers(scope);
        for (var index = layers.Count - 1; index >= 0; index--)
        {
            if (layers[index].Mode is { } mode)
                return mode;
        }
        return _defaultMode;
    }

    private ToolView View(ScopeKey? scope)
    {
        var layers = _layers.ChainLayers(scope);
        var own = scope is null ? null : _layers.LayerFor(scope);
        var inherited = new Dictionary<string, ToolDefinition>();
        var globalEntries = _layers.Global.Tools.Entries;
        foreach (var (name, definition) in globalEntries)
            inherited[name] = definition;
        foreach (var layer in layers)
        {
            if (ReferenceEquals(layer, own))
                continue;
            foreach (var (name, definition) in layer.Tools.Entries)
                inherited[name] = definition;
        }
        var visible = new Dictionary<string, ToolDefinition>();
        var knownNames = new HashSet<string>();
        var restrictableNames = new HashSet<string>();
        foreach (var (name, definition) in inherited)
        {
            knownNames.Add(name);
            restrictableNames.Add(name);
            if (layers.All(layer => layer.Admits(name)))
                visible[name] = definition;
        }
        if (own is not null)
        {
            foreach (var (name, definition) in own.Tools.Entries)
            {
                knownNames.Add(name);
                visible[name] = definition;
            }
        }
        return new ToolView(visible, knownNames, restrictableNames);
    }

    public ToolDefinition? Get(string name, ScopeKey? scope = null)
        => View(scope).Visible.TryGetValue(name, out var definition) ? definition : null;

    private ToolDefinition? ResolveExecution(string name, ScopeKey? scope, bool nested)
    {
        var tool = Get(name, scope);
        if (tool is null)
            return null;
        if (!nested && ModeFor(scope) == ToolPresentationMode.Ptc && name != RunCodeName)
            return null;
        return tool;
    }

    public IReadOnlyList<ToolSchema> Schemas(ScopeKey? scope = null)
        => View(scope).Visible.Values
            .Select(definition => new ToolSchema(definition.Name, definition.Description, definition.Parameters))
            .ToList();

    private ToolProviderResult WireSchemas(ScopeKey? scope)
    {
        var view = View(scope);
        var mode = ModeFor(scope);
        if (mode == ToolPresentationMode.Native)
        {
            return new ToolProviderResult(
                view.Visible.Values.Select(definition => new ToolSchema(definition.Name, definition.Description, definition.Parameters)).ToList(),
                [..view.KnownNames]);
        }
        throw new NotSupportedException($"tool presentation mode \"{mode}\" requires the PTC code runtime, which is not ported yet");
    }

    private static PreToolDecision NormalizePreDecision(object? value)
    {
        if (value is null or PreToolDecision.Allow) return new PreToolDecision.Allow();
        if (value is PreToolDecision decision) return decision;
        // JS 插件经 Node 桥返回普通对象 { kind, reason }。
        if (value is not IDictionary<string, object?> dict) return new PreToolDecision.Allow();
        var kind = dict.TryGetValue("kind", out var kindValue) ? kindValue as string : null;
        var reason = dict.TryGetValue("reason", out var reasonValue) ? reasonValue as string : null;
        return kind switch
        {
            "deny" => new PreToolDecision.Deny(reason ?? "tool call denied"),
            "ask" => new PreToolDecision.Ask(reason),
            _ => new PreToolDecision.Allow(),
        };
    }

    // cordis Node 桥按名字大小写敏感地解析成员,以下两个方法供 JS 插件以 camelCase 调用。
    public List<Dictionary<string, object?>> schemas(object? agent = null)
    {
        var scope = agent is IAgent resolved ? resolved.ScopeKey : null;
        return Schemas(scope).Select(schema => new Dictionary<string, object?>
        {
            ["name"] = schema.Name,
            ["description"] = schema.Description,
            ["parameters"] = schema.Parameters,
        }).ToList();
    }

    public async Task<Dictionary<string, object?>> execute(IDictionary<string, object?> input)
    {
        var name = input.TryGetValue("name", out var nameValue) ? nameValue as string : null;
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("tools.execute: \"name\" must be a non-empty string");
        var result = await Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create(input.TryGetValue("callId", out var callIdValue) ? callIdValue as string ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N")),
            Name = name,
            Arguments = ToJsonElement(input.TryGetValue("arguments", out var argumentsValue) ? argumentsValue : null),
            Agent = input.TryGetValue("agent", out var agentValue) ? agentValue as IAgent : null,
            Signal = input.TryGetValue("signal", out var signalValue) && signalValue is CancellationToken signal ? signal : default,
        });
        return ProjectResult(result);
    }

    private static JsonElement ToJsonElement(object? value) => value switch
    {
        null => JsonDocument.Parse("{}").RootElement,
        JsonElement element => element.Clone(),
        JsonNode node => JsonDocument.Parse(node.ToJsonString()).RootElement,
        _ => JsonSerializer.SerializeToElement(value, DshJson.Options),
    };

    private static Dictionary<string, object?> ProjectResult(ToolExecutionResult result)
    {
        var projected = new Dictionary<string, object?>
        {
            ["isError"] = result.IsError,
            ["content"] = result.Content
                .Select(block => (object?)new Dictionary<string, object?>
                {
                    ["type"] = block.Type,
                    ["text"] = block is TextBlock text ? text.Text : null,
                })
                .ToList(),
        };
        if (result is ToolExecutionResult.Success success)
            projected["value"] = JsonNode.Parse(success.Value.GetRawText());
        if (result is ToolExecutionResult.Failure failure)
            projected["error"] = failure.Error.Message;
        return projected;
    }

    // JS 插件经 Node 桥注册工具:execute/render 是 JS 函数句柄(JsHandle),经 InvokeCallbackAsync 回调进 Node 侧执行。
    // 注册绑定到 root 层(桥调用拿不到插件 fiber,无法按插件生命周期回收),preset 类插件进程级常驻,语义可接受。
    public void register(IDictionary<string, object?> definition)
    {
        var name = definition.TryGetValue("name", out var nameValue) ? nameValue as string : null;
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("tools.register: \"name\" must be a non-empty string");
        var description = definition.TryGetValue("description", out var descriptionValue) ? descriptionValue as string : null;
        if (string.IsNullOrEmpty(description))
            throw new ArgumentException("tools.register: \"description\" must be a non-empty string");
        if (!definition.TryGetValue("execute", out var executeValue) || executeValue is not Cordis.Node.JsHandle executeHandle)
            throw new ArgumentException("tools.register: \"execute\" must be a function");
        var parameters = ToJsonObject(definition.TryGetValue("parameters", out var parametersValue) ? parametersValue : null);
        var output = definition.TryGetValue("output", out var outputValue) ? outputValue as IDictionary<string, object?> : null;
        var render = output is not null && output.TryGetValue("render", out var renderValue) ? renderValue as Cordis.Node.JsHandle : null;
        var outputSchema = output is not null && output.TryGetValue("schema", out var schemaValue) ? ToJsonObject(schemaValue) : new JsonObject();
        var timeoutMs = definition.TryGetValue("timeoutMs", out var timeoutValue) && timeoutValue is long or int ? Convert.ToInt64(timeoutValue) : (long?)null;
        Register(new ToolDefinition
        {
            Name = name,
            Description = description,
            Parameters = parameters,
            TimeoutMs = timeoutMs,
            Execute = async (args, runContext) =>
            {
                var exec = new Dictionary<string, object?>
                {
                    ["name"] = runContext.Name,
                    ["callId"] = runContext.CallId.Value,
                    ["agent"] = runContext.Agent,
                    ["signal"] = null,
                };
                return await executeHandle.Host.InvokeCallbackAsync(
                    executeHandle.Id,
                    null,
                    [JsonNode.Parse(args.GetRawText()), exec]);
            },
            Output = new ToolOutputDefinition(outputSchema, (args, value) =>
            {
                if (render is null)
                    return [new TextBlock(value.GetRawText())];
                var blocks = render.Host.InvokeCallbackAsync(
                        render.Id,
                        null,
                        [JsonNode.Parse(args.GetRawText()), JsonNode.Parse(value.GetRawText())])
                    .GetAwaiter().GetResult();
                return RenderBlocks(blocks);
            }),
        });
    }

    private static IReadOnlyList<ContentBlock> RenderBlocks(object? blocks)
    {
        if (blocks is not IEnumerable<object?> list)
            return [];
        return list.OfType<IDictionary<string, object?>>()
            .Where(block => block.TryGetValue("type", out var typeValue) && typeValue as string == "text")
            .Select(block => (ContentBlock)new TextBlock(block.TryGetValue("text", out var textValue) ? textValue as string ?? "" : ""))
            .ToList();
    }

    private static JsonObject ToJsonObject(object? value) => value switch
    {
        null => new JsonObject(),
        JsonObject existing => (JsonObject)existing.DeepClone(),
        JsonNode node => JsonNode.Parse(node.ToJsonString())!.AsObject(),
        JsonElement element => JsonNode.Parse(element.GetRawText())!.AsObject(),
        _ => JsonSerializer.SerializeToNode(value, DshJson.Options)!.AsObject(),
    };

    public ToolExecutionModeKind ExecutionModeKind(ToolExecutionInput exec)
    {
        var tool = ResolveExecution(exec.Name, exec.Agent?.ScopeKey, exec.Parent is not null);
        if (tool?.IsConcurrencySafe is null)
            return Core.ToolExecutionModeKind.Exclusive;
        try
        {
            return tool.IsConcurrencySafe(exec.Arguments) ? Core.ToolExecutionModeKind.Parallel : Core.ToolExecutionModeKind.Exclusive;
        }
        catch
        {
            return Core.ToolExecutionModeKind.Exclusive;
        }
    }

    public async Task<ToolExecutionResult> Execute(ToolExecutionInput input)
    {
        var prepared = await PrepareScheduledExecution(input);
        return await CompleteScheduledExecution(prepared);
    }

    private async Task<ToolExecutionResult> CompleteScheduledExecution(ScheduledToolPreparation prepared)
    {
        switch (prepared)
        {
            case ScheduledToolPreparation.Dispatch dispatch:
            {
                var dispatched = await DispatchScheduledExecution(dispatch.Exec);
                return dispatched switch
                {
                    ScheduledToolDispatch.PostResult postResult => await FinalizeScheduledExecution(dispatch.Exec, postResult.Result),
                    ScheduledToolDispatch.FinalResult finalResult => FinishScheduledExecution(dispatch.Exec, finalResult.Result),
                    _ => throw new InvalidOperationException("unknown scheduled dispatch"),
                };
            }
            case ScheduledToolPreparation.PostResult postResult:
                return await FinalizeScheduledExecution(postResult.Exec, postResult.Result);
            case ScheduledToolPreparation.FinalResult finalResult:
                return FinishScheduledExecution(finalResult.Exec, finalResult.Result);
            default:
                throw new InvalidOperationException("unknown scheduled preparation");
        }
    }

    private (ToolRunContext Context, ScheduledToolPreparation? Early) CreateExecution(ToolExecutionInput input)
    {
        var runContext = new ToolRunContext
        {
            CallId = input.CallId,
            RootCallIdValue = input.RootCallId ?? input.CallId,
            Name = input.Name,
            Arguments = input.Arguments,
            Agent = input.Agent,
            Parent = input.Parent,
            Signal = input.Signal,
        };
        var visible = Get(input.Name, input.Agent?.ScopeKey);
        var collapsed = visible is not null && ResolveExecution(input.Name, input.Agent?.ScopeKey, input.Parent is not null) is null;
        runContext.CapturedFinalizer = collapsed && !input.Signal.IsCancellationRequested
            ? null
            : visible?.FinalizeContent;
        if (collapsed)
        {
            if (input.Signal.IsCancellationRequested)
                return (runContext, new ScheduledToolPreparation.FinalResult(runContext, AbortedBeforeDispatchResult()));
            return (runContext, new ScheduledToolPreparation.FinalResult(runContext, ErrorResult(new ToolNotFoundException(
                input.Name,
                $"only `{RunCodeName}` is callable directly — call `{input.Name}` from inside a `{RunCodeName}` program instead"))));
        }
        return (runContext, null);
    }

    public async Task<ScheduledToolPreparation> PrepareScheduledExecution(ToolExecutionInput input)
    {
        var (exec, early) = CreateExecution(input);
        if (early is not null)
            return early;
        if (exec.Signal.IsCancellationRequested)
            return new ScheduledToolPreparation.FinalResult(exec, AbortedBeforeDispatchResult());
        try
        {
            var carrier = DshScope.ScopeTarget(Ctx, exec.Agent?.ScopeKey);
            var gate = NormalizePreDecision(await Ctx.Events.Waterfall(
                carrier, PreExecuteEvent, [exec],
                () => new ValueTask<object?>(new PreToolDecision.Allow())));
            var (decision, approvalCancelled) = gate is PreToolDecision.Ask ask
                ? await ServiceAsk(exec, ask)
                : (gate, false);
            if (exec.Signal.IsCancellationRequested && approvalCancelled)
                return new ScheduledToolPreparation.PostResult(exec, AbortedBeforeDispatchResult());
            var denialReason = decision is PreToolDecision.Allow
                ? GuardReason(exec)
                : (decision as PreToolDecision.Deny)?.Reason;
            if (denialReason is not null)
            {
                return new ScheduledToolPreparation.PostResult(exec, new ToolExecutionResult.Failure
                {
                    IsError = true,
                    Content = [new TextBlock($"Error: {denialReason}")],
                    Error = new ToolFailure(denialReason),
                });
            }
            if (exec.Signal.IsCancellationRequested)
                return new ScheduledToolPreparation.PostResult(exec, AbortedBeforeDispatchResult());
            return new ScheduledToolPreparation.Dispatch(exec);
        }
        catch (Exception error)
        {
            return new ScheduledToolPreparation.FinalResult(exec, ErrorResult(error));
        }
    }

    private async Task<(PreToolDecision Decision, bool ApprovalCancelled)> ServiceAsk(ToolRunContext exec, PreToolDecision.Ask ask)
    {
        var approval = Ctx.Get<IApprovalService>("approval", false);
        if (approval is null)
            return (new PreToolDecision.Deny(ask.Reason ?? $"tool \"{exec.Name}\" requires approval (not yet supported)"), false);
        if (exec.Agent is null)
            return (new PreToolDecision.Deny($"tool \"{exec.Name}\" requires approval, but the call has no agent to route it through"), false);
        var outcome = await approval.Request(new ApprovalRequest(exec.Agent, exec.Name, exec.CallId, ask.Reason), exec.Signal);
        return outcome switch
        {
            ApprovalOutcome.AllowedOnce => (new PreToolDecision.Allow(), false),
            ApprovalOutcome.Rejected => (new PreToolDecision.Deny($"the user rejected tool \"{exec.Name}\""), false),
            ApprovalOutcome.Cancelled => (new PreToolDecision.Deny($"approval for tool \"{exec.Name}\" was cancelled"), true),
            ApprovalOutcome.Unavailable => (new PreToolDecision.Deny($"tool \"{exec.Name}\" requires approval, but no approval channel is available"), false),
            _ => throw new InvalidOperationException($"unknown approval outcome {outcome}"),
        };
    }

    private async Task<ToolExecutionResult> DispatchToolBody(ToolRunContext exec)
    {
        using var fused = FusedSignal.Fuse(exec.Signal, exec.WrapperSignal);
        var signal = fused.Token;
        if (signal.IsCancellationRequested)
            return AbortedBeforeDispatchResult();
        try
        {
            var tool = ResolveExecution(exec.Name, exec.Agent?.ScopeKey, exec.Parent is not null)
                ?? throw new ToolNotFoundException(exec.Name);
            exec.BodyInvoked = true;
            var returned = await tool.Execute(exec.Arguments, exec);
            var result = CreateSuccessResult(exec, tool, returned);
            return signal.IsCancellationRequested ? AbortedResult(result) : result;
        }
        catch (Exception error)
        {
            return ErrorResult(error);
        }
    }

    public async Task<ScheduledToolDispatch> DispatchScheduledExecution(ToolRunContext exec)
    {
        try
        {
            var carrier = DshScope.ScopeTarget(Ctx, exec.Agent?.ScopeKey);
            var result = await Ctx.Events.Waterfall(
                carrier, ExecuteEvent, [exec],
                async () => await DispatchToolBody(exec)) as ToolExecutionResult
                ?? throw new InvalidOperationException("tools/execute waterfall returned no result");
            var normalized = NormalizeDispatchResult(exec, result);
            var deferred = exec.DeferredContexts;
            ToolExecutionResult withDeferred = deferred.Count == 0
                ? normalized
                : normalized with { AdditionalContexts = [..deferred, ..normalized.AdditionalContexts ?? []] };
            return new ScheduledToolDispatch.PostResult(
                exec.Signal.IsCancellationRequested && !withDeferred.IsError
                    ? CancellationResult(exec, withDeferred)
                    : withDeferred);
        }
        catch (Exception error)
        {
            return new ScheduledToolDispatch.FinalResult(ErrorResult(error));
        }
    }

    public async Task<ToolExecutionResult> FinalizeScheduledExecution(ToolRunContext exec, ToolExecutionResult result)
    {
        try
        {
            var postResult = await PostExecute(exec, result);
            return FinishScheduledExecution(
                exec,
                exec.Signal.IsCancellationRequested && !postResult.IsError
                    ? CancellationResult(exec, postResult)
                    : postResult);
        }
        catch (Exception error)
        {
            return FinishScheduledExecution(exec, ErrorResult(error));
        }
    }

    public ToolExecutionResult FinishScheduledExecution(ToolRunContext exec, ToolExecutionResult result)
    {
        var finalResult = ApplyFinalContent(exec, result);
        NotifyResult(exec, finalResult);
        return finalResult;
    }

    private ToolExecutionResult ApplyFinalContent(ToolRunContext exec, ToolExecutionResult result)
        => exec.CapturedFinalizer?.Invoke(exec, result) is { } content
            ? result with { Content = content }
            : result;

    private void NotifyResult(ToolRunContext exec, ToolExecutionResult result)
    {
        var carrier = DshScope.ScopeTarget(Ctx, exec.Agent?.ScopeKey);
        Ctx.Events.Emit(carrier, ResultEvent, exec, result);
    }

    private async Task<ToolExecutionResult> PostExecute(ToolRunContext exec, ToolExecutionResult result)
    {
        var carrier = DshScope.ScopeTarget(Ctx, exec.Agent?.ScopeKey);
        var decision = await Ctx.Events.Waterfall(
            carrier, PostExecuteEvent, [exec, result],
            () => new ValueTask<object?>(new PostToolDecision.Accept())) as PostToolDecision ?? new PostToolDecision.Accept();
        switch (decision)
        {
            case PostToolDecision.Block block:
            {
                var message = FailureMessageFromContent(block.Feedback);
                return new ToolExecutionResult.Failure
                {
                    IsError = true,
                    Content = block.Feedback,
                    Error = new ToolFailure(message),
                    AdditionalContexts = block.AdditionalContexts,
                };
            }
            case PostToolDecision.Accept accept:
            {
                var additionalContexts = accept.AdditionalContexts is { } contexts
                    ? [..result.AdditionalContexts ?? [], ..contexts]
                    : result.AdditionalContexts;
                if (accept.Value is { } value)
                {
                    if (result.IsError)
                        throw new JsonException("tools/post-execute cannot replace the value of a failed result");
                    var tool = ResolveExecution(exec.Name, exec.Agent?.ScopeKey, exec.Parent is not null)
                        ?? throw new ToolNotFoundException(exec.Name);
                    var replaced = CreateSuccessResult(exec, tool, value);
                    return replaced with { AdditionalContexts = additionalContexts };
                }
                return result with
                {
                    Content = accept.Content ?? result.Content,
                    AdditionalContexts = additionalContexts,
                };
            }
            default:
                throw new InvalidOperationException("unknown post-execute decision");
        }
    }

    private ToolExecutionResult NormalizeDispatchResult(ToolRunContext exec, ToolExecutionResult result)
    {
        if (result is ToolExecutionResult.Success success && exec.ConcludedRequested)
            return success with { ConcludesTurn = true };
        return result;
    }

    private ToolExecutionResult CreateSuccessResult(ToolRunContext exec, ToolDefinition tool, object? candidate)
    {
        var value = SnapshotValue(tool.Name, candidate);
        var violations = JsonSchemaValidator.Validate(tool.Output.Schema, value, "value");
        if (violations.Count > 0)
            throw new ToolOutputException(tool.Name, violations);
        IReadOnlyList<ContentBlock> content;
        try
        {
            content = tool.Output.Render(exec.Arguments, value);
        }
        catch (Exception error)
        {
            throw new ToolOutputException(tool.Name, [$"output.render failed: {error.Message}"]);
        }
        JsonElement? meta = null;
        if (exec.Parent is null && tool.Output.PresentationMeta is { } presentationMeta)
        {
            try
            {
                meta = presentationMeta(exec.Arguments, value);
            }
            catch (Exception error)
            {
                throw new ToolOutputException(tool.Name, [$"output.presentationMeta failed: {error.Message}"]);
            }
        }
        return new ToolExecutionResult.Success
        {
            IsError = false,
            Value = value,
            Content = content,
            Meta = meta,
            ConcludesTurn = exec.ConcludedRequested,
        };
    }

    private ToolExecutionResult CancellationResult(ToolRunContext exec, ToolExecutionResult? prior = null)
        => exec.BodyInvoked ? AbortedResult(prior) : AbortedBeforeDispatchResult(prior);

    private sealed class FusedSignal : IDisposable
    {
        private readonly CancellationTokenSource? _linked;

        private FusedSignal(CancellationToken token, CancellationTokenSource? linked)
        {
            Token = token;
            _linked = linked;
        }

        public CancellationToken Token { get; }

        public static FusedSignal Fuse(CancellationToken caller, CancellationToken wrapper)
        {
            if (caller == wrapper || wrapper == default)
                return new FusedSignal(caller, null);
            if (caller == default)
                return new FusedSignal(wrapper, null);
            var linked = CancellationTokenSource.CreateLinkedTokenSource(caller, wrapper);
            return new FusedSignal(linked.Token, linked);
        }

        public void Dispose() => _linked?.Dispose();
    }

    internal static ToolExecutionResult ErrorResult(Exception error)
    {
        var info = error is HarnessException harness ? new ToolErrorInfo(harness.GetType().Name, harness.Code) : null;
        return new ToolExecutionResult.Failure
        {
            IsError = true,
            Content = [new TextBlock($"Error: {error.Message}")],
            Error = new ToolFailure(error.Message, info),
        };
    }

    internal static ToolExecutionResult AbortedResult(ToolExecutionResult? prior = null)
        => new ToolExecutionResult.Failure
        {
            IsError = true,
            Content = [new TextBlock("Error: tool call aborted")],
            Error = new ToolFailure("tool call aborted", new ToolErrorInfo("AbortError", ToolErrorCodes.Aborted)),
            AdditionalContexts = prior?.AdditionalContexts,
        };

    internal static ToolExecutionResult AbortedBeforeDispatchResult(ToolExecutionResult? prior = null)
        => new ToolExecutionResult.Failure
        {
            IsError = true,
            Content = [new TextBlock("Error: tool call aborted before dispatch")],
            Error = new ToolFailure("tool call aborted before dispatch", new ToolErrorInfo("AbortError", ToolErrorCodes.AbortedBeforeDispatch)),
            AdditionalContexts = prior?.AdditionalContexts,
        };

    private static string FailureMessageFromContent(IReadOnlyList<ContentBlock> content)
    {
        var text = string.Join('\n', content.Select(block => block is TextBlock textBlock ? textBlock.Text : $"[{block.Type} content]"));
        return text.Length > 0 ? text : "tool result blocked by post-execute policy";
    }

    private static JsonElement SnapshotValue(string toolName, object? candidate)
    {
        try
        {
            return candidate switch
            {
                null => JsonDocument.Parse("null").RootElement,
                JsonElement element => element.Clone(),
                _ => JsonSerializer.SerializeToElement(candidate, DshJson.Options),
            };
        }
        catch (Exception error)
        {
            throw new ToolOutputException(toolName, [$"value snapshot failed: {error.Message}"]);
        }
    }
}

public enum ToolExecutionModeKind
{
    Parallel,
    Exclusive,
}
