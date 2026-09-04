using Cordis;
using Dsh.Llm;

namespace Dsh.Core;

public sealed record AssembleContext(ScopeKey? Scope = null, CancellationToken Signal = default);

public sealed record PromptSection(string Name, int Order, Func<AssembleContext, string> Text, bool Complete = false)
{
    public static PromptSection Literal(string name, int order, string text, bool complete = false)
        => new(name, order, _ => text, complete);
}

public sealed record PromptContext(string Name, int Order, Func<AssembleContext, string> Text)
{
    public static PromptContext Literal(string name, int order, string text)
        => new(name, order, _ => text);
}

public sealed record AssembledSection(string Name, string Text);

public sealed record AssembledContext(string Name, string Text);

public sealed record ToolProviderResult(IReadOnlyList<ToolSchema> Schemas, IReadOnlyList<string>? KnownNames = null);

public sealed record PromptAssembly(
    IReadOnlyList<AssembledSection> Sections,
    IReadOnlyList<AssembledContext> Contexts,
    IReadOnlyList<ToolSchema> Tools,
    IReadOnlyDictionary<string, string?> Variables);

public static class PromptOrders
{
    public const int HarnessIdentity = -1000;
    public const int HarnessSource = -900;
    public const int WebSurface = -800;
    public const int DeploymentPersona = 0;
    public const int PlanPolicy = 500;
    public const int TeamPolicy = 600;
    public const int PtcOnly = 800;
    public const int FileReference = 900;
    public const int ToolBash = 1000;
    public const int ToolPwsh = 1010;
    public const int ToolRead = 1100;
    public const int ToolWrite = 1200;
    public const int ToolEdit = 1300;
    public const int ToolGlob = 1400;
    public const int ToolGrep = 1500;
    public const int ToolJobs = 1600;
    public const int ToolPty = 1700;
    public const int ToolWebSearch = 2000;
    public const int ToolWebFetch = 2100;
    public const int ToolLsp = 2200;
    public const int ToolSessionQuery = 2300;
    public const int ToolGoal = 2400;
    public const int ToolCordis = 2500;
    public const int ToolWorkflow = 2600;
    public const int ToolRalph = 2700;
    public const int ToolSubagent = 2800;
    public const int ToolReport = 2900;
    public const int ToolsSdk = 5000;
    public const int DeliverableFileReferences = 9000;
    public const int StructuredOutput = 9900;

    public const int ContextSandboxPolicy = 110;
    public const int ContextApprovalPolicy = 115;
    public const int ContextSubagentDelegation = 120;
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
    public const string AssembleEvent = "system-prompt/assemble";
    public const string ChangeEvent = "system-prompt/change";

    private sealed class PromptLayer
    {
        public NamedEntries<PromptSection> Sections { get; }
        public NamedEntries<PromptContext> Contexts { get; }
        public AnonymousEntries<bool> RuntimeContextSuppressors { get; } = new();
        public AnonymousEntries<Func<AssembleContext, ToolProviderResult>> ToolProviders { get; } = new();
        public NamedEntries<Func<AssembleContext, string?>> Variables { get; }

        public PromptLayer(ScopeKey? scope)
        {
            Sections = new NamedEntries<PromptSection>(name => new InvalidOperationException(scope is null
                ? $"prompt section \"{name}\" is already registered (for a per-agent override, register through that agent's agent.ctx instead)"
                : $"prompt section \"{name}\" is already registered in this scope"));
            Contexts = new NamedEntries<PromptContext>(name => new InvalidOperationException(scope is null
                ? $"prompt context \"{name}\" is already registered (for a per-agent override, register through that agent's agent.ctx instead)"
                : $"prompt context \"{name}\" is already registered in this scope"));
            Variables = new NamedEntries<Func<AssembleContext, string?>>(name => new InvalidOperationException(scope is null
                ? $"prompt variable \"{name}\" is already registered (for a per-agent value, register through that agent's agent.ctx instead)"
                : $"prompt variable \"{name}\" is already registered in this scope"));
        }
    }

    private readonly ScopedLayers<PromptLayer> _layers;
    private readonly IReadOnlyList<string>? _toolOrder;

    public SystemPrompt(Context ctx, SystemPromptConfig config) : base(ctx, ServiceName)
    {
        _layers = new ScopedLayers<PromptLayer>(scope => new PromptLayer(scope), () => ctx.Emit(ChangeEvent));
        _toolOrder = ValidateToolOrder(config.ToolOrder);
        if (config.IncludeHarnessIdentity)
        {
            Section(PromptSection.Literal(
                "harness:identity",
                PromptOrders.HarnessIdentity,
                "You are an AI agent powered by DeepSeek Harness."));
        }
        Section(PromptSection.Literal(PersonaSection, PromptOrders.DeploymentPersona, config.Persona));
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
            layer => layer.Sections.Insert(section.Name, section),
            layer => layer.Sections.Remove(section.Name));

    public IDisposable ReplacePersona(string text, bool complete = false)
    {
        _layers.Global.Sections.Remove(PersonaSection);
        return Section(PromptSection.Literal(PersonaSection, PromptOrders.DeploymentPersona, text, complete));
    }

    public IDisposable Context(PromptContext context)
        => _layers.Effect(Ctx, null,
            layer => layer.Contexts.Insert(context.Name, context),
            layer => layer.Contexts.Remove(context.Name));

    public IDisposable SuppressRuntimeContext()
        => _layers.Effect(Ctx, null,
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
        var sectionDefinitions = sectionByName.Values
            .OrderBy(section => section.Order)
            .ThenBy(section => section.Name, StringComparer.Ordinal)
            .ToList();
        var completeSections = sectionDefinitions.Where(section => section.Complete).ToList();
        if (completeSections.Count > 1)
        {
            throw new InvalidOperationException(
                $"multiple complete prompt sections are active: {string.Join(", ", completeSections.Select(section => $"\"{section.Name}\""))}");
        }
        AssembledSection? completeSection = null;
        var sections = sectionDefinitions.Select(section =>
        {
            var assembled = new AssembledSection(section.Name, section.Text(context));
            if (section.Complete)
                completeSection = assembled;
            return assembled;
        }).ToList();
        var assembly = new PromptAssembly(
            sections,
            runtimeContextSuppressed
                ? []
                : contextByName.Values
                    .OrderBy(entry => entry.Order)
                    .Select(entry => new AssembledContext(entry.Name, entry.Text(context)))
                    .ToList(),
            OrderTools(collected, _toolOrder, knownNames),
            variables);
        var transformed = await Ctx.Events.Waterfall(
            DshScope.ScopeTarget(Ctx, scope),
            AssembleEvent,
            [assembly, context],
            () => new ValueTask<object?>(assembly)) as PromptAssembly ?? assembly;
        if (completeSection is null && !runtimeContextSuppressed)
            return transformed;
        return transformed with
        {
            Sections = completeSection is null ? transformed.Sections : [completeSection],
            Contexts = runtimeContextSuppressed ? [] : transformed.Contexts,
        };
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
