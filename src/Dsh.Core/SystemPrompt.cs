using Dsh.Runtime;
using Dsh.Llm;

namespace Dsh.Core;

public sealed record AssembleContext(ScopeKey? Scope = null, CancellationToken Signal = default, IAgent? Agent = null);

/**
 * 提示词段: Name 全局唯一; After/Before 声明相对位置(目标段不存在时该约束忽略)。
 * 均不声明的段落在组装时按注册序追加在尾部。
 */
public sealed record PromptSection(
    string Name,
    Func<AssembleContext, string> Text,
    bool Complete = false,
    bool Dynamic = false,
    IReadOnlyList<string>? After = null,
    IReadOnlyList<string>? Before = null)
{
    public static PromptSection Literal(string name, string text, bool complete = false)
        => new(name, _ => text, complete);
}

public sealed record PromptContext(
    string Name,
    Func<AssembleContext, string> Text,
    IReadOnlyList<string>? After = null,
    IReadOnlyList<string>? Before = null)
{
    public static PromptContext Literal(string name, string text)
        => new(name, _ => text);
}

public sealed record AssembledSection(string Name, string Text, bool Dynamic = false);

public sealed record AssembledContext(string Name, string Text);

public sealed record ToolProviderResult(IReadOnlyList<ToolSchema> Schemas, IReadOnlyList<string>? KnownNames = null);

public sealed record PromptAssembly(
    IReadOnlyList<AssembledSection> Sections,
    IReadOnlyList<AssembledContext> Contexts,
    IReadOnlyList<ToolSchema> Tools,
    IReadOnlyDictionary<string, string?> Variables);

/**
 * 内置段的基准顺序(由旧全局数字优先级迁移而来): 在册段按列表先后排列。
 * 不在表内的段(第三方插件)用 PromptSection.After/Before 自定位; 都不声明时按注册序追加在尾部。
 */
public static class PromptSpine
{
    public static readonly IReadOnlyList<string> Sections =
    [
        "harness:identity",
        "deployment:persona",
        "agent-instructions",
        "plan:policy",
        "memory:policy",
        "tools:ptc-only",
        "tool:bash",
        "tool:pwsh",
        "tool:read",
        "tool:write",
        "tool:edit",
        "tool:glob",
        "tool:grep",
        "tool:jobs",
        "tool:pty",
        "tool:web_search",
        "tool:web_fetch",
        "tool:session_search",
        "tool:compact",
        "tool:goal",
        "tool:e2b_run",
        "tool:memory_save",
        "tool:workflow",
        "tool:ralph",
        "creative:guidance",
        "tools:sdk",
    ];

    public static readonly IReadOnlyList<string> Contexts =
    [
        "memory:project",
        "approval:policy",
        "subagent:delegation",
        "board:coordination",
    ];
}

public sealed class SystemPromptConfig
{
    public bool IncludeHarnessIdentity { get; init; } = true;
    public bool IncludeRuntimeContext { get; init; } = true;
    public string Persona { get; init; } = "";
    public IReadOnlyList<string>? ToolOrder { get; init; }
}

public sealed class SystemPrompt : Service
{
    public const string ServiceName = "systemPrompt";
    public const string PersonaSection = "deployment:persona";
    public const string ToolOrderRest = "<unlisted-tools>";

    private sealed record SequencedSection(long Seq, PromptSection Section);

    private sealed record SequencedContext(long Seq, PromptContext Context);

    private sealed class PromptLayer
    {
        public NamedEntries<SequencedSection> Sections { get; }
        public NamedEntries<SequencedContext> Contexts { get; }
        public AnonymousEntries<bool> RuntimeContextSuppressors { get; } = new();
        public AnonymousEntries<Func<AssembleContext, ToolProviderResult>> ToolProviders { get; } = new();
        public NamedEntries<Func<AssembleContext, string?>> Variables { get; }

