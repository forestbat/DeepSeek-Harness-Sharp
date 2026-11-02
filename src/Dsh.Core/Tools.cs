using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Concurrent;
using Dsh.Runtime;
using Dsh.Llm;

namespace Dsh.Core;

public enum ApprovalOutcome
{
    AllowedOnce,
    Rejected,
    Cancelled,
    Unavailable,
    /** 用户选择「本会话内不再询问」: 新增值放在末尾, 避免改变已有取值的数值语义。 */
    AllowedForSession,
}

public sealed record ApprovalRequest(
    IAgent Agent,
    string ToolName,
    ToolCallId CallId,
    string? Reason = null,
    string? Arguments = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public JsonElement? RuleArguments { get; init; }
}

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

    /** 路由工具名: 组内工具在展开前不可见, 模型先用它拿到某个组的工具清单。 */
    public const string RouterToolName = "tool_router";

    /** settings.yaml 的 tools.progressive_disclosure 通过 ToolDisclosureSwitch.Context 属性传进来; 缺省关闭。 */
    private readonly bool _progressiveDisclosure;

    private readonly ConcurrentDictionary<string, byte> _disclosedGroups = new(StringComparer.Ordinal);

    /** 已从会话历史同步过展开状态(每个会话一次)。 */
    private readonly ConcurrentDictionary<string, byte> _hydratedSessions = new(StringComparer.Ordinal);

    /** 一条工具注册:definition 携带的最终名字(重名消解后)与注册来源包名(null = Apply 之外注册,归属未知)。 */
    private sealed record ToolEntry(ToolDefinition Definition, string? Owner, Guid RegistrationId);

    private sealed class ToolLayer
    {
        public NamedEntries<ToolEntry> Tools { get; }
        public AnonymousEntries<ToolRestriction> Restrictions { get; } = new();
        public AnonymousEntries<Func<ToolExecution, string?>> Guards { get; } = new();
        public Lock RegistrationGate { get; } = new();

        public ToolLayer(ScopeKey? scope)
        {
            Tools = new NamedEntries<ToolEntry>(name => new InvalidOperationException(scope is null
                ? $"tool \"{name}\" is already registered (for a per-agent variant, register through that agent's agent.ctx instead)"
                : $"tool \"{name}\" is already registered in this scope"));
        }

        public bool Admits(string name)
            => Restrictions.Values.All(filter =>
                (filter.Allow is null || filter.Allow.Contains(name))
                && (filter.Deny is null || !filter.Deny.Contains(name)));
    }

    private readonly ScopedLayers<ToolLayer> _layers;

    public ToolRuntime(Context ctx) : base(ctx, ServiceName)
    {
        _progressiveDisclosure = ctx.GetProp(ToolDisclosureSwitch.PropName) is true;
        _layers = new ScopedLayers<ToolLayer>(scope => new ToolLayer(scope), () => ctx.Emit(new ToolChangeNotification()));
        var systemPrompt = ctx.Get<SystemPrompt>(SystemPrompt.ServiceName, false)
            ?? throw new InvalidOperationException("tools requires the systemPrompt service");
        systemPrompt.Tools(WireSchemas);
        Register(RouterDefinition());
    }

    public IDisposable Register(ToolDefinition definition) => Register(definition, null);

    /** Registers a tool for a scope and its descendants. */
    public IDisposable Register(ToolDefinition definition, ScopeKey? scope)
        => RegisterCore(definition, scope, PluginApplyScope.CurrentPlugin);

    /** Trusted supervisor ownership metadata; this does not grant OS authority. */
    public IDisposable RegisterForPlugin(string package, ToolDefinition definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(package);
        return RegisterCore(definition, null, package);
    }

    private IDisposable RegisterCore(ToolDefinition definition, ScopeKey? scope, string? owner)
    {
        if (definition.TimeoutMs is <= 0)
            throw new ArgumentException($"tool \"{definition.Name}\" timeoutMs must be a positive finite number");
        if (!IsSimpleIdentifier(definition.Name))
        {
            Ctx.LoggerFor("tools").Warn(
                $"tool \"{definition.Name}\" is not a C# identifier; programs can call it as tools.call(\"{definition.Name}\", args)");
        }
        var registeredName = definition.Name;
        var registrationId = Guid.NewGuid();
        return _layers.Effect(Ctx, scope,
            layer =>
            {
                lock (layer.RegistrationGate)
                {
                    var existing = layer.Tools.Entries.FirstOrDefault(entry => entry.Key == registeredName);
                    if (existing.Value is not null)
                    {
                        registeredName = ResolveNameConflict(registeredName, owner, existing.Value, layer);
                    }
                    var entry = registeredName == definition.Name
                        ? new ToolEntry(definition, owner, registrationId)
                        : new ToolEntry(new ToolDefinition
                        {
                            Name = registeredName,
                            Description = definition.Description,
                            Parameters = definition.Parameters,
                            Output = definition.Output,
                            Execute = definition.Execute,
                            FinalizeContent = definition.FinalizeContent,
                            TimeoutMs = definition.TimeoutMs,
                            IsConcurrencySafe = definition.IsConcurrencySafe,
                            Disclosure = definition.Disclosure,
                            IsAvailable = definition.IsAvailable,
                            WriteTargets = definition.WriteTargets,
                        }, owner, registrationId);
                    layer.Tools.Insert(registeredName, entry);
                }
            },
            layer =>
            {
                lock (layer.RegistrationGate)
                {
                    var current = layer.Tools.Entries.FirstOrDefault(entry => entry.Key == registeredName);
                    if (current.Value?.RegistrationId == registrationId)
                        layer.Tools.Remove(registeredName);
                }
            });
    }

    /**
     * 重名消解: 先注册者保留短名, 后注册者改名为「包名末段-工具名」并 WARN 告知双方。
     * 同一插件重复注册同名是 bug, 仍然抛错; 归属未知(非 Apply 路径)时没有可用的改名前缀, 也抛错。
     */
    private string ResolveNameConflict(string name, string? owner, ToolEntry existing, ToolLayer layer)
    {
        var suffix = owner?.Split('/')[^1];
        if (suffix is null || existing.Owner == owner)
            throw new InvalidOperationException($"tool \"{name}\" is already registered");
        var renamed = $"{suffix}-{name}";
        if (layer.Tools.Entries.Any(entry => entry.Key == renamed))
            throw new InvalidOperationException($"tool \"{name}\" is already registered, and the renamed form \"{renamed}\" is taken as well");
        Ctx.LoggerFor("tools").Warn(
            $"tool \"{name}\" from <{owner}> conflicts with the one from <{existing.Owner ?? "unknown"}>; it is now visible as \"{renamed}\" — both authors should pick distinct names");
        return renamed;
    }

    /** PTC 以工具名生成 C# 方法名, 这里只做字符形状判断(关键字由 PTC 侧发射绑定时再过滤)。 */
    private static bool IsSimpleIdentifier(string name)
        => name.Length > 0
           && (char.IsLetter(name[0]) || name[0] == '_')
           && name.All(character => char.IsLetterOrDigit(character) || character == '_');

    public IDisposable Restrict(ToolRestriction filter)
        => Restrict(filter, DshScope.ScopeOf(Ctx)
            ?? throw new InvalidOperationException("tools.restrict() requires a scoped context (agent.ctx): a context-global restriction would mask every agent — deny the tool for the intended agent instead"));

    public IDisposable Restrict(ToolRestriction filter, ScopeKey scope)
    {
        if (filter.Allow is null && filter.Deny is null)
            throw new InvalidOperationException("tools.restrict({}) is a no-op: pass allow and/or deny (an empty filter is almost always a materialized-empty-config bug)");
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

    private ToolView View(ScopeKey? scope)
    {
        var layers = _layers.ChainLayers(scope);
        var own = scope is null ? null : _layers.LayerFor(scope);
        var inherited = new Dictionary<string, ToolDefinition>();
        var globalEntries = _layers.Global.Tools.Entries;
        foreach (var (name, entry) in globalEntries)
            inherited[name] = entry.Definition;
        foreach (var layer in layers)
        {
            if (ReferenceEquals(layer, own))
                continue;
            foreach (var (name, entry) in layer.Tools.Entries)
                inherited[name] = entry.Definition;
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
            foreach (var (name, entry) in own.Tools.Entries)
            {
                knownNames.Add(name);
                visible[name] = entry.Definition;
            }
        }
        return new ToolView(visible, knownNames, restrictableNames);
    }

    public ToolDefinition? Get(string name, ScopeKey? scope = null)
        => View(scope).Visible.TryGetValue(name, out var definition) ? definition : null;

    public IReadOnlyList<ToolSchema> Schemas(ScopeKey? scope = null)
        => View(scope).Visible.Values
            // 平台能力不满足的工具(如 Windows 上的 bash)任何界面都不暴露, 内部调用仍可按名执行。
            .Where(definition => definition.IsAvailable?.Invoke() != false)
            .Select(definition => new ToolSchema(definition.Name, definition.Description, definition.Parameters))
            .ToList();

    /** 只暴露未分组的工具与已展开组的工具; 存在未展开组时同时暴露路由工具。 */
    private ToolProviderResult WireSchemas(AssembleContext context)
    {
        var view = View(context.Scope);
        HydrateDisclosures(context.Agent?.Session);
        var session = SessionKeyOf(context.Agent);
        var schemas = view.Visible.Values
            .Where(definition => Visible(definition, session, context.Scope))
            .Select(definition => new ToolSchema(definition.Name, definition.Description, definition.Parameters))
            .ToList();
        return new ToolProviderResult(schemas, [.. view.KnownNames]);
    }

    /**
     * 从会话历史恢复展开过的分组: 上下文里已有该组的工具调用/结果, 重启进程后若这些工具又消失,
     * 模型看到的就是断裂的对话。每个会话只扫一次, 之后由 Disclose 增量维护。
     */
    private void HydrateDisclosures(Session? session)
    {
        if (session is null)
            return;
        var key = session.Header.Id.Value;
        if (!_hydratedSessions.TryAdd(key, 0))
            return;
        foreach (var sessionEvent in session.SnapshotEvents())
            if (sessionEvent.Data is ToolDisclosedPayload disclosed)
                _disclosedGroups[GroupKey(key, disclosed.Group)] = 0;
    }

    private static string SessionKeyOf(IAgent? agent)
        => agent is null ? "" : agent.Session.Header.Id.Value;

    private bool Visible(ToolDefinition definition, string session, ScopeKey? scope)
    {
        if (definition.IsAvailable?.Invoke() == false)
            return false;
        // 未启用渐进式披露: 不分组、不出现路由工具, 所有工具直接可见。
        if (!_progressiveDisclosure)
            return definition.Name != RouterToolName;
        // 路由工具只在确有未展开的分组时才出现: 没有分组就没有额外 schema 开销。
        if (definition.Name == RouterToolName)
            return HasUndisclosedGroups(scope, session);
        return definition.Disclosure is not { } disclosure
               || disclosure.DefaultVisible
               || _disclosedGroups.ContainsKey(GroupKey(session, disclosure.Group));
    }

    private bool HasUndisclosedGroups(ScopeKey? scope, string session)
        => Groups(scope).Any(group => !_disclosedGroups.ContainsKey(GroupKey(session, group.Name)));

    private static string GroupKey(string session, string group) => $"{session}\u0000{group}";

    /** 当前 scope 里所有分组(名字, 说明, 工具数), 供路由工具列出。 */
    public IReadOnlyList<ToolGroupInfo> Groups(ScopeKey? scope)
        => [.. View(scope).Visible.Values
            .Where(definition => definition.Disclosure is not null && definition.IsAvailable?.Invoke() != false)
            .GroupBy(definition => definition.Disclosure!.Group, StringComparer.Ordinal)
            .Select(grouping => new ToolGroupInfo(
                grouping.Key,
                grouping.First().Disclosure!.Summary,
                grouping.Select(definition => definition.Name).Order(StringComparer.Ordinal).ToList()))];

    public IReadOnlyList<ToolDefinition> GroupTools(string group, ScopeKey? scope)
        => [.. View(scope).Visible.Values
            .Where(definition => definition.IsAvailable?.Invoke() != false
                && string.Equals(definition.Disclosure?.Group, group, StringComparison.Ordinal))
            .OrderBy(definition => definition.Name, StringComparer.Ordinal)];

    private bool Disclose(string group, ScopeKey? scope, IAgent? agent)
    {
        if (Groups(scope).All(candidate => !string.Equals(candidate.Name, group, StringComparison.Ordinal)))
            return false;
        // 没有归属会话(直接调用工具运行时)时只在内存里展开, 没有可落盘的目标。
        if (!_disclosedGroups.TryAdd(GroupKey(SessionKeyOf(agent), group), 0))
            return true;
        agent?.Session.Append(new ToolDisclosedPayload(group));
        Ctx.Emit(new ToolChangeNotification());
        return true;
    }

    /**
     * 路由工具: 无参列出分组, 指定 group 或 query 时展开命中分组并返回其工具清单。
     * 展开后的工具在下一个请求的 schema 里出现, 同一轮内也能直接按清单调用。
     */
    private ToolDefinition RouterDefinition() => new()
    {
        Name = RouterToolName,
        Description =
            "List and unlock tool groups. Large tool sets (for example each MCP server) are hidden from your tool list "
            + "until you open their group: call with no arguments to see the groups, then call again with "
            + "{\"group\": \"<name>\"} (or {\"query\": \"<keywords>\"} to search) so that group's tools become callable.",
        Parameters = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["group"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Exact group name from the no-argument listing.",
                },
                ["query"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Keywords matched against group names, summaries and tool names/descriptions.",
                },
            },
            ["additionalProperties"] = false,
        },
        Output = new ToolOutputDefinition(
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["text"] = new JsonObject { ["type"] = "string" } } },
            (_, value) => [new TextBlock(value.TryGetProperty("text", out var text) ? text.GetString() ?? "" : value.GetRawText())]),
        Execute = (arguments, exec) =>
        {
            HydrateDisclosures(exec.Agent?.Session);
            var scope = exec.Agent?.ScopeKey;
            var group = arguments.TryGetProperty("group", out var groupValue) ? groupValue.GetString() : null;
            var query = arguments.TryGetProperty("query", out var queryValue) ? queryValue.GetString() : null;
            return Task.FromResult<object?>(JsonSerializer.SerializeToElement(new
            {
                text = RouterText(scope, exec.Agent, group, query),
            }));
        },
    };

    private string RouterText(ScopeKey? scope, IAgent? agent, string? group, string? query)
    {
        var groups = Groups(scope);
        if (groups.Count == 0)
            return "no tool groups are registered";
        if (string.IsNullOrWhiteSpace(group) && string.IsNullOrWhiteSpace(query))
            return RenderGroups(groups);
        List<ToolGroupInfo> matched = string.IsNullOrWhiteSpace(group)
            ? MatchGroups(groups, query!)
            : groups.Where(candidate => string.Equals(candidate.Name, group, StringComparison.Ordinal)).ToList();
        if (matched.Count == 0)
            return $"no group matches {(group is null ? $"query \"{query}\"" : $"\"{group}\"")}; available groups:\n{RenderGroups(groups)}";
        var builder = new System.Text.StringBuilder();
        foreach (var candidate in matched)
        {
            Disclose(candidate.Name, scope, agent);
            builder.AppendLine($"group {candidate.Name}: {candidate.Summary}");
            foreach (var tool in GroupTools(candidate.Name, scope))
            {
                builder.AppendLine($"- {tool.Name}: {FirstLine(tool.Description)}");
                if (tool.Parameters["properties"] is JsonObject properties && properties.Count > 0)
                    builder.AppendLine($"  parameters: {string.Join(", ", properties.Select(property => property.Key))}");
            }
            builder.AppendLine("these tools are now callable; call them directly by name");
        }
        return builder.ToString().TrimEnd();
    }

    private static List<ToolGroupInfo> MatchGroups(IReadOnlyList<ToolGroupInfo> groups, string query)
        => [.. groups.Where(candidate =>
            Contains(candidate.Name, query)
            || Contains(candidate.Summary, query)
            || candidate.Tools.Any(tool => Contains(tool, query)))];

    private static bool Contains(string? text, string query)
        => text is not null && text.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static string FirstLine(string text)
    {
        var line = text.Replace("\r\n", "\n").Split('\n')[0].Trim();
        return line.Length <= 160 ? line : $"{line[..160]}…";
    }

    private static string RenderGroups(IReadOnlyList<ToolGroupInfo> groups)
        => string.Join('\n', groups.Select(group => $"- {group.Name} ({group.Tools.Count} tools): {group.Summary}"));

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

    // 以下两个成员由 dsh/service.call 桥按名字字符串调用(匹配不区分大小写)。
    [RemoteCallable]
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

    [RemoteCallable]
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
        _ => DshJson.ToElementRuntime(value),
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

    public ToolExecutionModeKind ExecutionModeKind(ToolExecutionInput exec)
    {
        var tool = Get(exec.Name, exec.Agent?.ScopeKey);
        if (tool?.IsConcurrencySafe is null)
            return ToolExecutionModeKind.Exclusive;
        try
        {
            return tool.IsConcurrencySafe(exec.Arguments) ? ToolExecutionModeKind.Parallel : ToolExecutionModeKind.Exclusive;
        }
        catch
        {
            return ToolExecutionModeKind.Exclusive;
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

    private ToolRunContext CreateExecution(ToolExecutionInput input)
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
            HostContext = Ctx,
        };
        var visible = Get(input.Name, input.Agent?.ScopeKey);
        runContext.CapturedFinalizer = visible?.FinalizeContent;
        return runContext;
    }

    public async Task<ScheduledToolPreparation> PrepareScheduledExecution(ToolExecutionInput input)
    {
        var exec = CreateExecution(input);
        if (exec.Signal.IsCancellationRequested)
            return new ScheduledToolPreparation.FinalResult(exec, AbortedBeforeDispatchResult());
        try
        {
            var carrier = DshScope.ScopeTarget(Ctx, exec.Agent?.ScopeKey);
            var gate = WriteRootGate(exec, NormalizePreDecision(await Ctx.Events.Waterfall(
                carrier, new ToolPreExecuteNotification(exec),
                () => new ValueTask<object?>(new PreToolDecision.Allow()))));
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

    /** Writes outside the session's write permission root need approval, whatever the session's current directory is. */
    private PreToolDecision WriteRootGate(ToolRunContext exec, PreToolDecision gate)
    {
        if (gate is PreToolDecision.Deny || exec.Agent is not { } agent)
            return gate;
        var tool = Get(exec.Name, agent.ScopeKey);
        var targets = tool?.WriteTargets?.Invoke(exec.Arguments, exec) ?? [];
        // The actual settings writer requests its own diff decision instead of a second root-only decision.
        if (Ctx.Get<ISettingsMutationGate>(ISettingsMutationGate.ServiceName, false) is { } settings)
            targets = targets.Where(path => !settings.Owns(path)).ToArray();
        if (SessionWorkingDirectory.OutsideRootApprovalReason(agent.Session, targets) is not { } reason)
            return gate;
        return new PreToolDecision.Ask(gate is PreToolDecision.Ask { Reason: { } earlier } ? $"{reason}; {earlier}" : reason);
    }

    private async Task<(PreToolDecision Decision, bool ApprovalCancelled)> ServiceAsk(ToolRunContext exec, PreToolDecision.Ask ask)
    {
        var approval = Ctx.Get<IApprovalService>("approval", false);
        if (approval is null)
            return (new PreToolDecision.Deny(ask.Reason ?? $"tool \"{exec.Name}\" requires approval (not yet supported)"), false);
        if (exec.Agent is null)
            return (new PreToolDecision.Deny($"tool \"{exec.Name}\" requires approval, but the call has no agent to route it through"), false);
        var outcome = await approval.Request(new ApprovalRequest(exec.Agent, exec.Name, exec.CallId, ask.Reason, exec.Arguments.GetRawText()), exec.Signal);
        return outcome switch
        {
            ApprovalOutcome.AllowedOnce or ApprovalOutcome.AllowedForSession => (new PreToolDecision.Allow(), false),
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
            var tool = Get(exec.Name, exec.Agent?.ScopeKey)
                ?? throw new ToolNotFoundException(exec.Name);
            exec.BodyInvoked = true;
            var arguments = exec.Arguments.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                ? JsonDocument.Parse("{}").RootElement
                : exec.Arguments;
            var returned = await tool.Execute(arguments, exec);
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
                carrier, new ToolExecuteNotification(exec),
                async () => await DispatchToolBody(exec)) as ToolExecutionResult
                ?? throw new InvalidOperationException("tools/execute waterfall returned no result");
            var normalized = NormalizeDispatchResult(exec, result);
            var deferred = exec.DeferredContexts;
            ToolExecutionResult withDeferred = deferred.Count == 0
                ? normalized
                : normalized with { AdditionalContexts = [.. deferred, .. normalized.AdditionalContexts ?? []] };
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
        Ctx.Events.Emit(carrier, new ToolResultNotification(exec, result));
    }

    private async Task<ToolExecutionResult> PostExecute(ToolRunContext exec, ToolExecutionResult result)
    {
        var carrier = DshScope.ScopeTarget(Ctx, exec.Agent?.ScopeKey);
        var decision = await Ctx.Events.Waterfall(
            carrier, new ToolPostExecuteNotification(exec, result),
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
                        ? [.. result.AdditionalContexts ?? [], .. contexts]
                        : result.AdditionalContexts;
                    if (accept.Value is { } value)
                    {
                        if (result.IsError)
                            throw new JsonException("tools/post-execute cannot replace the value of a failed result");
                        var tool = Get(exec.Name, exec.Agent?.ScopeKey)
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
        if (exec.LastSettingsMutation is not null && value.ValueKind == JsonValueKind.Object)
        {
            var document = JsonNode.Parse(value.GetRawText())!.AsObject();
            if (document.ContainsKey("before"))
                document["before"] = Dsh.Runtime.Logging.Redaction.Apply(document["before"]?.GetValue<string>() ?? "");
            if (document.ContainsKey("after"))
                document["after"] = Dsh.Runtime.Logging.Redaction.Apply(document["after"]?.GetValue<string>() ?? "");
            value = JsonDocument.Parse(document.ToJsonString()).RootElement;
        }
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
        if (exec.LastSettingsMutation is { } mutation)
            meta = JsonDocument.Parse(new JsonObject
            {
                ["card"] = "diff",
                ["title"] = $"settings {mutation.Outcome}: {mutation.Path}",
                ["patch"] = mutation.Diff,
                ["beforeHash"] = mutation.BeforeHash,
                ["afterHash"] = mutation.AfterHash,
            }.ToJsonString()).RootElement;
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
        // 工具体抛出的 OCE 即中途取消：统一标记为 AbortError，使消费方能把"被取消"与"真实失败"区分开。
        var info = error switch
        {
            OperationCanceledException => new ToolErrorInfo("AbortError", ToolErrorCodes.Aborted),
            HarnessException harness => new ToolErrorInfo(harness.GetType().Name, harness.Code),
            _ => null,
        };
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
        JsonElement element => element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? JsonDocument.Parse("{}").RootElement : element.Clone(),
                _ => DshJson.ToElementRuntime(candidate),
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

/** 一个可被路由工具展开的工具组: 名字、说明与组内工具名。 */
public sealed record ToolGroupInfo(string Name, string Summary, IReadOnlyList<string> Tools);