        public PromptLayer(ScopeKey? scope)
        {
            Sections = new NamedEntries<SequencedSection>(name => new InvalidOperationException(scope is null
                ? $"prompt section \"{name}\" is already registered (for a per-agent override, register through that agent's agent.ctx instead)"
                : $"prompt section \"{name}\" is already registered in this scope"));
            Contexts = new NamedEntries<SequencedContext>(name => new InvalidOperationException(scope is null
                ? $"prompt context \"{name}\" is already registered (for a per-agent override, register through that agent's agent.ctx instead)"
                : $"prompt context \"{name}\" is already registered in this scope"));
            Variables = new NamedEntries<Func<AssembleContext, string?>>(name => new InvalidOperationException(scope is null
                ? $"prompt variable \"{name}\" is already registered (for a per-agent value, register through that agent's agent.ctx instead)"
                : $"prompt variable \"{name}\" is already registered in this scope"));
        }
    }

    private readonly ScopedLayers<PromptLayer> _layers;
    private readonly IReadOnlyList<string>? _toolOrder;
    private long _sequence;

    private long NextSeq() => Interlocked.Increment(ref _sequence);

    public SystemPrompt(Context ctx, SystemPromptConfig config) : base(ctx, ServiceName)
    {
        _layers = new ScopedLayers<PromptLayer>(scope => new PromptLayer(scope), () => ctx.Emit(new SystemPromptChangeNotification()));
        _toolOrder = ValidateToolOrder(config.ToolOrder);
        if (config.IncludeHarnessIdentity)
        {
            Section(PromptSection.Literal(
                "harness:identity",
                "You are an AI agent powered by DeepSeek Harness."));
        }
        Section(PromptSection.Literal(PersonaSection, config.Persona));
        if (!config.IncludeRuntimeContext)
            SuppressRuntimeContext();
    }

    private static IReadOnlyList<string>? ValidateToolOrder(IReadOnlyList<string>? toolOrder)
    {
        if (toolOrder is null)
            return null;
        var seen = new HashSet<string>();
        foreach (var name in toolOrder)
        {
            if (!seen.Add(name))
                throw new ArgumentException($"toolOrder lists \"{name}\" more than once");
        }
        if (!seen.Contains(ToolOrderRest))
            throw new ArgumentException($"toolOrder must contain the \"{ToolOrderRest}\" rest entry (where unlisted tools are inserted)");
        return toolOrder;
    }

    public IDisposable Section(PromptSection section)
        => _layers.Effect(Ctx, null,
            layer => layer.Sections.Insert(section.Name, new SequencedSection(NextSeq(), section)),
            layer => layer.Sections.Remove(section.Name));

    /** 按 scope(如 agent.ScopeKey)注册段: 同名段遮蔽全局层, 仅对该 scope 及其子 scope 生效。 */
    public IDisposable Section(PromptSection section, ScopeKey scope)
        => _layers.Effect(Ctx, scope,
            layer => layer.Sections.Insert(section.Name, new SequencedSection(NextSeq(), section)),
            layer => layer.Sections.Remove(section.Name));

    public IDisposable ReplacePersona(string text, bool complete = false)
    {
        _layers.Global.Sections.Remove(PersonaSection);
        return Section(PromptSection.Literal(PersonaSection, text, complete));
    }

    public IDisposable Context(PromptContext context)
        => _layers.Effect(Ctx, null,
            layer => layer.Contexts.Insert(context.Name, new SequencedContext(NextSeq(), context)),
            layer => layer.Contexts.Remove(context.Name));

    public IDisposable SuppressRuntimeContext()
        => _layers.Effect(Ctx, null,
            layer => layer.RuntimeContextSuppressors.Append(true),
            layer => layer.RuntimeContextSuppressors.Remove(true));

    public IDisposable SuppressRuntimeContext(ScopeKey scope)
        => _layers.Effect(Ctx, scope,
            layer => layer.RuntimeContextSuppressors.Append(true),
            layer => layer.RuntimeContextSuppressors.Remove(true));

    public IDisposable Tools(Func<AssembleContext, ToolProviderResult> provider)
        => _layers.Effect(Ctx, null,
            layer => layer.ToolProviders.Append(provider),
            layer => layer.ToolProviders.Remove(provider));

    public IDisposable Variable(string name, Func<AssembleContext, string?> provider)
    {
        if (!PromptRender.IsValidVariableName(name))
            throw new ArgumentException($"invalid prompt variable name \"{name}\" (must match ^[a-z][a-z0-9_]*$)");
        return _layers.Effect(Ctx, null,
            layer => layer.Variables.Insert(name, provider),
            layer => layer.Variables.Remove(name));
    }

    public async Task<PromptAssembly> Assemble(AssembleContext context)
    {
        var scope = context.Scope;
        var scopeLayers = _layers.ChainLayers(scope);
        var runtimeContextSuppressed = !_layers.Global.RuntimeContextSuppressors.IsEmpty
            || scopeLayers.Any(layer => !layer.RuntimeContextSuppressors.IsEmpty);
        var variables = new Dictionary<string, string?>();
        foreach (var (name, provider) in _layers.Global.Variables.Entries)
            variables[name] = provider(context);
        foreach (var layer in scopeLayers)
        {
            foreach (var (name, provider) in layer.Variables.Entries)
                variables[name] = provider(context);
        }
        var sectionByName = _layers.Merge(scope, layer => layer.Sections);
        var contextByName = _layers.Merge(scope, layer => layer.Contexts);
        var providers = _layers.Global.ToolProviders.Values
            .Concat(scopeLayers.SelectMany(layer => layer.ToolProviders.Values))
            .ToList();
        var collected = new List<ToolSchema>();
        var knownNames = new HashSet<string>();
        foreach (var provider in providers)
        {
            var result = provider(context);
            collected.AddRange(result.Schemas);
            foreach (var name in result.KnownNames ?? result.Schemas.Select(tool => tool.Name).ToList())
                knownNames.Add(name);
        }
        var sectionDefinitions = OrderByConstraints(
            sectionByName.Values.Select(entry => (entry.Seq, entry.Section)).ToList(),
            section => section.Name,
            section => section.After,
            section => section.Before,
            PromptSpine.Sections,
            "section");
        var completeSections = sectionDefinitions.Where(section => section.Complete).ToList();
        if (completeSections.Count > 1)
        {
            throw new InvalidOperationException(
                $"multiple complete prompt sections are active: {string.Join(", ", completeSections.Select(section => $"\"{section.Name}\""))}");
        }
        AssembledSection? completeSection = null;
        var sections = sectionDefinitions.Select(section =>
        {
            var assembled = new AssembledSection(section.Name, section.Text(context), section.Dynamic);
            if (section.Complete)
                completeSection = assembled;
            return assembled;
        }).ToList();
        var assembly = new PromptAssembly(
            sections,
            runtimeContextSuppressed
                ? []
                : OrderByConstraints(
                        contextByName.Values.Select(entry => (entry.Seq, entry.Context)).ToList(),
                        entry => entry.Name,
                        entry => entry.After,
                        entry => entry.Before,
                        PromptSpine.Contexts,
                        "context")
                    .Select(entry => new AssembledContext(entry.Name, entry.Text(context)))
                    .ToList(),
            OrderTools(collected, _toolOrder, knownNames),
            variables);
        var transformed = await Ctx.Events.Waterfall(
            DshScope.ScopeTarget(Ctx, scope),
            new SystemPromptAssembleNotification(assembly, context),
            () => new ValueTask<object?>(assembly)) as PromptAssembly ?? assembly;
        if (completeSection is null && !runtimeContextSuppressed)
            return transformed;
        return transformed with
        {
            Sections = completeSection is null ? transformed.Sections : [completeSection],
            Contexts = runtimeContextSuppressed ? [] : transformed.Contexts,
        };
    }

    /**
     * 命名段拓扑排序: 边来自脊柱(在册段相邻连边)与段自带的 After/Before 约束(目标缺席的约束忽略)。
     * 就绪集中无定位信息的段排在有定位者之后, 同级按注册序; 成环时按注册序打破并 WARN。
     */
    private List<T> OrderByConstraints<T>(
        IReadOnlyList<(long Seq, T Value)> entries,
        Func<T, string> nameOf,
        Func<T, IReadOnlyList<string>?> afterOf,
        Func<T, IReadOnlyList<string>?> beforeOf,
        IReadOnlyList<string> spine,
        string kind)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < entries.Count; i++)
            index[nameOf(entries[i].Value)] = i;
        var positioned = new bool[entries.Count];
        var edges = new List<(int Earlier, int Later)>();
        void Link(string earlier, string later)
        {
            if (earlier == later || !index.TryGetValue(earlier, out var from) || !index.TryGetValue(later, out var to))
                return;
            positioned[from] = true;
            positioned[to] = true;
            edges.Add((from, to));
        }
        var present = spine.Where(index.ContainsKey).ToList();
        for (var i = 1; i < present.Count; i++)
            Link(present[i - 1], present[i]);
        for (var i = 0; i < entries.Count; i++)
        {
            var (_, value) = entries[i];
            foreach (var earlier in afterOf(value) ?? [])
                Link(earlier, nameOf(value));
            foreach (var later in beforeOf(value) ?? [])
                Link(nameOf(value), later);
        }
        var indegree = new int[entries.Count];
        var outgoing = new List<int>?[entries.Count];
        foreach (var (earlier, later) in edges)
        {
            indegree[later]++;
            (outgoing[earlier] ??= []).Add(later);
        }
        var comparer = Comparer<int>.Create((a, b) =>
        {
            var zone = (positioned[a] ? 0 : 1).CompareTo(positioned[b] ? 0 : 1);
            return zone != 0 ? zone : entries[a].Seq.CompareTo(entries[b].Seq);
        });
        var ready = new SortedSet<int>(comparer);
        for (var i = 0; i < entries.Count; i++)
        {
            if (indegree[i] == 0)
                ready.Add(i);
        }
        var ordered = new List<T>(entries.Count);
        var emitted = new bool[entries.Count];
        while (ordered.Count < entries.Count)
        {
            if (ready.Count == 0)
            {
                var cycle = Enumerable.Range(0, entries.Count)
                    .Where(i => !emitted[i])
                    .Select(i => $"\"{nameOf(entries[i].Value)}\"")
                    .ToList();
                Ctx.LoggerFor("systemPrompt").Warn(
                    $"prompt {kind} ordering constraints form a cycle ({string.Join(", ", cycle)}); breaking it by registration order — declare consistent before/after to fix");
                var fallback = Enumerable.Range(0, entries.Count).Where(i => !emitted[i]).MinBy(i => entries[i].Seq);
                ready.Add(fallback);
            }
            var current = ready.Min;
            ready.Remove(current);
            if (emitted[current])
                continue;
            emitted[current] = true;
            ordered.Add(entries[current].Value);
            foreach (var next in outgoing[current] ?? [])
            {
                if (--indegree[next] == 0)
                    ready.Add(next);
            }
        }
        return ordered;
    }

    private static IReadOnlyList<ToolSchema> OrderTools(
        List<ToolSchema> tools,
        IReadOnlyList<string>? toolOrder,
        HashSet<string> knownNames)
    {
        if (tools.Any(tool => tool.Name == ToolOrderRest))
            throw new InvalidOperationException($"tool provider returned reserved tool name \"{ToolOrderRest}\" (reserved for toolOrder's rest entry)");
        if (toolOrder is null)
            return tools.OrderBy(tool => tool.Name, StringComparer.Ordinal).ToList();
        var unknown = toolOrder.Where(name => name != ToolOrderRest && !knownNames.Contains(name)).ToList();
        if (unknown.Count > 0)
        {
            throw new InvalidOperationException(
                $"toolOrder lists unregistered tool{(unknown.Count > 1 ? "s" : "")} {string.Join(", ", unknown.Select(name => $"\"{name}\""))}; known tools: {string.Join(", ", knownNames.Order())}");
        }
        var listed = new HashSet<string>(toolOrder);
        var rest = tools.Where(tool => !listed.Contains(tool.Name)).OrderBy(tool => tool.Name, StringComparer.Ordinal).ToList();
        var ordered = new List<ToolSchema>();
        foreach (var name in toolOrder)
        {
            if (name == ToolOrderRest)
                ordered.AddRange(rest);
            else
                ordered.AddRange(tools.Where(tool => tool.Name == name));
        }
        return ordered;
    }
}
